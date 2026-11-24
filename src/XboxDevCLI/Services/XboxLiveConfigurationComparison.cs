// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Frozen;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using XboxDevCLI.Commands;

namespace XboxDevCLI.Services;

/// <summary>How one document differs between the old side of a comparison and the new side.</summary>
internal static class XboxLiveDocumentDifferences
{
    /// <summary>Only the new side has the document.</summary>
    public const string Added = "added";

    /// <summary>Only the old side has the document.</summary>
    public const string Removed = "removed";

    public const string Changed = "changed";
    public const string Same = "same";
}

/// <summary>What one side of a comparison is: a draft, a published version, or local files.</summary>
internal static class XboxLiveSideViews
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Files = "files";
}

/// <summary>Where a side of a comparison came from, which decides whether it can be described.</summary>
internal enum XboxLiveSnapshotOrigin
{
    /// <summary>The scope's latest draft.</summary>
    Draft,

    /// <summary>The scope's current published version.</summary>
    Published,

    /// <summary>A version found by its number.</summary>
    Version,

    /// <summary>Local files.</summary>
    Files,
}

/// <summary>
/// Two configuration states compared document by document, by content, grouped by feature.
/// <c>-</c> lines come from <see cref="Old"/> and <c>+</c> lines from <see cref="New"/>.
/// </summary>
internal sealed record XboxLiveConfigurationDiff
{
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("scid")]
    public Guid? Scid { get; init; }

    [JsonPropertyName("old")]
    public required XboxLiveDiffSide Old { get; init; }

    [JsonPropertyName("new")]
    public required XboxLiveDiffSide New { get; init; }

    /// <summary>Whether any compared document isn't the same on both sides.</summary>
    [JsonPropertyName("hasDifferences")]
    public bool HasDifferences { get; init; }

    [JsonPropertyName("features")]
    public required IReadOnlyList<XboxLiveFeatureDiff> Features { get; init; }

    /// <summary>
    /// Documents one side holds that the comparison leaves out, by name: title documents that a
    /// sandbox's earlier draft doesn't hold, and a sandbox's internal <c>xms</c> files. Stderr says why.
    /// </summary>
    [JsonPropertyName("notCompared")]
    public IReadOnlyList<string> NotCompared { get; init; } = [];
}

/// <summary>One side of a comparison: what it is, where it's from, and its version.</summary>
internal sealed record XboxLiveDiffSide
{
    /// <summary><c>draft</c>, <c>published</c>, or <c>files</c>.</summary>
    [JsonPropertyName("view")]
    public required string View { get; init; }

    /// <summary>The sandbox it's from, for the sandbox scope.</summary>
    [JsonPropertyName("sandbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sandbox { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Version { get; init; }

    [JsonPropertyName("time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Time { get; init; }

    /// <summary>The ID of the developer who saved or published it.</summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; init; }

    [JsonPropertyName("userName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserName { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    /// <summary>For local files, their full paths.</summary>
    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>A short name for the side, such as <c>XDKS.1 published version 52</c>.</summary>
    public string Describe(string scope) => View switch
    {
        XboxLiveSideViews.Files => "your files",
        _ => string.Join(
            ' ',
            new[]
            {
                Sandbox ?? (scope == XboxLiveConfigurationScopes.Sandbox ? null : scope),
                View,
                Version is { } version ? $"version {version}" : null,
            }.Where(part => part is not null)),
    };

    public static XboxLiveDiffSide From(string view, string? sandbox, XboxLiveConfigurationVersion? version) => new()
    {
        View = view,
        Sandbox = sandbox,
        Version = version?.Version,
        Time = version?.Time,
        User = version?.User,
        UserName = version?.UserName,
        Message = version?.Message,
    };
}

/// <summary>A feature's documents in a comparison.</summary>
internal sealed record XboxLiveFeatureDiff
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("documents")]
    public required IReadOnlyList<XboxLiveDocumentDiff> Documents { get; init; }
}

/// <summary>One document compared by content: its state, both hashes, and optionally its line diff.</summary>
internal sealed record XboxLiveDocumentDiff
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// <c>sandbox</c>, <c>title</c> for a title document that goes live with each sandbox's own
    /// publish, or <c>account</c>.
    /// </summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>The old side's hash: uppercase hex SHA-1 of its bytes. Null when it's added.</summary>
    [JsonPropertyName("oldHash")]
    public string? OldHash { get; init; }

    /// <summary>The new side's hash: uppercase hex SHA-1 of its bytes. Null when it's removed.</summary>
    [JsonPropertyName("newHash")]
    public string? NewHash { get; init; }

    /// <summary>For a sandbox's draft compared with its own published version: whether its changes are described.</summary>
    [JsonPropertyName("descriptionStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DescriptionStatus { get; init; }

    /// <summary>For a sandbox's draft compared with its own published version: each save that changed it, newest first.</summary>
    [JsonPropertyName("changes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<XboxLiveDocumentChange>? Changes { get; init; }

    /// <summary>With <c>--lines</c>: the unified diff of the document's text, one line per item.</summary>
    [JsonPropertyName("lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Lines { get; init; }
}

/// <summary>A comparison's result, and notes for stderr about what it doesn't cover.</summary>
internal sealed record XboxLiveComparisonResult(XboxLiveConfigurationDiff Diff, IReadOnlyList<string> Notes);

/// <summary>What a comparison filters and adds.</summary>
internal sealed record XboxLiveComparisonOptions
{
    /// <summary>
    /// Features to keep: each matches a document's feature name, its file name, or its type,
    /// ignoring case. Empty keeps every document.
    /// </summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>Whether to add each differing document's line diff.</summary>
    public bool Lines { get; init; }
}

/// <summary>A document on one side of a comparison.</summary>
/// <param name="Name">The document's file name.</param>
/// <param name="Scope">The scope it belongs to: <c>sandbox</c>, <c>title</c>, or <c>account</c>.</param>
/// <param name="Hash">Uppercase hex SHA-1 of its bytes.</param>
/// <param name="Content">The bytes, when they're already read; null until a line diff needs them.</param>
internal sealed record XboxLiveSnapshotDocument(string Name, string Scope, string Hash, byte[]? Content = null);

/// <summary>
/// One side of a comparison, ready to compare: its documents' hashes, and how to read their bytes
/// when a line diff needs them.
/// </summary>
internal sealed class XboxLiveConfigurationSnapshot
{
    public required XboxLiveDiffSide Side { get; init; }

    public required XboxLiveSnapshotOrigin Origin { get; init; }

    /// <summary>The documents, by exact name.</summary>
    public required IReadOnlyDictionary<string, XboxLiveSnapshotDocument> Documents { get; init; }

    /// <summary>
    /// Whether this side holds the title documents that go live with a sandbox's publish. A
    /// sandbox's earlier draft doesn't: they're saved in the title's draft.
    /// </summary>
    public bool HoldsTitleDocuments { get; init; } = true;

    /// <summary>Reads the named documents' bytes, for a side that doesn't hold them yet.</summary>
    public Func<IReadOnlyList<XboxLiveSnapshotDocument>, CancellationToken, Task<IReadOnlyDictionary<string, byte[]>>>? ReadContentAsync { get; init; }
}

/// <summary>
/// Compares two configuration states by content, as XBOX services compare a draft with its published
/// version: a document is the same when its bytes are.
/// </summary>
internal static class XboxLiveConfigurationComparison
{
    /// <summary>A line diff's only line when the bytes differ but the lines don't.</summary>
    internal const string OnlyLineEndingsDiffer = "The lines are the same; only the bytes differ, such as in line endings, a byte order mark, or the encoding.";

    /// <summary>
    /// Compares <paramref name="old"/> with <paramref name="new"/>. A local files side is a partial
    /// working copy, so only its documents are compared. <paramref name="descriptions"/>, for a
    /// sandbox's draft compared with its own published version, has each document's description.
    /// </summary>
    public static async Task<XboxLiveComparisonResult> CompareAsync(
        XboxLiveScope scope,
        Guid? scid,
        XboxLiveConfigurationSnapshot old,
        XboxLiveConfigurationSnapshot @new,
        IReadOnlyDictionary<string, XboxLiveDocumentStatus>? descriptions,
        XboxLiveComparisonOptions options,
        CancellationToken cancellationToken)
    {
        string scopeName = XboxLiveConfigurationScopes.Name(scope);
        var notes = new List<string>();
        var notCompared = new List<string>();
        var names = new SortedSet<string>(old.Documents.Keys.Concat(@new.Documents.Keys), StringComparer.Ordinal);
        foreach (XboxLiveConfigurationSnapshot files in new[] { old, @new }.Where(side => side.Origin == XboxLiveSnapshotOrigin.Files))
        {
            names.IntersectWith(files.Documents.Keys);
            if (scope == XboxLiveScope.Sandbox)
            {
                string[] internalFiles = [.. names.Where(XboxLiveDiffDocuments.IsInternal)];
                names.ExceptWith(internalFiles);
                notCompared.AddRange(internalFiles);
                if (internalFiles.Length > 0)
                {
                    notes.Add($"Not compared: {string.Join(", ", internalFiles)}. XBOX services don't compare a sandbox's internal xms documents.");
                }

                string[] titleWide = [.. names.Where(XboxLiveDiffDocuments.PublishesThroughHidden)];
                names.ExceptWith(titleWide);
                notCompared.AddRange(titleWide);
                if (titleWide.Length > 0)
                {
                    notes.Add(
                        $"Not compared: {string.Join(", ", titleWide)}. They're title-wide documents, so compare them with " +
                        "--scope title.");
                }
            }
            else if (scope == XboxLiveScope.Title)
            {
                string[] sandboxPublished = [.. names.Where(name => !XboxLiveDiffDocuments.PublishesThroughHidden(name))];
                names.ExceptWith(sandboxPublished);
                notCompared.AddRange(sandboxPublished);
                if (sandboxPublished.Length > 0)
                {
                    notes.Add(
                        $"Not compared: {string.Join(", ", sandboxPublished)}. They go live with each sandbox's own publish, " +
                        "so compare them with --sandbox-id.");
                }
            }
        }

        XboxLiveConfigurationSnapshot? withoutTitle = !old.HoldsTitleDocuments ? old : !@new.HoldsTitleDocuments ? @new : null;
        if (withoutTitle is not null)
        {
            string[] skipped = [.. names.Where(name => ScopeOf(name, old, @new) == XboxLiveConfigurationScopes.Title)];
            names.ExceptWith(skipped);
            notCompared.AddRange(skipped);
            if (skipped.Length > 0)
            {
                notes.Add(
                    $"Not compared: {string.Join(", ", skipped)}. Title documents are saved in the title's draft, " +
                    $"so {withoutTitle.Side.Describe(scopeName)} doesn't hold them.");
            }
        }

        var documents = new List<(string Feature, XboxLiveDocumentDiff Document)>();
        foreach (string name in names)
        {
            old.Documents.TryGetValue(name, out XboxLiveSnapshotDocument? before);
            @new.Documents.TryGetValue(name, out XboxLiveSnapshotDocument? after);
            string state = (before, after) switch
            {
                (null, _) => XboxLiveDocumentDifferences.Added,
                (_, null) => XboxLiveDocumentDifferences.Removed,
                _ when string.Equals(before.Hash, after.Hash, StringComparison.OrdinalIgnoreCase) => XboxLiveDocumentDifferences.Same,
                _ => XboxLiveDocumentDifferences.Changed,
            };
            XboxLiveDocumentStatus? described = state == XboxLiveDocumentDifferences.Same ? null : descriptions?.GetValueOrDefault(name);
            documents.Add((
                XboxLiveFeatureNames.NameOf(name),
                new XboxLiveDocumentDiff
                {
                    Name = name,
                    Scope = ScopeOf(name, old, @new),
                    State = state,
                    OldHash = before?.Hash,
                    NewHash = after?.Hash,
                    DescriptionStatus = described?.DescriptionStatus,
                    Changes = described?.Changes,
                }));
        }

        if (options.Features.Count > 0)
        {
            foreach (string feature in options.Features.Where(feature => !documents.Any(document => Matches(feature, document.Feature, document.Document.Name))))
            {
                notes.Add($"--feature '{feature}' matches no compared document.");
            }

            documents.RemoveAll(document => !options.Features.Any(feature => Matches(feature, document.Feature, document.Document.Name)));
            notCompared.RemoveAll(name => !options.Features.Any(feature => Matches(feature, XboxLiveFeatureNames.NameOf(name), name)));
        }

        documents.Sort((left, right) =>
        {
            int byScope = ScopeOrder(left.Document.Scope).CompareTo(ScopeOrder(right.Document.Scope));
            return byScope != 0 ? byScope : StringComparer.OrdinalIgnoreCase.Compare(left.Document.Name, right.Document.Name);
        });

        if (options.Lines)
        {
            XboxLiveDocumentDiff[] differing = [.. documents.Select(document => document.Document).Where(document => document.State != XboxLiveDocumentDifferences.Same)];
            IReadOnlyDictionary<string, byte[]> before = await ReadAsync(old, differing, scopeName, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, byte[]> after = await ReadAsync(@new, differing, scopeName, cancellationToken).ConfigureAwait(false);
            string oldLabel = old.Side.Describe(scopeName);
            string newLabel = @new.Side.Describe(scopeName);
            for (int index = 0; index < documents.Count; index++)
            {
                XboxLiveDocumentDiff document = documents[index].Document;
                if (document.State != XboxLiveDocumentDifferences.Same)
                {
                    (IReadOnlyList<string> lines, bool minimal) = XboxLiveLineDiff.Compare(
                        $"{document.Name} ({oldLabel})",
                        $"{document.Name} ({newLabel})",
                        before.TryGetValue(document.Name, out byte[]? oldContent) ? XboxLiveDocumentText.Decode(oldContent) : null,
                        after.TryGetValue(document.Name, out byte[]? newContent) ? XboxLiveDocumentText.Decode(newContent) : null);
                    if (lines.Count == 2)
                    {
                        // The bytes differ, but not the text's lines.
                        lines = [.. lines, OnlyLineEndingsDiffer];
                    }

                    if (!minimal)
                    {
                        notes.Add(FormattableString.Invariant(
                            $"The line diff of {document.Name} shows the changed lines removed and added whole: it has more than {XboxLiveLineDiff.MaxEdits} inserted and deleted lines."));
                    }

                    documents[index] = (documents[index].Feature, document with { Lines = lines });
                }
            }
        }

        XboxLiveFeatureDiff[] features =
        [
            .. documents
                .GroupBy(document => document.Feature, StringComparer.Ordinal)
                .Select(group => new XboxLiveFeatureDiff
                {
                    Name = group.Key,
                    Documents = [.. group.Select(document => document.Document)],
                }),
        ];
        return new XboxLiveComparisonResult(
            new XboxLiveConfigurationDiff
            {
                Scope = scopeName,
                Scid = scid,
                Old = old.Side,
                New = @new.Side,
                HasDifferences = documents.Any(document => document.Document.State != XboxLiveDocumentDifferences.Same),
                Features = features,
                NotCompared = [.. notCompared.Order(StringComparer.Ordinal)],
            },
            notes);
    }

    /// <summary>A local files side: each file's bytes and hash, under its file name.</summary>
    public static XboxLiveConfigurationSnapshot FromFiles(
        XboxLiveScope scope,
        IReadOnlyList<string> paths,
        IReadOnlyList<XboxDocumentUpload> documents) => new()
        {
            Side = new XboxLiveDiffSide { View = XboxLiveSideViews.Files, Files = paths },
            Origin = XboxLiveSnapshotOrigin.Files,
            Documents = documents.ToDictionary(
                document => document.Name,
                document => new XboxLiveSnapshotDocument(
                    document.Name,
                    XboxLiveDiffDocuments.ScopeOf(scope, document.Name),
                    Hash(document.Content),
                    document.Content),
                StringComparer.Ordinal),
        };

    /// <summary>A document's hash as XBOX services report it: uppercase hex SHA-1 of its bytes.</summary>
    public static string Hash(byte[] content) => Convert.ToHexString(SHA1.HashData(content));

    private static string ScopeOf(string name, XboxLiveConfigurationSnapshot old, XboxLiveConfigurationSnapshot @new) =>
        (@new.Documents.GetValueOrDefault(name) ?? old.Documents[name]).Scope;

    private static int ScopeOrder(string scope) => scope switch
    {
        XboxLiveConfigurationScopes.Sandbox => 0,
        XboxLiveConfigurationScopes.Title => 1,
        _ => 2,
    };

    private static bool Matches(string feature, string featureName, string documentName) =>
        string.Equals(feature, featureName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(feature, documentName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(feature, XboxLiveFeatureNames.TypeOf(documentName), StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyDictionary<string, byte[]>> ReadAsync(
        XboxLiveConfigurationSnapshot side,
        IReadOnlyList<XboxLiveDocumentDiff> documents,
        string scopeName,
        CancellationToken cancellationToken)
    {
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var missing = new List<XboxLiveSnapshotDocument>();
        foreach (XboxLiveDocumentDiff document in documents)
        {
            if (side.Documents.TryGetValue(document.Name, out XboxLiveSnapshotDocument? held))
            {
                if (held.Content is { } bytes)
                {
                    content[held.Name] = bytes;
                }
                else
                {
                    missing.Add(held);
                }
            }
        }

        if (missing.Count > 0)
        {
            if (side.ReadContentAsync is null)
            {
                throw new InvalidOperationException("The comparison can't read the documents' content.");
            }

            IReadOnlyDictionary<string, byte[]> read = await side.ReadContentAsync(missing, cancellationToken).ConfigureAwait(false);
            foreach (XboxLiveSnapshotDocument document in missing)
            {
                if (!read.TryGetValue(document.Name, out byte[]? bytes))
                {
                    throw new InvalidDataException($"The download didn't contain {document.Name}.");
                }

                if (!string.Equals(Hash(bytes), document.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"The download of {document.Name} doesn't match {side.Side.Describe(scopeName)}: its hash differs. Run the command again.");
                }

                content[document.Name] = bytes;
            }
        }

        return content;
    }
}

/// <summary>
/// How a comparison sorts documents into scopes when the service doesn't say: for a downloaded
/// version and for local files. XBOX services sort them the same way when they compare a draft
/// with its published version.
/// </summary>
internal static class XboxLiveDiffDocuments
{
    /// <summary>The prefix of a draft document's copy in a published version.</summary>
    public const string PublishedSourcePrefix = "source-";

    /// <summary>The prefix of internal documents that XBOX services leave out of a sandbox's comparison.</summary>
    private const string InternalPrefix = "xms";

    /// <summary>
    /// Document types that only the title's configuration holds: those that go live with each
    /// sandbox's publish (<see cref="XboxLiveConfigurationScopes.PublishesWithSandbox"/>), and these.
    /// </summary>
    private static readonly FrozenSet<string> TitleOnlyTypes =
        new[] { "productdeveloper", "productsystem", "globalaccesspolicies", "xrexceptions" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsInternal(string name) => name.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsTitleOnly(string name) =>
        XboxLiveConfigurationScopes.PublishesWithSandbox(name) || PublishesThroughHidden(name);

    /// <summary>Whether a title document goes live only through a title-wide publish to <c>HIDDEN</c>.</summary>
    public static bool PublishesThroughHidden(string name) => TitleOnlyTypes.Contains(XboxLiveFeatureNames.TypeOf(name));

    /// <summary>The scope a document of a configuration set belongs to.</summary>
    public static string ScopeOf(XboxLiveScope scope, string name) => scope switch
    {
        XboxLiveScope.Sandbox when XboxLiveConfigurationScopes.PublishesWithSandbox(name) => XboxLiveConfigurationScopes.Title,
        _ => XboxLiveConfigurationScopes.Name(scope),
    };
}

/// <summary>Reads a configuration archive's documents in memory, within the same limits as a download.</summary>
internal static class XboxLiveDocumentArchives
{
    internal const int MaxEntries = 256;
    internal const long MaxExtractedBytes = XboxPartnerClient.DefaultMaxDocumentArchiveBytes;

    public static IReadOnlyDictionary<string, byte[]> Read(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"The document archive contained more than {MaxEntries} documents.");
        }

        var documents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (CommandLine.LocalFileNameValidator.IsInvalid(name, enforceWindowsRules: true) ||
                !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
                !names.Add(name))
            {
                throw new InvalidDataException("The document archive contained an invalid or duplicate document name.");
            }

            using Stream source = entry.Open();
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxExtractedBytes)
                {
                    throw new InvalidDataException($"The document archive expands beyond {MaxExtractedBytes} bytes.");
                }

                buffer.Write(chunk, 0, read);
            }

            documents[name] = buffer.ToArray();
        }

        return documents;
    }
}
