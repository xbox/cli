// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class XboxLiveWebServiceCommandTests : IDisposable
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    private const string Route = "/xboxpartner/v1/xboxlive/web-services";

    private const string Service =
        """{"webServiceId":"11111111-2222-3333-4444-555555555555","name":"Nod Arena matchmaker","businessPartnerId":"22222222-3333-4444-5555-666666666666","telemetryAccess":false,"appChannelsAccess":false}""";

    // What 'delete' writes to stderr about the web service before it asks.
    private const string Named =
        $"Web service {Id} (Nod Arena matchmaker) acts for business partner 22222222-3333-4444-5555-666666666666.";

    private static readonly string[] Seller = ["--seller-id", "1234567"];

    // DER-encoded OIDs of pbeWithSHAAnd3-KeyTripleDES-CBC (1.2.840.113549.1.12.1.3) and PBES2 (1.2.840.113549.1.5.13).
    private static readonly byte[] TripleDesSha1Oid = [0x06, 0x0A, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x0C, 0x01, 0x03];
    private static readonly byte[] Pbes2Oid = [0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x05, 0x0D];

    private readonly string _directory = Directory.CreateTempSubdirectory("xbox-web-service-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task List_ReadsEveryPageForTheSameSeller()
    {
        var handler = new RoutingHttpHandler(request => RoutingHttpHandler.Json(
            request.Text.Contains("continuationToken", StringComparison.Ordinal)
                ? """{"webServices":[{"webServiceId":"22222222-3333-4444-5555-666666666666","name":"Nod Rally stats"}]}"""
                : $$"""{"webServices":[{{Service}}],"continuationToken":"page2"}"""));

        var run = await InvokeAsync(handler, ["list", .. Seller, "--output", "json-compact"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            $$"""[{{Service}},{"webServiceId":"22222222-3333-4444-5555-666666666666","name":"Nod Rally stats"}]""",
            run.Stdout.Trim());
        Assert.All(handler.Requests, request => Assert.Equal(Route, request.Path));
        Assert.Equal(
            ["""{"sellerId":1234567,"pageSize":100}""", """{"sellerId":1234567,"continuationToken":"page2"}"""],
            handler.Requests.Select(request => request.Text));
    }

    [Fact]
    public async Task List_AsATable_ShowsTheDeclaredColumns()
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json($$"""{"webServices":[{{Service}}]}"""));

        var run = await InvokeAsync(handler, ["list", .. Seller, "--output", "table"]);

        Assert.Equal(0, run.ExitCode);
        string[] lines = run.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches("^WEB SERVICE ID +NAME +BUSINESS PARTNER +TELEMETRY +APP CHANNELS *$", lines[0]);
        Assert.Matches(
            $"^{Id} +Nod Arena matchmaker +22222222-3333-4444-5555-666666666666 +false +false *$", lines[^1]);
    }

    [Fact]
    public async Task Create_SendsTheNameAndAccessOnce()
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(Service, HttpStatusCode.Created));

        var run = await InvokeAsync(
            handler, ["create", "--name", "Nod Arena matchmaker", "--telemetry-access", .. Seller, "--output", "json-compact"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Service, run.Stdout.Trim());
        Assert.Contains($"Created web service {Id} (Nod Arena matchmaker).", run.Stderr, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Route}/create", request.Path);
        Assert.Equal(
            """{"sellerId":1234567,"name":"Nod Arena matchmaker","telemetryAccess":true,"appChannelsAccess":false}""",
            request.Text);
    }

    [Fact]
    public async Task Update_SendsOnlyTheChangesPassed()
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(Service));

        var run = await InvokeAsync(handler, ["update", Id, "--telemetry-access", "false", .. Seller]);

        Assert.Equal(0, run.ExitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Route}/update", request.Path);
        Assert.Equal($$"""{"sellerId":1234567,"webServiceId":"{{Id}}","telemetryAccess":false}""", request.Text);
    }

    [Fact]
    public async Task Delete_WithoutConfirmOutsideATerminal_NamesTheWebServiceAndDeletesNothing()
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(Service));

        var run = await InvokeAsync(handler, ["delete", Id, .. Seller]);

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains(Named, run.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            $"This deletes web service {Id} of seller 1234567, which can't be undone.", run.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "To stop using a web service, keep it: remove it from your games' access policies, and turn off its "
                + "telemetry and app channels access with 'xbox xbl web-service update'.",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("Run again with --confirm (--yes, -y)", run.Stderr, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Route}/get", request.Path);
        Assert.Equal($$"""{"sellerId":1234567,"webServiceId":"{{Id}}"}""", request.Text);
    }

    [Fact]
    public async Task Delete_WithConfirm_WritesTheWebServiceAsItWas()
    {
        using var stderr = new StringWriter();
        string? beforeDeleting = null;
        var handler = new RoutingHttpHandler(request =>
        {
            if (request.Path == $"{Route}/delete")
            {
                beforeDeleting = stderr.ToString();
            }

            return RoutingHttpHandler.Json(Service);
        });

        var run = await InvokeAsync(
            handler, ["delete", Id, "--confirm", .. Seller, "--output", "json-compact"], stderr: stderr);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Service, run.Stdout.Trim());
        Assert.Contains(Named, beforeDeleting, StringComparison.Ordinal);
        Assert.Contains($"Deleted web service {Id} (Nod Arena matchmaker).", run.Stderr, StringComparison.Ordinal);
        Assert.Equal([$"{Route}/get", $"{Route}/delete"], handler.Requests.Select(request => request.Path));
        Assert.Equal($$"""{"sellerId":1234567,"webServiceId":"{{Id}}"}""", handler.Requests[1].Text);
    }

    [Theory]
    [InlineData("y", 0, 2)]
    [InlineData("n", 1, 1)]
    public async Task Delete_AtATerminal_AsksWithTheWebServiceName(string answer, int exitCode, int requests)
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(Service));
        TextReader originalIn = Console.In;
        Console.SetIn(new StringReader(answer + Environment.NewLine));
        Run run;
        try
        {
            run = await InvokeAsync(handler, ["delete", Id, .. Seller], interactive: true);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Contains($"Delete web service {Id} (Nod Arena matchmaker)? [y/N]:", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(requests, handler.Requests.Count);
    }

    [Fact]
    public async Task Delete_OfAWebServiceTheSellerDoesNotHave_FailsBeforeAsking()
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(
            """{"status":404,"title":"Web service not found.","code":"web_service_not_found"}""", HttpStatusCode.NotFound));

        // No --confirm, so a delete that asked first would fail for that instead.
        var run = await InvokeAsync(handler, ["delete", Id, .. Seller]);

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("web_service_not_found", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("This deletes", run.Stderr, StringComparison.Ordinal);
        Assert.Equal($"{Route}/get", Assert.Single(handler.Requests).Path);
    }

    [Theory]
    [InlineData("create", "web_service_create_outcome_unknown", "Before you create it again, check with: xbox xbl web-service list --seller-id 1234567")]
    [InlineData("update", "web_service_update_outcome_unknown", $"doesn't show the change: xbox xbl web-service get {Id} --seller-id 1234567")]
    [InlineData("delete", "web_service_delete_outcome_unknown", $"Check with: xbox xbl web-service get {Id} --seller-id 1234567")]
    public async Task AChangeThatFailsAfterItIsSent_HasAnUnknownOutcome(string command, string code, string check)
    {
        var handler = AnswersTheRead(_ => RoutingHttpHandler.Json(
            $$"""{"status":502,"title":"Outcome unknown.","code":"{{code}}"}""", HttpStatusCode.BadGateway));
        string[] arguments = command switch
        {
            "create" => ["create", "--name", "Nod Arena matchmaker"],
            "update" => ["update", Id, "--name", "Nod Arena matchmaker EU"],
            _ => ["delete", Id, "--confirm"],
        };

        var run = await InvokeAsync(handler, [.. arguments, .. Seller]);

        Assert.Equal(XboxPartnerCommandSupport.OutcomeUnknownExitCode, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains(code, run.Stderr, StringComparison.Ordinal);
        Assert.Contains(check, run.Stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests, IsAChange);
    }

    [Fact]
    public async Task ARefusedChange_ExitsOne()
    {
        var handler = AnswersTheRead(_ => RoutingHttpHandler.Json(
            """{"status":404,"title":"Web service not found.","code":"web_service_not_found"}""", HttpStatusCode.NotFound));

        var run = await InvokeAsync(handler, ["delete", Id, "--confirm", .. Seller]);

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("web_service_not_found", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown", run.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, 1,
        "Request failed: The connection failed. The request didn't reach the service, so no web service was created.",
        "create", "--name", "Nod Arena matchmaker")]
    [InlineData(HttpRequestError.SecureConnectionError, 1,
        "Request failed: The connection failed. The request didn't reach the service, so no certificate was created.",
        "certificate", "create", "--web-service-id", Id, "--dest", "{pem}", "-y")]
    [InlineData(HttpRequestError.ConnectionError, 3,
        $"It is unknown whether web service {Id} was deleted.",
        "delete", Id, "--confirm")]
    public async Task ATransportFailure_LeavesTheOutcomeUnknown_UnlessTheRequestNeverReachedTheService(
        HttpRequestError error, int exitCode, string expected, params string[] arguments)
    {
        string file = Path.Combine(_directory, "matchmaker.pem");
        var handler = AnswersTheRead(_ => throw new HttpRequestException(error, "The connection failed."));

        var run = await InvokeAsync(
            handler, [.. arguments.Select(argument => argument == "{pem}" ? file : argument), .. Seller]);

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains(expected, run.Stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests, IsAChange);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task CertificateCreate_SavesAPfxWithTheKeyWhosePublicKeyItSent()
    {
        string file = Path.Combine(_directory, "matchmaker.pfx");
        string password = WriteFile("password.txt", "Nod-Arena-2027");
        byte[]? sent = null;
        var handler = new RoutingHttpHandler(request =>
        {
            sent = PublicKey(request);
            return Issue(request, sandbox: "XDKS.1");
        });

        var run = await InvokeAsync(
            handler,
            ["certificate", "create", "--web-service-id", Id, "--dest", file, "--sandbox-id", "xdks.1",
                "--password-file", password, "--confirm", .. Seller]);

        Assert.Equal(0, run.ExitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Route}/certificates/create", request.Path);
        using JsonDocument body = JsonDocument.Parse(request.Text);
        Assert.Equal("xdks.1", body.RootElement.GetProperty("sandbox").GetString());

        // The platform's own writer of the format produces the same blob.
        using var sentKey = new RSACryptoServiceProvider();
        sentKey.ImportCspBlob(sent!);
        Assert.Equal(sent, sentKey.ExportCspBlob(includePrivateParameters: false));

        using X509Certificate2 saved = X509CertificateLoader.LoadPkcs12FromFile(file, "Nod-Arena-2027");
        Assert.True(saved.HasPrivateKey);
        using RSA savedKey = saved.GetRSAPrivateKey()!;
        Assert.Equal(sentKey.ExportParameters(false).Modulus, savedKey.ExportParameters(false).Modulus);

        // Windows Server 2016 and earlier import only a PFX file encrypted with 3DES and SHA-1, not AES.
        byte[] pfx = File.ReadAllBytes(file);
        Assert.True(pfx.AsSpan().IndexOf(TripleDesSha1Oid) >= 0, "Expected 3DES and SHA-1 encryption.");
        Assert.True(pfx.AsSpan().IndexOf(Pbes2Oid) < 0, "Expected no AES (PBES2) encryption.");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }

        JsonElement output = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(Id, output.GetProperty("webServiceId").GetString());
        Assert.Equal("XDKS.1", output.GetProperty("sandbox").GetString());
        Assert.Equal(saved.Thumbprint, output.GetProperty("thumbprint").GetString());
        Assert.Equal(file, output.GetProperty("file").GetString());
        Assert.Contains($"Saved the certificate and its private key to '{file}'", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificateCreate_ToAPemFile_SavesTheCertificateAndItsKey()
    {
        string file = Path.Combine(_directory, "matchmaker.pem");
        var handler = new RoutingHttpHandler(request => Issue(request, sandbox: null));

        var run = await InvokeAsync(
            handler, ["certificate", "create", "--web-service-id", Id, "--dest", file, "-y", .. Seller]);

        Assert.Equal(0, run.ExitCode);
        using X509Certificate2 saved = X509Certificate2.CreateFromPemFile(file);
        Assert.True(saved.HasPrivateKey);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("sandbox").ValueKind);
    }

    [Theory]
    [InlineData("pem")]
    [InlineData("blob")]
    public async Task CertificateCreate_WithYourOwnKey_SendsItAndSavesOnlyTheCertificate(string format)
    {
        using RSA key = RSA.Create(2048);
        using var csp = new RSACryptoServiceProvider();
        csp.ImportParameters(key.ExportParameters(false));
        byte[] blob = csp.ExportCspBlob(includePrivateParameters: false);
        string file = Path.Combine(_directory, "matchmaker.cer");
        byte[]? sent = null;
        var handler = new RoutingHttpHandler(request =>
        {
            sent = PublicKey(request);
            return Issue(request, sandbox: null);
        });
        string[] input = format == "pem"
            ? ["--public-key-file", WriteFile("public.pem", key.ExportSubjectPublicKeyInfoPem())]
            : ["--public-key-file", "-"];

        var run = await InvokeAsync(
            handler,
            ["certificate", "create", "--web-service-id", Id, .. input, "--dest", file, "-y", .. Seller],
            new StringReader(Convert.ToBase64String(blob) + Environment.NewLine));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(blob, sent);
        string saved = File.ReadAllText(file);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", saved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private", "holds a private key, so nothing was sent.")]
    [InlineData("4096", "is 4096-bit. XBOX services sign only 2048-bit RSA keys.")]
    [InlineData("certificate", "doesn't hold an RSA public key")]
    public async Task CertificateCreate_RefusesAnythingButA2048BitPublicKey(string kind, string message)
    {
        using RSA key = RSA.Create(kind == "4096" ? 4096 : 2048);
        string pem = kind switch
        {
            "private" => key.ExportPkcs8PrivateKeyPem(),
            "4096" => key.ExportSubjectPublicKeyInfoPem(),
            _ => new CertificateRequest("CN=Nod Arena", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)).ExportCertificatePem(),
        };
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));
        string file = Path.Combine(_directory, "matchmaker.cer");

        var run = await InvokeAsync(
            handler,
            ["certificate", "create", "--web-service-id", Id, "--public-key-file", WriteFile("key.pem", pem),
                "--dest", file, "-y", .. Seller]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(message, run.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"status":400,"title":"Invalid request.","code":"sandbox_not_found"}""", 1)]
    [InlineData(HttpStatusCode.BadGateway, """{"status":502,"title":"XBOX services failed."}""", 3)]
    public async Task CertificateCreate_ThatFails_LeavesNoFile(HttpStatusCode status, string error, int exitCode)
    {
        string file = Path.Combine(_directory, "matchmaker.pem");
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(error, status));

        var run = await InvokeAsync(
            handler, ["certificate", "create", "--web-service-id", Id, "--dest", file, "-y", .. Seller]);

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.False(File.Exists(file));
        Assert.Single(handler.Requests);
        Assert.Equal(
            exitCode == 3,
            run.Stderr.Contains("It is unknown whether a certificate was created.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CertificateCreate_NeverReplacesAFile()
    {
        string file = WriteFile("matchmaker.pem", "keep me");
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        var run = await InvokeAsync(
            handler, ["certificate", "create", "--web-service-id", Id, "--dest", file, "-y", .. Seller]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains($"'{file}' already exists.", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("keep me", File.ReadAllText(file));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CertificateCreate_RefusesAFolderThatIsALink()
    {
        string target = Directory.CreateDirectory(Path.Combine(_directory, "target")).FullName;
        string link = Path.Combine(_directory, "link");
        if (!TryLinkFolder(link, target))
        {
            return;
        }

        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        var run = await InvokeAsync(
            handler,
            ["certificate", "create", "--web-service-id", Id, "--dest", Path.Combine(link, "matchmaker.pem"), "-y", .. Seller]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains($"such as a symbolic link or junction: '{link}'", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task CertificateCreate_RefusesAFolderThatBecomesALinkBeforeItWrites()
    {
        string target = Directory.CreateDirectory(Path.Combine(_directory, "target")).FullName;
        if (!TryLinkFolder(Path.Combine(_directory, "probe"), target))
        {
            return;
        }

        string folder = Path.Combine(_directory, "out");
        using RSA key = RSA.Create(2048);
        // The folder doesn't exist when the command first checks; it becomes a link while the public key is read.
        using var stdin = new ReadingStdin(
            key.ExportSubjectPublicKeyInfoPem(), () => Directory.CreateSymbolicLink(folder, target));
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        var run = await InvokeAsync(
            handler,
            [
                "certificate", "create", "--web-service-id", Id, "--public-key-file", "-",
                "--dest", Path.Combine(folder, "matchmaker.cer"), "-y", .. Seller,
            ],
            stdin);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains($"such as a symbolic link or junction: '{folder}'", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task CertificateCreate_WithoutConfirmOutsideATerminal_SendsNothing()
    {
        string file = Path.Combine(_directory, "matchmaker.pem");
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        var run = await InvokeAsync(handler, ["certificate", "create", "--web-service-id", Id, "--dest", file, .. Seller]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("valid in every sandbox, RETAIL included,", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("Run again with --confirm (--yes, -y)", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task CertificateCreate_RefusesToReadThePasswordFromATerminal()
    {
        string file = Path.Combine(_directory, "matchmaker.pfx");
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        // With no stdin passed, InvokeAsync's stdin is a terminal, but no prompt can run, as when stderr is redirected.
        var run = await InvokeAsync(
            handler,
            ["certificate", "create", "--web-service-id", Id, "--dest", file, "--password-file", "-", "-y", .. Seller]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            "--password-file - reads the password from stdin, which is a terminal here", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Leave out --password-file", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task CertificateCreate_OnWindows_RefusesAnUnencryptedKeyFile()
    {
        string file = Path.Combine(_directory, "matchmaker.pem");
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        var run = await InvokeAsync(
            handler, ["certificate", "create", "--web-service-id", Id, "--dest", file, "-y", .. Seller], windows: true);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("On Windows, --dest must be a .pfx or .p12 file", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task CertificateCreate_ClosesTheFileBeforeWritingItsPath()
    {
        string file = Path.Combine(_directory, "matchmaker.pem");
        var stdout = new ReadingWriter(file);
        var handler = new RoutingHttpHandler(request => Issue(request, sandbox: null));

        var run = await InvokeAsync(
            handler, ["certificate", "create", "--web-service-id", Id, "--dest", file, "-y", .. Seller],
            stdout: stdout);

        Assert.Null(stdout.Failure);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(File.ReadAllBytes(file), stdout.Read);
    }

    private static byte[] PublicKey(RecordedRequest request)
    {
        using JsonDocument body = JsonDocument.Parse(request.Text);
        return Convert.FromBase64String(body.RootElement.GetProperty("publicKey").GetString()!);
    }

    /// <summary>Answers the read 'delete' sends first with <see cref="Service"/>, and every other request with <paramref name="respond"/>.</summary>
    private static RoutingHttpHandler AnswersTheRead(Func<RecordedRequest, HttpResponseMessage> respond) =>
        new(request => IsAChange(request) ? respond(request) : RoutingHttpHandler.Json(Service));

    private static bool IsAChange(RecordedRequest request) => request.Path != $"{Route}/get";

    /// <summary>
    /// Makes <paramref name="link"/> a symbolic link to <paramref name="target"/>, or returns false where Windows can't:
    /// without Developer Mode or the privilege to make links, it throws an IOException.
    /// </summary>
    private static bool TryLinkFolder(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return false;
        }
    }

    /// <summary>Answers as XBOX services do: signs the public key the request sent into a client certificate.</summary>
    private static HttpResponseMessage Issue(RecordedRequest request, string? sandbox)
    {
        using var publicKey = new RSACryptoServiceProvider();
        publicKey.ImportCspBlob(PublicKey(request));
        using RSA issuerKey = RSA.Create(2048);
        var authority = new CertificateRequest(
            "CN=Nod Publishers Test CA", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authority.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 issuer =
            authority.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(600));
        using X509Certificate2 issued = new CertificateRequest(
                "CN=Nod Arena matchmaker", publicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Create(issuer, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(547), [1, 2, 3, 4]);
        return RoutingHttpHandler.Json(JsonSerializer.Serialize(new
        {
            webServiceId = Id,
            sandbox,
            certificate = Convert.ToBase64String(issued.RawData),
        }));
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<Run> InvokeAsync(
        HttpMessageHandler handler, string[] arguments, TextReader? stdin = null, bool windows = false,
        StringWriter? stdout = null, StringWriter? stderr = null, bool interactive = false)
    {
        RootCommand root = Program.BuildRootCommand();
        Command xbl = root.Subcommands.Single(command => command.Name == "xbl");
        xbl.Subcommands.Remove(xbl.Subcommands.Single(command => command.Name == "web-service"));
        xbl.Subcommands.Add(XboxLiveWebServiceCommandFactory.Create(
            new TestAccountCommandDependencies(
                (_, _) => Task.CompletedTask,
                () => DateTimeOffset.UtcNow,
                () => interactive,
                _ => throw new InvalidOperationException("No prompt is expected."),
                () => throw new InvalidOperationException("No prompt is expected."),
                () => stdin ?? throw new InvalidOperationException("No stdin is expected."),
                () => stdin is not null),
            windows));
        CommandErrorBoundary.Install(root);

        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter output = stdout ?? new StringWriter();
        using StringWriter error = stderr ?? new StringWriter();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        Console.SetOut(output);
        Console.SetError(error);
        Program.XboxPartnerClientFactory = _ => new XboxPartnerClient(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XboxPartner:BaseUrl"] = "https://gateway.example" })
                .Build(),
            httpClient,
            () => Task.FromResult<string?>("caller-token"));
        try
        {
            int exitCode = await root.Parse(["xbl", "web-service", .. arguments], Program.NoResponseFileConfig).InvokeAsync();
            return new Run(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    /// <summary>Stdin that runs <paramref name="reading"/> when the command reads it.</summary>
    private sealed class ReadingStdin(string text, Action reading) : StringReader(text)
    {
        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken)
        {
            reading();
            return base.ReadToEndAsync(cancellationToken);
        }
    }

    /// <summary>Stdout that reads <c>path</c> as each line is written, as a program reading stdout would.</summary>
    private sealed class ReadingWriter(string path) : StringWriter
    {
        public byte[]? Read { get; private set; }

        public string? Failure { get; private set; }

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            try
            {
                Read = File.ReadAllBytes(path);
            }
            catch (IOException exception)
            {
                Failure = exception.Message;
            }
        }
    }
}
