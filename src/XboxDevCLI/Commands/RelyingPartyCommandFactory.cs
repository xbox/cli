// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using static XboxDevCLI.Commands.XboxLiveAccountCommandSupport;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl relying-party</c>, which lists, gets, creates, and saves a seller account's relying parties:
/// the audiences of the tokens XBOX services issue a game for its web services.
/// </summary>
internal static class RelyingPartyCommandFactory
{
    private const string Scope =
        "The relying parties of the seller account selected by --seller-id, XBOX_SELLER_ID, or 'context set', "
        + "or of your only seller.";

    private const string RecordOutput =
        "name, its token audience; fileName, its document in the account's configuration; businessPartnerId, the "
        + "business partner that owns it; lifetimeInHours; encryptionType, which is null until its settings are "
        + "saved; jkuEnabled; claims, the claim type URIs its tokens carry; and encryptionCertificate, the thumbprint, "
        + "subject, notBefore, and notAfter of the certificate its tokens are encrypted to. Settings come from the "
        + "account's draft, so they include changes not yet published.";

    private const string GuideLink =
        "How a web service gets XBOX tokens: "
        + "https://docs.xbox.com/services/xbox-services/fundamentals/s2s-auth-calls/custom-service-config/web-services/live-web-services";

    private const string ReadPermission = "Requires Partner Center's Relying parties Read permission.";

    private const string WritePermission = "Requires Partner Center's Relying parties Write permission.";

    private const string PublishNote =
        "Saves to the account's draft. 'xbox xbl config publish --scope account' makes it live.";

    // The claims Partner Center names User Pairwise ID (upi) and Partner xbox user ID (ptx).
    private const string UserPairwiseIdClaim = "http://schemas.microsoft.com/xbox/2013/03/claims/user/pwid";

    private const string PartnerXuidClaim = "http://schemas.microsoft.com/xbox/2011/07/claims/user/pxuid";

    private static readonly TableColumn[] Columns =
    [
        new("NAME", Camel(nameof(XboxLiveRelyingParty.Name)), 60),
        new("BUSINESS PARTNER ID", Camel(nameof(XboxLiveRelyingParty.BusinessPartnerId)), 36),
        new("LIFETIME HOURS", Camel(nameof(XboxLiveRelyingParty.LifetimeInHours)), 14),
        new(
            "CERTIFICATE EXPIRES",
            $"{Camel(nameof(XboxLiveRelyingParty.EncryptionCertificate))}.{Camel(nameof(XboxLiveRelyingPartyCertificate.NotAfter))}",
            19,
            Transform: ExpiryDate),
    ];

    // `list` writes a bare array; the collection name only names it in errors.
    private static readonly TableView ListTable = new("relyingParties", Columns);

    private static readonly TableView RecordTable = TableView.Record(Columns);

    public static Command Create(XboxPartnerJobDependencies? dependencies = null)
    {
        var relyingParty = new Command(
            "relying-party",
            "List, get, create, and save relying parties, which set the tokens XBOX services issue for your web services");
        relyingParty.Subcommands.Add(CreateList());
        relyingParty.Subcommands.Add(CreateGet());
        relyingParty.Subcommands.Add(CreateCreate(dependencies ?? XboxPartnerJobDependencies.Default));
        relyingParty.Subcommands.Add(CreateSave());
        return relyingParty;
    }

    private static Command CreateList()
    {
        var seller = new XdtsSellerOption();
        var command = new Command("list", "List a seller account's relying parties");
        seller.AddTo(command);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, async (client, sellerId) =>
            {
                string relyingParties;
                using (RequestProgress.Start("Reading relying parties"))
                {
                    relyingParties = await client.ListRelyingPartiesAsync(sellerId, cancellationToken);
                }

                Write(result, relyingParties);
                return 0;
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: Scope,
            Output: "A JSON array of the relying parties, by name. Each has " + RecordOutput,
            Examples:
            [
                "xbox xbl relying-party list",
                "xbox xbl relying-party list --seller-id 1234567 --output table",
            ],
            LearnMore: [ReadPermission, GuideLink],
            Table: ListTable));
    }

    private static Command CreateGet()
    {
        var seller = new XdtsSellerOption();
        var name = CreateNameArgument(
            "The relying party's name, such as rp://api.nodpublishers.com/. rp:// and the trailing / are optional.");
        var command = new Command("get", "Get one relying party");
        command.Arguments.Add(name);
        seller.AddTo(command);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, async (client, sellerId) =>
            {
                XboxPartnerOperationResult<XboxLiveRelyingParty> relyingParty;
                using (RequestProgress.Start("Reading the relying party"))
                {
                    relyingParty = await client.GetRelyingPartyAsync(
                        sellerId, result.GetRequiredValue(name).Trim(), cancellationToken);
                }

                Write(result, relyingParty.Body);
                return 0;
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: Scope,
            Output: "The relying party as a JSON object with " + RecordOutput,
            Examples:
            [
                "xbox xbl relying-party get rp://api.nodpublishers.com/",
                "xbox xbl relying-party get api.nodpublishers.com --seller-id 1234567 --output table",
            ],
            LearnMore: [ReadPermission, GuideLink],
            Table: RecordTable));
    }

    private static Command CreateCreate(XboxPartnerJobDependencies dependencies)
    {
        var seller = new XdtsSellerOption();
        var name = CreateNameArgument(
            "The new relying party's name, the audience of its tokens: a host name such as api.nodpublishers.com, "
            + "which becomes rp://api.nodpublishers.com/. It doesn't have to be your service's address. Names are "
            + "unique across XBOX services, and can't be changed or deleted.");
        var certificateFile = CreateCertificateFileOption(
            "The public certificate (.cer), DER or PEM, that its tokens are encrypted to. Your service decrypts them "
            + "with the private key, which stays with your service: a file holding a private key is refused.");
        certificateFile.Required = true;
        var businessPartner = new Option<string?>("--business-partner-id")
        {
            HelpName = "id",
            Description = "The business partner that owns it, from 'xbox xbl business-partner list'. Default: the "
                + "account's default business partner. Can't be changed later.",
        };
        businessPartner.Validators.Add(option =>
        {
            if (option.Tokens.Count > 0 && !Guid.TryParse(option.Tokens[0].Value, out _))
            {
                option.AddError($"Invalid --business-partner-id '{option.Tokens[0].Value}': it must be a GUID, "
                    + "such as 11111111-2222-3333-4444-555555555555.");
            }
        });
        var lifetime = CreateLifetimeOption("Default: 16, as in Partner Center.");
        var jku = CreateJkuOption("Default: true, as in Partner Center.");
        var claims = CreateClaimOption(
            "Without it, its tokens carry the claims Partner Center selects for its business partner's type, and with "
            + "it, only the ones you pass.");
        var confirm = CreateConfirmOption("Confirm creating the relying party without prompting.");
        var command = new Command("create", "Create a relying party, and save its settings to the account's draft");
        command.Arguments.Add(name);
        seller.AddTo(command);
        command.Options.Add(certificateFile);
        command.Options.Add(businessPartner);
        command.Options.Add(lifetime);
        command.Options.Add(jku);
        command.Options.Add(claims);
        command.Options.Add(confirm);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, (client, sellerId) =>
            {
                RelyingPartyCertificate certificate = ReadCertificate(result.GetRequiredValue(certificateFile)!);
                string relyingParty = CompleteName(result.GetRequiredValue(name));
                string shown = XboxPartnerCommandSupport.ForTerminal(relyingParty);
                Guid? businessPartnerId = result.GetValue(businessPartner) is { } id ? Guid.Parse(id) : null;
                var request = new XboxLiveCreateRelyingPartyRequest(
                    sellerId,
                    relyingParty,
                    certificate.Base64,
                    businessPartnerId,
                    result.GetValue(lifetime),
                    result.GetValue(jku),
                    ReadClaims(result, claims));
                return ChangeAsync(
                    result,
                    new Change(
                        $"Creating relying party {shown}",
                        "no relying party was created",
                        $"whether relying party {shown} was created",
                        $"Check with: {CheckCommand(result, sellerId, relyingParty)}. Running the same create again "
                            + "is safe: it saves nothing over a relying party whose settings are saved."),
                    (correlationId, onSending) =>
                        client.CreateRelyingPartyAsync(request, correlationId, cancellationToken, onSending),
                    created => Done(result, sellerId, created),
                    cancellationToken,
                    () => PlaytestCommandFactory.ConfirmAsync(
                        IsConfirmed(result, confirm),
                        dependencies,
                        $"Creating relying party {shown}{ForSeller(sellerId)}, owned by "
                            + (businessPartnerId is { } partner
                                ? $"business partner {partner:D}"
                                : "the account's default business partner")
                            + $", with tokens encrypted to {Describe(certificate)}. Its name is unique across XBOX "
                            + "services: nobody can rename, delete, or reuse it, and its business partner can't be "
                            + "changed. Its settings go live when you publish the account's configuration.",
                        "Create this relying party?",
                        "Canceled. No relying party was created.",
                        cancellationToken),
                    exception => TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code") switch
                    {
                        "relying_party_exists" =>
                            "To change its settings, run 'xbox xbl relying-party save'. If its settings were never "
                            + "saved, create it again with that business partner's ID as --business-partner-id.",
                        "relying_party_settings_not_saved" =>
                            "To save them, run 'xbox xbl relying-party create' again with the same name and business "
                            + "partner. If the error lists validation messages, change the settings they name first.",
                        _ => null,
                    });
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: Scope,
            Output: "The created relying party, as 'xbox xbl relying-party get' writes it. Nothing is written to "
                + "stdout when the create fails or its outcome is unknown.",
            SafetyDetails: "Registers the name across XBOX services at once: nobody can rename, delete, or reuse it, "
                + "and its business partner can't be changed. Its settings, with public-key encryption to the "
                + "certificate, go to the account's draft. Requires --confirm (--yes, -y) or terminal confirmation. "
                + "The request is sent once and never retried. Agent approval is still required.",
            Examples:
            [
                "xbox xbl relying-party create api.nodpublishers.com --certificate-file rp.cer --seller-id 1234567",
                "xbox xbl relying-party create api.nodpublishers.com --certificate-file rp.cer --business-partner-id 11111111-2222-3333-4444-555555555555 --lifetime-hours 4 --seller-id 1234567",
            ],
            LearnMore:
            [
                WritePermission,
                PublishNote,
                "The claims every relying party carries, such as the user's age group, are always added. XBOX services "
                    + "refuse a claim the business partner's type doesn't allow.",
                "Exit codes: 0 created; 1 not created, or, with relying_party_settings_not_saved, its name was "
                    + "registered without its settings; 3 the request was sent, but its outcome is unknown. After 3, "
                    + "check with the 'xbox xbl relying-party get' command that stderr shows.",
                GuideLink,
            ],
            Table: RecordTable));
    }

    private static Command CreateSave()
    {
        var seller = new XdtsSellerOption();
        var name = CreateNameArgument(
            "The relying party's name, such as rp://api.nodpublishers.com/. rp:// and the trailing / are optional.");
        var certificateFile = CreateCertificateFileOption(
            "A new public certificate (.cer), DER or PEM, to encrypt its tokens to. A file holding a private key is "
            + "refused. Without it, the certificate is kept.");
        var lifetime = CreateLifetimeOption("Without it, the lifetime is kept.");
        var jku = CreateJkuOption("Without --jku-enabled, the setting is kept.");
        var claims = CreateClaimOption(
            "The claims you pass replace all of its claims: pass every claim it should carry, as 'xbox xbl "
            + "relying-party get' lists them, because a claim you leave out is removed. Without it, the claims are "
            + "kept.");
        var command = new Command("save", "Save changes to a relying party's settings in the account's draft");
        command.Arguments.Add(name);
        seller.AddTo(command);
        command.Options.Add(certificateFile);
        command.Options.Add(lifetime);
        command.Options.Add(jku);
        command.Options.Add(claims);
        command.Validators.Add(commandResult =>
        {
            if (new Option[] { certificateFile, lifetime, jku, claims }.All(option => commandResult.GetResult(option) is null))
            {
                commandResult.AddError(
                    "Pass --certificate-file, --lifetime-hours, --jku-enabled, or --claim: there's nothing to change.");
            }
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, (client, sellerId) =>
            {
                RelyingPartyCertificate? certificate = result.GetValue(certificateFile) is { } path
                    ? ReadCertificate(path)
                    : null;
                string relyingParty = result.GetRequiredValue(name).Trim();
                string shown = XboxPartnerCommandSupport.ForTerminal(relyingParty);
                var request = new XboxLiveUpdateRelyingPartyRequest(
                    sellerId,
                    relyingParty,
                    certificate?.Base64,
                    result.GetValue(lifetime),
                    result.GetValue(jku),
                    ReadClaims(result, claims));
                return ChangeAsync(
                    result,
                    new Change(
                        $"Saving relying party {shown}",
                        $"relying party {shown} wasn't changed",
                        $"whether relying party {shown} was changed",
                        $"Check with: {CheckCommand(result, sellerId, relyingParty)}. Running the same save again is "
                            + "safe."),
                    (correlationId, onSending) =>
                        client.UpdateRelyingPartyAsync(request, correlationId, cancellationToken, onSending),
                    saved => Done(result, sellerId, saved),
                    cancellationToken);
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: Scope,
            Output: "The relying party, as 'xbox xbl relying-party get' writes it. Nothing is written to stdout when "
                + "the save fails or its outcome is unknown.",
            SafetyDetails: "Changes only the settings you pass, in the account's draft; --claim replaces all of its "
                + "claims. Nothing goes live until the account's configuration is published. The request is sent once "
                + "and never retried. Agent approval is still required.",
            Examples:
            [
                "xbox xbl relying-party save rp://api.nodpublishers.com/ --lifetime-hours 4 --seller-id 1234567",
                "xbox xbl relying-party save rp://api.nodpublishers.com/ --certificate-file rp-2028.cer --seller-id 1234567",
            ],
            LearnMore:
            [
                WritePermission,
                PublishNote,
                "Fails (HTTP 409) when the configuration fails XBOX services validation, such as a claim the business "
                    + "partner's type doesn't allow; the error lists each message under validation. Also fails "
                    + "(HTTP 409) when the relying party changed while the save ran; run it again.",
                "Exit codes: 0 saved; 1 not saved; 3 the request was sent, but its outcome is unknown. After 3, check "
                    + "with the 'xbox xbl relying-party get' command that stderr shows.",
            ],
            Table: RecordTable));
    }

    private static string CheckCommand(ParseResult result, long? sellerId, string relyingParty) =>
        XboxLiveAccountCommandSupport.CheckCommand(
            result, sellerId, "xbl", "relying-party", "get", ShellQuoting.Quote(relyingParty));

    /// <summary>
    /// What stderr says after a create or save: what Partner Center's relying party page warns of in the saved
    /// settings, then how to publish them.
    /// </summary>
    private static string Done(ParseResult result, long? sellerId, XboxLiveRelyingParty relyingParty) =>
        string.Join(Environment.NewLine, [.. Warnings(relyingParty), Saved(result, sellerId)]);

    /// <summary>
    /// What Partner Center's relying party page warns of in saved settings: <c>jku</c> off, which it shows for every
    /// encryption type but a shared key, and the upi claim.
    /// </summary>
    private static IEnumerable<string> Warnings(XboxLiveRelyingParty relyingParty)
    {
        if (relyingParty is { JkuEnabled: false, EncryptionType: not "SharedKeyEncryption" })
        {
            yield return "Warning: JKU is strongly recommended. Without it, your service must update the token signing "
                + "certificate by hand. To turn it on, run 'xbox xbl relying-party save' with --jku-enabled true.";
        }

        if (relyingParty.Claims?.Contains(UserPairwiseIdClaim) == true)
        {
            yield return $"Warning: the User Pairwise ID (upi) claim, {UserPairwiseIdClaim}, isn't recommended. Use the "
                + $"Partner XBOX user ID (ptx) claim, {PartnerXuidClaim}, instead.";
        }
    }

    /// <summary>The date, in UTC, of a date and time such as <c>2028-11-18T16:31:40+00:00</c>.</summary>
    private static string ExpiryDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ValueKind == JsonValueKind.String ? value.GetString()! : "-";

    /// <summary>
    /// Completes a new relying party's name as Partner Center does: <c>rp://</c> and the trailing <c>/</c> when
    /// they're missing.
    /// </summary>
    private static string CompleteName(string name)
    {
        string trimmed = name.Trim();
        string completed = trimmed.StartsWith("rp://", StringComparison.Ordinal) ? trimmed : "rp://" + trimmed;
        return completed.EndsWith('/') ? completed : completed + "/";
    }

    /// <summary>
    /// A relying party's encryption certificate: its DER bytes in base64, which are sent, and what the prompt shows.
    /// </summary>
    private sealed record RelyingPartyCertificate(string Base64, string Subject, string Thumbprint, DateTime NotAfter);

    /// <summary>
    /// Reads one public X.509 certificate, DER or PEM. A PKCS#12 file, or anything else holding a private key, is
    /// refused before anything is sent: the private key stays with the developer's service.
    /// </summary>
    private static RelyingPartyCertificate ReadCertificate(string path)
    {
        string shown = TerminalText.SingleLine(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw new IOException(
                $"The certificate file '{shown}' could not be read: {TerminalText.SingleLine(exception.Message)}",
                exception);
        }

        // The text check comes first, because on macOS the content type check fails on a file that isn't a certificate.
        bool holdsPrivateKey = Encoding.ASCII.GetString(bytes).Contains("PRIVATE KEY", StringComparison.Ordinal);
        try
        {
            if (holdsPrivateKey || X509Certificate2.GetCertContentType(bytes) == X509ContentType.Pkcs12)
            {
                throw new InvalidOperationException(
                    $"The certificate file '{shown}' holds a private key, so nothing was sent. Pass only the public "
                    + "certificate (.cer); the private key stays with your service.");
            }

            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(bytes);
            return new RelyingPartyCertificate(
                Convert.ToBase64String(certificate.RawData), certificate.Subject, certificate.Thumbprint, certificate.NotAfter);
        }
        // GetCertContentType throws ArgumentException for an empty file.
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new InvalidDataException(
                $"The certificate file '{shown}' isn't an X.509 certificate (.cer) in DER or PEM.", exception);
        }
    }

    private static string Describe(RelyingPartyCertificate certificate) =>
        $"{XboxPartnerCommandSupport.ForTerminal(certificate.Subject)} (thumbprint {certificate.Thumbprint}, expires "
        + certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";

    private static Argument<string> CreateNameArgument(string description)
    {
        var name = new Argument<string>("name") { Description = description };
        name.Validators.Add(argument =>
        {
            if (argument.Tokens.Count > 0 && string.IsNullOrWhiteSpace(argument.Tokens[0].Value))
            {
                argument.AddError("Invalid relying party name: it can't be empty.");
            }
        });
        return name;
    }

    private static Option<string?> CreateCertificateFileOption(string description) =>
        new("--certificate-file") { HelpName = "path", Description = description };

    private static Option<int?> CreateLifetimeOption(string defaultDescription) =>
        new("--lifetime-hours")
        {
            HelpName = "hours",
            Description = $"How long its tokens last, from 1 to 720 hours. {defaultDescription}",
        };

    private static Option<bool?> CreateJkuOption(string defaultDescription) =>
        new("--jku-enabled")
        {
            HelpName = "true|false",
            Description = "Whether its tokens carry a jku header, the address of the keys XBOX services sign them "
                + "with, so your service follows key changes. Partner Center strongly recommends it; otherwise your "
                + $"service must update the token signing certificate by hand. {defaultDescription}",
        };

    private static Option<string[]> CreateClaimOption(string description)
    {
        var claim = new Option<string[]>("--claim")
        {
            HelpName = "uri",
            Description = "A claim type URI its tokens carry, such as "
                + "http://schemas.microsoft.com/xbox/2011/07/claims/user/gamertag. Repeat for more. " + description,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        // Arity counts the values of every --claim together, so one without a value would drop out of the list.
        claim.Validators.Add(result =>
        {
            if (result.Tokens.Count != result.IdentifierTokenCount)
            {
                result.AddError("Each --claim requires a claim type URI.");
            }
        });
        return claim;
    }

    // Null when --claim isn't passed, so the service keeps the claims, or uses its defaults on create.
    private static string[]? ReadClaims(ParseResult result, Option<string[]> claims) =>
        result.GetResult(claims) is null ? null : result.GetValue(claims);
}
