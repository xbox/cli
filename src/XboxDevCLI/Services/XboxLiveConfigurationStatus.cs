// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Frozen;
using System.Text.Json.Serialization;
using XboxDevCLI.Commands;

namespace XboxDevCLI.Services;

/// <summary>The names the service uses for each configuration scope.</summary>
internal static class XboxLiveConfigurationScopes
{
    public const string Sandbox = "sandbox";
    public const string Title = "title";
    public const string Account = "account";

    public static string Name(XboxLiveScope scope) => scope switch
    {
        XboxLiveScope.Sandbox => Sandbox,
        XboxLiveScope.Title => Title,
        XboxLiveScope.Account => Account,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    /// <summary>
    /// The title documents that go live with each sandbox's own publish. A publish to
    /// <c>HIDDEN</c> makes the title's other documents live.
    /// </summary>
    private static readonly FrozenSet<string> SandboxPublishedTitleTypes =
        FrozenSet.ToFrozenSet(["dataplatformconfig", "titlepriv", "nsal"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a title document goes live with each sandbox's own publish, rather than through <c>HIDDEN</c>.</summary>
    public static bool PublishesWithSandbox(string? documentName) =>
        SandboxPublishedTitleTypes.Contains(XboxLiveFeatureNames.TypeOf(documentName));

    /// <summary>
    /// XBOX services' own sandboxes, which hold the title's configuration rather than one
    /// sandbox's: the title scope reads them.
    /// </summary>
    public static bool IsTitleSandbox(string sandbox) =>
        sandbox.Equals("ALL", StringComparison.OrdinalIgnoreCase)
        || sandbox.Equals("HIDDEN", StringComparison.OrdinalIgnoreCase)
        || sandbox.Equals("HISTORY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One document's draft compared with its published copy, by content hash.</summary>
internal static class XboxLiveDocumentStates
{
    public const string DraftOnly = "draft-only";
    public const string PublishedOnly = "published-only";
    public const string Changed = "changed";
    public const string Same = "same";
}

/// <summary>Whether the draft's changes to a document are described.</summary>
internal static class XboxLiveDescriptionStatuses
{
    /// <summary>Every save since the last publish is described.</summary>
    public const string Ready = "ready";

    /// <summary>The draft's latest save isn't described yet; it will be shortly.</summary>
    public const string Pending = "pending";

    /// <summary>No description exists for this change, or descriptions couldn't be read.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// A configuration scope's status: each document's draft compared with what's published, the draft
/// and published versions, and the last publish. The CLI adds who saved each version and, for a
/// sandbox, what each save changed.
/// </summary>
internal sealed record XboxLiveConfigurationStatus
{
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("scid")]
    public Guid? Scid { get; init; }

    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    [JsonPropertyName("draft")]
    public XboxLiveConfigurationVersion? Draft { get; init; }

    /// <summary>
    /// For a sandbox: the title draft's version, whose <c>dataplatformconfig</c>, <c>titlepriv</c>, and
    /// <c>nsal</c> the sandbox's publish takes.
    /// </summary>
    [JsonPropertyName("titleDraft")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XboxLiveConfigurationVersion? TitleDraft { get; init; }

    [JsonPropertyName("published")]
    public XboxLiveConfigurationVersion? Published { get; init; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<XboxLiveDocumentStatus>? Documents { get; init; }

    [JsonPropertyName("lastPublish")]
    public XboxLiveLastPublish? LastPublish { get; init; }

    /// <summary>
    /// Whether a compared document is draft-only or changed: whether there's anything to publish.
    /// XBOX services don't publish a draft whose only difference is a published-only document.
    /// </summary>
    [JsonPropertyName("hasChanges")]
    public bool HasChanges { get; init; }
}

/// <summary>One configuration set's row in <c>config list</c>: its status without its documents.</summary>
internal sealed record XboxLiveConfigurationSetStatus
{
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    [JsonPropertyName("draft")]
    public XboxLiveConfigurationVersion? Draft { get; init; }

    [JsonPropertyName("published")]
    public XboxLiveConfigurationVersion? Published { get; init; }

    [JsonPropertyName("lastPublish")]
    public XboxLiveLastPublish? LastPublish { get; init; }

    [JsonPropertyName("hasChanges")]
    public bool HasChanges { get; init; }

    public static XboxLiveConfigurationSetStatus From(XboxLiveConfigurationStatus status) => new()
    {
        Scope = status.Scope,
        Sandbox = status.Sandbox,
        Draft = status.Draft,
        Published = status.Published,
        LastPublish = status.LastPublish,
        HasChanges = status.HasChanges,
    };
}

/// <summary>A draft or published version, and who saved or published it, and when.</summary>
internal sealed record XboxLiveConfigurationVersion
{
    [JsonPropertyName("version")]
    public ulong? Version { get; init; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; init; }

    /// <summary>The ID of the developer who saved or published it, when the configuration history has it.</summary>
    [JsonPropertyName("user")]
    public string? User { get; init; }

    /// <summary>The developer's name, when the configuration history has it.</summary>
    [JsonPropertyName("userName")]
    public string? UserName { get; init; }

    /// <summary>The save's message, when the configuration history has it.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed record XboxLiveDocumentStatus
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The feature the document configures, such as <c>Achievements</c>.</summary>
    [JsonPropertyName("feature")]
    public string? Feature { get; init; }

    /// <summary>
    /// <c>sandbox</c>, or <c>title</c> for a title document. In a sandbox's status, title
    /// documents are only those that go live with the sandbox's own publish.
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("draftHash")]
    public string? DraftHash { get; init; }

    [JsonPropertyName("publishedHash")]
    public string? PublishedHash { get; init; }

    /// <summary>
    /// In the title's status: where the document goes live, <c>HIDDEN</c> for a title-wide publish,
    /// or <c>sandbox</c> for each sandbox's own publish.
    /// </summary>
    [JsonPropertyName("publishesWith")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublishesWith { get; init; }

    /// <summary>For a document that isn't the same in the draft and the published version: whether its changes are described.</summary>
    [JsonPropertyName("descriptionStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DescriptionStatus { get; init; }

    /// <summary>For a document that isn't the same in the draft and the published version: each save that changed it, newest first.</summary>
    [JsonPropertyName("changes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<XboxLiveDocumentChange>? Changes { get; init; }
}

/// <summary>What one save changed in one document, as the configuration history describes it.</summary>
internal sealed record XboxLiveDocumentChange
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("version")]
    public ulong? Version { get; init; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; init; }

    [JsonPropertyName("author")]
    public XboxLiveAuthor? Author { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed record XboxLiveAuthor
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record XboxLiveLastPublish
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// The service's answer for one configuration set: its draft and published versions and when each was
/// committed, and each document's draft compared with its published copy.
/// </summary>
internal sealed record XboxLiveConfigurationGetResponse
{
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    [JsonPropertyName("workingVersion")]
    public ulong? WorkingVersion { get; init; }

    [JsonPropertyName("workingCommitTime")]
    public DateTimeOffset? WorkingCommitTime { get; init; }

    [JsonPropertyName("publishedVersion")]
    public ulong? PublishedVersion { get; init; }

    [JsonPropertyName("publishedCommitTime")]
    public DateTimeOffset? PublishedCommitTime { get; init; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<XboxLiveDocumentStatus?>? Documents { get; init; }

    /// <summary>This answer, or an error when it has no documents array or a document without a name.</summary>
    public XboxLiveConfigurationGetResponse RequireNamedDocuments() =>
        Documents is null || Documents.Any(document => document is null || string.IsNullOrWhiteSpace(document.Name))
            ? throw new InvalidDataException("The configuration status response must contain a documents array of named documents.")
            : this;
}

/// <summary>A page of a sandbox's configuration history, newest first.</summary>
internal sealed record XboxLiveConfigurationHistoryPage
{
    [JsonPropertyName("entries")]
    public IReadOnlyList<XboxLiveHistoryEntry>? Entries { get; init; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; init; }
}

internal static class XboxLiveHistoryActions
{
    public const string Save = "save";
    public const string Publish = "publish";
    public const string Copy = "copy";
}

internal sealed record XboxLiveHistoryEntry
{
    /// <summary><c>save</c>, <c>publish</c>, <c>copy</c>, or <c>other</c>.</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("version")]
    public ulong? Version { get; init; }

    /// <summary>The sandbox the change applies to, or null when the service couldn't tell.</summary>
    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; init; }

    [JsonPropertyName("author")]
    public XboxLiveAuthor? Author { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<XboxLiveHistoryDocument>? Documents { get; init; }
}

internal sealed record XboxLiveHistoryDocument
{
    [JsonPropertyName("documentType")]
    public string? DocumentType { get; init; }

    /// <summary>The feature the document type configures, such as <c>Achievements</c>. The CLI adds it.</summary>
    [JsonPropertyName("feature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Feature { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// <c>config history</c>'s output: the sandbox it read, and its saves, publishes, and copies,
/// newest first.
/// </summary>
internal sealed record XboxLiveConfigurationHistory
{
    [JsonPropertyName("scid")]
    public Guid? Scid { get; init; }

    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<XboxLiveHistoryEntry> Entries { get; init; } = [];
}
