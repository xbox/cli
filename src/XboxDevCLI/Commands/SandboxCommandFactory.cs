// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds the root sandbox namespace. Sandboxes belong to the Partner Center account and isolate
/// Store products as well as XBOX services, so they are not nested under <c>xbl</c>.
/// </summary>
internal static class SandboxCommandFactory
{
    /// <summary>The creation job failed, which can leave the sandbox's state unknown.</summary>
    internal const int JobFailedExitCode = 2;

    /// <summary>
    /// The creation job was accepted but the command stopped before confirming the sandbox: the wait
    /// timed out or was canceled, polling failed, or the created sandbox could not be read back.
    /// Matches <c>test-account batch create</c>.
    /// </summary>
    internal const int WaitEndedExitCode = 4;

    public static Command Create() =>
        Create(clientFactory: null);

    internal static Command Create(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies? creationDependencies = null)
    {
        var sandbox = new Command("sandbox", "List, get, and create XBOX sandboxes");
        sandbox.Subcommands.Add(CreateListCommand(clientFactory));
        sandbox.Subcommands.Add(CreateGetCommand(clientFactory));
        sandbox.Subcommands.Add(CreateCreateCommand(
            clientFactory, creationDependencies ?? XboxPartnerJobDependencies.Default));
        return sandbox.Requires(CommandRequirement.Authentication).Calls(ServiceEndpoints.XboxPartner);
    }

    private static bool TryNormalize(string? value, bool forCreate, out string normalized) =>
        forCreate
            ? XboxSandboxId.TryNormalizeForCreate(value, out normalized)
            : XboxSandboxId.TryNormalizeForRead(value, out normalized);

    private static string FormatDescription(bool forCreate) =>
        forCreate ? XboxSandboxId.CreateFormatDescription : XboxSandboxId.ReadFormatDescription;

    private static Argument<string> CreateSandboxIdArgument(string description, bool forCreate)
    {
        var argument = new Argument<string>("sandbox-id") { Description = description };
        argument.Validators.Add(result =>
        {
            if (result.Tokens.Count == 0)
            {
                return;
            }

            var value = result.Tokens[0].Value;
            if (XboxSandboxId.IsNumber(value))
            {
                if (!XboxSandboxId.TryNormalizeNumber(value, out var number))
                {
                    result.AddError($"Invalid sandbox number '{value}'. {XboxSandboxId.NumberFormatDescription}");
                }
                else if (forCreate && XboxSandboxId.IsReservedNumber(number))
                {
                    result.AddError(
                        $"Sandbox number {number} is reserved for Isolated sandboxes, so it can't be created. " +
                        "Choose another number.");
                }
            }
            else if (!TryNormalize(value, forCreate, out _))
            {
                result.AddError($"Invalid sandbox ID '{value}'. {FormatDescription(forCreate)}");
            }
        });
        return argument;
    }

    /// <summary>
    /// Returns the sandbox ID to use: a sandbox ID as normalized, or a sandbox number joined to the
    /// seller's account moniker, which is read only for a number. A command that
    /// <paramref name="changes"/> something always shows the ID a number resolved to.
    /// </summary>
    internal static async Task<string?> ResolveSandboxIdAsync(
        ParseResult result,
        string value,
        bool forCreate,
        bool changes,
        XboxPartnerClient client,
        string baseUrl,
        string sellerId,
        CancellationToken cancellationToken)
    {
        if (!XboxSandboxId.TryNormalizeNumber(value, out var number))
        {
            TryNormalize(value, forCreate, out var id);
            return id;
        }

        var moniker = await ReadMonikerAsync(
            result, client, baseUrl, sellerId,
            $"to resolve sandbox number {number}",
            $"Sandbox number {number} could not be resolved",
            cancellationToken);
        var resolved = $"{moniker}.{number}";
        if (!TryNormalize(resolved, forCreate, out var normalized))
        {
            Console.Error.WriteLine(
                $"Sandbox number {number} resolves to '{XboxPartnerCommandSupport.ForTerminal(resolved)}' " +
                $"for seller {sellerId}, which isn't a valid sandbox ID here. {FormatDescription(forCreate)}");
            return null;
        }

        // A command that changes something always shows the ID it filled in, even with --confirm.
        if (changes)
        {
            Console.Error.WriteLine($"Sandbox number {number} is sandbox {normalized}.");
        }
        else
        {
            Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                () => $"Sandbox number {number} is sandbox {normalized}.");
        }

        return normalized;
    }

    /// <summary>
    /// Returns the ID of the seller's lowest-numbered sandbox that the listing doesn't show and that
    /// can be created, or <see langword="null"/> after explaining why there's none.
    /// </summary>
    private static async Task<string?> ChooseSandboxIdAsync(
        ParseResult result,
        XboxPartnerClient client,
        string baseUrl,
        string sellerId,
        CancellationToken cancellationToken)
    {
        const string NotChosen = "A sandbox number could not be chosen";
        var moniker = await ReadMonikerAsync(
            result, client, baseUrl, sellerId, "to choose a sandbox number", NotChosen, cancellationToken);
        if (!XboxSandboxId.TryNormalizeForCreate($"{moniker}.0", out _))
        {
            Console.Error.WriteLine(
                $"The account moniker for seller {sellerId}, '{XboxPartnerCommandSupport.ForTerminal(moniker)}', " +
                $"doesn't form a sandbox ID that can be created. {XboxSandboxId.CreateFormatDescription}");
            return null;
        }

        IReadOnlyList<XboxPartnerSandboxSummary> sandboxes;
        try
        {
            Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                () => $"Listing sandboxes for seller {sellerId}, following every page, to choose a sandbox number.");
            string json;
            using (var progress = ListProgress.Start("Reading sandboxes"))
            {
                json = await client.ListSandboxesAsync(baseUrl, sellerId, cancellationToken, progress.PageRead);
            }

            sandboxes = XboxPartnerCommandSupport.DeserializeResponse<List<XboxPartnerSandboxSummary>>(json, "sandbox list");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && XboxPartnerCommandSupport.IsRequestError(exception))
        {
            Console.Error.WriteLine(
                $"{NotChosen}, because the sandboxes of seller {sellerId} could not be listed." +
                Advice(exception, sellerId, "Pass a sandbox number or ID instead of --free-number."));
            throw;
        }

        var id = XboxSandboxId.LowestFreeId(moniker, sandboxes.Select(sandbox => sandbox.SandboxId));
        if (id is null)
        {
            Console.Error.WriteLine(
                $"Seller {sellerId} has no free sandbox number: every number from 0 to 999 under " +
                $"{XboxPartnerCommandSupport.ForTerminal(moniker.ToUpperInvariant())} is taken or reserved.");
            return null;
        }

        // A command that changes something always shows the ID it filled in, even with --confirm.
        Console.Error.WriteLine(
            $"Sandbox number {id[(id.LastIndexOf('.') + 1)..]} is the seller's lowest free number, so the new sandbox is {id}.");
        return id;
    }

    /// <summary>
    /// Reads the seller's account moniker, or explains why it couldn't be read and what to do
    /// instead (see Advice), and rethrows.
    /// </summary>
    internal static async Task<string> ReadMonikerAsync(
        ParseResult result,
        XboxPartnerClient client,
        string baseUrl,
        string sellerId,
        string purpose,
        string failure,
        CancellationToken cancellationToken)
    {
        try
        {
            Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                () => $"Reading the account moniker for seller {sellerId} {purpose}.");
            var moniker = await client.GetAccountMonikerAsync(baseUrl, sellerId, cancellationToken);
            return moniker.Trim();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && XboxPartnerCommandSupport.IsRequestError(exception))
        {
            var list = XboxPartnerCommandSupport.BuildRecoveryCommand(result, "sandbox list", sellerId);
            Console.Error.WriteLine(
                $"{failure}, because the account moniker for seller {sellerId} could not be read." +
                Advice(exception, sellerId, $"Pass the full sandbox ID instead. To see the seller's sandbox IDs, run: {list}"));
            throw;
        }
    }

    /// <summary>
    /// What to do after a request that a sandbox number or --free-number needs fails:
    /// <paramref name="instead"/>, which avoids the request, unless the failure stops that too.
    /// </summary>
    private static string Advice(Exception exception, string sellerId, string instead) => exception switch
    {
        // A refused sign-in or seller isn't something to route around with other input.
        XboxPartnerApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            $" Check that you're signed in and have access to seller {sellerId}.",
        // Sign-in and local failures stop every request, and are reported on their own.
        _ when StopsEveryRequest(exception) => string.Empty,
        _ => " " + instead,
    };

    /// <summary>
    /// Whether <paramref name="exception"/> would stop any request, such as being signed out or
    /// having no seller, so <see cref="Advice"/> doesn't suggest another request instead.
    /// </summary>
    private static bool StopsEveryRequest(Exception exception) =>
        exception is XboxPartnerApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            or MsalException or InvalidOperationException or ArgumentException or UnauthorizedAccessException;

    private static Command CreateListCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var owned = new Option<bool>("--owned")
        {
            Description = "Only list the seller's own sandboxes, leaving out global sandboxes such as RETAIL.",
        };
        var type = new Option<string?>("--type")
        {
            Description = "Only list shared or isolated sandboxes.",
        };
        type.AcceptOnlyFromAmong("shared", "isolated");
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("list", "List the XBOX sandboxes available to a Partner Center seller")
        {
            owned, type, seller, baseUrl,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Listing sandboxes for seller {sellerId}, following every page.");
                    string json;
                    using (var progress = ListProgress.Start("Reading sandboxes"))
                    {
                        json = await client.ListSandboxesAsync(url, sellerId, cancellationToken, progress.PageRead);
                    }

                    var ownedOnly = result.GetValue(owned);
                    var typeFilter = result.GetValue(type);
                    if (ownedOnly || typeFilter is not null)
                    {
                        json = FilterSandboxes(json, ownedOnly, typeFilter);
                    }

                    json = $"{{\"sandboxes\":{json}}}";
                    var output = OutputOptions.From(result);
                    if (output.TableView is { } view)
                    {
                        using var document = JsonDocument.Parse(json);
                        Console.WriteLine(TableFormatter.Format(document.RootElement, view));
                    }
                    else
                    {
                        Console.WriteLine(Program.FormatOutput(json, output.IsCompact, dataOnly: false));
                    }

                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "Sandboxes visible to the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set', including global sandboxes unless --owned is passed.",
            Output: "A JSON object with a sandboxes array of sandbox objects with sandboxId, type (Shared, Isolated, or Unknown), " +
                "sellerId, and global; table output has SANDBOX, TYPE, and GLOBAL columns. --owned and --type " +
                "keep only the sandboxes that match every filter passed; none matching is an empty array. " +
                ListProgress.HelpOutput,
            Examples:
            [
                "xbox sandbox list",
                "xbox sandbox list --output table",
                "xbox sandbox list --owned --type shared --output table",
                "xbox sandbox list --type isolated --seller-id 1234567 --output json-compact",
            ],
            LearnMore:
            [
                "Lists the same sandboxes as the Sandboxes page in Partner Center.",
                "Pass any listed sandboxId to 'xbox sandbox get'.",
            ],
            Table: XboxPartnerCommandSupport.SandboxTableView));
    }

    private static Command CreateGetCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory)
    {
        var sandboxId = CreateSandboxIdArgument(
            "Sandbox ID, such as ABCDEF.1 or RETAIL, or a sandbox number in the seller's account, such as 1.",
            forCreate: false);
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("get", "Get one XBOX sandbox by ID or number")
        {
            sandboxId, seller, baseUrl,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    var id = await ResolveSandboxIdAsync(
                        result, result.GetValue(sandboxId)!, forCreate: false, changes: false, client, url, sellerId, cancellationToken);
                    if (id is null)
                    {
                        return 1;
                    }

                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Reading sandbox {id} for seller {sellerId}.");
                    var json = await client.GetSandboxAsync(url, sellerId, id, cancellationToken);
                    WriteSandbox(json, OutputOptions.From(result));
                    return 0;
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One sandbox, read as the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "The sandbox as a JSON object with sandboxId, type (Shared, Isolated, or Unknown), " +
                "sellerId (null for a global sandbox), and global; " +
                "table output has SANDBOX ID, TYPE, and SELLER ID columns.",
            Examples:
            [
                "xbox sandbox get ABCDEF.1",
                "xbox sandbox get RETAIL",
                "xbox sandbox get 1 --seller-id 1234567",
                "xbox sandbox get ABCDEF.1 --seller-id 1234567 --output table",
            ],
            LearnMore:
            [
                "Sandbox IDs are case-insensitive and normalized to upper case. " +
                XboxSandboxId.ReadFormatDescription,
                XboxSandboxId.NumberFormatDescription + " The number is joined to the seller's account " +
                "moniker, such as ABCDEF, to form the sandbox ID ABCDEF.1.",
            ],
            Table: XboxPartnerCommandSupport.SandboxRecordTableView));
    }

    private static Command CreateCreateCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var sandboxId = CreateSandboxIdArgument(
            "ID of the new development sandbox, such as ABCDEF.1, or its number in the seller's account, " +
            "such as 1. Required unless --free-number is passed.",
            forCreate: true);
        sandboxId.Arity = ArgumentArity.ZeroOrOne;
        var freeNumber = new Option<bool>("--free-number")
        {
            Description = "Use the seller's lowest free sandbox number instead of a sandbox ID.",
        };
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var confirm = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm production sandbox creation without prompting.",
        };
        var command = new Command("create", "Create a Shared XBOX development sandbox and wait for it")
        {
            sandboxId, freeNumber, seller, baseUrl, confirm,
        };
        command.Validators.Add(commandResult =>
        {
            var hasId = commandResult.GetResult(sandboxId) is { Tokens.Count: > 0 };
            if (hasId && commandResult.GetFlag(freeNumber))
            {
                commandResult.AddError("--free-number can't be combined with a sandbox ID.");
            }
            else if (!hasId && !commandResult.GetFlag(freeNumber))
            {
                commandResult.AddError(
                    "Pass a sandbox ID or number, or --free-number to use the seller's lowest free number.");
            }
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    var chosen = result.GetValue(freeNumber);
                    var id = chosen
                        ? await ChooseSandboxIdAsync(result, client, url, sellerId, cancellationToken)
                        : await ResolveSandboxIdAsync(
                            result, result.GetValue(sandboxId)!, forCreate: true, changes: true, client, url, sellerId, cancellationToken);
                    if (id is null)
                    {
                        return 1;
                    }

                    var confirmed = result.GetResult(confirm) is { Implicit: false }
                        ? result.GetValue(confirm)
                        : result.GetValue(Program.ConfirmOption);
                    if (!ConfirmCreation(result, confirmed, dependencies, id, sellerId, chosen))
                    {
                        return 1;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var recovery = XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result, $"sandbox get {ShellQuoting.Quote(id)}", sellerId);
                    Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                        () => $"Submitting sandbox creation for seller {sellerId}.");
                    XboxPartnerSandboxCreationJob job;
                    string? sentCorrelationId = null;
                    try
                    {
                        Console.Error.WriteLine("Submitting sandbox creation job...");
                        // Do not retry an uncertain submission after a request or polling failure.
                        job = await client.CreateSandboxAsync(
                            url, sellerId,
                            new CreateXboxSandboxRequest(id, XboxSandboxId.SharedType),
                            cancellationToken,
                            onSending: correlationId => sentCorrelationId = correlationId);
                    }
                    catch (OperationCanceledException) when (sentCorrelationId is null && cancellationToken.IsCancellationRequested)
                    {
                        Console.Error.WriteLine("Canceled before the request was sent, so no sandbox was created.");
                        return 1;
                    }
                    catch (Exception exception) when (sentCorrelationId is not null && XboxPartnerCommandSupport.IsUncertainSubmission(exception))
                    {
                        Console.Error.WriteLine(
                            "Sandbox creation was interrupted or its response could not be verified; " +
                            $"it is unknown whether sandbox {id} was created. Do not resubmit.");
                        Console.Error.WriteLine($"Check whether it exists with: {recovery}");
                        XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken, sentCorrelationId);
                        return XboxPartnerCommandSupport.OutcomeUnknownExitCode;
                    }


                    var correlation = string.IsNullOrWhiteSpace(job.CorrelationId)
                        ? string.Empty
                        : $" (Correlation ID: {XboxPartnerCommandSupport.ForTerminal(job.CorrelationId)})";
                    Console.Error.WriteLine($"Sandbox creation job submitted: {XboxPartnerCommandSupport.ForTerminal(job.JobId)}{correlation}");
                    // The gateway's job route only accepts a GUID, so any other ID can't be followed. A
                    // job that is already complete but has no recognized result can't be classified.
                    if (!Guid.TryParse(job.JobId, out _) || (job.Completed && !job.Succeeded && !job.Failed))
                    {
                        Console.Error.WriteLine(
                            "The sandbox creation job response was invalid; " +
                            $"it is unknown whether sandbox {id} was created. Do not resubmit.");
                        Console.Error.WriteLine($"Check whether it exists with: {recovery}");
                        return XboxPartnerCommandSupport.OutcomeUnknownExitCode;
                    }

                    return await WaitForJobAsync(
                        result, client, url, sellerId, id, job, recovery, dependencies, chosen, cancellationToken);
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: "Creates a Shared sandbox for --seller-id, XBOX_SELLER_ID, or the seller saved by 'context set'.",
            Output: "The created sandbox as a JSON object, or the failed job object when the creation job " +
                "fails. Nothing is written to stdout when the outcome is unknown or the job is still running. " +
                "Submission progress, job ID, correlation ID, and recovery commands go to stderr.",
            SafetyDetails: "Creates production state that cannot be deleted by the CLI or in Partner Center. Requires " +
                "--confirm (--yes, -y) or terminal confirmation. " +
                "Submission is never retried. After exit 3 or 4, do not run it again. After exit 2 with " +
                "--free-number, run it again with the chosen sandbox ID instead, so it can't create a second " +
                "sandbox. Agent approval is still required.",
            LearnMore:
            [
                "Only Shared sandboxes can be created. " + XboxSandboxId.CreateFormatDescription,
                XboxSandboxId.NumberFormatDescription + " The number is joined to the seller's account " +
                "moniker, such as ABCDEF, to form the sandbox ID ABCDEF.1, which the confirmation names. " +
                "The number 58 is reserved.",
                "With --free-number, the command lists the seller's sandboxes and uses the lowest number " +
                "from 0 to 999 that isn't taken or reserved. Listing and creating aren't atomic, so another " +
                "create can take that number first; the command never tries another number itself.",
                "Waits up to 120 seconds, polling every 5 seconds unless the service supplies Retry-After, " +
                "and retries brief failures reading the job's status. " +
                "Exit codes: 0 created; 1 not created (rejected, or failed before the request was sent); " +
                "2 the creation job failed; 3 the request was sent but its outcome is unknown; " +
                "4 the job was accepted but the wait ended (timeout, Ctrl+C, polling failure, or the new " +
                "sandbox could not be read back).",
                "After exit 3 or 4, do not resubmit: use 'xbox sandbox get <sandbox-id>' to check whether the sandbox exists.",
            ],
            Examples:
            [
                "xbox sandbox create ABCDEF.1 --seller-id 1234567 --confirm",
                "xbox sandbox create 2 --seller-id 1234567 --confirm",
                "xbox sandbox create --free-number --seller-id 1234567 --confirm",
                "xbox sandbox create ABCDEF.2 --confirm --output json-compact",
            ]));
    }

    private static async Task<int> WaitForJobAsync(
        ParseResult result,
        XboxPartnerClient client,
        string baseUrl,
        string sellerId,
        string sandboxId,
        XboxPartnerSandboxCreationJob initial,
        string recovery,
        XboxPartnerJobDependencies dependencies,
        bool chosen,
        CancellationToken cancellationToken)
    {
        var verbose = result.GetValue(Program.VerboseOption);
        var compact = OutputOptions.From(result).IsCompact;
        var jobId = XboxPartnerCommandSupport.ForTerminal(initial.JobId);
        if (!initial.Completed)
        {
            Console.Error.WriteLine($"Waiting for sandbox creation job {jobId}...");
        }

        XboxPartnerSandboxCreationJob? completed;
        try
        {
            completed = await XboxPartnerJobPoller.PollAsync(
                initial,
                requestToken =>
                {
                    Program.WriteVerbose(verbose,
                        () => $"Reading sandbox creation job {jobId}.");
                    return client.GetSandboxCreationJobAsync(baseUrl, sellerId, initial.JobId, requestToken);
                },
                dependencies.DelayAsync, cancellationToken, dependencies.GetUtcNow);
        }
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            Console.Error.WriteLine(
                exception switch
                {
                    OperationCanceledException =>
                        $"Stopped waiting for job {jobId}; it may still be running.",
                    XboxPartnerApiException =>
                        "The sandbox was submitted, but its status could not be read.",
                    _ =>
                        $"The sandbox was submitted, but its status could not be read: {XboxPartnerCommandSupport.ForTerminal(exception.Message)}",
                });
            Console.Error.WriteLine($"Check whether it exists with: {recovery}");
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            return WaitEndedExitCode;
        }

        if (completed is null)
        {
            Console.Error.WriteLine(
                $"Sandbox creation job {jobId} is still running after " +
                $"{XboxPartnerJobPoller.DefaultTimeout.TotalSeconds:0} seconds. Do not resubmit.");
            Console.Error.WriteLine($"Check whether it exists with: {recovery}");
            return WaitEndedExitCode;
        }

        if (!completed.Succeeded && !completed.Failed)
        {
            Console.Error.WriteLine(
                $"Sandbox creation job {jobId} completed without a recognized result " +
                $"('{XboxPartnerCommandSupport.ForTerminal(completed.Result)}'); it is unknown whether " +
                $"sandbox {sandboxId} was created. Do not resubmit.");
            Console.Error.WriteLine($"Check whether it exists with: {recovery}");
            return WaitEndedExitCode;
        }

        if (completed.Failed)
        {
            Console.WriteLine(Program.FormatOutput(completed.RawResponse, compact, dataOnly: false));
            Console.Error.WriteLine($"Sandbox creation job {jobId} failed for sandbox {sandboxId}.");
            if (chosen)
            {
                // Running --free-number again could choose another number, and a failed job can leave the
                // sandbox's state unknown, so pin the chosen ID.
                Console.Error.WriteLine("To try again without creating a second sandbox, run: " +
                    XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result, $"sandbox create {ShellQuoting.Quote(sandboxId)} --confirm", sellerId));
            }

            return JobFailedExitCode;
        }

        Console.Error.WriteLine($"Sandbox creation job {jobId} completed.");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    // A just-created sandbox can take a moment to become readable. The delay is inside
                    // the try so cancelling it still reports that the sandbox was created.
                    await dependencies.DelayAsync(XboxPartnerJobPoller.DefaultInterval, cancellationToken);
                }

                Program.WriteVerbose(verbose,
                    () => $"Reading sandbox {sandboxId}.");
                var json = await client.GetSandboxAsync(baseUrl, sellerId, sandboxId, cancellationToken);
                WriteSandbox(json, compact ? OutputOptions.JsonCompact : OutputOptions.Json);
                return 0;
            }
            catch (Exception exception) when (attempt == 1
                && !cancellationToken.IsCancellationRequested
                && XboxPartnerCommandSupport.IsRequestError(exception))
            {
                Program.WriteVerbose(verbose, () => $"Reading sandbox {sandboxId} failed; retrying once.");
            }
            catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
            {
                Console.Error.WriteLine($"Sandbox {sandboxId} was created, but it could not be read back.");
                Console.Error.WriteLine($"Check it with: {recovery}");
                XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
                return WaitEndedExitCode;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with a client, the service URL, and the seller, and reports
    /// a failed request. Without <paramref name="baseUrlOption"/>, the URL comes from configuration.
    /// <paramref name="offline"/>, when set, is written after a failure that would stop any request,
    /// such as being signed out or having no seller, to name input that needs no request.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        ParseResult result,
        Option<string?> sellerOption,
        Option<string?>? baseUrlOption,
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        Func<XboxPartnerClient, string, string, Task<int>> action,
        CancellationToken cancellationToken,
        string? offline = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = Program.GetConfiguration(result);
            var baseUrl = baseUrlOption is null
                ? XboxPartnerCommandSupport.ResolveBaseUrl(configuration)
                : XboxPartnerCommandSupport.ResolveBaseUrl(result, baseUrlOption, configuration);
            using var client = clientFactory is null
                ? new XboxPartnerClient(
                    configuration,
                    authenticationService: Program.GetAuthentication(result))
                : clientFactory(configuration);
            var sellerId = await XboxPartnerCommandSupport.GetSellerIdAsync(result, sellerOption)
                .WaitAsync(cancellationToken);
            return await action(client, baseUrl, sellerId);
        }
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception)
            && !XboxPartnerCommandSupport.IsCallerCancellation(exception, cancellationToken))
        {
            XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken);
            if (offline is not null && StopsEveryRequest(exception))
            {
                Console.Error.WriteLine(offline);
            }

            return 1;
        }
    }

    /// <summary>
    /// Keeps the sandboxes in a JSON array that match every filter given, as their original JSON.
    /// </summary>
    private static string FilterSandboxes(string json, bool ownedOnly, string? type)
    {
        using var document = JsonDocument.Parse(json);
        var kept = document.RootElement.EnumerateArray()
            .Where(sandbox =>
                (!ownedOnly || !(sandbox.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.True))
                && (type is null || string.Equals(ReadString(sandbox, "type"), type, StringComparison.OrdinalIgnoreCase)))
            .Select(sandbox => sandbox.GetRawText());
        return "[" + string.Join(',', kept) + "]";
    }

    private static string? ReadString(JsonElement sandbox, string name) =>
        sandbox.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void WriteSandbox(string json, OutputOptions output)
    {
        var sandbox = XboxPartnerCommandSupport.DeserializeResponse<XboxPartnerSandbox>(json, "sandbox");
        if (string.IsNullOrWhiteSpace(sandbox.SandboxId))
        {
            throw new InvalidDataException("The sandbox response did not contain a 'sandboxId'.");
        }

        if (output.TableView is { } view)
        {
            using var document = JsonDocument.Parse(json);
            Console.WriteLine(TableFormatter.Format(document.RootElement, view));
            return;
        }

        Console.WriteLine(Program.FormatOutput(json, output.IsCompact, dataOnly: false));
    }

    private static bool ConfirmCreation(
        ParseResult result, bool confirmed, XboxPartnerJobDependencies dependencies, string sandboxId,
        string sellerId, bool chosen)
    {
        if (confirmed)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"The Shared sandbox '{sandboxId}' under seller {sellerId} cannot be deleted after it is created.");
        if (!dependencies.IsInteractive())
        {
            // Running --free-number again could choose another number, so pin the one shown.
            Console.Error.WriteLine(chosen
                ? "Re-run with: " + XboxPartnerCommandSupport.BuildRecoveryCommand(
                    result, $"sandbox create {ShellQuoting.Quote(sandboxId)} --confirm", sellerId)
                : "Re-run with --confirm (--yes, -y) to proceed without an interactive prompt.");
            TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
            return false;
        }

        ConfirmationPrompt.Write(Console.Error, "Create this sandbox? [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Console.Error.WriteLine("Sandbox creation canceled.");
        TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
        return false;
    }
}
