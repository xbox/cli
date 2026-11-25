// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// After a title-wide publish, says which sandboxes still need their own publish to take its stats
/// system, privilege, and single sign-on changes. Those documents are kept with the title-wide
/// configuration, but each sandbox takes them only when that sandbox publishes.
/// </summary>
internal static class TitleWidePublishHint
{
    /// <summary>The title-wide documents a sandbox takes when it publishes, and what each configures.</summary>
    internal static readonly (string Document, string Feature)[] PerSandboxDocuments =
    [
        ("dataplatformconfig.xml", "stats system"),
        ("titlepriv.xml", "privilege"),
        ("nsal.xml", "single sign-on"),
    ];

    /// <summary>The prefix XBOX services give a working document's copy in a published set.</summary>
    private const string PublishedCopyPrefix = "source-";

    /// <summary>How many sandboxes' copies the hint reads at once.</summary>
    private const int MaxConcurrentReads = 4;

    /// <summary>What the hint says when the CLI can't tell which sandboxes need a publish.</summary>
    internal const string Unchecked =
        "Stats system, privilege, and single sign-on changes in this publish go live in each sandbox only when " +
        "that sandbox publishes.";

    /// <summary>
    /// Writes the hint to stderr: a sentence naming the changes, then a ready-to-run publish for each published
    /// sandbox whose copy of those documents differs from the title-wide draft. Writes nothing when no sandbox
    /// differs, and the sentence alone when the reads fail.
    /// </summary>
    public static async Task WriteAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLivePublishRequest request,
        XboxLiveResolvedConfigurationTarget? resolved,
        string? recoveryPrefix,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The publish already started and its result is written; Ctrl+C only skips the hint.
            return;
        }

        string? hint;
        try
        {
            using (RequestProgress.Start("Checking which sandboxes need a publish", stderr))
            {
                hint = await FormatAsync(client, sellerId, request, resolved, recoveryPrefix, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (!XboxPartnerCommandSupport.IsCallerCancellation(ex, cancellationToken))
        {
            hint = Unchecked;
        }

        if (hint is not null)
        {
            stderr.WriteLine(hint);
        }
    }

    internal static async Task<string?> FormatAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLivePublishRequest request,
        XboxLiveResolvedConfigurationTarget? resolved,
        string? recoveryPrefix,
        CancellationToken cancellationToken)
    {
        // A sandbox publish takes the latest ALL draft of these documents, so comparing with the latest draft
        // says which sandboxes don't match what their next publish applies.
        // Every read uses the configuration the publish resolved, so a binding change can't mix two.
        long? readSeller = resolved?.SellerId ?? sellerId;
        Guid? readScid = resolved?.Scid ?? request.Scid;
        string? readTitle = resolved is null ? request.TitleId : null;
        string[] names = [.. PerSandboxDocuments.Select(document => document.Document)];
        Dictionary<string, byte[]> draft = await ReadAsync(
            client, readSeller, readScid, readTitle, XboxLiveConfigurationScope.TitleDraftSandbox, "working",
            names, cancellationToken).ConfigureAwait(false);
        if (draft.Count == 0)
        {
            return null;
        }

        // A sandbox's own published set keeps the copies it took at its last publish, so comparing with it
        // doesn't depend on whether the title-wide publish, which runs in the background, has finished.
        string[] sandboxes = ReadPublishedSandboxes(await client
            .GetXboxLiveConfigurationsAsync(readSeller, readScid, cancellationToken, readTitle)
            .ConfigureAwait(false));
        using var reads = new SemaphoreSlim(MaxConcurrentReads);
        Dictionary<string, byte[]>[] allCopies = await Task.WhenAll(sandboxes.Select(async sandbox =>
        {
            await reads.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReadAsync(
                    client, readSeller, readScid, readTitle, sandbox, "published",
                    [.. names.Select(name => PublishedCopyPrefix + name)], cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                reads.Release();
            }
        })).ConfigureAwait(false);

        var behind = new List<string>();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sandboxes.Length; i++)
        {
            string sandbox = sandboxes[i];
            Dictionary<string, byte[]> copies = allCopies[i];
            bool differs = false;
            foreach ((string document, string feature) in PerSandboxDocuments)
            {
                if (draft.TryGetValue(document, out byte[]? content) &&
                    !(copies.TryGetValue(PublishedCopyPrefix + document, out byte[]? copy) &&
                      content.AsSpan().SequenceEqual(copy)))
                {
                    changed.Add(feature);
                    differs = true;
                }
            }

            if (differs)
            {
                behind.Add(sandbox);
            }
        }

        if (behind.Count == 0)
        {
            return null;
        }

        string[] features = [.. PerSandboxDocuments.Select(document => document.Feature).Where(changed.Contains)];
        string listed = features.Length switch
        {
            1 => features[0],
            2 => $"{features[0]} and {features[1]}",
            _ => $"{features[0]}, {features[1]}, and {features[2]}",
        };
        // Name the target the user gave. A publish by --title-id is checked for configuration other titles
        // share, and one by a bound title is refused; a publish by --scid is neither.
        string target = XboxPartnerCommandSupport.FormatConfigurationTarget(null, request.Scid, request.TitleId, readSeller);
        string prefix = recoveryPrefix ?? CliIdentity.CommandName;
        return
            $"{char.ToUpperInvariant(listed[0])}{listed[1..]} changes in this publish go live in each sandbox only " +
            "when that sandbox publishes." + Environment.NewLine +
            string.Join(
                Environment.NewLine,
                behind.Select(sandbox =>
                    $"Next: {prefix} xbl config publish {target} --sandbox-id {ShellQuoting.Quote(sandbox)}"));
    }

    /// <summary>
    /// The named documents of one set, by name, or none when the set doesn't exist. Fails, like
    /// <c>config download</c>, when the documents expand beyond its limit.
    /// </summary>
    private static async Task<Dictionary<string, byte[]>> ReadAsync(
        XboxPartnerClient client,
        long? sellerId,
        Guid? scid,
        string? titleId,
        string sandbox,
        string view,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        XboxPartnerDocumentArchive archive;
        try
        {
            archive = await client.GetXboxLiveDocumentsAsync(
                sellerId,
                new XboxLiveGetDocumentsRequest("sandbox", scid, sandbox, view, null, documents, titleId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (XboxPartnerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long remaining = GetDocumentsCommandFactory.MaxExtractedArchiveBytes;
        byte[] buffer = new byte[81920];
        using var stream = new MemoryStream(archive.Content, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using var content = new MemoryStream();
            await using (Stream source = entry.Open())
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    remaining -= read;
                    if (remaining < 0)
                    {
                        throw new InvalidDataException(
                            $"The document archive expands beyond {GetDocumentsCommandFactory.MaxExtractedArchiveBytes} bytes.");
                    }

                    content.Write(buffer, 0, read);
                }
            }

            contents[entry.FullName] = content.ToArray();
        }

        return contents;
    }

    /// <summary>The sandboxes in a configuration list that have been published, other than HIDDEN and ALL.</summary>
    private static string[] ReadPublishedSandboxes(string response) =>
    [
        .. (JsonSerializer.Deserialize<IReadOnlyList<XboxLiveConfigurationSummary>>(
                response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [])
            .Where(row => row.HasPublishedConfiguration && !XboxLiveConfigurationScope.IsTitleWide(row.Sandbox))
            .Select(row => row.Sandbox),
    ];
}
