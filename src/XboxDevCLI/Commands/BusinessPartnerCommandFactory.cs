// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using static XboxDevCLI.Commands.XboxLiveAccountCommandSupport;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl business-partner</c>, which lists, gets, creates, and renames a seller account's business
/// partners: the identities that own its relying parties and web services.
/// </summary>
internal static class BusinessPartnerCommandFactory
{
    private const string Scope =
        "The business partners of the seller account selected by --seller-id, XBOX_SELLER_ID, or 'context set', "
        + "or of your only seller.";

    private const string RecordOutput =
        "businessPartnerId; name, which is often empty for the default one; type, which decides the claims its "
        + "relying parties can carry; and isDefault. They come from the account's draft, so they include changes "
        + "not yet published.";

    private const string ReadPermission =
        "Requires Partner Center's Relying parties Read or Business partners Read permission.";

    private const string WritePermission =
        "Requires Partner Center's Relying parties Write or Business partners Write permission.";

    private const string PublishNote =
        "Saves to the account's draft. 'xbox xbl config publish --scope account' makes it live.";

    // Least privileged first, the order Partner Center offers them in.
    private static readonly string[] Types = ["Partner", "LegacyPartner", "LegacyAnonymized", "Legacy", "Internal"];

    private static readonly TableColumn[] Columns =
    [
        new("ID", Camel(nameof(XboxLiveBusinessPartner.BusinessPartnerId)), 36),
        new("NAME", Camel(nameof(XboxLiveBusinessPartner.Name)), 60),
        new("TYPE", Camel(nameof(XboxLiveBusinessPartner.Type)), 16),
        new("DEFAULT", Camel(nameof(XboxLiveBusinessPartner.IsDefault)), 7),
    ];

    // `list` writes a bare array; the collection name only names it in errors.
    private static readonly TableView ListTable = new("businessPartners", Columns);

    private static readonly TableView RecordTable = TableView.Record(Columns);

    public static Command Create(XboxPartnerJobDependencies? dependencies = null)
    {
        var businessPartner = new Command(
            "business-partner",
            "List, get, create, and save business partners, which own a seller account's relying parties and web services");
        businessPartner.Subcommands.Add(CreateList());
        businessPartner.Subcommands.Add(CreateGet());
        businessPartner.Subcommands.Add(CreateCreate(dependencies ?? XboxPartnerJobDependencies.Default));
        businessPartner.Subcommands.Add(CreateSave());
        return businessPartner;
    }

    private static Command CreateList()
    {
        var seller = new XdtsSellerOption();
        var command = new Command("list", "List a seller account's business partners");
        seller.AddTo(command);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, async (client, sellerId) =>
            {
                string businessPartners;
                using (RequestProgress.Start("Reading business partners"))
                {
                    businessPartners = await client.ListBusinessPartnersAsync(sellerId, cancellationToken);
                }

                Write(result, businessPartners);
                return 0;
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: Scope,
            Output: "A JSON array of the business partners, the default one first, which is empty until the "
                + "account has a relying party. Each has " + RecordOutput,
            Examples:
            [
                "xbox xbl business-partner list",
                "xbox xbl business-partner list --seller-id 1234567 --output table",
            ],
            LearnMore: [ReadPermission],
            Table: ListTable));
    }

    private static Command CreateGet()
    {
        var seller = new XdtsSellerOption();
        var id = CreateIdArgument();
        var command = new Command("get", "Get one business partner");
        command.Arguments.Add(id);
        seller.AddTo(command);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, async (client, sellerId) =>
            {
                XboxPartnerOperationResult<XboxLiveBusinessPartner> businessPartner;
                using (RequestProgress.Start("Reading the business partner"))
                {
                    businessPartner = await client.GetBusinessPartnerAsync(
                        sellerId, Guid.Parse(result.GetRequiredValue(id)), cancellationToken);
                }

                Write(result, businessPartner.Body);
                return 0;
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: Scope,
            Output: "The business partner as a JSON object with " + RecordOutput,
            Examples:
            [
                "xbox xbl business-partner get 11111111-2222-3333-4444-555555555555",
                "xbox xbl business-partner get 11111111-2222-3333-4444-555555555555 --seller-id 1234567 --output table",
            ],
            LearnMore: [ReadPermission],
            Table: RecordTable));
    }

    private static Command CreateCreate(XboxPartnerJobDependencies dependencies)
    {
        var seller = new XdtsSellerOption();
        var name = CreateNameOption("The new business partner's name.");
        var type = new Option<string?>("--type")
        {
            HelpName = "type",
            Description = "Its type, which decides the claims its relying parties can carry: one the account's "
                + "business partners already have. Default: the least privileged of those, in the order Partner, "
                + "LegacyPartner, LegacyAnonymized, Legacy, Internal. Can't be changed later.",
        };
        type.AcceptOnlyFromAmong(Types);
        var confirm = CreateConfirmOption("Confirm creating the business partner without prompting.");
        var command = new Command("create", "Create a business partner in the account's draft");
        seller.AddTo(command);
        command.Options.Add(name);
        command.Options.Add(type);
        command.Options.Add(confirm);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, (client, sellerId) =>
            {
                string businessPartner = result.GetRequiredValue(name).Trim();
                string shown = XboxPartnerCommandSupport.ForTerminal(businessPartner);
                string? partnerType = result.GetValue(type);
                var request = new XboxLiveCreateBusinessPartnerRequest(sellerId, businessPartner, partnerType);
                return ChangeAsync(
                    result,
                    new Change(
                        $"Creating business partner '{shown}'",
                        "no business partner was created",
                        $"whether business partner '{shown}' was created",
                        $"Check with: {CheckCommand(result, sellerId, "xbl", "business-partner", "list")}. "
                            + "Don't run the create again until you've checked: another create adds a second business "
                            + "partner."),
                    (correlationId, onSending) =>
                        client.CreateBusinessPartnerAsync(request, correlationId, cancellationToken, onSending),
                    _ => Saved(result, sellerId),
                    cancellationToken,
                    () => PlaytestCommandFactory.ConfirmAsync(
                        IsConfirmed(result, confirm),
                        dependencies,
                        $"Creating business partner '{shown}'"
                            + (partnerType is null
                                ? $"{ForSeller(sellerId)}. It gets the least privileged type the account's business "
                                    + "partners have."
                                : $" of type {partnerType}{ForSeller(sellerId)}.")
                            + " A business partner can't be deleted, and its type can't be changed. It goes live when "
                            + "you publish the account's configuration.",
                        "Create this business partner?",
                        "Canceled. No business partner was created.",
                        cancellationToken),
                    exception => TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code")
                        is "no_business_partners"
                        ? "Run 'xbox xbl relying-party create' to create the account's first relying party."
                        : null);
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: Scope,
            Output: "The created business partner, as 'xbox xbl business-partner get' writes it. Nothing is written "
                + "to stdout when the create fails or its outcome is unknown.",
            SafetyDetails: "Adds a business partner, with a new ID and anonymizing salt, to the account's draft. "
                + "Nobody can delete it or change its type. Requires --confirm (--yes, -y) or terminal confirmation. "
                + "The request is sent once and never retried. Agent approval is still required.",
            Examples:
            [
                "xbox xbl business-partner create --name \"Nod Arena services\" --seller-id 1234567",
            ],
            LearnMore:
            [
                WritePermission,
                PublishNote,
                "An account gets its first, default business partner with its first relying party, from 'xbox xbl "
                    + "relying-party create'. Until then, create fails (HTTP 409).",
                "Exit codes: 0 created; 1 not created; 3 the request was sent, but its outcome is unknown. After 3, "
                    + "check with the 'xbox xbl business-partner list' command that stderr shows before you create it "
                    + "again.",
            ],
            Table: RecordTable));
    }

    private static Command CreateSave()
    {
        var seller = new XdtsSellerOption();
        var id = CreateIdArgument();
        var name = CreateNameOption("Its new name, the only setting a business partner can change.");
        var command = new Command("save", "Rename a business partner in the account's draft");
        command.Arguments.Add(id);
        seller.AddTo(command);
        command.Options.Add(name);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, (client, sellerId) =>
            {
                Guid businessPartner = Guid.Parse(result.GetRequiredValue(id));
                var request = new XboxLiveUpdateBusinessPartnerRequest(
                    sellerId, businessPartner, result.GetRequiredValue(name).Trim());
                return ChangeAsync(
                    result,
                    new Change(
                        $"Saving business partner {businessPartner:D}",
                        $"business partner {businessPartner:D} wasn't renamed",
                        $"whether business partner {businessPartner:D} was renamed",
                        "Check with: "
                            + CheckCommand(result, sellerId, "xbl", "business-partner", "get", businessPartner.ToString("D"))
                            + ". Running the same save again is safe."),
                    (correlationId, onSending) =>
                        client.UpdateBusinessPartnerAsync(request, correlationId, cancellationToken, onSending),
                    _ => Saved(result, sellerId),
                    cancellationToken);
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: Scope,
            Output: "The business partner, as 'xbox xbl business-partner get' writes it. Nothing is written to stdout "
                + "when the save fails or its outcome is unknown.",
            SafetyDetails: "Renames the business partner in the account's draft, and keeps everything else. Nothing "
                + "goes live until the account's configuration is published. The request is sent once and never "
                + "retried. Agent approval is still required.",
            Examples:
            [
                "xbox xbl business-partner save 11111111-2222-3333-4444-555555555555 --name \"Nod Arena services\" --seller-id 1234567",
            ],
            LearnMore:
            [
                WritePermission,
                PublishNote,
                "Exit codes: 0 saved; 1 not saved; 3 the request was sent, but its outcome is unknown. After 3, check "
                    + "with the 'xbox xbl business-partner get' command that stderr shows.",
            ],
            Table: RecordTable));
    }

    private static Argument<string> CreateIdArgument()
    {
        var id = new Argument<string>("business-partner-id")
        {
            Description = "The business partner's ID, as 'xbox xbl business-partner list' shows it, such as "
                + "11111111-2222-3333-4444-555555555555.",
        };
        id.Validators.Add(argument =>
        {
            if (argument.Tokens.Count > 0 && !Guid.TryParse(argument.Tokens[0].Value, out _))
            {
                argument.AddError($"Invalid business partner ID '{argument.Tokens[0].Value}': it must be a GUID, "
                    + "such as 11111111-2222-3333-4444-555555555555.");
            }
        });
        return id;
    }

    private static Option<string> CreateNameOption(string description)
    {
        var option = new Option<string>("--name") { HelpName = "text", Description = description, Required = true };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("Invalid --name: it can't be empty.");
            }
        });
        return option;
    }
}
