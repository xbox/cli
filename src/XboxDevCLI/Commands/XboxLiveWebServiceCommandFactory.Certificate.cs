// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.CommandLine;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using static XboxDevCLI.Commands.XboxLiveAccountCommandSupport;

namespace XboxDevCLI.Commands;

internal static partial class XboxLiveWebServiceCommandFactory
{
    // XBOX services sign only a 2048-bit RSA key, sent as a Windows PUBLICKEYBLOB: a BLOBHEADER (type, version,
    // reserved, algorithm) and an RSAPUBKEY ("RSA1", bit length, public exponent), then the modulus, all
    // little-endian. Partner Center's PowerShell script prints the same blob.
    private const int KeySize = 2048;
    private const int PublicKeyBlobLength = 20 + (KeySize / 8);
    private const byte PublicKeyBlobType = 0x06;
    private const byte BlobVersion = 0x02;
    private const int KeyExchangeAlgorithm = 0xA400;
    private const uint PublicKeyMagic = 0x31415352;

    private static readonly string[] PfxFiles = [".pfx", ".p12"];
    private static readonly string[] KeyFiles = [.. PfxFiles, ".pem"];
    private static readonly string[] CertificateFiles = [".pem", ".cer", ".crt"];

    private static readonly TableView CertificateTable = TableView.Record(
    [
        new TableColumn("THUMBPRINT", Camel(nameof(WebServiceCertificateFile.Thumbprint)), 40),
        new TableColumn("EXPIRES", Camel(nameof(WebServiceCertificateFile.NotAfter)), 20),
        new TableColumn("SANDBOX", Camel(nameof(WebServiceCertificateFile.Sandbox)), 12),
    ]);

    private static Command CreateCertificateCreate(TestAccountCommandDependencies dependencies, bool windows)
    {
        var seller = new XdtsSellerOption();
        var webServiceId = new Option<string>("--web-service-id")
        {
            HelpName = "id",
            Description = $"The ID of the web service the certificate is for. {WebServiceIdFormat}",
            Required = true,
        };
        webServiceId.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !Guid.TryParse(result.Tokens[0].Value, out _))
            {
                result.AddError($"Invalid --web-service-id '{result.Tokens[0].Value}'. {WebServiceIdFormat}");
            }
        });
        var destination = new Option<string>("--dest")
        {
            HelpName = "file",
            Description = "The new file to save the certificate and its private key to: .pfx or .p12, protected by a "
                + "password, or, on macOS and Linux, .pem, unencrypted. With --public-key-file: .pem, .cer, or .crt, "
                + "for the certificate alone. An existing file is never replaced, and its folders can't be symbolic "
                + "links or junctions.",
            Required = true,
        };
        var sandbox = new Option<string?>("--sandbox-id")
        {
            HelpName = "id",
            Description = "Limit the certificate to one sandbox, such as XDKS.1, RETAIL, CERT, or CERT.DEBUG. Default: "
                + "every sandbox. XBOX_SANDBOX_ID and the sandbox saved with 'context set' aren't used.",
        };
        var passwordFile = new Option<string?>("--password-file")
        {
            HelpName = "file",
            Description = "A file that holds the password for a .pfx or .p12 file, or - to read it from piped stdin. A "
                + "single trailing line break is removed. Default: ask at the terminal.",
        };
        var publicKeyFile = new Option<string?>("--public-key-file")
        {
            HelpName = "file",
            Description = "Use your own key instead of one the CLI makes: a file that holds its 2048-bit RSA public "
                + "key, in PEM or as the base64 key blob Partner Center's PowerShell script prints, or - to read it "
                + "from stdin. A private key is refused.",
        };
        var confirm = CreateConfirmOption("Create the certificate without asking first.");
        var command = new Command("create", "Create a certificate for a web service, and save it with its private key");
        seller.AddTo(command);
        command.Options.Add(webServiceId);
        command.Options.Add(destination);
        command.Options.Add(sandbox);
        command.Options.Add(passwordFile);
        command.Options.Add(publicKeyFile);
        command.Options.Add(confirm);
        command.Validators.Add(result =>
        {
            // Not GetValue, which throws for a missing or repeated value. The parser reports those.
            if (result.GetResult(destination)?.Tokens is not [{ Value: var file }])
            {
                return;
            }

            bool ownKey = result.GetResult(publicKeyFile) is not null;
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (!(ownKey ? CertificateFiles : windows ? PfxFiles : KeyFiles).Contains(extension))
            {
                result.AddError(ownKey
                    ? "With --public-key-file, --dest must be a .pem, .cer, or .crt file, for the certificate."
                    : windows
                        ? "On Windows, --dest must be a .pfx or .p12 file, protected by a password: a new file there "
                            + "gets its folder's permissions, so the CLI doesn't save a private key unencrypted."
                        : "--dest must be a .pfx or .p12 file, protected by a password, or a .pem file, unencrypted.");
            }
            else if (result.GetResult(passwordFile) is not null && (ownKey || extension == ".pem"))
            {
                result.AddError(ownKey
                    ? "--password-file can't be combined with --public-key-file: the file gets only the certificate."
                    : "--password-file needs a .pfx or .p12 --dest: a .pem file isn't encrypted.");
            }
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(
                result,
                seller,
                (client, sellerId) => CreateCertificateAsync(
                    result,
                    client,
                    new CertificateInput(
                        sellerId,
                        Guid.Parse(result.GetRequiredValue(webServiceId)),
                        Path.GetFullPath(result.GetRequiredValue(destination)),
                        result.GetValue(sandbox),
                        result.GetValue(passwordFile),
                        result.GetValue(publicKeyFile),
                        IsConfirmed(result, confirm)),
                    dependencies,
                    cancellationToken),
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: $"One web service of {Seller}.",
            Output: "A JSON object with webServiceId; sandbox, or null for every sandbox; thumbprint, the certificate's "
                + "SHA-1 thumbprint; notAfter, when it expires; and file, where it's saved. Nothing is written to "
                + "stdout, and no file is left, when the create is declined, refused, or its outcome is unknown.",
            SafetyDetails: "Makes a 2048-bit RSA key on this computer, unless you pass --public-key-file, and sends only "
                + "its public key. The certificate lets whoever has its private key call XBOX services as the web "
                + "service for about 18 months, in every sandbox unless --sandbox-id limits it. Neither the CLI nor "
                + "Partner Center can list or revoke it; if its key leaks, ask your developer account manager (DAM) to "
                + "have XBOX revoke it, which takes about 20 minutes, and tokens already issued with it keep working "
                + "for up to two weeks. Never replaces a file. Asks first at a terminal, and fails "
                + "without --confirm (--yes, -y) otherwise. The request is sent once and never retried, and each run "
                + "creates another certificate. Agent approval is still required.",
            Examples:
            [
                "xbox xbl web-service certificate create --web-service-id 11111111-2222-3333-4444-555555555555 --dest nod-arena-matchmaker.pfx",
                "xbox xbl web-service certificate create --web-service-id 11111111-2222-3333-4444-555555555555 --dest nod-arena-matchmaker-xdks.pfx --sandbox-id XDKS.1 --password-file password.txt",
                "xbox xbl web-service certificate create --web-service-id 11111111-2222-3333-4444-555555555555 --public-key-file matchmaker-public.pem --dest nod-arena-matchmaker.cer",
            ],
            LearnMore:
            [
                GuideLink,
                "If the web service's business partner is in the seller account's draft but not published, XBOX services "
                    + "refuse security tokens for the certificate with business_partner_required. Check with "
                    + "'xbox xbl config status --scope account', review the draft with "
                    + "'xbox xbl config diff --scope account --lines', then publish it with "
                    + "'xbox xbl config publish --scope account'.",
                "Exit codes: 0 created and saved; 1 not created; 3 the request was sent, but its outcome is unknown. "
                    + "After 3, run the command again to get a certificate: one created then can't be retrieved.",
            ],
            Table: CertificateTable));
    }

    /// <summary>What <c>certificate create</c> was asked for.</summary>
    private sealed record CertificateInput(
        long? SellerId,
        Guid WebServiceId,
        string Destination,
        string? Sandbox,
        string? PasswordFile,
        string? PublicKeyFile,
        bool Confirmed);

    /// <summary>
    /// Reads the caller's public key, or makes a key pair, asks first, and sends the public key once. The file is
    /// reserved before the request is sent, and removed unless the certificate is saved in it.
    /// </summary>
    private static async Task<int> CreateCertificateAsync(
        ParseResult result,
        XboxPartnerClient client,
        CertificateInput input,
        TestAccountCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        if (File.Exists(input.Destination) || Directory.Exists(input.Destination))
        {
            throw new InvalidOperationException(
                $"'{input.Destination}' already exists. Pass a new file: the CLI never replaces a key or certificate.");
        }

        // Fails before asking. SafeOutputDirectory.Create checks again just before the file is made.
        SafeOutputDirectory.ValidateExistingComponents(Path.GetDirectoryName(input.Destination)!);

        bool pfx = input.PublicKeyFile is null
            && Path.GetExtension(input.Destination).ToLowerInvariant() is ".pfx" or ".p12";
        string correlationId = Guid.NewGuid().ToString();
        var sent = false;
        var saved = false;
        FileStream? file = null;
        try
        {
            byte[]? ownKey = input.PublicKeyFile is null
                ? null
                : await ReadPublicKeyAsync(input.PublicKeyFile, dependencies, cancellationToken);
            if (pfx)
            {
                TestAccountSecretReader.EnsureSource(input.PasswordFile, "--password-file", "Password", dependencies);
            }

            if (!await PlaytestCommandFactory.ConfirmAsync(
                input.Confirmed,
                JobDependencies(dependencies),
                $"This creates a certificate for web service {input.WebServiceId:D}{OfSeller(input.SellerId)}, valid "
                    + (input.Sandbox is null
                        ? "in every sandbox, RETAIL included,"
                        : $"only in {XboxPartnerCommandSupport.ForTerminal(input.Sandbox)}")
                    + " for about 18 months. Whoever has its private key can call XBOX services as the web service "
                    + "until it expires. Neither the CLI nor Partner Center can revoke it: if the key leaks, ask your "
                    + "developer account manager (DAM) to have XBOX revoke it.",
                "Create the certificate?",
                "Canceled. No certificate was created.",
                cancellationToken))
            {
                return 1;
            }

            string? password = pfx
                ? await TestAccountSecretReader.ReadAsync(
                    input.PasswordFile, "--password-file", "Password", dependencies, cancellationToken,
                    prompt: "Password for the PFX file")
                : null;
            using RSA? key = ownKey is null ? RSA.Create(KeySize) : null;
            SafeOutputDirectory.Create(Path.GetDirectoryName(input.Destination)!);
            file = CreateNew(input.Destination);
            XboxLiveWebServiceCertificate issued;
            using (RequestProgress.Start($"Creating a certificate for web service {input.WebServiceId:D}"))
            {
                issued = await client.CreateWebServiceCertificateAsync(
                    input.SellerId, input.WebServiceId, ownKey ?? ToPublicKeyBlob(key!), input.Sandbox, correlationId,
                    cancellationToken, () => sent = true);
            }

            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(issued.Certificate);
            file.Write(key is null ? Pem(certificate.ExportCertificatePem())
                : pfx ? ToPfx(certificate, key, password!)
                : Pem(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem()));
            file.Flush(flushToDisk: true);
            // Closed before its path is written: on Windows, whatever reads stdout can't open the file while it's open.
            file.Dispose();
            saved = true;
            Write(result, JsonSerializer.Serialize(new WebServiceCertificateFile(
                issued.WebServiceId,
                issued.Sandbox,
                certificate.Thumbprint,
                certificate.NotAfter.ToUniversalTime(),
                input.Destination)));
            Console.Error.WriteLine(key is null
                ? $"Saved the certificate to '{input.Destination}'. Use it with your private key."
                : pfx
                    ? $"Saved the certificate and its private key to '{input.Destination}', protected by your password."
                    : $"Saved the certificate and its private key to '{input.Destination}', unencrypted. Keep the file "
                        + "secret.");
            return 0;
        }
        catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Canceled before the request was sent, so no certificate was created.");
            return 1;
        }
        // The certificate request is the command's only request, so a name resolution or TLS failure proves it never
        // left the machine.
        catch (HttpRequestException exception) when (XboxPartnerClient.NeverReachedService(exception))
        {
            Console.Error.WriteLine(
                $"Request failed: {TerminalText.SingleLine(exception.Message).TrimEnd('.')}. The request didn't reach "
                + "the service, so no certificate was created.");
            return 1;
        }
        catch (Exception exception) when (sent && !saved && XboxPartnerCommandSupport.IsUncertainSubmission(exception))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(
                exception, OutputOptions.From(result).IsCompact, Console.Error, cancellationToken, correlationId);
            Console.Error.WriteLine(
                "It is unknown whether a certificate was created. One created then can't be retrieved, so run the "
                + "command again to get one.");
            return XboxPartnerCommandSupport.OutcomeUnknownExitCode;
        }
        finally
        {
            file?.Dispose();
            if (file is not null && !saved)
            {
                File.Delete(input.Destination);
            }
        }
    }

    /// <summary>
    /// The caller's public key, as the <c>PUBLICKEYBLOB</c> that's sent: a 2048-bit RSA public key in PEM, or the
    /// base64 blob Partner Center's PowerShell script prints. Anything else, a private key included, is refused.
    /// </summary>
    private static async Task<byte[]> ReadPublicKeyAsync(
        string file,
        TestAccountCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        bool stdin = file == TestAccountSecretReader.StandardInput;
        string source = stdin ? "stdin" : $"'{Path.GetFullPath(file)}'";
        string text = stdin
            ? await dependencies.GetStandardInput().ReadToEndAsync(cancellationToken).WaitAsync(cancellationToken)
            : await File.ReadAllTextAsync(file, cancellationToken);
        if (text.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{source} holds a private key, so nothing was sent. Pass only the public key, such as the one "
                + "'openssl rsa -in key.pem -pubout' writes.");
        }

        int bits;
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            using RSA key = RSA.Create();
            try
            {
                key.ImportFromPem(text);
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            {
                throw NotAPublicKey(source);
            }

            if (key.KeySize == KeySize)
            {
                return ToPublicKeyBlob(key);
            }

            bits = key.KeySize;
        }
        else
        {
            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                throw NotAPublicKey(source);
            }

            if (blob.Length < 20 || blob[0] != PublicKeyBlobType
                || BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8)) != PublicKeyMagic)
            {
                throw NotAPublicKey(source);
            }

            bits = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(12));
            if (bits == KeySize)
            {
                return blob.Length == PublicKeyBlobLength ? blob : throw NotAPublicKey(source);
            }
        }

        throw new InvalidOperationException(
            $"The public key in {source} is {bits}-bit. XBOX services sign only 2048-bit RSA keys.");
    }

    private static InvalidOperationException NotAPublicKey(string source) =>
        new($"{source} doesn't hold an RSA public key, in PEM or as the base64 key blob Partner Center's PowerShell "
            + "script prints.");

    /// <summary>The key as the 276-byte <c>PUBLICKEYBLOB</c> XBOX services sign.</summary>
    private static byte[] ToPublicKeyBlob(RSA key)
    {
        RSAParameters parameters = key.ExportParameters(includePrivateParameters: false);
        byte[] blob = new byte[PublicKeyBlobLength];
        blob[0] = PublicKeyBlobType;
        blob[1] = BlobVersion;
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), KeyExchangeAlgorithm);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), PublicKeyMagic);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(12), KeySize);

        // RSAParameters hold big-endian numbers.
        parameters.Exponent!.CopyTo(blob, 16);
        Array.Reverse(blob, 16, parameters.Exponent.Length);
        parameters.Modulus!.CopyTo(blob, 20);
        Array.Reverse(blob, 20, parameters.Modulus.Length);
        return blob;
    }

    private static byte[] ToPfx(X509Certificate2 certificate, RSA key, string password)
    {
        using X509Certificate2 withKey = certificate.CopyWithPrivateKey(key);

        // 3DES and SHA-1, which Windows' Export-PfxCertificate writes: Windows Server 2016 and earlier, and Azure App
        // Service, can't import a PFX file encrypted with AES.
        return withKey.ExportPkcs12(Pkcs12ExportPbeParameters.Pkcs12TripleDesSha1, password);
    }

    private static byte[] Pem(params string[] blocks) => Encoding.ASCII.GetBytes(string.Join('\n', blocks) + "\n");

    /// <summary>Creates <paramref name="path"/>, which must not exist, readable only by you on macOS and Linux.</summary>
    private static FileStream CreateNew(string path)
    {
        // Unbuffered, so a write that fails, such as on a full disk, leaves nothing for Dispose to write again: it
        // would fail the same way and keep the file from being deleted.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }
}

/// <summary>What <c>xbl web-service certificate create</c> writes once the certificate is saved.</summary>
/// <param name="WebServiceId">The web service the certificate is for.</param>
/// <param name="Sandbox">The sandbox it's limited to, or null for every sandbox.</param>
/// <param name="Thumbprint">Its SHA-1 thumbprint.</param>
/// <param name="NotAfter">When it expires, in UTC.</param>
/// <param name="File">The file it's saved in.</param>
internal sealed record WebServiceCertificateFile(
    [property: JsonPropertyName("webServiceId")] Guid WebServiceId,
    [property: JsonPropertyName("sandbox")] string? Sandbox,
    [property: JsonPropertyName("thumbprint")] string Thumbprint,
    [property: JsonPropertyName("notAfter")] DateTime NotAfter,
    [property: JsonPropertyName("file")] string File);
