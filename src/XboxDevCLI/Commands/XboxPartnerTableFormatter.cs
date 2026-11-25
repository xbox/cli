// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class XboxPartnerTableFormatter
{
    /// <summary><c>config list</c>: one row per configuration set.</summary>
    public static string FormatConfigurations(IReadOnlyList<XboxLiveConfigurationSetStatus> sets) =>
        Render(
            XboxPartnerCommandSupport.ConfigurationTableView,
            sets.Select(set => new[]
            {
                set.Scope ?? string.Empty,
                set.Sandbox ?? string.Empty,
                set.HasChanges ? "Yes" : "No",
                FormatVersion(set.Draft?.Version),
                FormatVersion(set.Published?.Version),
                FormatTime(set.Published?.Time),
                set.LastPublish?.Status ?? string.Empty,
            }));

    /// <summary>A table for stdout, and lines for stderr that say what the table is about.</summary>
    internal sealed record NotedTable(string Text, IReadOnlyList<string> Notes);

    /// <summary>
    /// <c>config status</c>: <paramref name="view"/>'s rows, and notes on the published and draft
    /// versions. The rows come from a copy of the status. A sandbox's or the account's keeps only the
    /// documents that differ, and adds a "description pending" change where one is. The title's keeps
    /// every title document, and says where each goes live in place of its changes.
    /// </summary>
    public static NotedTable FormatConfigurationStatus(XboxLiveConfigurationStatus status, TableView view)
    {
        bool title = string.Equals(status.Scope, XboxLiveConfigurationScopes.Title, StringComparison.OrdinalIgnoreCase);
        XboxLiveDocumentStatus[] compared = [.. (status.Documents ?? []).Where(document => document.State is not null)];
        int differ = compared.Count(document => !string.Equals(document.State, XboxLiveDocumentStates.Same, StringComparison.OrdinalIgnoreCase));

        // Like hasChanges, a published-only document isn't counted: publishing never removes it.
        int publishable = compared.Count(document =>
            string.Equals(document.State, XboxLiveDocumentStates.DraftOnly, StringComparison.OrdinalIgnoreCase)
            || string.Equals(document.State, XboxLiveDocumentStates.Changed, StringComparison.OrdinalIgnoreCase));
        XboxLiveDocumentStatus[] rows = title
            ? [
                .. (status.Documents ?? []).Select(document => document with
                {
                    Changes =
                    [
                        new XboxLiveDocumentChange
                        {
                            Description = string.Equals(document.PublishesWith, XboxLiveConfigurationScopes.Sandbox, StringComparison.Ordinal)
                                ? "Publishes with each sandbox"
                                : $"Publishes through {document.PublishesWith}",
                        },
                    ],
                }),
            ]
            : [
                .. compared
                    .Where(document => !string.Equals(document.State, XboxLiveDocumentStates.Same, StringComparison.OrdinalIgnoreCase))
                    .Select(document => string.Equals(document.DescriptionStatus, XboxLiveDescriptionStatuses.Pending, StringComparison.Ordinal)
                        ? document with { Changes = [.. document.Changes ?? [], new XboxLiveDocumentChange { Description = "description pending" }] }
                        : document),
            ];

        string? publishTo = title ? XboxLiveConfigurationScope.TitleLiveSandbox : status.Sandbox;
        var notes = new List<string>
        {
            (status.Published?.Version is null
                ? "Published: never."
                : $"Published: version {FormatVersion(status.Published.Version)}{(title ? $" ({publishTo})" : string.Empty)}.")
                + (status.LastPublish?.Status is { Length: > 0 } publish ? $" Last publish: {NormalizeCell(publish)}." : string.Empty),
            (status.Draft?.Version is null
                ? "Draft: never saved."
                : $"Draft: version {FormatVersion(status.Draft.Version)}{(title ? $" ({XboxLiveConfigurationScope.TitleDraftSandbox})" : string.Empty)}.")
                + (status.TitleDraft?.Version is { } titleDraft
                    ? $" Title draft: version {FormatVersion(titleDraft)} ({XboxLiveConfigurationScope.TitleDraftSandbox})."
                    : string.Empty),
        };
        string differing = publishable == 1 ? "1 document differs" : FormattableString.Invariant($"{publishable} documents differ");
        notes.Add(differ == 0
            ? (title ? $"The draft matches what's published to {publishTo}." : "The draft matches what's published.")
            : publishable == 0
                ? (publishTo is null ? "Nothing to publish." : $"Nothing to publish to {publishTo}.")
                : publishTo is null
                    ? $"{differing} between the draft and the published version."
                    : $"{differing} for a publish to {publishTo}.");
        int eachSandbox = (status.Documents ?? []).Count(document => document.State is null);
        if (title && eachSandbox > 0)
        {
            notes.Add(FormattableString.Invariant(
                $"{eachSandbox} {(eachSandbox == 1 ? "document publishes" : "documents publish")} with each sandbox. Check them with config status --sandbox-id <sandbox>."));
        }

        return new(FormatThrough(view, status with { Documents = rows }), notes);
    }

    private static string FormatThrough(TableView view, XboxLiveConfigurationStatus status)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(status, ConfigStatusCommandFactory.OutputJson));
        return TableFormatter.Format(document.RootElement, view);
    }

    public static string FormatSellers(string responseBody)
    {
        using var document = Parse(responseBody, "seller");
        return TableFormatter.Format(document.RootElement, XboxPartnerCommandSupport.SellerTableView);
    }

    public static string FormatGames(string responseBody) =>
        FormatGamesWithCount(responseBody).Text;

    public static string FormatRows(TableView view, IEnumerable<string[]> rows) =>
        Render(view, rows);

    public static TableFormatResult FormatGamesWithCount(string responseBody)
    {
        using var document = Parse(responseBody, "game");
        var games = GetItems(document.RootElement, "games");
        var rows = games
            .Select((game, index) => new { Game = game, Index = index })
            .GroupBy(
                item =>
                {
                    var titleId = ReadTitleId(item.Game);
                    return titleId is not null
                        ? $"title:{titleId}"
                        : $"game:{item.Index}";
                },
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var gamesForTitle = group.Select(item => item.Game).ToArray();
                return new[]
                {
                    ReadTitleId(gamesForTitle[0]) ?? string.Empty,
                    JoinDistinct(gamesForTitle, "storeId"),
                    JoinDistinct(gamesForTitle, "name"),
                    JoinDistinct(gamesForTitle, "msaAppId"),
                    JoinDistinct(gamesForTitle, "packageFamilyName"),
                };
            })
            .OrderBy(row => row[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row[1], StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row[2], StringComparer.OrdinalIgnoreCase);

        var projectedRows = rows.ToArray();
        return new(
            projectedRows.Length == 0
                ? TableFormatter.EmptyNotice
                : Render(XboxPartnerCommandSupport.GameTableView, projectedRows),
            games.Count);
    }

    internal static void ValidateTitleId(JsonElement game) => _ = ReadTitleId(game);

    internal static string? ReadTitleId(JsonElement game)
    {
        if (game.ValueKind != JsonValueKind.Object
            || !game.TryGetProperty("xboxTitleId", out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: 8 } titleId
            && titleId.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            return titleId;
        }

        throw new InvalidDataException(
            "The game response contained an invalid 'xboxTitleId' value.");
    }

    /// <summary>
    /// Table text for stdout plus summary notes the command writes to stderr, so stdout holds
    /// only the requested table.
    /// </summary>
    internal sealed record TestAccountTable(string Text, IReadOnlyList<string> Notes);

    public static TestAccountTable FormatTestAccountBatches(string responseBody, TestAccountBatchFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        TestAccountBatchesResponse response = XboxPartnerCommandSupport.DeserializeResponse<TestAccountBatchesResponse>(
            responseBody,
            "test account batch list",
            serviceName: null);
        if (response.Batches is null || response.Batches.Any(batch => batch is null))
        {
            throw new InvalidDataException(
                "The test account batch list response must contain a 'batches' array.");
        }

        IReadOnlyList<XboxPartnerCommandSupport.TableField<TestAccountBatch>> fields = filter.IncludeSandboxes
            ? [.. XboxPartnerCommandSupport.TestAccountBatchFields, XboxPartnerCommandSupport.TestAccountSandboxesField]
            : XboxPartnerCommandSupport.TestAccountBatchFields;
        TableView view = XboxPartnerCommandSupport.TestAccountBatchesView with
        {
            Columns = [.. fields.Select(field => field.Column)],
        };
        var rows = response.Batches.Select(batch => fields.Select(field => field.Cell(batch)).ToArray());
        string kind = filter.Expired ? "expired " : string.Empty;
        return new(Render(view, rows), [FormattableString.Invariant($"Returned {response.Batches.Count} {kind}batches.")]);
    }

    public static TestAccountTable FormatTestAccountLookup(string responseBody)
    {
        var result = XboxPartnerCommandSupport.DeserializeResponse<TestAccountLookupResult>(
            responseBody,
            "test account lookup",
            serviceName: null);
        var fields = XboxPartnerCommandSupport.TestAccountLookupFields;
        var rows = new[] { fields.Select(field => field.Cell(result)).ToArray() };
        return new(Render(XboxPartnerCommandSupport.TestAccountLookupView, rows), []);
    }

    public static TestAccountTable FormatTestAccountBatch(string responseBody)
    {
        TestAccountBatchResponse response = XboxPartnerCommandSupport.DeserializeResponse<TestAccountBatchResponse>(
            responseBody,
            "test account batch",
            serviceName: null);
        if (response.Batch is null ||
            response.Accounts is null ||
            response.Accounts.Any(account => account is null))
        {
            throw new InvalidDataException(
                "The test account batch response must contain a 'batch' object and an " +
                "'accounts' array.");
        }

        TestAccountBatch batch = response.Batch;
        var notes = new List<string>
        {
            FormattableString.Invariant(
                $"Batch {batch.Id} ({NormalizeCell(XboxPartnerCommandSupport.TestAccountText(batch.EmailTemplate))}): {XboxPartnerCommandSupport.TestAccountState(batch.IsComplete)}, {response.Accounts.Count} accounts returned of batch size {batch.BatchSize}."),
        };
        if (batch.SandboxAccess is { Count: > 0 })
        {
            notes.Add($"Sandbox access: {NormalizeCell(XboxPartnerCommandSupport.TestAccountList(batch.SandboxAccess))}");
        }

        if (!batch.IsComplete && !string.IsNullOrWhiteSpace(batch.Progress))
        {
            notes.Add($"Progress: {NormalizeCell(batch.Progress)}");
        }

        var fields = XboxPartnerCommandSupport.TestAccountFields;
        var rows = response.Accounts
            .OrderBy(account => account.Email, StringComparer.OrdinalIgnoreCase)
            .Select(account => fields.Select(field => field.Cell(account)).ToArray());
        return new(Render(XboxPartnerCommandSupport.TestAccountBatchView, rows), notes);
    }

    /// <summary>
    /// One row for a test account creation job. The error code and progress go to stderr notes,
    /// since they are long free text that would not fit a row.
    /// </summary>
    public static TestAccountTable FormatTestAccountJob(TestAccountJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.Progress))
        {
            notes.Add($"Progress: {NormalizeCell(job.Progress)}");
        }

        if (!string.IsNullOrWhiteSpace(job.ErrorCode) || !string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            notes.Add(
                $"Error: {NormalizeCell(XboxPartnerCommandSupport.TestAccountText(job.ErrorCode))}: " +
                NormalizeCell(XboxPartnerCommandSupport.TestAccountText(job.ErrorMessage)));
        }

        var fields = XboxPartnerCommandSupport.TestAccountJobFields;
        return new(
            Render(XboxPartnerCommandSupport.TestAccountJobView, [fields.Select(field => field.Cell(job)).ToArray()]),
            notes);
    }

    /// <summary>One row for a test account batch delete.</summary>
    public static TestAccountTable FormatTestAccountBatchDeletion(TestAccountBatchDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        var fields = XboxPartnerCommandSupport.TestAccountBatchDeletionFields;
        return new(
            Render(
                XboxPartnerCommandSupport.TestAccountBatchDeletionView,
                [fields.Select(field => field.Cell(deletion)).ToArray()]),
            []);
    }

    /// <summary>One row for a test account batch update, with its sandboxes and keywords as stderr notes.</summary>
    public static TestAccountTable FormatTestAccountBatchUpdate(TestAccountBatchUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        string[] notes =
        [
            $"Sandboxes: {NormalizeCell(XboxPartnerCommandSupport.TestAccountList(update.Sandboxes))}",
            $"Keywords: {NormalizeCell(XboxPartnerCommandSupport.TestAccountList(update.Keywords))}",
        ];
        var fields = XboxPartnerCommandSupport.TestAccountBatchUpdateFields;
        return new(
            Render(
                XboxPartnerCommandSupport.TestAccountBatchUpdateView,
                [fields.Select(field => field.Cell(update)).ToArray()]),
            notes);
    }

    private static string FormatVersion(ulong? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatTime(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss 'Z'",
            CultureInfo.InvariantCulture) ?? string.Empty;

    private static string JoinDistinct(IEnumerable<JsonElement> games, string propertyName) =>
        string.Join(
            ", ",
            games
                .Select(game => XboxPartnerCommandSupport.ReadString(game, propertyName))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static JsonDocument Parse(string responseBody, string resource)
    {
        try
        {
            return JsonDocument.Parse(responseBody);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                $"The {resource} response was not valid JSON.",
                exception);
        }
    }

    private static IReadOnlyList<JsonElement> GetItems(
        JsonElement root,
        string collectionProperty)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The response must be a JSON object.");
        }

        if (!root.TryGetProperty(collectionProperty, out var collection)
            || collection.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The response property '{collectionProperty}' must be an array.");
        }

        var items = collection.EnumerateArray().ToArray();
        if (Array.Exists(items, item => item.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException(
                $"The response property '{collectionProperty}' must contain only objects.");
        }

        return items;
    }

    /// <summary>
    /// Sanitizes extracted rows and lays them out with the PlayFab renderer.
    /// </summary>
    private static string Render(TableView view, IEnumerable<string[]> rows)
    {
        var normalized = rows
            .Select(row => row.Select(NormalizeCell).ToArray())
            .ToArray();

        return normalized.Length == 0
            ? TableFormatter.EmptyNotice
            : TableFormatter.Render(view.Columns, normalized);
    }

    // Uses the same sanitizer as PlayFab table output so gateway-supplied names cannot break or
    // visually reorder the fixed columns. Trimmed as well, because a gateway name padded with
    // spaces would otherwise be sized and aligned as though the padding were content.
    internal static string NormalizeCell(string value) =>
        TableFormatter.NormalizeCell(value).Trim();

    internal sealed record TableFormatResult(string Text, int ResultCount);
}
