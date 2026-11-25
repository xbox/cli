// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl config status</c>: what's in one configuration scope's draft, what's published,
/// and the last publish.
/// </summary>
internal static class ConfigStatusCommandFactory
{
    internal static readonly JsonSerializerOptions OutputJson = new();

    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var scope = new XboxLiveConfigurationScope(
            "The case-sensitive sandbox ID. ALL and HIDDEN are the same as --scope title.",
            account: true);

        var command = new Command(
            "status",
            "Show what's in a configuration's draft, what's published, and the last publish");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX title's sandbox or title configuration, or the seller account's configuration.",
            Output:
                "One JSON object by default: each document's state (draft-only, published-only, " +
                "changed, or same) with its draft and published hashes; the draft and published versions " +
                "and who saved or published them; the last publish under lastPublish; and, for a " +
                "sandbox, what each save since the last publish changed. Table output shows one " +
                "row per change that isn't published yet, and writes the published and draft versions to stderr. " +
                "With --scope title, it lists every title document with where it goes live (publishesWith), and " +
                "compares only those a publish to HIDDEN makes live.",
            Examples:
            [
                "xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1",
                "xbox xbl config status --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --output table",
                "xbox xbl config status --title-id 1234ABCD --sandbox-id XDKS.1",
                "xbox xbl config status --scope title --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "xbox xbl config status --scope title --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --output table",
                "xbox xbl config status --scope account",
            ],
            LearnMore:
            [
                "A save changes the draft; config publish updates the published version with every document in the draft, and never removes a published document. Players and test accounts see only what's published.",
                "A sandbox's status also lists the title documents that go live with the sandbox's own publish: dataplatformconfig, titlepriv, and nsal. --scope title lists every title document and compares the rest, which go live through a publish to HIDDEN.",
                "config publish waits for the publish to finish by default. After --no-wait, or when its wait ends early, run config status until lastPublish.status settles. The account has no lastPublish: its draft is live when hasChanges is false.",
            ],
            Table: XboxPartnerCommandSupport.ConfigurationStatusTableView));
        sellerOption.AddTo(command);
        scope.AddTo(command);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            XboxLiveConfigurationScope.Selection selected = await scope.SelectAsync(parseResult, sellerId: null);
            XboxLiveScope selectedScope = selected.Scope;
            string? sandbox = selected.Sandbox;
            if (selectedScope == XboxLiveScope.Sandbox)
            {
                if (string.Equals(sandbox, XboxLiveConfigurationScope.TitleDraftSandbox, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(sandbox, XboxLiveConfigurationScope.TitleLiveSandbox, StringComparison.OrdinalIgnoreCase))
                {
                    // The title draft is kept in ALL and goes live through HIDDEN: both name the title scope.
                    selectedScope = XboxLiveScope.Title;
                    sandbox = null;
                }
                else if (XboxLiveConfigurationScopes.IsTitleSandbox(sandbox!))
                {
                    string title = selected.Scid is { } givenScid
                        ? $"--scid {givenScid:D}"
                        : $"--title-id {selected.TitleId}";
                    Console.Error.WriteLine(
                        $"{sandbox} holds the title's configuration, not one sandbox's. Run " +
                        $"'{CliIdentity.CommandName} xbl config status --scope title {title}'.");
                    return 1;
                }
            }

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

                Program.WriteVerbose(verbose, () => $"Get the {Describe(selectedScope, sandbox)} configuration status");
                return await RunAsync(
                    client,
                    sellerId,
                    selectedScope,
                    selected.Scid,
                    sandbox,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    selected.TitleId,
                    verbose).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsReadFailure(ex, cancellationToken))
            {
                return WriteFailure(ex, Console.Error, output.IsCompact, cancellationToken);
            }
        });

        return command;
    }

    /// <summary>
    /// Whether <see cref="WriteFailure"/> reports <paramref name="exception"/>: the service's refusal or
    /// unreadable response, a timeout, or another failure the error boundary expects. Ctrl+C and bugs
    /// reach the boundary instead.
    /// </summary>
    internal static bool IsReadFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && (exception is XboxPartnerApiException or XboxPartnerResponseException or OperationCanceledException
            || CommandErrorBoundary.IsExpected(exception));

    /// <summary>
    /// Reports a failed read on stderr and returns the exit code: a service error as its JSON, which
    /// carries the correlation ID, and a timeout or any other expected failure as one line, after the
    /// request's correlation ID when it has one. Ctrl+C isn't a failure
    /// to report: callers let it through.
    /// </summary>
    internal static int WriteFailure(Exception exception, TextWriter stderr, bool compact, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case XboxPartnerApiException api:
                stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(api, compact));
                break;
            case XboxPartnerResponseException { InnerException: OperationCanceledException } response:
                XboxPartnerCommandSupport.WriteCorrelationId(response.CorrelationId, stderr);
                stderr.WriteLine("Request timed out.");
                break;
            case XboxPartnerResponseException response:
                XboxPartnerCommandSupport.WriteCorrelationId(response.CorrelationId, stderr);
                stderr.WriteLine($"Request failed: {TerminalText.SingleLine(response.Message)}");
                break;
            case OperationCanceledException:
                XboxPartnerCommandSupport.WriteCorrelationId(exception, stderr);
                stderr.WriteLine("Request timed out.");
                break;
            default:
                XboxPartnerCommandSupport.WriteCorrelationId(exception, stderr);
                stderr.WriteLine($"Request failed: {TerminalText.SingleLine(exception.Message)}");
                break;
        }

        return 1;
    }

    /// <summary>
    /// Whether a description read failed in a way that only removes descriptions: the service
    /// refused or timed out, or its response couldn't be read. Ctrl+C and bugs aren't.
    /// </summary>
    private static bool IsDescriptionFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is XboxPartnerApiException or XboxPartnerResponseException or HttpRequestException
            or InvalidDataException or IOException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string DescribeFailure(Exception exception) =>
        exception is OperationCanceledException ? "The request timed out." : TerminalText.SingleLine(exception.Message);

    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLiveScope scope,
        Guid? scid,
        string? sandbox,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        string? titleId = null,
        bool verbose = false)
    {
        try
        {
            XboxLiveConfigurationStatus status;
            IReadOnlyList<string> notes;
            XboxLiveResolvedConfigurationTarget? resolved = null;
            using (RequestProgress.Start($"Getting the {Describe(scope, sandbox)} configuration status", stderr))
            {
                (status, notes) = await GetAsync(
                        client, sellerId, scope, scid, sandbox, cancellationToken, titleId, target => resolved = target)
                    .ConfigureAwait(false);
            }

            if (verbose && resolved is not null)
            {
                XboxLiveConfigurationTarget.WriteResolved(resolved, sellerId, stderr);
            }

            foreach (string note in notes)
            {
                stderr.WriteLine(note);
            }

            if (output.TableView is not null)
            {
                var table = XboxPartnerTableFormatter.FormatConfigurationStatus(status, output.TableView);
                foreach (string note in table.Notes)
                {
                    stderr.WriteLine(note);
                }

                stdout.WriteLine(table.Text);
            }
            else
            {
                stdout.WriteLine(PlayFabApiClient.FormatJson(
                    JsonSerializer.Serialize(status, OutputJson),
                    output.IsCompact));
            }

            return 0;
        }
        catch (Exception ex) when (IsReadFailure(ex, cancellationToken))
        {
            return WriteFailure(ex, stderr, output.IsCompact, cancellationToken);
        }
    }

    /// <summary>
    /// Reads a scope's status and, for a sandbox, describes its changes. Descriptions are best
    /// effort: when they can't be read, the status is still complete, and a note says why. The
    /// status is read first, since the history needs the SCID the service resolved a title ID to.
    /// </summary>
    internal static async Task<(XboxLiveConfigurationStatus Status, IReadOnlyList<string> Notes)> GetAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLiveScope scope,
        Guid? scid,
        string? sandbox,
        CancellationToken cancellationToken,
        string? titleId = null,
        Action<XboxLiveResolvedConfigurationTarget>? targetResolved = null,
        bool readLastPublish = true)
    {
        XboxLiveResolvedConfigurationTarget? resolved = null;
        string statusBody = await client.GetXboxLiveConfigurationStatusAsync(
            sellerId,
            scope,
            scid,
            scope == XboxLiveScope.Sandbox ? sandbox : null,
            cancellationToken,
            titleId,
            target =>
            {
                resolved = target;
                targetResolved?.Invoke(target);
            }).ConfigureAwait(false);
        // Pin the history read to the target the status read resolved, so a binding change between
        // the reads can't mix two configurations.
        scid = resolved?.Scid ?? scid;
        sellerId ??= resolved?.SellerId;
        titleId = scid is null ? titleId : null;

        XboxLiveConfigurationStatus status = ParseStatus(statusBody, scid);
        Task<XboxLiveLastPublish?> lastPublish = readLastPublish
            ? GetLastPublishAsync(client, sellerId, scope, scid, sandbox, cancellationToken)
            : Task.FromResult<XboxLiveLastPublish?>(null);
        if (scope != XboxLiveScope.Sandbox)
        {
            status = status with { LastPublish = await lastPublish.ConfigureAwait(false) };
            return (XboxLiveChangeDescriptions.WithoutDescriptions(status), []);
        }

        // The rest of a sandbox's reads don't depend on each other, so they run together. A failed
        // history read only removes descriptions; the others fail the status.
        Task<XboxLiveConfigurationVersion?> titleDraft = GetTitleDraftAsync(client, sellerId, scid, titleId, cancellationToken);
        Task<(XboxLiveConfigurationHistoryPage? Page, Exception? Failure)> history = ReadHistoryAsync();
        await Task.WhenAll(lastPublish, titleDraft, history).ConfigureAwait(false);
        status = status with { LastPublish = await lastPublish.ConfigureAwait(false), TitleDraft = await titleDraft.ConfigureAwait(false) };
        (XboxLiveConfigurationHistoryPage? page, Exception? failure) = await history.ConfigureAwait(false);
        if (page is null)
        {
            return (
                XboxLiveChangeDescriptions.WithoutDescriptions(status),
                status.HasChanges ? [$"Change descriptions are unavailable. {DescribeFailure(failure!)}"] : []);
        }

        return XboxLiveChangeDescriptions.Describe(status, page);

        async Task<(XboxLiveConfigurationHistoryPage?, Exception?)> ReadHistoryAsync()
        {
            try
            {
                return (ParseHistory(await client.GetXboxLiveConfigurationHistoryAsync(
                        sellerId,
                        scid!.Value,
                        sandbox!,
                        XboxLiveChangeDescriptions.HistoryPageSize,
                        cancellationToken)
                    .ConfigureAwait(false)), null);
            }
            catch (Exception ex) when (IsDescriptionFailure(ex, cancellationToken))
            {
                return (null, ex);
            }
        }
    }

    /// <summary>
    /// Reads the last publish of a sandbox, or of the title, which goes live through a publish to
    /// <c>HIDDEN</c>. Null for the account, and when nothing was published (HTTP 404).
    /// </summary>
    internal static async Task<XboxLiveLastPublish?> GetLastPublishAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLiveScope scope,
        Guid? scid,
        string? sandbox,
        CancellationToken cancellationToken)
    {
        if (scope == XboxLiveScope.Account)
        {
            return null;
        }

        string response;
        try
        {
            response = await client
                .GetXboxLivePublishStatusAsync(
                    sellerId,
                    scid,
                    scope == XboxLiveScope.Title ? TitleWidePublishSandbox : sandbox!,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (XboxPartnerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        PublishStatus publish = XboxPartnerCommandSupport.DeserializeResponse<PublishStatus>(response, "publish status");
        return new XboxLiveLastPublish
        {
            Status = NullIfBlank(publish.Status),
            JobId = NullIfBlank(publish.JobId),
            Message = NullIfBlank(publish.StatusMessage),
        };

        static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The sandbox a title-wide publish goes to.</summary>
    private const string TitleWidePublishSandbox = XboxLiveConfigurationScope.TitleLiveSandbox;

    /// <summary>The title draft's version, which a sandbox status shows beside the sandbox's own draft.</summary>
    private static async Task<XboxLiveConfigurationVersion?> GetTitleDraftAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid? scid,
        string? titleId,
        CancellationToken cancellationToken)
    {
        XboxLiveConfigurationGetResponse title = XboxPartnerCommandSupport.DeserializeResponse<XboxLiveConfigurationGetResponse>(
            await client.GetXboxLiveConfigurationStatusAsync(
                    sellerId, XboxLiveScope.Title, scid, null, cancellationToken, titleId)
                .ConfigureAwait(false),
            "configuration status");
        return title.WorkingVersion is null
            ? null
            : new XboxLiveConfigurationVersion { Version = title.WorkingVersion, Time = title.WorkingCommitTime };
    }

    internal sealed record PublishStatus(string? Status, string? JobId, string? StatusMessage);

    private static XboxLiveConfigurationHistoryPage ParseHistory(string response)
    {
        XboxLiveConfigurationHistoryPage history =
            XboxPartnerCommandSupport.DeserializeResponse<XboxLiveConfigurationHistoryPage>(
                response,
                "configuration history");
        if (history.Entries is null
            || history.Entries.Any(entry => entry is null || (entry.Documents?.Any(document => document is null) ?? false)))
        {
            throw new InvalidDataException("The configuration history response must contain an entries array of objects.");
        }

        return history;
    }

    /// <summary>
    /// Reads one configuration set's status from the service's answer. Who saved and published
    /// each version, the save's message, and the last publish are read afterward.
    /// <list type="bullet">
    /// <item>A sandbox's status compares its own documents, and the title documents that go live
    /// with its publish, which the service picks.</item>
    /// <item>The title's status lists every title document with where it goes live
    /// (<c>publishesWith</c>), those through <c>HIDDEN</c> first, each group by feature. It compares only those a publish to <c>HIDDEN</c> makes live. The
    /// rest go live with each sandbox's own publish, so comparing them with <c>HIDDEN</c> says
    /// nothing: their state is null, and they don't count toward <c>hasChanges</c>.</item>
    /// </list>
    /// </summary>
    internal static XboxLiveConfigurationStatus ParseStatus(string response, Guid? scid)
    {
        XboxLiveConfigurationGetResponse answer =
            XboxPartnerCommandSupport.DeserializeResponse<XboxLiveConfigurationGetResponse>(
                response,
                "configuration status").RequireNamedDocuments();

        bool titleScope = string.Equals(answer.Scope, XboxLiveConfigurationScopes.Title, StringComparison.OrdinalIgnoreCase);
        XboxLiveDocumentStatus[] documents =
        [
            .. answer.Documents!
                .Select(document => document! with { Feature = XboxLiveFeatureNames.NameOf(document.Name) })
                .Select(document => titleScope ? WherePublished(document) : document)
                .OfType<XboxLiveDocumentStatus>(),
        ];
        if (titleScope)
        {
            // Grouped by where each goes live, HIDDEN's first, and by feature within each group.
            documents =
            [
                .. documents
                    .OrderBy(document => document.PublishesWith == XboxLiveConfigurationScopes.Sandbox)
                    .ThenBy(document => document.Feature, StringComparer.OrdinalIgnoreCase),
            ];
        }

        return new XboxLiveConfigurationStatus
        {
            Scope = answer.Scope,
            Scid = string.Equals(answer.Scope, XboxLiveConfigurationScopes.Account, StringComparison.OrdinalIgnoreCase) ? null : scid,
            Sandbox = answer.Sandbox,
            Draft = answer.WorkingVersion is null
                ? null
                : new XboxLiveConfigurationVersion { Version = answer.WorkingVersion, Time = answer.WorkingCommitTime },
            Published = answer.PublishedVersion is null
                ? null
                : new XboxLiveConfigurationVersion { Version = answer.PublishedVersion, Time = answer.PublishedCommitTime },
            Documents = documents,
            // XBOX services publish only a draft that changed, so a published-only document isn't a change on its own.
            HasChanges = documents.Any(document =>
                string.Equals(document.State, XboxLiveDocumentStates.DraftOnly, StringComparison.OrdinalIgnoreCase)
                || string.Equals(document.State, XboxLiveDocumentStates.Changed, StringComparison.OrdinalIgnoreCase)),
        };

        // A title document's publish, and for one each sandbox publishes, no comparison with
        // HIDDEN. One that's only in HIDDEN's published copy isn't in the title draft, so it's left out.
        static XboxLiveDocumentStatus? WherePublished(XboxLiveDocumentStatus document) =>
            !XboxLiveConfigurationScopes.PublishesWithSandbox(document.Name)
                ? document with { PublishesWith = XboxLiveConfigurationScope.TitleLiveSandbox }
                : string.Equals(document.State, XboxLiveDocumentStates.PublishedOnly, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : document with { State = null, PublishedHash = null, PublishesWith = XboxLiveConfigurationScopes.Sandbox };
    }

    private static string Describe(XboxLiveScope scope, string? sandbox) => scope switch
    {
        XboxLiveScope.Sandbox => sandbox!,
        XboxLiveScope.Title => "title",
        _ => "account",
    };
}
