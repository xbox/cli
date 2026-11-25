// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl binding</c>, which reads, creates, and deletes the binding that makes a game,
/// named by its Store ID, use another game's XBOX services configuration.
/// </summary>
internal static class XboxLiveBindingCommandFactory
{
    /// <summary>Exit code when the bind or unbind finished, but failed.</summary>
    internal const int FailedExitCode = 2;

    /// <summary>Exit code when the bind or unbind started, but the wait ended before it finished.</summary>
    internal const int NotFinishedExitCode = 4;

    private const string Scope =
        "One game, named by --store-id, of the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set', "
        + "or of your only seller.";

    private const string GuideLink =
        "How binding works, and what a bound game's builds need: "
        + "https://docs.xbox.com/services/xbox-services/fundamentals/game-binding/game-binding-overview";

    private static readonly TableView BindingTable = TableView.Record(
    [
        new TableColumn("STORE ID", Camel(nameof(XboxLiveBinding.StoreId)), 12),
        new TableColumn("TITLE ID", Camel(nameof(XboxLiveBinding.TitleId)), 8),
        new TableColumn("PRIMARY TITLE ID", Camel(nameof(XboxLiveBinding.PrimaryTitleId)), 16),
        new TableColumn("STATUS", Camel(nameof(XboxLiveBinding.Status)), 8),
        new TableColumn("BOUND GAMES", Camel(nameof(XboxLiveBinding.BoundStoreIds)), Transform: JoinStoreIds),
    ]);

    public static Command Create(XboxPartnerJobDependencies? dependencies = null)
    {
        dependencies ??= XboxPartnerJobDependencies.Default;
        var binding = new Command(
            "binding",
            "Get, create, and delete the binding that makes a game use another game's configuration");
        binding.Subcommands.Add(CreateGet());
        binding.Subcommands.Add(CreateCreate(dependencies));
        binding.Subcommands.Add(CreateDelete(dependencies));
        return binding;
    }

    private static Command CreateGet()
    {
        var seller = new XdtsSellerOption();
        var storeId = CreateStoreIdOption("The game's Store ID.");
        var command = new Command("get", "Get a game's binding to another game's XBOX services configuration");
        seller.AddTo(command);
        command.Options.Add(storeId);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(
                result, seller, storeId,
                async (client, sellerId, store) =>
                {
                    XboxLiveBinding binding;
                    using (RequestProgress.Start($"Reading the binding of game {store}"))
                    {
                        binding = await client.GetXboxLiveBindingAsync(sellerId, store, cancellationToken);
                    }

                    Write(result, binding);
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: Scope,
            Output: "A JSON object with storeId; titleId, the game's own XBOX title ID; primaryTitleId, the XBOX "
                + "title ID of the game it's bound to, or null; boundStoreIds, the Store IDs of the games bound to "
                + "it; status, Running, Success, or Failed for the last bind or unbind, or null when the game isn't "
                + "bound; and statusMessage, "
                + "why it failed.",
            Examples:
            [
                "xbox xbl binding get --store-id 9N0000000000",
                "xbox xbl binding get --store-id 9N0000000000 --output table",
            ],
            LearnMore: [GuideLink],
            Table: BindingTable));
    }

    private static Command CreateCreate(XboxPartnerJobDependencies dependencies)
    {
        var seller = new XdtsSellerOption();
        var storeId = CreateStoreIdOption("The Store ID of the game to bind.");
        var primaryTitleId = new Option<string>("--primary-title-id")
        {
            HelpName = "id",
            Description = "The XBOX title ID, not the PlayFab title ID, of the game whose configuration to use. "
                + XboxPartnerGameResolver.TitleIdFormat,
            Required = true,
        };
        primaryTitleId.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !XboxTitleId.TryNormalize(result.Tokens[0].Value, out _))
            {
                result.AddError(
                    $"Invalid XBOX title ID '{result.Tokens[0].Value}'. {XboxPartnerGameResolver.TitleIdFormat}");
            }
        });
        var command = new Command(
            "create", "Bind a game to another game's XBOX services configuration, and wait until it's bound");
        seller.AddTo(command);
        command.Options.Add(storeId);
        command.Options.Add(primaryTitleId);
        var options = AddChangeOptions(command, "bind");
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(
                result, seller, storeId,
                (client, sellerId, store) =>
                {
                    XboxTitleId.TryNormalize(result.GetRequiredValue(primaryTitleId), out var primary);
                    return ChangeAsync(
                        result, client, sellerId, store, options, dependencies,
                        // Another bind or unbind can change the binding while this one is waited on, so the bind is
                        // done only if the game is then bound to the primary.
                        new Change(
                            "bind",
                            $"Binding game {store} to {primary}",
                            $"bound to {primary}",
                            binding => IsStatus(binding, "Running") ? null
                                : IsStatus(binding, "Failed") ? FailedExitCode
                                : string.Equals(binding.PrimaryTitleId, primary, StringComparison.OrdinalIgnoreCase) ? 0
                                : FailedExitCode,
                            $"Once it's bound, game {store} uses {primary}'s XBOX services configuration, so you "
                                + $"change and publish its configuration through {primary}. Binding game {store} to "
                                + $"{primary} publishes both games' title-wide configuration, which every sandbox uses, "
                                + "RETAIL included, so their title-wide changes that were saved but not published go "
                                + "live.",
                            $"Bind game {store} to {primary}?",
                            $"Game {store} isn't bound to {primary}, maybe because of another change to its "
                                + "binding. Before you run 'xbox xbl binding create' again, check it with: "
                                + CheckCommand(result, sellerId, store)),
                        (correlationId, onSending) => client.CreateXboxLiveBindingAsync(
                            sellerId, store, primary!, correlationId, cancellationToken, onSending),
                        cancellationToken);
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: Scope,
            Output: "Once the bind finishes, the binding, as 'xbox xbl binding get' writes it, including when the "
                + "bind failed. With --no-wait, the binding as soon as the bind starts. Nothing is written to "
                + "stdout when the bind is refused, its outcome is unknown, or the wait ends first.",
            SafetyDetails: "Turns off the game's own XBOX services configuration and gives the game the primary "
                + "game's XBOX title ID, so its builds must use that title ID. 'xbox xbl binding delete' undoes it. "
                + "Also publishes both games' title-wide configuration, which every sandbox uses, RETAIL included, so "
                + "their title-wide changes that were saved but not published go live, and unbinding doesn't undo "
                + "that. Requires --confirm (--yes, -y) or terminal confirmation. The request is sent once and never "
                + "retried. Agent approval is still required.",
            Examples:
            [
                "xbox xbl binding create --store-id 9N0000000000 --primary-title-id 1234ABCD",
                "xbox xbl binding create --store-id 9N0000000000 --primary-title-id 1234ABCD --no-wait",
            ],
            LearnMore:
            [
                GuideLink,
                "Waits until the bind finishes, or --timeout passes. Ctrl+C stops waiting; the bind continues.",
                "Exit codes: 0 bound, or started with --no-wait; 1 not bound; 2 the bind failed, or the game isn't "
                    + "bound to the primary when the wait ends; 3 the request was sent, but its outcome is unknown; 4 "
                    + "the bind started, but the wait ended before it finished. After 3 or 4, check with the "
                    + "'xbox xbl binding get' command that stderr shows.",
            ],
            Table: BindingTable));
    }

    private static Command CreateDelete(XboxPartnerJobDependencies dependencies)
    {
        var seller = new XdtsSellerOption();
        var storeId = CreateStoreIdOption("The Store ID of the game to unbind.");
        var command = new Command(
            "delete", "Unbind a game from another game's XBOX services configuration, and wait until it's unbound");
        seller.AddTo(command);
        command.Options.Add(storeId);
        var options = AddChangeOptions(command, "unbind");
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(
                result, seller, storeId,
                (client, sellerId, store) => ChangeAsync(
                    result, client, sellerId, store, options, dependencies,
                    // An unbind clears primaryTitleId before it finishes, and the game is unbound from then on.
                    new Change(
                        "unbind",
                        $"Unbinding game {store}",
                        "unbound",
                        binding => binding.PrimaryTitleId is null ? 0
                            : IsStatus(binding, "Failed") ? FailedExitCode
                            : null,
                        $"Unbinding game {store} returns it to its own XBOX services configuration as it was before "
                            + "it was bound, without the changes made while it was bound. Unbinding also publishes its "
                            + "title-wide configuration, which every sandbox uses, RETAIL included, so its title-wide "
                            + "changes that were saved but not published go live.",
                        $"Unbind game {store}?"),
                    (correlationId, onSending) => client.DeleteXboxLiveBindingAsync(
                        sellerId, store, correlationId, cancellationToken, onSending),
                    cancellationToken),
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: Scope,
            Output: "Once the unbind finishes, the binding, as 'xbox xbl binding get' writes it, including when the "
                + "unbind failed. With --no-wait, the binding as soon as the unbind starts. Nothing is written to "
                + "stdout when the unbind is refused, its outcome is unknown, or the wait ends first.",
            SafetyDetails: "Restores the game's own XBOX services configuration and XBOX title ID, so its builds must "
                + "use its own title ID again. Also publishes the game's title-wide configuration, which every sandbox "
                + "uses, RETAIL included, so its title-wide changes that were saved but not published go live. "
                + "Requires --confirm (--yes, -y) or terminal confirmation. The request is sent once and never "
                + "retried. Agent approval is still required.",
            Examples:
            [
                "xbox xbl binding delete --store-id 9N0000000000",
                "xbox xbl binding delete --store-id 9N0000000000 --timeout 10m",
            ],
            LearnMore:
            [
                GuideLink,
                "Waits until the game shows unbound, or --timeout passes. XBOX services finish the unbind after "
                    + "that, and refuse to bind the game until they do; a failure in that last part isn't reported. "
                    + "Ctrl+C stops waiting; the unbind continues.",
                "Exit codes: 0 unbound, or started with --no-wait; 1 not unbound; 2 the unbind failed; 3 the request "
                    + "was sent, but its outcome is unknown; 4 the unbind started, but the wait ended before it "
                    + "finished. After 3 or 4, check with the 'xbox xbl binding get' command that stderr shows.",
            ],
            Table: BindingTable));
    }

    private static Option<string> CreateStoreIdOption(string description)
    {
        var option = new Option<string>("--store-id")
        {
            HelpName = "id",
            Description = $"{description} {XboxPartnerGameResolver.StoreIdFormat}",
            Required = true,
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && !XboxPartnerGameResolver.TryNormalizeStoreId(result.Tokens[0].Value, out _))
            {
                result.AddError($"Invalid Store ID '{result.Tokens[0].Value}'. {XboxPartnerGameResolver.StoreIdFormat}");
            }
        });
        return option;
    }

    private sealed record ChangeOptions(Option<bool> NoWait, Option<string?> Timeout, Option<bool> Confirm);

    private static ChangeOptions AddChangeOptions(Command command, string verb)
    {
        var noWait = new Option<bool>("--no-wait")
        {
            Description = $"Return as soon as the {verb} starts, without waiting for it to finish.",
        };
        var timeout = TestAccountCommandFactory.CreateTimeoutOption($"the {verb} to finish");
        var confirm = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = $"Confirm the {verb} without prompting.",
        };
        command.Options.Add(noWait);
        command.Options.Add(timeout);
        command.Options.Add(confirm);
        command.Validators.Add(result =>
        {
            if (result.GetFlag(noWait) && result.GetResult(timeout) is { Implicit: false })
            {
                result.AddError("--no-wait and --timeout cannot be combined.");
            }
        });
        return new ChangeOptions(noWait, timeout, confirm);
    }

    /// <summary>A bind or unbind: how messages name it, what it asks, and when it's over.</summary>
    /// <param name="Verb">The change, such as "bind".</param>
    /// <param name="Activity">The change in progress, such as "Binding game 9N0000000000 to 1234ABCD".</param>
    /// <param name="Done">The finished state, such as "bound to 1234ABCD".</param>
    /// <param name="Finish">Given a read of the binding, the exit code once the change is over, or null.</param>
    /// <param name="Warning">What the change does that its name doesn't say, written before it asks.</param>
    /// <param name="Question">What it asks, such as "Bind game 9N0000000000 to 1234ABCD?".</param>
    /// <param name="NotDone">
    /// The message when the change is over without failing, but the game isn't <paramref name="Done"/>, which only a
    /// bind can be.
    /// </param>
    private sealed record Change(
        string Verb,
        string Activity,
        string Done,
        Func<XboxLiveBinding, int?> Finish,
        string Warning,
        string Question,
        string? NotDone = null);

    private static bool IsStatus(XboxLiveBinding binding, string status) =>
        string.Equals(binding.Status, status, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks first, then sends a bind or unbind, then waits for it to finish unless <c>--no-wait</c> was passed. A
    /// failure after the request was sent, other than a refusal (4xx other than 408), leaves the outcome unknown.
    /// </summary>
    private static async Task<int> ChangeAsync(
        ParseResult result,
        XboxPartnerClient client,
        long? sellerId,
        string storeId,
        ChangeOptions options,
        XboxPartnerJobDependencies dependencies,
        Change change,
        Func<string, Action, Task<XboxLiveBinding>> send,
        CancellationToken cancellationToken)
    {
        string check = CheckCommand(result, sellerId, storeId);
        string correlationId = Guid.NewGuid().ToString();
        bool confirmed = result.GetResult(options.Confirm) is { Implicit: false }
            ? result.GetValue(options.Confirm)
            : result.GetValue(Program.ConfirmOption);
        var sent = false;
        XboxLiveBinding binding;
        try
        {
            if (!await PlaytestCommandFactory.ConfirmAsync(
                confirmed, dependencies, change.Warning, change.Question, $"Game {storeId} wasn't {change.Done}.",
                cancellationToken))
            {
                return 1;
            }

            Program.WriteVerbose(
                result.GetValue(Program.VerboseOption),
                () => $"{change.Activity} with correlation ID {correlationId}.");
            using (RequestProgress.Start(change.Activity))
            {
                binding = await send(correlationId, () => sent = true);
            }
        }
        catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Canceled before the request was sent, so game {storeId} wasn't {change.Done}.");
            return 1;
        }
        catch (Exception exception) when (sent && XboxPartnerCommandSupport.IsUncertainSubmission(exception))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(
                exception, OutputOptions.From(result).IsCompact, Console.Error, cancellationToken, correlationId);
            Console.Error.WriteLine($"It is unknown whether the {change.Verb} of game {storeId} started. Don't resubmit it.");
            Console.Error.WriteLine($"Check with: {check}");
            return XboxPartnerCommandSupport.OutcomeUnknownExitCode;
        }

        if (change.Finish(binding) is { } exitCode)
        {
            return Finish(result, storeId, change, binding, exitCode);
        }

        if (result.GetValue(options.NoWait))
        {
            Write(result, binding);
            Console.Error.WriteLine($"The {change.Verb} of game {storeId} started. Check it with: {check}");
            return 0;
        }

        return await WaitAsync(
            result, client, sellerId, storeId, change, TestAccountCommandFactory.ReadTimeout(result, options.Timeout),
            check, dependencies, cancellationToken);
    }

    /// <summary>
    /// Reads the binding until the change is over or the timeout passes. The wait runs on
    /// <see cref="XboxPartnerPoller"/>, which retries reads that fail briefly.
    /// </summary>
    private static async Task<int> WaitAsync(
        ParseResult result,
        XboxPartnerClient client,
        long? sellerId,
        string storeId,
        Change change,
        TimeSpan timeout,
        string check,
        XboxPartnerJobDependencies dependencies,
        CancellationToken cancellationToken)
    {
        string waited = TestAccountCommandFactory.FormatDuration(timeout);
        string stillRunning = $"The {change.Verb} may still be running. Check it with: {check}";
        Console.Error.WriteLine($"Waiting up to {waited} for game {storeId} to be {change.Done}...");
        XboxLiveBinding? last = null;
        int exitCode = 0;
        try
        {
            bool finished;
            using (RequestProgress.Start(change.Activity))
            {
                finished = await XboxPartnerPoller.PollAsync(
                    token => client.GetXboxLiveBindingAsync(sellerId, storeId, token),
                    binding =>
                    {
                        if (change.Finish(binding) is not { } code)
                        {
                            return false;
                        }

                        (last, exitCode) = (binding, code);
                        return true;
                    },
                    failedReadRetryAfter => failedReadRetryAfter is { } retryAfter
                        ? TestAccountJobWaiter.Clamp(retryAfter)
                        : TestAccountJobWaiter.DefaultInterval,
                    timeout,
                    dependencies.DelayAsync,
                    dependencies.GetUtcNow,
                    cancellationToken);
            }

            if (finished)
            {
                return Finish(result, storeId, change, last!, exitCode);
            }

            Console.Error.WriteLine($"Game {storeId} wasn't {change.Done} within {waited}. {stillRunning}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Stopped waiting. {stillRunning}");
        }
        // The change keeps running whatever stopped the reads, so the way to check it is what matters.
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(
                exception, OutputOptions.From(result).IsCompact, Console.Error, cancellationToken);
            Console.Error.WriteLine($"Couldn't read the binding of game {storeId}. {stillRunning}");
        }

        return NotFinishedExitCode;
    }

    private static int Finish(ParseResult result, string storeId, Change change, XboxLiveBinding binding, int exitCode)
    {
        Write(result, binding);
        Console.Error.WriteLine(
            exitCode == 0 ? $"Game {storeId} is {change.Done}."
            : IsStatus(binding, "Failed")
                ? $"The {change.Verb} of game {storeId} failed"
                    + (string.IsNullOrWhiteSpace(binding.StatusMessage)
                        ? "."
                        : $": {XboxPartnerCommandSupport.ForTerminal(binding.StatusMessage)}")
            : change.NotDone);
        return exitCode;
    }

    private static void Write(ParseResult result, XboxLiveBinding binding)
    {
        var output = OutputOptions.From(result);
        if (output.TableView is { } view)
        {
            using var document = JsonDocument.Parse(binding.RawResponse);
            Console.WriteLine(TableFormatter.Format(document.RootElement, view));
            return;
        }

        Console.WriteLine(PlayFabApiClient.FormatJson(binding.RawResponse, output.IsCompact));
    }

    /// <summary>The read-only <c>binding get</c> command that checks the game, with the caller's sign-in options.</summary>
    private static string CheckCommand(ParseResult result, long? sellerId, string storeId)
    {
        var arguments = RecoveryCommandArguments.Create(result);
        arguments.AddRange(["xbl", "binding", "get", "--store-id", ShellQuoting.Quote(storeId)]);
        if (sellerId is { } seller)
        {
            arguments.AddRange(["--seller-id", seller.ToString(CultureInfo.InvariantCulture)]);
        }

        return string.Join(' ', arguments);
    }

    private static async Task<int> RunAsync(
        ParseResult result,
        XdtsSellerOption sellerOption,
        Option<string> storeIdOption,
        Func<XboxPartnerClient, long?, string, Task<int>> action,
        CancellationToken cancellationToken)
    {
        var configuration = Program.GetConfiguration(result);
        _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
        using var client = Program.XboxPartnerClientFactory is null
            ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(result))
            : Program.XboxPartnerClientFactory(configuration);
        XboxPartnerGameResolver.TryNormalizeStoreId(result.GetRequiredValue(storeIdOption), out var storeId);
        try
        {
            return await action(client, sellerOption.GetSellerId(result), storeId);
        }
        // Only the service's failures and a request timeout, which cancels without the caller, are reported
        // here. Everything else, including the caller's cancellation, reaches the shared boundary.
        catch (Exception exception) when (exception is XboxPartnerApiException or XboxPartnerResponseException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(
                exception, OutputOptions.From(result).IsCompact, Console.Error, cancellationToken);
            return 1;
        }
    }

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>The Store IDs of the games bound to a primary game, or "-" when there are none.</summary>
    private static string JoinStoreIds(JsonElement storeIds) =>
        storeIds.ValueKind == JsonValueKind.Array && storeIds.GetArrayLength() > 0
            ? string.Join(", ", storeIds.EnumerateArray().Select(storeId => storeId.GetString()))
            : "-";
}
