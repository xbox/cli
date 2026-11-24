// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Reads the sides of a configuration comparison from XBOX services and compares them: a
/// scope's draft, its published version, any version by number, or local files.
/// </summary>
/// <remarks>
/// The draft and published sides come from the configuration status, which has every document's hash,
/// so they're read without downloading anything; their bytes are downloaded, pinned to the
/// status's versions, only for a line diff. Any other version is downloaded whole. Each side holds
/// the documents XBOX services compare for its scope, the same ones <c>config status</c> lists.
/// A title named by <paramref name="titleId"/> is resolved to its SCID by the first status read,
/// which every comparison starts with, and the rest of the reads use that SCID.
/// </remarks>
internal sealed class XboxLiveConfigurationComparer(
    XboxPartnerClient client,
    long? sellerId,
    XboxLiveScope scope,
    Guid? scid,
    string? titleId = null)
{
    private const string TitleDraftSandbox = XboxLiveConfigurationScope.TitleDraftSandbox;
    private const string TitlePublishedSandbox = XboxLiveConfigurationScope.TitleLiveSandbox;
    private const string WorkingView = "working";
    private const string PublishedView = "published";

    private readonly Dictionary<string, (XboxLiveConfigurationStatus Status, IReadOnlyList<string> Notes)> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, XboxLiveConfigurationSnapshot> _versions = [];

    /// <summary>
    /// The scope's draft compared with its published version, with what each save since the last
    /// publish changed, as <c>config status</c> describes it.
    /// </summary>
    public async Task<XboxLiveComparisonResult> CompareDraftWithLiveAsync(
        string? sandbox,
        XboxLiveComparisonOptions options,
        CancellationToken cancellationToken)
    {
        (XboxLiveConfigurationStatus status, IReadOnlyList<string> notes) = await ReadStatusAsync(sandbox, cancellationToken).ConfigureAwait(false);
        Dictionary<string, XboxLiveDocumentStatus> descriptions = (status.Documents ?? [])
            .Where(document => document.DescriptionStatus is not null)
            .ToDictionary(document => document.Name!, StringComparer.Ordinal);
        XboxLiveComparisonResult result = await XboxLiveConfigurationComparison
            .CompareAsync(scope, scid, Published(status, sandbox), Draft(status, sandbox), descriptions, options, cancellationToken)
            .ConfigureAwait(false);
        return result with { Notes = [.. notes, .. result.Notes] };
    }

    /// <summary>
    /// Compares two sides. A sandbox's published version compared with its own draft is described as
    /// <see cref="CompareDraftWithLiveAsync"/> describes it, from the same status.
    /// </summary>
    public Task<XboxLiveComparisonResult> CompareAsync(
        XboxLiveConfigurationSnapshot old,
        XboxLiveConfigurationSnapshot @new,
        XboxLiveComparisonOptions options,
        CancellationToken cancellationToken) =>
        old.Origin == XboxLiveSnapshotOrigin.Published
        && @new.Origin == XboxLiveSnapshotOrigin.Draft
        && string.Equals(old.Side.Sandbox, @new.Side.Sandbox, StringComparison.Ordinal)
            ? CompareDraftWithLiveAsync(@new.Side.Sandbox, options, cancellationToken)
            : XboxLiveConfigurationComparison.CompareAsync(scope, scid, old, @new, null, options, cancellationToken);

    /// <summary>The scope's latest draft. <paramref name="sandbox"/> names the sandbox, for the sandbox scope.</summary>
    public async Task<XboxLiveConfigurationSnapshot> GetDraftAsync(string? sandbox, CancellationToken cancellationToken) =>
        Draft(await GetStatusAsync(sandbox, cancellationToken).ConfigureAwait(false), sandbox);

    /// <summary>
    /// A version of <paramref name="sandbox"/>'s configuration, or of the title's or the account's
    /// for those scopes, by its number: the draft or published version it names.
    /// </summary>
    public async Task<XboxLiveConfigurationSnapshot> GetVersionAsync(
        ulong version,
        string? sandbox,
        CancellationToken cancellationToken)
    {
        if (!_versions.TryGetValue(version, out XboxLiveConfigurationSnapshot? snapshot))
        {
            snapshot = await FindVersionAsync(version, sandbox, cancellationToken).ConfigureAwait(false);
            _versions[version] = snapshot;
        }

        return snapshot;
    }

    /// <summary>Local files, compared under their file names.</summary>
    public XboxLiveConfigurationSnapshot FromFiles(IReadOnlyList<string> paths, IReadOnlyList<XboxDocumentUpload> documents) =>
        XboxLiveConfigurationComparison.FromFiles(scope, paths, documents);

    /// <summary>
    /// A scope's current draft or published version reads from its status, which has every document's
    /// hash. Any other version is downloaded from the scope's drafts, and then, if they don't hold
    /// it, from its published versions.
    /// </summary>
    private async Task<XboxLiveConfigurationSnapshot> FindVersionAsync(
        ulong version,
        string? sandbox,
        CancellationToken cancellationToken)
    {
        XboxLiveConfigurationStatus status = await GetStatusAsync(sandbox, cancellationToken).ConfigureAwait(false);
        if (status.Draft?.Version == version)
        {
            return Draft(status, sandbox);
        }

        if (status.Published?.Version == version)
        {
            return Published(status, sandbox);
        }

        XboxPartnerDocumentArchive? archive = null;
        bool published = false;
        string? notFoundCorrelationId = null;
        foreach ((string? set, string view) in new[]
                 {
                     (DraftSet(sandbox), WorkingView),
                     (PublishedSet(sandbox), PublishedView),
                 })
        {
            try
            {
                archive = await FetchAsync(set, view, version, [], cancellationToken).ConfigureAwait(false);
                published = view == PublishedView;
                break;
            }
            catch (XboxPartnerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                notFoundCorrelationId = ex.CorrelationId;
            }
        }

        if (archive is null)
        {
            string where = scope switch
            {
                XboxLiveScope.Sandbox => sandbox!,
                XboxLiveScope.Title => "the title",
                _ => "the account",
            };
            string statusOptions = scope switch
            {
                XboxLiveScope.Sandbox => $"--scid {scid:D} --sandbox-id {sandbox}",
                XboxLiveScope.Title => $"--scope title --scid {scid:D}",
                _ => "--scope account",
            };
            throw new XboxLiveComparisonException(
                $"Version {version} wasn't found in {where}'s draft or published versions. Run " +
                $"'{CliIdentity.CommandName} xbl config status {statusOptions}' to see its version numbers" +
                (scope == XboxLiveScope.Sandbox ? ", and name another sandbox's with that sandbox's --sandbox-id." : "."),
                notFoundCorrelationId);
        }

        var documents = new Dictionary<string, XboxLiveSnapshotDocument>(StringComparer.Ordinal);
        foreach ((string entry, byte[] content) in XboxLiveDocumentArchives.Read(archive.Content))
        {
            string? name = published ? SourceName(entry) : entry;
            string? documentScope = name is null ? null : ScopeOf(name, published);
            if (documentScope is not null)
            {
                documents[name!] = new XboxLiveSnapshotDocument(name!, documentScope, XboxLiveConfigurationComparison.Hash(content), content);
            }
        }

        return new XboxLiveConfigurationSnapshot
        {
            Side = new XboxLiveDiffSide
            {
                View = published ? XboxLiveSideViews.Published : XboxLiveSideViews.Draft,
                Sandbox = SideSandbox(sandbox),
                Version = archive.Version,
            },
            Origin = XboxLiveSnapshotOrigin.Version,
            Documents = documents,
            HoldsTitleDocuments = scope != XboxLiveScope.Sandbox || published,
        };
    }
    private async Task<XboxLiveConfigurationStatus> GetStatusAsync(string? sandbox, CancellationToken cancellationToken) =>
        (await ReadStatusAsync(sandbox, cancellationToken).ConfigureAwait(false)).Status;

    /// <summary>
    /// A scope's status, read once, as <c>config status</c> reads it: for a sandbox, with what
    /// each save since the last publish changed, and a note when that couldn't be read.
    /// </summary>
    private async Task<(XboxLiveConfigurationStatus Status, IReadOnlyList<string> Notes)> ReadStatusAsync(
        string? sandbox,
        CancellationToken cancellationToken)
    {
        if (!_statuses.TryGetValue(Key(sandbox), out var read))
        {
            read = await ConfigStatusCommandFactory
                .GetAsync(client, sellerId, scope, scid, sandbox, cancellationToken, scid is null ? titleId : null, readLastPublish: false)
                .ConfigureAwait(false);
            scid ??= read.Status.Scid;
            _statuses[Key(sandbox)] = read;
        }

        return read;
    }
    /// <summary>
    /// The scope a document of a found version belongs to, or null when the scope's comparison
    /// leaves it out. A published sandbox version also holds copies of the title's documents; only
    /// those that go live with the sandbox's own publish belong to the sandbox.
    /// </summary>
    private string? ScopeOf(string name, bool published)
    {
        switch (scope)
        {
            case XboxLiveScope.Account:
                return XboxLiveConfigurationScopes.Account;
            case XboxLiveScope.Title:
                return XboxLiveConfigurationScopes.PublishesWithSandbox(name) ? null : XboxLiveConfigurationScopes.Title;
        }

        if (XboxLiveDiffDocuments.IsInternal(name))
        {
            return null;
        }

        if (XboxLiveConfigurationScopes.PublishesWithSandbox(name))
        {
            return published ? XboxLiveConfigurationScopes.Title : null;
        }

        if (published && XboxLiveDiffDocuments.IsTitleOnly(name))
        {
            return null;
        }

        return XboxLiveConfigurationScopes.Sandbox;
    }

    private XboxLiveConfigurationSnapshot Draft(XboxLiveConfigurationStatus status, string? sandbox) => new()
    {
        Side = XboxLiveDiffSide.From(XboxLiveSideViews.Draft, SideSandbox(sandbox), status.Draft),
        Origin = XboxLiveSnapshotOrigin.Draft,
        Documents = Documents(status, document => document.DraftHash),
        ReadContentAsync = async (documents, cancellationToken) =>
        {
            var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (IGrouping<bool, XboxLiveSnapshotDocument> group in documents.GroupBy(document =>
                         scope == XboxLiveScope.Sandbox && document.Scope == XboxLiveConfigurationScopes.Title))
            {
                (string? from, ulong? version) = scope switch
                {
                    XboxLiveScope.Sandbox when group.Key => (TitleDraftSandbox, status.TitleDraft?.Version),
                    XboxLiveScope.Sandbox => (sandbox, status.Draft?.Version),
                    XboxLiveScope.Title => (TitleDraftSandbox, status.Draft?.Version),
                    _ => (null, status.Draft?.Version),
                };
                foreach ((string name, byte[] bytes) in await DownloadAsync(from, WorkingView, version, group, string.Empty, cancellationToken).ConfigureAwait(false))
                {
                    content[name] = bytes;
                }
            }

            return content;
        },
    };

    private XboxLiveConfigurationSnapshot Published(XboxLiveConfigurationStatus status, string? sandbox) => new()
    {
        Side = XboxLiveDiffSide.From(XboxLiveSideViews.Published, SideSandbox(sandbox), status.Published),
        Origin = XboxLiveSnapshotOrigin.Published,
        Documents = Documents(status, document => document.PublishedHash),
        ReadContentAsync = (documents, cancellationToken) => DownloadAsync(
            PublishedSet(sandbox),
            PublishedView,
            status.Published?.Version,
            documents,
            XboxLiveDiffDocuments.PublishedSourcePrefix,
            cancellationToken),
    };

    private string? SideSandbox(string? sandbox) => scope == XboxLiveScope.Sandbox ? sandbox : null;

    /// <summary>The sandbox whose draft holds the scope's draft: the title's is <c>ALL</c>.</summary>
    private string? DraftSet(string? sandbox) => scope switch
    {
        XboxLiveScope.Sandbox => sandbox,
        XboxLiveScope.Title => TitleDraftSandbox,
        _ => null,
    };

    /// <summary>The sandbox whose published view holds the scope's published versions: the title's is <c>HIDDEN</c>.</summary>
    private string? PublishedSet(string? sandbox) => scope switch
    {
        XboxLiveScope.Sandbox => sandbox,
        XboxLiveScope.Title => TitlePublishedSandbox,
        _ => null,
    };

    /// <summary>
    /// The status's documents that hold <paramref name="hash"/>. The title's status also lists the
    /// title documents each sandbox's own publish makes live; a publish to <c>HIDDEN</c> doesn't
    /// change them, so the title's comparison leaves them out, as <c>config status</c> does.
    /// </summary>
    private Dictionary<string, XboxLiveSnapshotDocument> Documents(
        XboxLiveConfigurationStatus status,
        Func<XboxLiveDocumentStatus, string?> hash) =>
        (status.Documents ?? [])
            .Where(document => hash(document) is not null
                && !string.Equals(document.PublishesWith, XboxLiveConfigurationScopes.Sandbox, StringComparison.Ordinal))
            .ToDictionary(
                document => document.Name!,
                document => new XboxLiveSnapshotDocument(
                    document.Name!,
                    document.Scope ?? XboxLiveConfigurationScopes.Name(scope),
                    hash(document)!),
                StringComparer.Ordinal);

    /// <summary>
    /// Downloads the named documents of one version, and returns them by document name. A published
    /// version's copies of draft documents are named with <paramref name="prefix"/>. A null
    /// <paramref name="version"/> reads the latest, when the status names no version; the
    /// documents' hashes confirm the download matches.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, byte[]>> DownloadAsync(
        string? sandbox,
        string view,
        ulong? version,
        IEnumerable<XboxLiveSnapshotDocument> documents,
        string prefix,
        CancellationToken cancellationToken)
    {
        XboxPartnerDocumentArchive archive = await FetchAsync(
            sandbox,
            view,
            version,
            [.. documents.Select(document => prefix + document.Name)],
            cancellationToken).ConfigureAwait(false);
        return XboxLiveDocumentArchives.Read(archive.Content)
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) && entry.Key.Length > prefix.Length)
            .ToDictionary(entry => entry.Key[prefix.Length..], entry => entry.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Downloads <paramref name="documents"/> (all of them when empty) from one view of a version,
    /// and checks the service returned that view and version.
    /// </summary>
    private async Task<XboxPartnerDocumentArchive> FetchAsync(
        string? sandbox,
        string view,
        ulong? version,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        bool account = scope == XboxLiveScope.Account;
        XboxPartnerDocumentArchive archive = await client.GetXboxLiveDocumentsAsync(
            sellerId,
            new XboxLiveGetDocumentsRequest(
                account ? "account" : "sandbox",
                account ? null : scid,
                account ? null : sandbox,
                view,
                version,
                documents),
            cancellationToken).ConfigureAwait(false);
        if ((version is not null && archive.Version != version) || !string.Equals(archive.View, view, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(FormattableString.Invariant(
                $"The download returned {DisplayView(archive.View)} version {archive.Version}, not {DisplayView(view)} version {version}. Run the command again."));
        }

        return archive;
    }

    private static string DisplayView(string view) =>
        string.Equals(view, WorkingView, StringComparison.OrdinalIgnoreCase) ? XboxLiveSideViews.Draft : view;

    private static string? SourceName(string entry) =>
        entry.StartsWith(XboxLiveDiffDocuments.PublishedSourcePrefix, StringComparison.Ordinal)
        && entry.Length > XboxLiveDiffDocuments.PublishedSourcePrefix.Length
            ? entry[XboxLiveDiffDocuments.PublishedSourcePrefix.Length..]
            : null;

    private static string Key(string? sandbox) => sandbox ?? string.Empty;
}

/// <summary>A comparison that can't run as asked, with a message that says what to do instead.</summary>
internal sealed class XboxLiveComparisonException(string message, string? correlationId = null) : Exception(message)
{
    public string? CorrelationId { get; } = correlationId;
}
