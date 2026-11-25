// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>Tests <c>xbox xbl relying-party</c> and <c>xbox xbl business-partner</c>.</summary>
[Collection("ConsoleRedirect")]
public sealed class XboxLiveAccountCommandTests : IDisposable
{
    private const string Routes = "/xboxpartner/v1/xboxlive/";

    private const string Partner = "11111111-2222-3333-4444-555555555555";

    private const string Saved = "Saved to the account draft. Not published yet. Run 'xbox xbl config publish --scope account";

    private static readonly Lazy<X509Certificate2> Certificate = new(() =>
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=api.nodpublishers.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 19, 0, 0, 0, TimeSpan.Zero));
    });

    private readonly string _directory = Directory.CreateTempSubdirectory("xbox-account-").FullName;

    /// <summary>A public certificate in PEM, as a relying party's encryption certificate.</summary>
    internal static string CertificatePem => Certificate.Value.ExportCertificatePem();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string RelyingParty(string? notAfter = "2027-01-19T00:00:00+00:00") => JsonSerializer.Serialize(new
    {
        name = "rp://api.nodpublishers.com/",
        fileName = "relyingparty-22222222-3333-4444-5555-666666666666.xml",
        businessPartnerId = Partner,
        lifetimeInHours = 16,
        encryptionType = notAfter is null ? null : "PublicKeyEncryption",
        jkuEnabled = notAfter is not null,
        claims = new[] { "http://schemas.microsoft.com/xbox/2011/07/claims/user/agegroup" },
        encryptionCertificate = notAfter is null
            ? null
            : new { thumbprint = "9F1C", subject = "CN=api.nodpublishers.com", notBefore = "2026-01-01T00:00:00+00:00", notAfter },
    });

    private const string BusinessPartner =
        """{"businessPartnerId":"11111111-2222-3333-4444-555555555555","name":"Nod Arena services","type":"Partner","isDefault":false}""";

    [Theory]
    [InlineData("relyingparties/get", """{"name":"api.nodpublishers.com"}""", "relying-party", "get", " api.nodpublishers.com ")]
    [InlineData("businesspartners/get", """{"businessPartnerId":"11111111-2222-3333-4444-555555555555"}""", "business-partner", "get", Partner)]
    [InlineData(
        "relyingparties/create",
        """{"sellerId":1234567,"name":"rp://api.nodpublishers.com/","encryptionCertificate":"{der}","businessPartnerId":"11111111-2222-3333-4444-555555555555","lifetimeInHours":4,"jkuEnabled":false,"claims":["urn:a","urn:b"]}""",
        "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "--business-partner-id", Partner,
        "--lifetime-hours", "4", "--jku-enabled", "false", "--claim", "urn:a", "--claim", "urn:b", "--seller-id", "1234567", "--confirm")]
    [InlineData(
        "relyingparties/create",
        """{"name":"rp://api.nodpublishers.com/","encryptionCertificate":"{der}"}""",
        "relying-party", "create", "rp://api.nodpublishers.com", "--certificate-file", "{cer}", "-y")]
    [InlineData(
        "relyingparties/update",
        """{"name":"rp://api.nodpublishers.com/","encryptionCertificate":"{der}","lifetimeInHours":8,"jkuEnabled":true}""",
        "relying-party", "save", "rp://api.nodpublishers.com/", "--certificate-file", "{pem}", "--lifetime-hours", "8", "--jku-enabled")]
    [InlineData(
        "businesspartners/create",
        """{"sellerId":1234567,"name":"Nod Arena services","type":"Partner"}""",
        "business-partner", "create", "--name", " Nod Arena services ", "--type", "Partner", "--seller-id", "1234567", "--confirm")]
    [InlineData(
        "businesspartners/update",
        """{"businessPartnerId":"11111111-2222-3333-4444-555555555555","name":"Nod Arena services"}""",
        "business-partner", "save", Partner, "--name", "Nod Arena services")]
    public async Task EachCommand_SendsTheRequestTheServiceExpects_AndWritesItsResponse(
        string route, string body, params string[] arguments)
    {
        const string Response = """{"name":"rp://api.nodpublishers.com/"}""";
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(Response));

        var run = await InvokeAsync(handler, [.. Prepare(arguments), "--output", "json-compact"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Response, run.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Routes + route, request.Path);
        Assert.Equal(Normalize(body.Replace("{der}", Convert.ToBase64String(Certificate.Value.RawData))), Normalize(request.Text));
        Assert.Equal(
            route.Contains('/', StringComparison.Ordinal) && !route.EndsWith("/get", StringComparison.Ordinal),
            run.Stderr.Contains(Saved, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("relyingparties", "relyingParties", """{"sellerId":1234567}""", "relying-party", "list", "--seller-id", "1234567")]
    [InlineData("businesspartners", "businessPartners", "{}", "business-partner", "list")]
    public async Task List_WritesABareArray_AsTheOtherXblListsDo(
        string route, string collection, string body, params string[] arguments)
    {
        const string Items = """[{"name":"rp://api.nodpublishers.com/"},{"name":"Nod Arena services"}]""";
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json("{\"" + collection + "\":" + Items + "}"));

        var run = await InvokeAsync(handler, [.. arguments, "--output", "json-compact"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Items, run.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Routes + route, request.Path);
        Assert.Equal(body, request.Text);
    }

    [Fact]
    public async Task RelyingPartySave_Claim_ReplacesAllOfItsClaims_AndHelpSaysSo()
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(RelyingParty()));

        var save = await InvokeAsync(
            handler, "relying-party", "save", "api.nodpublishers.com", "--claim", "urn:a", "--seller-id", "1234567");
        var help = await InvokeAsync(handler, "relying-party", "save", "--help");

        Assert.Equal(0, save.ExitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Routes + "relyingparties/update", request.Path);
        Assert.Equal(
            Normalize("""{"sellerId":1234567,"name":"api.nodpublishers.com","claims":["urn:a"]}"""), Normalize(request.Text));
        Assert.Equal(0, help.ExitCode);
        Assert.Contains(
            "The claims you pass replace all of its claims",
            string.Join(' ', help.Stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PublicKeyEncryption", false, "http://schemas.microsoft.com/xbox/2011/07/claims/user/gamertag",
        "Warning: JKU is strongly recommended.", "relying-party", "save", "api.nodpublishers.com", "--jku-enabled", "false")]
    [InlineData("PublicKeyEncryption", true, "http://schemas.microsoft.com/xbox/2013/03/claims/user/pwid",
        "Warning: the User Pairwise ID (upi) claim", "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "-y")]
    [InlineData("SharedKeyEncryption", false, "http://schemas.microsoft.com/xbox/2011/07/claims/user/gamertag",
        null, "relying-party", "save", "api.nodpublishers.com", "--lifetime-hours", "4")]
    public async Task RelyingPartyWrites_WarnAsPartnerCenterDoes(
        string encryptionType, bool jkuEnabled, string claim, string? warning, params string[] arguments)
    {
        string response = JsonSerializer.Serialize(
            new { name = "rp://api.nodpublishers.com/", encryptionType, jkuEnabled, claims = new[] { claim } });

        var run = await InvokeAsync(new RoutingHttpHandler(_ => RoutingHttpHandler.Json(response)), Prepare(arguments));

        Assert.Equal(0, run.ExitCode);
        string[] warnings =
            [.. run.Stderr.Split(Environment.NewLine).Where(line => line.StartsWith("Warning: ", StringComparison.Ordinal))];
        Assert.Equal(warning is null ? 0 : 1, warnings.Length);
        Assert.All(warnings, line => Assert.StartsWith(warning!, line, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(
        "relying-party",
        "^NAME +BUSINESS PARTNER ID +LIFETIME HOURS +CERTIFICATE EXPIRES *$",
        "^rp://api.nodpublishers.com/ +11111111-2222-3333-4444-555555555555 +16 +2027-01-19 *$",
        "^rp://api.nodpublishers.com/ +11111111-2222-3333-4444-555555555555 +16 +- *$")]
    [InlineData(
        "business-partner",
        "^ID +NAME +TYPE +DEFAULT *$",
        "^11111111-2222-3333-4444-555555555555 +Nod Arena services +Partner +false *$",
        "^11111111-2222-3333-4444-555555555555 +Nod Arena services +Partner +false *$")]
    public async Task List_TableOutput_ShowsTheDeclaredColumns(string group, string header, string first, string second)
    {
        string list = group == "relying-party"
            ? $$"""{"relyingParties":[{{RelyingParty()}},{{RelyingParty(notAfter: null)}}]}"""
            : $$"""{"businessPartners":[{{BusinessPartner}},{{BusinessPartner}}]}""";

        var run = await InvokeAsync(new RoutingHttpHandler(_ => RoutingHttpHandler.Json(list)), group, "list", "--output", "table");

        Assert.Equal(0, run.ExitCode);
        var lines = run.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches(header, lines[0]);
        Assert.Matches(first, lines[^2]);
        Assert.Matches(second, lines[^1]);
    }

    [Theory]
    [InlineData("missing", "could not be read")]
    [InlineData("empty", "isn't an X.509 certificate (.cer) in DER or PEM.")]
    [InlineData("pfx", "holds a private key, so nothing was sent. Pass only the public certificate (.cer)")]
    [InlineData("pem-with-key", "holds a private key, so nothing was sent. Pass only the public certificate (.cer)")]
    [InlineData("text", "isn't an X.509 certificate (.cer) in DER or PEM.")]
    public async Task RelyingPartyCreate_RefusesAFileThatIsntOnlyAPublicCertificate_BeforeSendingAnything(
        string kind, string expected)
    {
        string path = Path.Combine(_directory, "rp.pfx");
        using (RSA key = Certificate.Value.GetRSAPrivateKey()!)
        {
            if (kind != "missing")
            {
                await File.WriteAllBytesAsync(path, kind switch
                {
                    "empty" => Array.Empty<byte>(),
                    "pfx" => Certificate.Value.Export(X509ContentType.Pkcs12),
                    "pem-with-key" => System.Text.Encoding.ASCII.GetBytes(CertificatePem + key.ExportPkcs8PrivateKeyPem()),
                    _ => "not a certificate"u8.ToArray(),
                });
            }
        }

        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(RelyingParty()));

        var run = await InvokeAsync(
            handler, "relying-party", "create", "api.nodpublishers.com", "--certificate-file", path, "--confirm");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(run.Stdout);
        Assert.Contains(expected, run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "Creating relying party rp://api.nodpublishers.com/ for seller 1234567, owned by the account's default business "
            + "partner, with tokens encrypted to CN=api.nodpublishers.com (thumbprint {thumbprint}, expires 2027-01-19). Its "
            + "name is unique across XBOX services: nobody can rename, delete, or reuse it",
        "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "--seller-id", "1234567")]
    [InlineData(
        "Creating business partner 'Nod Arena services'. It gets the least privileged type the account's business "
            + "partners have. A business partner can't be deleted, and its type can't be changed.",
        "business-partner", "create", "--name", "Nod Arena services")]
    [InlineData(
        "Creating business partner 'Nod Arena services' of type Legacy for seller 1234567. A business partner can't be "
            + "deleted, and its type can't be changed.",
        "business-partner", "create", "--name", "Nod Arena services", "--type", "Legacy", "--seller-id", "1234567")]
    public async Task Create_WithoutATerminal_SaysWhatItWouldCreateAndIsRefusedUnlessConfirmed(
        string summary, params string[] arguments)
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(BusinessPartner));

        var run = await InvokeAsync(handler, Prepare(arguments));

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(run.Stdout);
        Assert.Contains(summary.Replace("{thumbprint}", Certificate.Value.Thumbprint), run.Stderr, StringComparison.Ordinal);
        Assert.Contains("Run again with --confirm (--yes, -y)", run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(404, "relying_party_not_found", "relying-party", "get", "rp://api.nodpublishers.com/")]
    [InlineData(403, "identity_override_not_authorized", "business-partner", "list", "--seller-id", "7654321")]
    public async Task AReadTheServiceRefuses_ExitsWithOneAndItsError(int status, string code, params string[] arguments)
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(
            $$"""{"status":{{status}},"title":"Refused.","detail":"The service's detail.","code":"{{code}}"}""",
            (HttpStatusCode)status));

        var run = await InvokeAsync(handler, [.. arguments, "--output", "json-compact"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stdout);
        var error = JsonDocument.Parse(run.Stderr).RootElement;
        Assert.Equal(status, error.GetProperty("status").GetInt32());
        Assert.Equal(code, error.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(409, "relying_party_exists", "To change its settings, run 'xbox xbl relying-party save'.",
        "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "-y")]
    [InlineData(503, "relying_party_settings_not_saved",
        "To save them, run 'xbox xbl relying-party create' again with the same name and business partner.",
        "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "-y")]
    [InlineData(409, "no_business_partners",
        "Run 'xbox xbl relying-party create' to create the account's first relying party.",
        "business-partner", "create", "--name", "Nod Arena services", "-y")]
    public async Task Create_KnownRefusals_SayWhatToDoNext(int status, string code, string next, params string[] arguments)
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(
            $$"""{"status":{{status}},"title":"Refused.","detail":"The service's detail.","code":"{{code}}"}""",
            (HttpStatusCode)status));

        var run = await InvokeAsync(handler, Prepare(arguments));

        Assert.Equal(1, run.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(run.Stdout);
        var error = JsonDocument.Parse(run.Stderr).RootElement;
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.StartsWith($"The service's detail. {next}", error.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        HttpStatusCode.BadGateway, """{"status":502}""",
        "It is unknown whether relying party rp://api.nodpublishers.com/ was created. Check with: xbox xbl relying-party "
            + "get rp://api.nodpublishers.com/ --seller-id 1234567. Running the same create again is safe",
        "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "-y")]
    [InlineData(
        HttpStatusCode.OK, "not json",
        "It is unknown whether relying party api.nodpublishers.com was changed. Check with: xbox xbl relying-party get "
            + "api.nodpublishers.com --seller-id 1234567. Running the same save again is safe.",
        "relying-party", "save", "api.nodpublishers.com", "--lifetime-hours", "4")]
    [InlineData(
        HttpStatusCode.GatewayTimeout, """{"status":504}""",
        "It is unknown whether business partner 'Nod Arena services' was created. Check with: xbox xbl business-partner "
            + "list --seller-id 1234567. Don't run the create again until you've checked: another create adds a second "
            + "business partner.",
        "business-partner", "create", "--name", "Nod Arena services", "-y")]
    public async Task AFailureAfterSending_ExitsWithOutcomeUnknownAndHowToCheck(
        HttpStatusCode status, string response, string expected, params string[] arguments)
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(response, status));

        var run = await InvokeAsync(handler, [.. Prepare(arguments), "--seller-id", "1234567"]);

        Assert.Equal(XboxPartnerCommandSupport.OutcomeUnknownExitCode, run.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(run.Stdout);
        Assert.Contains(expected, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("response-correlation", run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, 1,
        "Request failed: The connection failed. The request didn't reach the service, so no relying party was created.",
        "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "-y")]
    [InlineData(HttpRequestError.SecureConnectionError, 1,
        "Request failed: The connection failed. The request didn't reach the service, so no business partner was created.",
        "business-partner", "create", "--name", "Nod Arena services", "-y")]
    [InlineData(HttpRequestError.ConnectionError, 3,
        "It is unknown whether business partner 'Nod Arena services' was created.",
        "business-partner", "create", "--name", "Nod Arena services", "-y")]
    public async Task ATransportFailure_LeavesTheOutcomeUnknown_UnlessTheRequestNeverReachedTheService(
        HttpRequestError error, int exitCode, string expected, params string[] arguments)
    {
        var handler = new RoutingHttpHandler(_ => throw new HttpRequestException(error, "The connection failed."));

        var run = await InvokeAsync(handler, Prepare(arguments));

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains(expected, run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pass --certificate-file, --lifetime-hours, --jku-enabled, or --claim: there's nothing to change.",
        "relying-party", "save", "api.nodpublishers.com")]
    [InlineData("Each --claim requires a claim type URI.",
        "relying-party", "save", "api.nodpublishers.com", "--claim", "urn:a", "--claim")]
    [InlineData("Each --claim requires a claim type URI.",
        "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "-y", "--claim", "urn:a", "--claim")]
    [InlineData("Invalid --business-partner-id 'nod': it must be a GUID",
        "relying-party", "create", "api.nodpublishers.com", "--certificate-file", "{pem}", "--business-partner-id", "nod", "-y")]
    [InlineData("Invalid business partner ID 'nod': it must be a GUID", "business-partner", "get", "nod")]
    [InlineData("Invalid --name: it can't be empty.", "business-partner", "create", "--name", " ", "-y")]
    public async Task InvalidInvocations_AreRefusedWithoutARequest(string expected, params string[] arguments)
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(BusinessPartner));

        var run = await InvokeAsync(handler, Prepare(arguments));

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(expected, run.Stderr, StringComparison.Ordinal);
    }

    // The same JSON, property order included, whichever characters its strings escape.
    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Writes the certificate files the arguments name: {pem} in PEM, and {cer} in DER.</summary>
    private string[] Prepare(string[] arguments)
    {
        string pem = Path.Combine(_directory, "rp.pem");
        string cer = Path.Combine(_directory, "rp.cer");
        File.WriteAllText(pem, CertificatePem);
        File.WriteAllBytes(cer, Certificate.Value.RawData);
        return [.. arguments.Select(argument => argument.Replace("{pem}", pem).Replace("{cer}", cer))];
    }

    private static async Task<Run> InvokeAsync(HttpMessageHandler handler, params string[] arguments)
    {
        RootCommand root = Program.BuildRootCommand();
        Command xbl = root.Subcommands.Single(command => command.Name == "xbl");
        var dependencies = new XboxPartnerJobDependencies((_, _) => Task.CompletedTask, () => DateTimeOffset.UtcNow, () => false);
        xbl.Subcommands.Remove(xbl.Subcommands.Single(command => command.Name == "relying-party"));
        xbl.Subcommands.Remove(xbl.Subcommands.Single(command => command.Name == "business-partner"));
        xbl.Subcommands.Add(RelyingPartyCommandFactory.Create(dependencies));
        xbl.Subcommands.Add(BusinessPartnerCommandFactory.Create(dependencies));
        CommandErrorBoundary.Install(root);

        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = _ => new XboxPartnerClient(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XboxPartner:BaseUrl"] = "https://gateway.example" })
                .Build(),
            httpClient,
            () => Task.FromResult<string?>("caller-token"));
        try
        {
            int exitCode = await root.Parse(["xbl", .. arguments], Program.NoResponseFileConfig).InvokeAsync();
            return new Run(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);
}
