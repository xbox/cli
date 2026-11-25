// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Presentation;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;

namespace XboxDevCLI.Commands;

internal static class PublishCommandFactory
{
    /// <summary>The write was sent, but whether it took effect is unknown.</summary>
    internal const int OutcomeUnknownExitCode = 3;

    /// <summary>Global sandboxes where a publish reaches certification or players.</summary>
    private static readonly HashSet<string> GlobalDestinations =
        new(["RETAIL", "CERT", "CERT.DEBUG"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Exit code when the publish job finished with a status other than <c>Success</c>.</summary>
    internal const int PublishFailedExitCode = 2;

    /// <summary>Exit code when the publish started, but the wait ended before it finished.</summary>
    internal const int PublishNotFinishedExitCode = 4;

    private const string RunningStatus = "Running";
    private const string SuccessStatus = "Success";

    public static Command Create(
        PublishConfirmationDependencies? confirmationDependencies = null,
        XboxPartnerJobDependencies? jobDependencies = null)
    {
        var dependencies = confirmationDependencies ?? PublishConfirmationDependencies.Default;
        var clock = jobDependencies ?? XboxPartnerJobDependencies.Default;
        var sellerOption = new XdtsSellerOption();
        var scope = new XboxLiveConfigurationScope(
            "The destination sandbox ID whose published configuration will be updated.", account: true);
        var sourceSandboxOption = CreateOptionalSandboxOption(
            "--source-sandbox-id",
            "The source sandbox ID. Default: --sandbox-id.");
        var validateOnlyOption = new Option<bool>("--validate-only")
        {
            Description = "Validate without publishing. Default: false, which starts publishing.",
        };
        var configSetOption = new Option<ulong?>("--configset")
        {
            Description =
                "The source sandbox's working version to publish. Default: its latest working version.",
        };
        configSetOption.Validators.Add(result =>
        {
            // Read the token rather than the value, which throws when it didn't parse; the
            // parser reports that itself.
            if (result.Tokens.Count > 0 &&
                ulong.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value) &&
                value == 0)
            {
                result.AddError("--configset must be positive when provided.");
            }
        });
        var republishOption = new Option<bool>("--republish")
        {
            Description =
                "Publish in full even when nothing changed since the last publish. On a RETAIL locked " +
                "after a migration, forces the selected configuration, by default RETAIL's latest working one, " +
                "onto RETAIL. Default: false, which publishes only changes.",
        };
        var confirmOption = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description =
                "Confirm publishing to RETAIL, CERT, or CERT.DEBUG, the title-wide or account configuration, or " +
                "from another sandbox, without prompting.",
        };
        var allowSharedOption = XboxLiveConfigurationTarget.CreateAllowSharedConfigurationOption();
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Return as soon as the publish starts, without waiting for it to finish.",
        };
        var timeoutOption = TestAccountCommandFactory.CreateTimeoutOption("the publish to finish");

        var command = new Command(
            "publish",
            "Publish XBOX services working configuration to a sandbox, or the title-wide or account configuration, and wait for it to finish");
        command.Validators.Add(result =>
        {
            if (result.GetFlag(republishOption) && result.GetFlag(validateOnlyOption))
            {
                result.AddError("--republish publishes, so it can't be combined with --validate-only.");
            }

            bool timeoutGiven = result.GetResult(timeoutOption) is { Implicit: false };
            if (result.GetFlag(noWaitOption) && timeoutGiven)
            {
                result.AddError("--no-wait and --timeout cannot be combined.");
            }

            if (result.GetFlag(validateOnlyOption) && (result.GetFlag(noWaitOption) || timeoutGiven))
            {
                result.AddError("--validate-only starts no publish to wait for, so it can't be combined with --no-wait or --timeout.");
            }
        });
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: "One XBOX services configuration set promoted from a source sandbox to a destination sandbox, " +
                "the title-wide configuration, or the seller account's configuration.",
            Output: "The publish operation result as JSON or a table. Once the publish finishes, Status is " +
                "its final status, Success or Failed, and StatusMessage is the service's message about it. " +
                "With --no-wait, Status is Running. Nothing is written to stdout when the wait ends " +
                "before the publish finishes. Validation diagnostics from " +
                "XBOX services, such as warnings, go to stderr. A --validate-only to RETAIL also lists " +
                "the achievements the publish adds, on stderr and in achievementChanges. The publish job ID, " +
                "progress, and how to check a publish still running also go to stderr. With --scope account, " +
                "the result has status, jobId, sourceVersion, previousPublishedVersion, and validation. Once the " +
                "account's published version passes previousPublishedVersion, it adds publishedVersion; status " +
                "stays Running, because XBOX services don't report how an account publish ends.",
            Examples:
            [
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1",
                "xbox xbl config publish --title-id 1234ABCD --sandbox-id XDKS.1",
                "xbox xbl config publish --title-id 1234ABCD --scope title --validate-only",
                "xbox xbl config publish --scope account --seller-id 1234567 --validate-only",
                "xbox xbl config publish --scope account --seller-id 1234567 --confirm",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --no-wait",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --timeout 20m",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --validate-only",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --confirm",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id XDKS.1 --sandbox-id TESTSB.42 --validate-only --output table",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --source-sandbox-id STAGE.0 --sandbox-id RETAIL --configset 42 --confirm",
                "xbox xbl config publish --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --republish",
            ],
            LearnMore:
            [
                "Status AlreadyPublished means nothing changed since the last publish, so no publish " +
                "started. Except with --scope account, --republish publishes again.",
                "Stats system, privilege, and single sign-on changes in a --scope title publish go live in each " +
                "sandbox only when that sandbox publishes. Once the publish starts, stderr lists the publish to run " +
                "for each published sandbox that doesn't have them yet.",
                "Waits for the publish job by checking the sandbox's last publish, as config status reports it, " +
                "every 10 seconds, until the job finishes or --timeout passes. Ctrl+C stops waiting; the " +
                "publish continues on the service.",
                "Publishing with --scope account needs Partner Center's Relying parties Write or Business partners " +
                "Write permission, and --validate-only also accepts Relying parties Read or Business partners Read. " +
                "A refusal names the permissions that allow the request. XBOX services don't " +
                "report how an account publish ends, so its wait checks the account's published version, as " +
                "config status --scope account reports it, until it passes previousPublishedVersion, and exits 0. " +
                "Any publish of the account raises it, so that means this publish or another one committed; the " +
                "draft is live when config status shows hasChanges false. A failed account publish leaves the " +
                "version unchanged, so the wait ends with exit 4. Running an account publish again is safe: it " +
                "publishes only what isn't live yet.",
                "Exit codes: 0 published, validated, nothing to publish, or started with --no-wait; 1 not " +
                "published; 2 the publish job failed; 3 the publish was sent, but its outcome is unknown; 4 the " +
                "publish started, but waiting ended before it finished, or another publish started after it. On 3 " +
                "or 4, check it with the config status command on stderr before publishing again.",
            ],
            SafetyDetails:
                "Without --validate-only, starts publishing configuration to the destination sandbox. " +
                "Publishing from a different --source-sandbox-id also replaces the destination's global " +
                "title storage files with the source's and deletes files only the destination holds. " +
                "Publishing to RETAIL, CERT, or CERT.DEBUG, the title-wide configuration (--scope title or --sandbox-id HIDDEN), " +
                "the seller account's configuration (--scope account), or from " +
                "a different --source-sandbox-id, requires --confirm (--yes, -y) or terminal confirmation. " +
                "Before asking at a terminal, a RETAIL publish is validated and the achievements it adds " +
                "are listed; the publish then sends the same working version. " +
                "Every title bound to a primary title shares its configuration. A --title-id that selects a " +
                "bound title is refused, even with --validate-only; rerun with the primary's --scid. A publish by " +
                "--title-id, validation included, of configuration other titles are bound to lists them and " +
                "asks before changing it; without a terminal it's refused unless you pass " +
                "--allow-shared-configuration. A RETAIL publish that asks first is refused without the list " +
                "unless you pass --allow-shared-configuration. A publish by --scid isn't checked. " +
                "Agent approval is still required.",
            Table: XboxPartnerCommandSupport.PublishTableView));
        sellerOption.AddTo(command);
        scope.AddTo(command);
        scope.RejectWithTitleScope(command, sourceSandboxOption, configSetOption);
        scope.RejectWithAccountScope(command, sourceSandboxOption, configSetOption, republishOption, allowSharedOption);
        command.Options.Add(sourceSandboxOption);
        command.Options.Add(validateOnlyOption);
        command.Options.Add(configSetOption);
        command.Options.Add(republishOption);
        command.Options.Add(confirmOption);
        command.Options.Add(allowSharedOption);
        command.Options.Add(noWaitOption);
        command.Options.Add(timeoutOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            try
            {
                var configuration = Program.GetConfiguration(parseResult);
                using var client = Program.XboxPartnerClientFactory is null
                    ? new XboxPartnerClient(
                        configuration,
                        authenticationService: Program.GetAuthentication(parseResult))
                    : Program.XboxPartnerClientFactory(configuration);
                _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
                long? sellerId = sellerOption.GetSellerId(parseResult);
                var selected = await scope.SelectAsync(parseResult, sellerId);
                bool confirmed = parseResult.GetResult(confirmOption) is { Implicit: false }
                    ? parseResult.GetValue(confirmOption)
                    : parseResult.GetValue(Program.ConfirmOption);
                PublishFollowUp? follow = parseResult.GetValue(validateOnlyOption)
                    ? null
                    : new PublishFollowUp(
                        parseResult.GetValue(noWaitOption)
                            ? null
                            : TestAccountCommandFactory.ReadTimeout(parseResult, timeoutOption),
                        clock);
                if (selected.Scope == XboxLiveScope.Account)
                {
                    bool validateOnly = parseResult.GetValue(validateOnlyOption);
                    Program.WriteVerbose(parseResult.GetValue(Program.VerboseOption), () =>
                        $"{(validateOnly ? "Validate" : "Publish")} the account configuration of " +
                        new XboxLiveConfigurationTarget.SelectedTarget(sellerId, null, null).Describe());
                    return await AccountPublish.RunAsync(
                        client,
                        sellerId,
                        validateOnly,
                        confirmed,
                        dependencies,
                        output,
                        Console.Out,
                        Console.Error,
                        string.Join(' ', RecoveryCommandArguments.Create(parseResult)),
                        follow,
                        cancellationToken).ConfigureAwait(false);
                }

                string sandbox = selected.LiveSandbox!;
                var request = new XboxLivePublishRequest(
                    selected.Scid,
                    sandbox,
                    parseResult.GetValue(sourceSandboxOption)?.Trim() ?? sandbox,
                    parseResult.GetValue(validateOnlyOption),
                    parseResult.GetValue(configSetOption),
                    parseResult.GetValue(republishOption),
                    selected.TitleId,
                    parseResult.GetValue(allowSharedOption));
                bool proceed;
                try
                {
                    // The service lists the achievements a RETAIL publish adds only when validating, so the
                    // prompt validates first and then pins the publish to the working version it checked.
                    if (IsRetail(sandbox) && Prompts(request, confirmed) && dependencies.IsInteractive())
                    {
                        XboxLivePublishRequest validation = request with { ValidateOnly = true, Republish = false };
                        XboxLivePublishResponse result;
                        XboxLiveResolvedConfigurationTarget? resolved = null;
                        try
                        {
                            using (RequestProgress.Start(DescribePublish(validation), Console.Error))
                            {
                                result = (await client.PublishXboxLiveConfigurationAsync(
                                    selected.SellerId, validation, cancellationToken,
                                    target => resolved = target).ConfigureAwait(false)).Value;
                            }
                        }
                        finally
                        {
                            if (resolved is not null)
                            {
                                XboxLiveConfigurationTarget.WriteResolved(resolved, selected.SellerId, Console.Error);
                            }
                        }

                        WriteDiagnostics(result, Console.Error);
                        Console.Error.WriteLine(FormatAchievementChanges(result, validateOnly: false));
                        if (FormatSourceWarning(request, result) is { } sourceWarning)
                        {
                            Console.Error.WriteLine(sourceWarning);
                        }

                        request = request with { ConfigSetVersion = request.ConfigSetVersion ?? result.SourceVersionNumber };
                        if (request.ConfigSetVersion is null)
                        {
                            Console.Error.WriteLine(
                                "Nothing was published. The validation didn't say which working version it checked, so " +
                                "the publish can't be limited to what you were shown. Pass --configset, or try again.");
                            return 1;
                        }
                    }

                    proceed = await ConfirmAsync(
                        request, confirmed, dependencies, Console.Error, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Publish canceled.");
                    return 1;
                }

                if (!proceed)
                {
                    return 1;
                }

                cancellationToken.ThrowIfCancellationRequested();
                Program.WriteVerbose(parseResult.GetValue(Program.VerboseOption), () =>
                    $"{(request.ValidateOnly ? "Validate" : request.Republish ? "Republish" : "Publish")} " +
                        $"{new XboxLiveConfigurationTarget.SelectedTarget(selected.SellerId, selected.Scid, selected.TitleId).Describe()} " +
                        (IsTitleWide(request)
                            ? "title-wide configuration"
                            : $"from {request.SourceSandbox} " +
                                $"(working version {request.ConfigSetVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "latest"}) " +
                                $"to {request.Sandbox}"));
                return await RunAsync(
                    client,
                    selected.SellerId,
                    request,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    writeGlobalStorageWarning: !Prompts(request, confirmed),
                    sharedConfirmation: dependencies,
                    recoveryPrefix: string.Join(' ', RecoveryCommandArguments.Create(parseResult)),
                    follow: follow).ConfigureAwait(false);
            }
            catch (XboxPartnerApiException ex)
            {
                Console.Error.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, output.IsCompact));
                return 1;
            }
            catch (XboxPartnerResponseException ex)
            {
                XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, Console.Error);
                return XboxPartnerCommandSupport.WriteLocalError(ex);
            }
            catch (Exception ex) when (!XboxPartnerCommandSupport.IsCallerCancellation(ex, cancellationToken))
            {
                XboxPartnerCommandSupport.WriteCorrelationId(ex, Console.Error);
                Console.Error.WriteLine($"Request failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Sends the publish and writes its result. <paramref name="writeGlobalStorageWarning"/> is false when the
    /// confirmation prompt already showed the global title storage warning. When the service refuses to change
    /// configuration other titles share, <paramref name="sharedConfirmation"/> asks whether to resend with the opt-in.
    /// When <paramref name="follow"/> is set and the publish starts a job, says how to check the job, and waits
    /// for it unless <see cref="PublishFollowUp.Timeout"/> is null.
    /// </summary>
    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLivePublishRequest request,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        bool writeGlobalStorageWarning = true,
        PublishConfirmationDependencies? sharedConfirmation = null,
        string? recoveryPrefix = null,
        PublishFollowUp? follow = null)
    {
        bool compact = output.IsCompact;
        TableView? tableView = output.TableView;
        string? globalStorageWarning = writeGlobalStorageWarning ? FormatGlobalStorageWarning(request) : null;
        if (globalStorageWarning is not null)
        {
            stderr.WriteLine(globalStorageWarning);
        }

        var sent = false;
        string? sentCorrelationId = null;
        var answered = false;
        XboxLiveResolvedConfigurationTarget? resolved = null;

        // The config status command that shows the destination's last publish.
        string CheckCommand() =>
            $"{recoveryPrefix ?? CliIdentity.CommandName} xbl config status " +
            XboxPartnerCommandSupport.FormatConfigurationTarget(resolved, request.Scid, request.TitleId, sellerId) +
            (IsTitleWide(request) ? " --scope title" : $" --sandbox-id {ShellQuoting.Quote(request.Sandbox)}");

        try
        {
            XboxPartnerOperationResult<XboxLivePublishResponse> response;
            try
            {
                response = await SendAsync(request).ConfigureAwait(false);
            }
            catch (XboxPartnerApiException ex) when (
                !request.AllowSharedConfiguration &&
                XboxLiveConfigurationTarget.ReadSharedConfiguration(ex) is not null)
            {
                // The service refused the first send, so nothing started.
                sent = false;
                if (!await XboxLiveConfigurationTarget.ConfirmSharedConfigurationAsync(
                    XboxLiveConfigurationTarget.ReadSharedConfiguration(ex)!, ex, compact, request.ValidateOnly,
                    sharedConfirmation, stderr,
                    cancellationToken).ConfigureAwait(false))
                {
                    return 1;
                }

                request = request with { AllowSharedConfiguration = true };
                response = await SendAsync(request).ConfigureAwait(false);
            }

            async Task<XboxPartnerOperationResult<XboxLivePublishResponse>> SendAsync(XboxLivePublishRequest body)
            {
                resolved = null;
                try
                {
                    using (RequestProgress.Start(DescribePublish(body), stderr))
                    {
                        return await client.PublishXboxLiveConfigurationAsync(
                            sellerId, body, cancellationToken,
                            target => resolved = target,
                            onSending: correlationId =>
                            {
                                sent = true;
                                sentCorrelationId = correlationId;
                            })
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (resolved is not null)
                    {
                        XboxLiveConfigurationTarget.WriteResolved(resolved, sellerId, stderr);
                    }
                }
            }

            answered = true;
            XboxLivePublishResponse result = response.Value;
            WriteDiagnostics(result, stderr);
            if (request.ValidateOnly && IsRetail(request.Sandbox))
            {
                stderr.WriteLine(FormatAchievementChanges(result, validateOnly: true));
            }

            if (string.Equals(result.Status, AlreadyPublishedStatus, StringComparison.OrdinalIgnoreCase))
            {
                stderr.WriteLine(
                    (IsTitleWide(request)
                        ? "Nothing changed in the title-wide configuration since its last publish, "
                        : $"Nothing changed in {request.SourceSandbox} since its last publish to {request.Sandbox}, ") +
                    "so no publish started. Use --republish to publish it again.");
            }

            if (FormatSourceWarning(request, result) is { } warning)
            {
                stderr.WriteLine(warning);
            }

            string body = response.Body;
            string status = result.Status!;
            int exitCode = 0;
            if (follow is not null &&
                string.Equals(result.Status, RunningStatus, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.JobId))
            {
                // Pin the status reads to the configuration the publish resolved, as config status does.
                Guid? scid = resolved?.Scid ?? request.Scid;
                long? statusSellerId = sellerId ?? resolved?.SellerId;
                string? titleId = resolved is null ? request.TitleId : null;
                string check = CheckCommand();
                string job = XboxPartnerCommandSupport.ForTerminal(result.JobId);
                stderr.WriteLine($"Publish job {job} started.");
                if (follow.Timeout is not { } timeout)
                {
                    stderr.WriteLine($"Publishing continues on the service. Check it with: {check}");
                }
                else
                {
                    ConfigStatusCommandFactory.PublishStatus? finished = await WaitForPublishAsync(
                        client, statusSellerId, scid, titleId, request, result.JobId, timeout, follow.Dependencies,
                        check, stderr, cancellationToken).ConfigureAwait(false);
                    if (finished is null)
                    {
                        await WriteTitleWideHintAsync().ConfigureAwait(false);
                        return PublishNotFinishedExitCode;
                    }

                    status = finished.Status ?? "unknown";
                    body = WithFinalStatus(body, finished);
                    string message = finished.StatusMessage is { Length: > 0 } text
                        ? $": {XboxPartnerCommandSupport.ForTerminal(text)}"
                        : ".";
                    if (string.Equals(finished.Status, SuccessStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        stderr.WriteLine(IsTitleWide(request)
                            ? "Published the title-wide configuration."
                            : $"Published to {request.Sandbox}.");
                    }
                    else
                    {
                        stderr.WriteLine(
                            $"Publish job {job} ended as {XboxPartnerCommandSupport.ForTerminal(status)}{message}");
                        exitCode = PublishFailedExitCode;
                    }
                }
            }

            stdout.WriteLine(tableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    tableView,
                    [
                        [
                            status,
                            request.SourceSandbox,
                            request.Sandbox,
                            request.ValidateOnly.ToString(),
                            result.SourceVersionText ??
                                request.ConfigSetVersion?.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture) ??
                                "latest",
                        ],
                    ])
                : PlayFabApiClient.FormatJson(body, compact));
            if (exitCode == 0)
            {
                await WriteTitleWideHintAsync().ConfigureAwait(false);
            }

            return exitCode;

            // A title-wide publish that started says which sandboxes still need their own publish.
            async Task WriteTitleWideHintAsync()
            {
                if (IsTitleWide(request) && !request.ValidateOnly &&
                    !string.Equals(result.Status, AlreadyPublishedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    await TitleWidePublishHint.WriteAsync(
                        client, sellerId, request, resolved, recoveryPrefix, stderr, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!request.ValidateOnly && !sent && cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine(
                $"Canceled before the request was sent, so {NamePublish(request)} didn't start.");
            return 1;
        }
        catch (Exception ex) when (!request.ValidateOnly && sent && !answered
            && XboxPartnerCommandSupport.IsUncertainSubmission(ex))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(ex, compact, stderr, cancellationToken, sentCorrelationId);
            stderr.WriteLine(
                $"It is unknown whether {NamePublish(request)} started. " +
                "Don't resubmit it.");
            stderr.WriteLine($"Check with: {CheckCommand()}");
            return OutcomeUnknownExitCode;
        }
        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(ex, compact));
            return 1;
        }
        catch (XboxPartnerResponseException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (!XboxPartnerCommandSupport.IsCallerCancellation(ex, cancellationToken))
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex, stderr);
            stderr.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    internal static string DescribePublish(XboxLivePublishRequest request)
    {
        if (IsTitleWide(request))
        {
            return request.ValidateOnly ? "Validating the title-wide publish"
                : request.Republish ? "Republishing the title-wide configuration"
                : "Publishing the title-wide configuration";
        }

        string verb = request.ValidateOnly ? "Validating publish"
            : request.Republish ? "Republishing"
            : "Publishing";
        return string.Equals(request.SourceSandbox, request.Sandbox, StringComparison.OrdinalIgnoreCase)
            ? $"{verb} to {request.Sandbox}"
            : $"{verb} from {request.SourceSandbox} to {request.Sandbox}";
    }

    /// <summary>Whether <paramref name="request"/> publishes the title-wide configuration, which goes live in HIDDEN.</summary>
    internal static bool IsTitleWide(XboxLivePublishRequest request) =>
        string.Equals(request.Sandbox, XboxLiveConfigurationScope.TitleLiveSandbox, StringComparison.OrdinalIgnoreCase);

    private static string NamePublish(XboxLivePublishRequest request) =>
        IsTitleWide(request) ? "the title-wide publish" : $"the publish to {TerminalText.SingleLine(request.Sandbox)}";

    /// <summary>
    /// Reads the destination's last publish, as config status does, until publish job <paramref name="jobId"/>
    /// finishes or <paramref name="timeout"/> passes. Returns the finished status, or null after saying on stderr
    /// why waiting ended and how to check the job. A read naming another job ends the wait, because this job
    /// finished but its outcome can't be read. A read with no job ID and status Success is this job's success,
    /// because a job that succeeds is cleared. <see cref="XboxPartnerPoller"/> does the waiting.
    /// </summary>
    private static async Task<ConfigStatusCommandFactory.PublishStatus?> WaitForPublishAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid? scid,
        string? titleId,
        XboxLivePublishRequest request,
        string jobId,
        TimeSpan timeout,
        XboxPartnerJobDependencies dependencies,
        string check,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string stillRunning = $"Publishing continues on the service. Check it with: {check}";
        string job = XboxPartnerCommandSupport.ForTerminal(jobId);
        stderr.WriteLine($"Waiting up to {TestAccountCommandFactory.FormatDuration(timeout)} for it to finish...");
        string? otherJob = null;
        ConfigStatusCommandFactory.PublishStatus? finished = null;
        try
        {
            using (RequestProgress.Start(DescribePublish(request), stderr))
            {
                await XboxPartnerPoller.PollAsync(
                    async token => XboxPartnerCommandSupport.DeserializeResponse<ConfigStatusCommandFactory.PublishStatus>(
                        await ReadStatusAsync(() => client.GetXboxLivePublishStatusAsync(
                            sellerId, scid, request.Sandbox, token, titleId)).ConfigureAwait(false),
                        "publish status"),
                    status =>
                    {
                        // The started job's ID is recorded before the publish answers, and no other publish can
                        // start while it runs. So a read naming another job means this one finished and another
                        // started, and this one's outcome can no longer be read.
                        if (!string.IsNullOrWhiteSpace(status.JobId) &&
                            !string.Equals(status.JobId, jobId, StringComparison.OrdinalIgnoreCase))
                        {
                            otherJob = status.JobId;
                            return true;
                        }

                        // A job that succeeds is cleared from the sandbox, so its success reads back with no job
                        // ID. A failed job keeps its ID.
                        bool ended = string.IsNullOrWhiteSpace(status.JobId)
                            ? string.Equals(status.Status, SuccessStatus, StringComparison.OrdinalIgnoreCase)
                            : !string.Equals(status.Status, RunningStatus, StringComparison.OrdinalIgnoreCase);
                        finished = ended ? status with { JobId = jobId } : null;
                        return ended;
                    },
                    retryAfter => retryAfter is { } after
                        ? TestAccountJobWaiter.Clamp(after)
                        : TestAccountJobWaiter.DefaultInterval,
                    timeout,
                    dependencies.DelayAsync,
                    dependencies.GetUtcNow,
                    cancellationToken).ConfigureAwait(false);
            }

            if (finished is not null)
            {
                return finished;
            }

            stderr.WriteLine(otherJob is not null
                ? $"Publish job {job} finished, and another publish, job " +
                    $"{XboxPartnerCommandSupport.ForTerminal(otherJob)}, started on " +
                    $"{(IsTitleWide(request) ? "the title-wide configuration" : request.Sandbox)}, so " +
                    $"how publish job {job} ended can't be read. Check the latest publish with: {check}"
                : $"Publish job {job} didn't finish within {TestAccountCommandFactory.FormatDuration(timeout)}. {stillRunning}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine($"Stopped waiting. {stillRunning}");
        }
        catch (XboxPartnerApiException exception)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(exception, compact: false));
            stderr.WriteLine($"Could not read publish job {job}. {stillRunning}");
        }
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            if (exception is XboxPartnerResponseException { CorrelationId: var correlationId })
            {
                XboxPartnerCommandSupport.WriteCorrelationId(correlationId, stderr);
            }
            else
            {
                XboxPartnerCommandSupport.WriteCorrelationId(exception, stderr);
            }

            stderr.WriteLine($"Request failed: {exception.Message}");
            stderr.WriteLine($"Could not read publish job {job}. {stillRunning}");
        }

        return null;
    }

    /// <summary>
    /// Runs one read for a wait. A read wraps a dropped connection or timeout, and rethrowing the cause lets
    /// the poller retry it; an unreadable response stays a failure.
    /// </summary>
    internal static async Task<string> ReadStatusAsync(Func<Task<string>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (XboxPartnerResponseException exception) when (exception.InnerException is
            HttpRequestException or IOException or OperationCanceledException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
    }

    /// <summary>Replaces the publish response's <c>Status</c> and <c>StatusMessage</c> with the finished job's.</summary>
    private static string WithFinalStatus(string body, ConfigStatusCommandFactory.PublishStatus finished)
    {
        JsonObject response = JsonNode.Parse(body)!.AsObject();
        response["Status"] = finished.Status;
        response["StatusMessage"] = finished.StatusMessage;
        return response.ToJsonString();
    }

    // A cross-sandbox publish also copies global title storage, mirroring the source's
    // global title storage files into the destination. XBOX services compare
    // sandbox IDs ignoring case, so a case-only difference publishes in place and copies nothing.
    internal static string? FormatGlobalStorageWarning(XboxLivePublishRequest request) =>
        string.Equals(request.SourceSandbox, request.Sandbox, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Warning: publishing from {request.SourceSandbox} to {request.Sandbox} replaces " +
                $"{request.Sandbox}'s global title storage files with {request.SourceSandbox}'s. " +
                $"Files with the same path are overwritten, files only {request.SourceSandbox} " +
                $"holds are added, and files only {request.Sandbox} holds are deleted. " +
                "Deleted files can't be recovered.";

    /// <summary>
    /// Says when the source's latest working version, which a publish without --configset uses, isn't
    /// what's live in the source, and names the working version that is.
    /// </summary>
    internal static string? FormatSourceWarning(
        XboxLivePublishRequest request,
        XboxLivePublishResponse result)
    {
        if (result.SourceHasUnpublishedChanges != true)
        {
            return null;
        }

        string action = request.ValidateOnly ? "validation" : "publish";
        string latest = result.SourceVersionText is { } version
            ? $"its latest working version, {XboxPartnerCommandSupport.ForTerminal(version)}"
            : "its latest working version";
        if (result.SourceLiveVersion is not ulong live)
        {
            return $"Warning: {request.SourceSandbox} has unpublished changes. This {action} uses " +
                $"{latest}, which isn't what's live in {request.SourceSandbox}.";
        }

        string liveText = live.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"Warning: {request.SourceSandbox} has unpublished changes. This {action} uses " +
            $"{latest}; what's live in {request.SourceSandbox} is working version {liveText}. " +
            $"To {(request.ValidateOnly ? "validate" : "publish")} that instead, add --configset {liveText}.";
    }

    /// <summary>The status XboxPartner reports when nothing changed since the last publish.</summary>
    internal const string AlreadyPublishedStatus = "AlreadyPublished";

    private static bool IsRetail(string sandbox) =>
        string.Equals(sandbox, "RETAIL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Says which achievements a publish to RETAIL adds, from a validation's response. Once they're live,
    /// achievements can't be removed and their unlock rules and gamerscore can't change, so this is what
    /// to check before publishing.
    /// </summary>
    internal static string FormatAchievementChanges(XboxLivePublishResponse result, bool validateOnly)
    {
        if (result.AchievementChanges?.Added is not { } added)
        {
            return "The service didn't report which achievements this publish adds to RETAIL.";
        }

        string[] ids = added
            .Where(id => id is not null)
            .Select(id => XboxPartnerCommandSupport.ForTerminal(id))
            .ToArray();
        string lead = validateOnly ? "This publish would add" : "This publish adds";
        if (ids.Length == 0)
        {
            return $"{lead} no achievements to RETAIL.";
        }

        return $"{lead} {ids.Length} achievement{(ids.Length == 1 ? "" : "s")} to RETAIL: {string.Join(", ", ids)}. " +
            "Once they're live in RETAIL, achievements can't be removed, and their unlock rules and gamerscore can't change.";
    }

    /// <summary>
    /// Returns why publishing <paramref name="request"/> needs confirmation, one sentence per reason, or
    /// nothing when it doesn't. A validation never does.
    /// </summary>
    internal static IReadOnlyList<string> DescribeHighImpact(XboxLivePublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ValidateOnly)
        {
            return [];
        }

        var reasons = new List<string>();
        if (string.Equals(request.Sandbox, "RETAIL", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("RETAIL is live: players see the change when the publish finishes.");
        }
        else if (GlobalDestinations.Contains(request.Sandbox))
        {
            reasons.Add($"{request.Sandbox} is the sandbox XBOX certification tests in.");
        }
        else if (IsTitleWide(request))
        {
            reasons.Add(
                "The title-wide configuration applies to every sandbox, RETAIL included.");
        }

        if (!string.Equals(request.SourceSandbox, request.Sandbox, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(
                $"Publishing from {request.SourceSandbox} first replaces {request.Sandbox}'s working " +
                "documents that have the same names.");
        }

        return reasons;
    }

    /// <summary>
    /// Whether <see cref="ConfirmAsync"/> describes <paramref name="request"/> and asks, which it does for a
    /// publish <see cref="DescribeHighImpact"/> flags unless it's already <paramref name="confirmed"/>.
    /// </summary>
    internal static bool Prompts(XboxLivePublishRequest request, bool confirmed) =>
        !confirmed && DescribeHighImpact(request).Count > 0;

    /// <summary>
    /// Asks before a publish that <see cref="DescribeHighImpact"/> flags. Returns whether to go ahead:
    /// always when nothing is flagged or <paramref name="confirmed"/>, otherwise only when the user says
    /// yes at a terminal. Without a terminal it refuses. Cancellation ends a wait for the answer.
    /// </summary>
    internal static async Task<bool> ConfirmAsync(
        XboxLivePublishRequest request,
        bool confirmed,
        PublishConfirmationDependencies dependencies,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> reasons = DescribeHighImpact(request);
        if (!Prompts(request, confirmed))
        {
            return true;
        }

        string? version = request.ConfigSetVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string target = request.Scid is { } scid ? $"SCID {scid:D}" : $"title {request.TitleId}";
        stderr.WriteLine(IsTitleWide(request)
            ? $"This publishes the latest title-wide draft for {target}."
            : $"This publishes {(version is null
                ? $"{request.SourceSandbox}'s latest working configuration"
                : $"working version {version} of {request.SourceSandbox}'s configuration")} " +
                $"to {request.Sandbox} for {target}.");
        foreach (string reason in reasons)
        {
            stderr.WriteLine(reason);
        }

        if (request.Republish)
        {
            stderr.WriteLine(
                "--republish publishes in full even if nothing changed, and forces the selected configuration, by default RETAIL's latest working one, onto a locked RETAIL.");
        }

        // Shown here, before the answer, so RunAsync doesn't repeat it after a prompt.
        if (FormatGlobalStorageWarning(request) is string globalStorageWarning)
        {
            stderr.WriteLine(globalStorageWarning);
        }

        return await AskAsync(
            IsTitleWide(request) ? "Publish the title-wide configuration? [y/N]: " : $"Publish to {request.Sandbox}? [y/N]: ",
            dependencies,
            stderr,
            cancellationToken,
            IsRetail(request.Sandbox)
                ? "Run with --validate-only first to see which achievements this publish adds to RETAIL."
                : null).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks <paramref name="question"/> at a terminal, and returns whether the answer was yes. Without a
    /// terminal it refuses, after saying how to confirm, and <paramref name="withoutTerminal"/> when set.
    /// </summary>
    internal static async Task<bool> AskAsync(
        string question,
        PublishConfirmationDependencies dependencies,
        TextWriter stderr,
        CancellationToken cancellationToken,
        string? withoutTerminal = null)
    {
        if (!dependencies.IsInteractive())
        {
            stderr.WriteLine(
                "Re-run with --confirm (--yes, -y) to publish without an interactive prompt.");
            if (withoutTerminal is not null)
            {
                stderr.WriteLine(withoutTerminal);
            }

            TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
            return false;
        }

        ConfirmationPrompt.Write(stderr, question);
        string? answer = (await dependencies.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        stderr.WriteLine("Publish canceled.");
        TelemetryRecorder.RecordError(TelemetryErrorTypes.NotConfirmed);
        return false;
    }

    /// <summary>
    /// Writes the validation diagnostics from XBOX services, such as the warning for a first RETAIL
    /// publish without an embargo, to stderr. The JSON output keeps them too.
    /// </summary>
    private static void WriteDiagnostics(XboxLivePublishResponse result, TextWriter stderr)
    {
        if (result.AdditionalProperties is null)
        {
            return;
        }

        WriteDiagnostics(
            result.AdditionalProperties
                .FirstOrDefault(property => string.Equals(
                    property.Key,
                    "ValidationResult",
                    StringComparison.OrdinalIgnoreCase))
                .Value,
            stderr);
    }

    /// <summary>Writes each validation diagnostic in <paramref name="diagnostics"/>, a JSON array, to stderr.</summary>
    internal static void WriteDiagnostics(JsonElement diagnostics, TextWriter stderr)
    {
        if (diagnostics.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
        {
            if (diagnostic.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string severity = ReadString(diagnostic, "Severity") ?? "Diagnostic";
            string? error = ReadString(diagnostic, "Error");
            string? document = ReadString(diagnostic, "DocumentName");
            string? message = ReadString(diagnostic, "Message");
            var line = new System.Text.StringBuilder("XBOX services ")
                .Append(XboxPartnerCommandSupport.ForTerminal(severity).ToLowerInvariant());
            if (error is not null)
            {
                line.Append(" (").Append(XboxPartnerCommandSupport.ForTerminal(error)).Append(')');
            }

            if (document is not null)
            {
                line.Append(" in ").Append(XboxPartnerCommandSupport.ForTerminal(document));
            }

            if (message is not null)
            {
                line.Append(": ").Append(XboxPartnerCommandSupport.ForTerminal(message));
            }

            stderr.WriteLine(line.ToString());
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static Option<Guid> CreateRequiredGuidOption(
        string name,
        string description)
    {
        var option = new Option<Guid>(name)
        {
            Description = description,
            Required = true,
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                (!Guid.TryParse(result.Tokens[0].Value, out Guid value) ||
                 value == Guid.Empty))
            {
                result.AddError($"{name} must be a non-empty GUID.");
            }
        });
        return option;
    }

    private static Option<string?> CreateOptionalSandboxOption(
        string name,
        string description)
    {
        var option = new Option<string?>(name)
        {
            HelpName = "id",
            Description = description,
        };
        option.Validators.Add(XboxLiveConfigurationScope.ValidateSandbox);
        return option;
    }
}

/// <summary>The terminal seams <c>xbl config publish</c> asks for confirmation through.</summary>
internal sealed record PublishConfirmationDependencies(
    Func<bool> IsInteractive,
    Func<CancellationToken, Task<string?>> ReadLineAsync)
{
    internal static PublishConfirmationDependencies Default { get; } = new(
        XboxPartnerCommandSupport.IsInteractiveTerminal,
        FeedbackDependencies.CancelableReadLine(Console.ReadLine));
}

/// <summary>
/// What <c>xbl config publish</c> does once a publish starts: wait up to <paramref name="Timeout"/>, or, when
/// it's null, return at once.
/// </summary>
internal sealed record PublishFollowUp(TimeSpan? Timeout, XboxPartnerJobDependencies Dependencies);
