// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Adds to a sandbox's status what each save since the last publish changed, and who saved and
/// published each version, from the sandbox's configuration history.
/// </summary>
/// <remarks>
/// Document states always come from content hashes, so a change is shown whether or not it's
/// described. Descriptions cover only a sandbox's draft against its own published version: a title
/// document in a sandbox's status compares the title's draft with the sandbox's published version, so
/// it gets none. The history describes only some document types, and describes a save a few
/// seconds after it; until then the save has no history entry. Entries are matched by version,
/// which is unique within a SCID, because an entry's sandbox is null when the service couldn't
/// read it.
/// </remarks>
internal static class XboxLiveChangeDescriptions
{
    /// <summary>History entries read for descriptions: one page, the most the service returns.</summary>
    internal const int HistoryPageSize = 100;

    /// <summary>The document types the history describes; it writes nothing for the others.</summary>
    internal static readonly IReadOnlySet<string> DescribedTypes = new HashSet<string>(
        [
            "accesspolicies", "achievements", "achievements2017", "custominvitestrings", "featuredstats",
            "gameclips", "leaderboards", "localization", "matchmaking", "presence", "sessiontemplates",
            "statsleaderboards", "titlestorage", "tournaments", "userstats", "userstats2013", "xrexceptions",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A status whose changed documents say their description is unavailable, for a scope the
    /// history doesn't describe or when it couldn't be read.
    /// </summary>
    public static XboxLiveConfigurationStatus WithoutDescriptions(XboxLiveConfigurationStatus status) =>
        status with
        {
            Documents = status.Documents?
                .Select(document => IsSame(document) || document.State is null
                    ? document
                    : document with
                    {
                        DescriptionStatus = XboxLiveDescriptionStatuses.Unavailable,
                        Changes = [],
                    })
                .ToArray(),
        };

    /// <summary>
    /// Adds descriptions to a sandbox's status from its newest history page, and returns any note
    /// for stderr about what the descriptions don't cover.
    /// </summary>
    public static (XboxLiveConfigurationStatus Status, IReadOnlyList<string> Notes) Describe(
        XboxLiveConfigurationStatus status,
        XboxLiveConfigurationHistoryPage history,
        DateTimeOffset? now = null)
    {
        string sandbox = status.Sandbox ?? string.Empty;
        IReadOnlyList<XboxLiveHistoryEntry> entries = history.Entries ?? [];

        // The draft's saves are the sandbox's own saves newer than its last publish, including a
        // save whose sandbox is unknown. A copy replaced the draft with another sandbox's, and the
        // history doesn't describe what it changed, so saves before it don't describe the draft
        // either. Entries newer than both versions the status read are a later save or publish,
        // so they're left out. Right after a publish, the published version is the newer one.
        ulong? newest = status.Published?.Version > status.Draft?.Version ? status.Published?.Version : status.Draft?.Version;
        var saves = new List<XboxLiveHistoryEntry>();
        XboxLiveHistoryEntry? lastPublish = null;
        bool reachedCopy = false;
        foreach (XboxLiveHistoryEntry entry in entries)
        {
            if ((entry.Sandbox is not null && !IsSandbox(entry.Sandbox, sandbox)) || entry.Version > newest)
            {
                continue;
            }

            if (Is(entry.Action, XboxLiveHistoryActions.Save))
            {
                saves.Add(entry);
            }
            else if (Is(entry.Action, XboxLiveHistoryActions.Publish))
            {
                lastPublish = entry;
                break;
            }
            else if (Is(entry.Action, XboxLiveHistoryActions.Copy))
            {
                reachedCopy = true;
                break;
            }
        }

        // Every save since the last publish is in the page when the page reaches the publish the
        // status read, or, for a sandbox never published, when it's the whole history. A new
        // sandbox has an empty published version that no publish entry describes.
        bool complete = (lastPublish is not null && lastPublish.Version == status.Published?.Version)
            || (lastPublish is null && !reachedCopy && history.ContinuationToken is null);
        // The latest save isn't described: pending while it's recent, and unavailable after that.
        bool undescribed = IsPending(status.Draft?.Version, entries, history.ContinuationToken is null);
        bool pending = undescribed
            && status.Draft?.Time is { } saved
            && (now ?? DateTimeOffset.UtcNow) - saved < PendingWindow;
        var notes = new List<string>();
        var documents = status.Documents?
            .Select(document => IsSame(document) ? document : DescribeDocument(document, saves, pending, complete && !undescribed))
            .ToArray();
        if (!complete && status.HasChanges)
        {
            notes.Add(reachedCopy
                ? "The draft was copied from another sandbox since the last publish, and copies aren't described. " +
                    "Change descriptions cover only the saves since the copy."
                : FormattableString.Invariant(
                    $"Change descriptions cover the latest {HistoryPageSize} saves and publishes only, which don't reach the last publish."));
        }

        return (
            status with
            {
                Documents = documents,
                Draft = WithSaver(status.Draft, FindVersion(entries, status.Draft?.Version)),
                Published = WithSaver(
                    status.Published,
                    lastPublish is not null && lastPublish.Version == status.Published?.Version ? lastPublish : null),
            },
            notes);
    }

    private static XboxLiveDocumentStatus DescribeDocument(
        XboxLiveDocumentStatus document,
        IReadOnlyList<XboxLiveHistoryEntry> saves,
        bool pending,
        bool complete)
    {
        // The history describes neither title documents in a sandbox's status nor the types it
        // doesn't cover, so those changes are never pending.
        if (!Is(document.Scope, XboxLiveConfigurationScopes.Sandbox) || !DescribedTypes.Contains(XboxLiveFeatureNames.TypeOf(document.Name)))
        {
            return document with
            {
                DescriptionStatus = XboxLiveDescriptionStatuses.Unavailable,
                Changes = [],
            };
        }

        string type = XboxLiveFeatureNames.TypeOf(document.Name);
        XboxLiveDocumentChange[] changes =
        [
            .. saves.SelectMany(save => (save.Documents ?? [])
                .Where(item => Is(item.DocumentType, type))
                .Select(item => new XboxLiveDocumentChange
                {
                    Description = item.Description,
                    Version = save.Version,
                    Time = save.Time,
                    Author = save.Author,
                    Message = save.Message,
                })),
        ];
        return document with
        {
            DescriptionStatus = pending
                ? XboxLiveDescriptionStatuses.Pending
                : complete && changes.Length > 0
                    ? XboxLiveDescriptionStatuses.Ready
                    : XboxLiveDescriptionStatuses.Unavailable,
            Changes = changes,
        };
    }

    /// <summary>
    /// How long after a save its description can still be on the way. The history describes a
    /// save within seconds, so one that's still missing after this never will be.
    /// </summary>
    internal static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the draft's latest save isn't described yet. The history lists entries newest
    /// first by version: reaching the draft's version means it's described, and reaching a lower
    /// version first means it isn't yet. A page of only newer entries, such as the title's later
    /// saves, settles nothing unless it's the whole history.
    /// </summary>
    internal static bool IsPending(ulong? draftVersion, IReadOnlyList<XboxLiveHistoryEntry> entries, bool wholeHistory)
    {
        if (draftVersion is not { } version)
        {
            return false;
        }

        foreach (XboxLiveHistoryEntry entry in entries)
        {
            if (entry.Version == version)
            {
                return false;
            }

            if (entry.Version < version)
            {
                return true;
            }
        }

        return wholeHistory;
    }

    private static XboxLiveHistoryEntry? FindVersion(IReadOnlyList<XboxLiveHistoryEntry> entries, ulong? version) =>
        version is null ? null : entries.FirstOrDefault(entry => entry.Version == version);

    /// <summary>Adds who saved or published a version, and the save's message, from its history entry.</summary>
    private static XboxLiveConfigurationVersion? WithSaver(
        XboxLiveConfigurationVersion? version,
        XboxLiveHistoryEntry? entry) =>
        version is null || entry is null
            ? version
            : version with
            {
                User = entry.Author?.Id ?? version.User,
                UserName = entry.Author?.Name ?? version.UserName,
                Message = entry.Message ?? version.Message,
            };

    private static bool IsSame(XboxLiveDocumentStatus document) =>
        Is(document.State, XboxLiveDocumentStates.Same);

    private static bool IsSandbox(string? entrySandbox, string sandbox) =>
        entrySandbox is not null && entrySandbox.Equals(sandbox, StringComparison.OrdinalIgnoreCase);

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
