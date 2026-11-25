// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using static XboxDevCLI.Commands.XboxLiveAccountCommandSupport;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl web-service</c>, which lists, gets, creates, updates, and deletes the seller's web
/// services, the identities a game's own services use to call XBOX services, and creates their certificates.
/// </summary>
internal static partial class XboxLiveWebServiceCommandFactory
{
    private const string Seller =
        "the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set', or your only seller";

    private const string GuideLink =
        "Web services and their certificates in Partner Center: https://docs.xbox.com/services/xbox-services/"
        + "fundamentals/s2s-auth-calls/custom-service-config/web-services/live-web-services";

    private const string WebServiceIdFormat = "A GUID, such as 11111111-2222-3333-4444-555555555555.";

    private static readonly TableColumn[] Columns =
    [
        new("WEB SERVICE ID", Camel(nameof(XboxLiveWebService.WebServiceId)), 36),
        new("NAME", Camel(nameof(XboxLiveWebService.Name)), 40),
        new("BUSINESS PARTNER", Camel(nameof(XboxLiveWebService.BusinessPartnerId)), 36),
        new("TELEMETRY", Camel(nameof(XboxLiveWebService.TelemetryAccess)), 9),
        new("APP CHANNELS", Camel(nameof(XboxLiveWebService.AppChannelsAccess)), 12),
    ];

    // `list` writes a bare array; the collection name only names it in errors.
    private static readonly TableView ListTable = new("webServices", Columns);

    private static readonly TableView WebServiceTable = TableView.Record(Columns);

    public static Command Create(TestAccountCommandDependencies? dependencies = null, bool? windows = null)
    {
        dependencies ??= TestAccountCommandDependencies.Default;
        var webService = new Command(
            "web-service",
            "List, get, create, update, and delete the web services your own services use to call XBOX services, "
                + "and create their certificates");
        webService.Subcommands.Add(CreateList());
        webService.Subcommands.Add(CreateGet());
        webService.Subcommands.Add(CreateCreate());
        webService.Subcommands.Add(CreateUpdate());
        webService.Subcommands.Add(CreateDelete(dependencies));
        var certificate = new Command("certificate", "Create certificates that let your own service sign in as a web service");
        certificate.Subcommands.Add(CreateCertificateCreate(dependencies, windows ?? OperatingSystem.IsWindows()));
        webService.Subcommands.Add(certificate);
        return webService;
    }

    private static Command CreateList()
    {
        var seller = new XdtsSellerOption();
        var command = new Command("list", "List the seller's web services");
        seller.AddTo(command);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, async (client, sellerId) =>
            {
                string services;
                using (var progress = ListProgress.Start("Reading web services"))
                {
                    services = await client.ListWebServicesAsync(sellerId, cancellationToken, progress.PageRead);
                }

                Write(result, services);
                return 0;
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: $"The web services of {Seller}.",
            Output: "A JSON array of web services, ordered by ID, or [] when there are none. Each has webServiceId; "
                + "name; businessPartnerId, the business partner it acts for, which is the seller account's own ID for "
                + "its default business partner; telemetryAccess; and appChannelsAccess. "
                + ListProgress.HelpOutput,
            Examples:
            [
                "xbox xbl web-service list",
                "xbox xbl web-service list --seller-id 1234567 --output table",
            ],
            LearnMore: [GuideLink],
            Table: ListTable));
    }

    private static Command CreateGet()
    {
        var seller = new XdtsSellerOption();
        var id = CreateWebServiceIdArgument("The web service's ID.");
        var command = new Command("get", "Get one web service") { id };
        seller.AddTo(command);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, async (client, sellerId) =>
            {
                XboxPartnerOperationResult<XboxLiveWebService> service;
                using (RequestProgress.Start("Reading the web service"))
                {
                    service = await client.GetWebServiceAsync(
                        sellerId, Guid.Parse(result.GetRequiredValue(id)), cancellationToken);
                }

                Write(result, service.Body);
                return 0;
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: $"One web service of {Seller}.",
            Output: "The web service as a JSON object, with the fields 'xbox xbl web-service list' writes for each.",
            Examples:
            [
                "xbox xbl web-service get 11111111-2222-3333-4444-555555555555",
                "xbox xbl web-service get 11111111-2222-3333-4444-555555555555 --output table",
            ],
            LearnMore: [GuideLink],
            Table: WebServiceTable));
    }

    private static Command CreateCreate()
    {
        var seller = new XdtsSellerOption();
        var name = new Option<string>("--name")
        {
            HelpName = "name",
            Description = "The web service's name, 1 to 100 characters, such as the host your service runs on. It "
                + "can't contain <, >, &, ', or \".",
            Required = true,
        };
        var telemetry = new Option<bool>("--telemetry-access")
        {
            Description = "Let the web service read telemetry for every game of the seller.",
        };
        var appChannels = new Option<bool>("--app-channels-access")
        {
            Description = "Let the web service publish app channels to consoles, as a media provider.",
        };
        var command = new Command("create", "Create a web service");
        seller.AddTo(command);
        command.Options.Add(name);
        command.Options.Add(telemetry);
        command.Options.Add(appChannels);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, (client, sellerId) =>
            {
                string serviceName = result.GetRequiredValue(name);
                string shown = XboxPartnerCommandSupport.ForTerminal(serviceName);
                return ChangeAsync(
                    result,
                    new Change(
                        $"Creating web service '{shown}'",
                        "no web service was created",
                        $"whether web service '{shown}' was created",
                        "Before you create it again, check with: "
                            + CheckCommand(result, sellerId, "xbl", "web-service", "list")),
                    (correlationId, onSending) => client.CreateWebServiceAsync(
                        sellerId, serviceName, result.GetValue(telemetry), result.GetValue(appChannels), correlationId,
                        cancellationToken, onSending),
                    service => $"Created web service {service.WebServiceId:D} "
                        + $"({XboxPartnerCommandSupport.ForTerminal(service.Name)}).",
                    cancellationToken);
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: $"A new web service of {Seller}, for its default business partner.",
            Output: "The new web service, with the ID XBOX services gave it, as 'xbox xbl web-service get' writes it. "
                + "Nothing is written to stdout when the create is refused or its outcome is unknown.",
            SafetyDetails: "Creates the web service for the seller's default business partner. For another business "
                + "partner, use Partner Center. The request is sent once and never retried. Agent approval is still "
                + "required.",
            Examples:
            [
                "xbox xbl web-service create --name \"Nod Arena matchmaker\" --seller-id 1234567",
                "xbox xbl web-service create --name stats.nodpublishers.com --telemetry-access --seller-id 1234567",
            ],
            LearnMore:
            [
                GuideLink,
                "Exit codes: 0 created; 1 not created; 3 the create was sent, but its outcome is unknown. After 3, "
                    + "check with the 'xbox xbl web-service list' command that stderr shows before you create it again.",
            ],
            Table: WebServiceTable));
    }

    private static Command CreateUpdate()
    {
        var seller = new XdtsSellerOption();
        var id = CreateWebServiceIdArgument("The ID of the web service to change.");
        var name = new Option<string?>("--name")
        {
            HelpName = "name",
            Description = "A new name, 1 to 100 characters, without <, >, &, ', or \".",
        };
        var telemetry = new Option<bool?>("--telemetry-access")
        {
            Description = "true to let the web service read telemetry for every game of the seller, false to stop it. "
                + "Default: unchanged.",
        };
        var appChannels = new Option<bool?>("--app-channels-access")
        {
            Description = "true to let the web service publish app channels to consoles, false to stop it. "
                + "Default: unchanged.",
        };
        var command = new Command("update", "Rename a web service, or change its access") { id };
        seller.AddTo(command);
        command.Options.Add(name);
        command.Options.Add(telemetry);
        command.Options.Add(appChannels);
        command.Validators.Add(result =>
        {
            if (result.GetResult(name) is null && result.GetResult(telemetry) is null
                && result.GetResult(appChannels) is null)
            {
                result.AddError("Pass at least one change: --name, --telemetry-access, or --app-channels-access.");
            }
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, (client, sellerId) =>
            {
                Guid webServiceId = Guid.Parse(result.GetRequiredValue(id));
                return ChangeAsync(
                    result,
                    new Change(
                        $"Updating web service {webServiceId:D}",
                        $"web service {webServiceId:D} wasn't changed",
                        $"whether web service {webServiceId:D} was changed",
                        "Check it, and run the update again if it doesn't show the change: "
                            + CheckCommand(result, sellerId, "xbl", "web-service", "get", webServiceId.ToString("D"))),
                    (correlationId, onSending) => client.UpdateWebServiceAsync(
                        sellerId, webServiceId, result.GetValue(name), result.GetValue(telemetry),
                        result.GetValue(appChannels), correlationId, cancellationToken, onSending),
                    service => $"Updated web service {service.WebServiceId:D}.",
                    cancellationToken);
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: $"One web service of {Seller}.",
            Output: "The web service after the change, as 'xbox xbl web-service get' writes it. Nothing is written to "
                + "stdout when the update is refused or its outcome is unknown.",
            SafetyDetails: "Changes only what you pass. The request is sent once and never retried. Agent approval is "
                + "still required.",
            Examples:
            [
                "xbox xbl web-service update 11111111-2222-3333-4444-555555555555 --name \"Nod Arena matchmaker EU\"",
                "xbox xbl web-service update 11111111-2222-3333-4444-555555555555 --telemetry-access false",
            ],
            LearnMore:
            [
                GuideLink,
                "Exit codes: 0 changed; 1 not changed; 3 the update was sent, but its outcome is unknown. After 3, "
                    + "check with the 'xbox xbl web-service get' command that stderr shows before you run it again.",
            ],
            Table: WebServiceTable));
    }

    private static Command CreateDelete(TestAccountCommandDependencies dependencies)
    {
        var seller = new XdtsSellerOption();
        var id = CreateWebServiceIdArgument("The ID of the web service to delete.");
        var confirm = CreateConfirmOption("Delete the web service without asking first.");
        var command = new Command("delete", "Delete a web service") { id };
        seller.AddTo(command);
        command.Options.Add(confirm);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, seller, (client, sellerId) =>
            {
                Guid webServiceId = Guid.Parse(result.GetRequiredValue(id));
                return ChangeAsync(
                    result,
                    new Change(
                        $"Deleting web service {webServiceId:D}",
                        $"web service {webServiceId:D} wasn't deleted",
                        $"whether web service {webServiceId:D} was deleted",
                        "Check with: "
                            + CheckCommand(result, sellerId, "xbl", "web-service", "get", webServiceId.ToString("D"))
                            + Environment.NewLine + "A web_service_not_found error means it was deleted."),
                    (correlationId, onSending) => client.DeleteWebServiceAsync(
                        sellerId, webServiceId, correlationId, cancellationToken, onSending),
                    service => $"Deleted web service {service.WebServiceId:D} "
                        + $"({XboxPartnerCommandSupport.ForTerminal(service.Name)}).",
                    cancellationToken,
                    async () =>
                    {
                        XboxLiveWebService service;
                        using (RequestProgress.Start("Reading the web service"))
                        {
                            service = (await client.GetWebServiceAsync(sellerId, webServiceId, cancellationToken)).Value;
                        }

                        string name = XboxPartnerCommandSupport.ForTerminal(service.Name);
                        Console.Error.WriteLine(
                            $"Web service {webServiceId:D} ({name}) acts for business partner {service.BusinessPartnerId:D}.");
                        return await PlaytestCommandFactory.ConfirmAsync(
                            IsConfirmed(result, confirm),
                            JobDependencies(dependencies),
                            $"This deletes web service {webServiceId:D}{OfSeller(sellerId)}, which can't be undone. It "
                                + "takes away none of the web service's access: certificates created for it keep "
                                + "working, with that access, until they expire. To stop using a web service, keep it: "
                                + "remove it from your games' access policies, and turn off its telemetry and app "
                                + "channels access with 'xbox xbl web-service update'. XBOX services take that access "
                                + "away in the background, which deleting the web service stops. A game whose access "
                                + "policies name the web service fails its next configuration validation until you "
                                + "remove it.",
                            $"Delete web service {webServiceId:D} ({name})?",
                            "Canceled. Nothing was deleted.",
                            cancellationToken);
                    });
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: $"One web service of {Seller}.",
            Output: "The web service as it was, as 'xbox xbl web-service get' writes it. Nothing is written to stdout "
                + "when the delete is declined, refused, or its outcome is unknown.",
            SafetyDetails: "Can't be undone. Takes away none of the web service's access: certificates created for it "
                + "keep working, with that access, until they expire, about 18 months after they were created. Neither "
                + "the CLI nor Partner Center can revoke them; if a certificate's key leaks, your developer account "
                + "manager (DAM) can have XBOX revoke it. To stop using a web service, keep it: remove it from your "
                + "games' access policies, and turn off its telemetry and app channels access with 'xbox xbl "
                + "web-service update'. XBOX services take that access away in the background, which deleting the web "
                + "service stops. Kept or deleted, a web service keeps its access to content in each of the seller's "
                + "sandboxes. A game whose access policies name a deleted web service fails its next configuration "
                + "validation until you remove it. Shows the web service's name and business partner on stderr. Asks "
                + "first at a terminal, and fails without --confirm (--yes, -y) otherwise. The request is sent once and "
                + "never retried. Agent approval is still required.",
            Examples:
            [
                "xbox xbl web-service delete 11111111-2222-3333-4444-555555555555",
            ],
            LearnMore:
            [
                GuideLink,
                "Exit codes: 0 deleted; 1 not deleted; 3 the delete was sent, but its outcome is unknown. After 3, "
                    + "check with the 'xbox xbl web-service get' command that stderr shows: web_service_not_found "
                    + "means it was deleted.",
            ],
            Table: WebServiceTable));
    }

    private static Argument<string> CreateWebServiceIdArgument(string description)
    {
        var argument = new Argument<string>("web-service-id") { Description = $"{description} {WebServiceIdFormat}" };
        argument.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !Guid.TryParse(result.Tokens[0].Value, out _))
            {
                result.AddError($"Invalid web service ID '{result.Tokens[0].Value}'. {WebServiceIdFormat}");
            }
        });
        return argument;
    }

    private static XboxPartnerJobDependencies JobDependencies(TestAccountCommandDependencies dependencies) =>
        new(dependencies.DelayAsync, dependencies.GetUtcNow, dependencies.IsInteractive);

    private static string OfSeller(long? sellerId) =>
        sellerId is { } seller ? $" of seller {seller.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
}
