// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl config history</c>: a sandbox's saves, publishes, and copies, newest first,
/// with what each changed by feature, as XBOX services describe it.
/// </summary>
/// <remarks>
/// XBOX services record no history of the title configuration: they leave saves to <c>ALL</c> and
/// publishes to <c>HIDDEN</c> out of it. So the command reads a sandbox's history only.
/// </remarks>
internal static class ConfigHistoryCommandFactory
{
    /// <summary>Entries read per request: the most the service returns.</summary>
    internal const int PageSize = 100;

    private static readonly string[] Actions =
        [XboxLiveHistoryActions.Save, XboxLiveHistoryActions.Publish, XboxLiveHistoryActions.Copy];

    /// <summary>One row per feature an entry describes, or one row for an entry that describes none.</summary>
    internal static TableView HistoryTableView { get; } = new(
        Name(nameof(XboxLiveConfigurationHistory.Entries)),
        [
            new CommandLine.TableColumn("VERSION", Name(nameof(XboxLiveHistoryEntry.Version)), 12),
            new CommandLine.TableColumn("ACTION", Name(nameof(XboxLiveHistoryEntry.Action)), 7),
            new CommandLine.TableColumn("SANDBOX", Name(nameof(XboxLiveHistoryEntry.Sandbox)), 12),
            new CommandLine.TableColumn("TIME", Name(nameof(XboxLiveHistoryEntry.Time)), 21, Transform: FormatTime),
            new CommandLine.TableColumn(
                "AUTHOR",
                FieldPath(nameof(XboxLiveHistoryEntry.Author), nameof(XboxLiveAuthor.Name)),
                24),
            new CommandLine.TableColumn(
                "FEATURE",
                FieldPath(nameof(XboxLiveHistoryEntry.Documents), nameof(XboxLiveHistoryDocument.Feature)),
                32),
            new CommandLine.TableColumn(
                "CHANGE",
                FieldPath(nameof(XboxLiveHistoryEntry.Documents), nameof(XboxLiveHistoryDocument.Description)),
                60),
        ])
    {
        Expand = Name(nameof(XboxLiveHistoryEntry.Documents)),
    };

    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var scope = new XboxLiveConfigurationScope(
            "The case-sensitive sandbox ID. ALL, HIDDEN, and HISTORY hold the title's configuration, which has no history.",
            account: false);
        var actionOption = new Option<string?>("--action")
        {
            Description = "Show only saves, publishes, or copies. Default: all of them.",
        };
        actionOption.AcceptOnlyFromAmong(Actions);
        var featureOption = new Option<string[]>("--feature")
        {
            HelpName = "name",
            Description =
                "Show only changes to this feature: its name as the output shows it, a document name such as " +
                "achievements2017.xml, or a document type such as achievements2017. Repeat for more.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Return at most this many of the newest matching entries. Default: every entry.",
        };
        limitOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 &&
                int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                value <= 0)
            {
                result.AddError("--limit must be a positive number.");
            }
        });

        var command = new Command(
            "history",
            "Show a sandbox configuration's saves, publishes, and copies, newest first, and what each changed");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "One XBOX title's sandbox configuration. The title configuration has no history.",
            Output:
                "One JSON object by default: the SCID, sandbox, and entries, newest first. Each entry has its " +
                "action (save, publish, or copy), version, sandbox, time, author, the save's message, and documents: " +
                "what it changed in each feature XBOX services describe. Table output shows one row per feature in " +
                "each entry. " + ListProgress.HelpOutput,
            Examples:
            [
                "xbox xbl config history --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1",
                "xbox xbl config history --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --limit 10 --output table",
                "xbox xbl config history --title-id 1234ABCD --sandbox-id XDKS.1 --feature achievements2017",
                "xbox xbl config history --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --action publish",
            ],
            LearnMore:
            [
                "XBOX services keep no history of the title configuration. Run config status --scope title for its state.",
                "XBOX services describe only some features, such as achievements, leaderboards, stats, localization, presence, and multiplayer sessions, and add a save to the history shortly after it. A save that changed only other features has no documents.",
            ],
            Table: HistoryTableView));
        sellerOption.AddTo(command);
        scope.AddTo(command);
        command.Options.Add(actionOption);
        command.Options.Add(featureOption);
        command.Options.Add(limitOption);
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
            XboxLiveConfigurationScope.Selection selected = await scope.SelectAsync(parseResult, sellerId: null);
            if (selected.Scope == XboxLiveScope.Title || XboxLiveConfigurationScopes.IsTitleSandbox(selected.Sandbox!))
            {
                string title = selected.Scid is { } givenScid ? $"--scid {givenScid:D}" : $"--title-id {selected.TitleId}";
                throw new InvalidOperationException(
                    "XBOX services keep no history of the title configuration. For its state, run " +
                    $"'{CliIdentity.CommandName} xbl config status --scope title {title}'.");
            }

            var request = new ConfigHistoryRequest(
                selected.Scid,
                selected.Scid is null ? selected.TitleId : null,
                selected.Sandbox!,
                parseResult.GetValue(actionOption)?.ToLowerInvariant(),
                parseResult.GetValue(featureOption) ?? [],
                parseResult.GetValue(limitOption));
            var configuration = Program.GetConfiguration(parseResult);
            using var client = Program.XboxPartnerClientFactory is null
                ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(parseResult))
                : Program.XboxPartnerClientFactory(configuration);
            _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
            long? sellerId = sellerOption.GetSellerId(parseResult);
            Program.WriteVerbose(verbose, () => $"Get the {request.Sandbox} configuration history");
            return await RunAsync(client, sellerId, request, output, Console.Out, Console.Error, cancellationToken, verbose)
                .ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>
    /// The sandbox whose history to read, its title by <see cref="Scid"/> or <see cref="TitleId"/>,
    /// and which entries to keep.
    /// </summary>
    internal sealed record ConfigHistoryRequest(
        Guid? Scid,
        string? TitleId,
        string Sandbox,
        string? Action,
        IReadOnlyList<string> Features,
        int? Limit);

    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        ConfigHistoryRequest request,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        bool verbose = false)
    {
        XboxLiveConfigurationHistory history;
        XboxLiveResolvedConfigurationTarget? resolved = null;
        var notes = new List<string>();
        try
        {
            using var progress = ListProgress.Start($"Reading the {request.Sandbox} configuration history", stderr);
            history = await ReadAsync(client, sellerId, request, notes, progress.PageRead, target => resolved = target, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ConfigStatusCommandFactory.IsReadFailure(ex, cancellationToken))
        {
            return ConfigStatusCommandFactory.WriteFailure(ex, stderr, output.IsCompact, cancellationToken);
        }

        if (verbose && resolved is not null)
        {
            XboxLiveConfigurationTarget.WriteResolved(resolved, sellerId, stderr);
        }

        foreach (string note in notes)
        {
            stderr.WriteLine(note);
        }

        string json = JsonSerializer.Serialize(history, ConfigStatusCommandFactory.OutputJson);
        if (output.TableView is { } view)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            stdout.WriteLine(TableFormatter.Format(document.RootElement, view));
        }
        else
        {
            stdout.WriteLine(PlayFabApiClient.FormatJson(json, output.IsCompact));
        }

        return 0;
    }

    /// <summary>
    /// Reads the configuration's status, for its SCID and its draft's version, then pages through
    /// its history until <see cref="ConfigHistoryRequest.Limit"/> entries match or none are left.
    /// </summary>
    private static async Task<XboxLiveConfigurationHistory> ReadAsync(
        XboxPartnerClient client,
        long? sellerId,
        ConfigHistoryRequest request,
        List<string> notes,
        Action<int> pageRead,
        Action<XboxLiveResolvedConfigurationTarget> targetResolved,
        CancellationToken cancellationToken)
    {
        XboxLiveResolvedConfigurationTarget? resolved = null;
        XboxLiveConfigurationGetResponse status = XboxPartnerCommandSupport.DeserializeResponse<XboxLiveConfigurationGetResponse>(
            await client.GetXboxLiveConfigurationStatusAsync(
                    sellerId,
                    XboxLiveScope.Sandbox,
                    request.Scid,
                    request.Sandbox,
                    cancellationToken,
                    request.TitleId,
                    target =>
                    {
                        resolved = target;
                        targetResolved(target);
                    })
                .ConfigureAwait(false),
            "configuration status");

        // Pin the history read to the SCID and seller the status read resolved a title ID to.
        Guid scid = resolved?.Scid ?? request.Scid
            ?? throw new InvalidDataException("The configuration status response did not name the SCID it used.");
        sellerId ??= resolved?.SellerId;

        foreach (string feature in request.Features.Where(feature =>
            !XboxLiveChangeDescriptions.DescribedTypes.Any(type => Matches(feature, type))))
        {
            notes.Add($"XBOX services don't describe changes to '{feature}', so --feature '{feature}' matches no entry.");
        }

        // Every entry read, unfiltered, settles whether the latest save is in the history yet.
        var read = new List<XboxLiveHistoryEntry>();
        var entries = new List<XboxLiveHistoryEntry>();
        bool limited = false;
        await client.GetXboxLiveConfigurationHistoryEntriesAsync(
                sellerId,
                scid,
                request.Sandbox,
                PageSize,
                entry =>
                {
                    read.Add(entry);
                    if (entries.Count != request.Limit && Keep(entry, request) is { } kept)
                    {
                        entries.Add(kept);
                    }
                },
                cancellationToken,
                pageRead,
                stop: () => limited = entries.Count == request.Limit)
            .ConfigureAwait(false);
        if (IsLatestSavePending(status, read, wholeHistory: !limited, request.Action))
        {
            notes.Add(FormattableString.Invariant(
                $"The latest save, version {status.WorkingVersion}, isn't in the history yet. XBOX services add a save shortly after it; run the command again in a minute."));
        }

        return new XboxLiveConfigurationHistory
        {
            Scid = scid,
            Sandbox = request.Sandbox,
            Entries = entries,
        };
    }

    /// <summary>
    /// The entry with each document's feature, keeping only the documents <c>--feature</c> names;
    /// or null when <c>--action</c> or <c>--feature</c> leaves it out.
    /// </summary>
    private static XboxLiveHistoryEntry? Keep(XboxLiveHistoryEntry entry, ConfigHistoryRequest request)
    {
        if (request.Action is not null && !string.Equals(entry.Action, request.Action, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        XboxLiveHistoryDocument[] documents =
        [
            .. (entry.Documents ?? [])
                .Where(document => request.Features.Count == 0
                    || request.Features.Any(feature => Matches(feature, document.DocumentType)))
                .Select(document => document with { Feature = XboxLiveFeatureNames.NameOf(document.DocumentType) }),
        ];
        return request.Features.Count > 0 && documents.Length == 0 ? null : entry with { Documents = documents };
    }

    /// <summary>
    /// Whether the draft's latest save is recent and not in the history yet, when saves are shown.
    /// XBOX services add a save once they've described it, a few seconds after it.
    /// </summary>
    private static bool IsLatestSavePending(
        XboxLiveConfigurationGetResponse status,
        IReadOnlyList<XboxLiveHistoryEntry> entries,
        bool wholeHistory,
        string? action) =>
        action is null or XboxLiveHistoryActions.Save
        && status.WorkingCommitTime is { } saved
        && DateTimeOffset.UtcNow - saved < XboxLiveChangeDescriptions.PendingWindow
        && XboxLiveChangeDescriptions.IsPending(status.WorkingVersion, entries, wholeHistory);

    /// <summary>Whether <c>--feature</c>'s value names a document type: by feature name, type, or document name.</summary>
    private static bool Matches(string feature, string? documentType) =>
        documentType is not null
        && (string.Equals(feature, XboxLiveFeatureNames.NameOf(documentType), StringComparison.OrdinalIgnoreCase)
            || string.Equals(XboxLiveFeatureNames.TypeOf(feature), documentType, StringComparison.OrdinalIgnoreCase));

    private static string FormatTime(JsonElement value) =>
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time)
            ? time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'Z'", CultureInfo.InvariantCulture)
            : value.GetString() ?? "-";

    private static string Name(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);

    private static string FieldPath(params string[] properties) => string.Join('.', properties.Select(Name));
}
