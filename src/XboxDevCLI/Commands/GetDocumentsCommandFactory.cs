// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static class GetDocumentsCommandFactory
{
    internal const int MaxArchiveEntries = 256;
    internal const long MaxExtractedArchiveBytes =
        XboxPartnerClient.DefaultMaxDocumentArchiveBytes;

    public static Command Create()
    {
        var sellerOption = new XdtsSellerOption();
        var scope = new XboxLiveConfigurationScope("Case-sensitive sandbox ID.", account: true);
        var destinationOption = new Option<string>("--dest")
        {
            Description = "Directory where downloaded documents and ETag metadata are saved.",
            Required = true,
        };
        var viewOption = new Option<string>("--view")
        {
            Description = "Document set to download: 'draft' (the working set) or 'published' (the live set).",
            Required = true,
        };
        viewOption.Validators.Add(result =>
            ValidateAllowedValue(result, "draft", "published"));
        viewOption.CompletionSources.Add("draft", "published");
        var versionOption = new Option<ulong?>("--version")
        {
            Description = "Historical document-set version. Default: latest version.",
        };
        versionOption.Validators.Add(result =>
        {
            // Read the token rather than the value, which throws when it didn't parse; the
            // parser reports that itself.
            if (result.Tokens.Count > 0 &&
                ulong.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value) &&
                value == 0)
            {
                result.AddError("--version must be positive when provided.");
            }
        });
        var documentOption = new Option<string[]>("--document")
        {
            Description = "Document filename filter. Default: all documents. May be repeated.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command(
            "download",
            "Download XBOX services configuration documents");
        command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: "A sandbox's, the title-wide, or the account's XBOX services configuration document set.",
            Output: "Extracted documents, ETag metadata, and a JSON or table download summary.",
            Examples:
            [
                "xbox xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --view draft --dest xbox-config",
                "xbox xbl config download --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --view published --dest xbox-config --output table",
                "xbox xbl config download --title-id 1234ABCD --scope title --view draft --dest xbox-config-title",
                "xbox xbl config download --scope account --view draft --dest xbox-config-account",
                "xbox xbl config download --title-id 1234ABCD --sandbox-id XDKS.1 --view draft --dest xbox-config",
            ],
            Table: XboxPartnerCommandSupport.DocumentDownloadTableView));
        sellerOption.AddTo(command);
        scope.AddTo(command);
        command.Options.Add(destinationOption);
        command.Options.Add(viewOption);
        command.Options.Add(versionOption);
        command.Options.Add(documentOption);
        command.Validators.Add(result =>
        {
            if (!IsValidEtagSandbox(scope.GetSandboxToken(result)))
            {
                result.AddError("--sandbox-id can't contain path characters.");
            }
        });
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var output = OutputOptions.From(parseResult);
            var verbose = parseResult.GetValue(Program.VerboseOption);
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
                bool published = string.Equals(
                    parseResult.GetValue(viewOption), "published", StringComparison.OrdinalIgnoreCase);
                var request = new XboxLiveGetDocumentsRequest(
                    selected.Scope == XboxLiveScope.Account ? "account" : "sandbox",
                    selected.Scid,
                    published ? selected.LiveSandbox : selected.DraftSandbox,
                    published ? "published" : "working",
                    parseResult.GetValue(versionOption),
                    parseResult.GetValue(documentOption) ?? [],
                    selected.TitleId);
                Program.WriteVerbose(
                    verbose,
                    () => $"Download {DisplayView(request.View)} documents to " +
                        parseResult.GetValue(destinationOption) +
                        $" for {selected.Describe()}");
                return await RunAsync(
                    client,
                    selected.SellerId,
                    request,
                    parseResult.GetValue(destinationOption)!,
                    output,
                    Console.Out,
                    Console.Error,
                    cancellationToken,
                    verbose).ConfigureAwait(false);
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

    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        XboxLiveGetDocumentsRequest request,
        string destination,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        bool verbose = false)
    {
        bool compact = output.IsCompact;
        TableView? tableView = output.TableView;
        try
        {
            XboxPartnerDocumentArchive archive;
            using (RequestProgress.Start(DescribeDownload(request), stderr))
            {
                archive = await client
                    .GetXboxLiveDocumentsAsync(sellerId, request, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (verbose && archive.Target is { } effectiveTarget)
            {
                XboxLiveConfigurationTarget.WriteResolved(effectiveTarget, sellerId, stderr);
            }
            string directory = Path.GetFullPath(destination);
            XboxLiveScope scope = XboxLiveConfigurationScope.ScopeOf(request.DocumentType, request.Sandbox, IsPublished(request));
            string eTagFileName = XboxLiveConfigurationScope.EtagFileName(scope, request.Sandbox);
            if (LocalFileNameValidator.IsInvalid(
                    eTagFileName,
                    enforceWindowsRules: OperatingSystem.IsWindows()))
            {
                throw new InvalidDataException(
                    "The sandbox name cannot be used for ETag metadata.");
            }
            string eTagPath = Path.Combine(directory, eTagFileName);
            ValidateEtagPath(eTagPath);
            string[] paths = await ExtractArchiveAsync(
                archive.Content,
                directory,
                MaxExtractedArchiveBytes,
                MaxArchiveEntries,
                cancellationToken).ConfigureAwait(false);
            await WriteEtagAsync(eTagPath, archive.ETag, cancellationToken)
                .ConfigureAwait(false);

            stdout.WriteLine(tableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    tableView,
                    [
                        [
                            XboxLiveConfigurationScope.Name(scope),
                            scope == XboxLiveScope.Sandbox ? request.Sandbox ?? "" : "",
                            DisplayView(archive.View),
                            archive.Version.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            paths.Length.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            directory,
                        ],
                    ])
                : JsonSerializer.Serialize(
                    new
                    {
                        scope = XboxLiveConfigurationScope.Name(scope),
                        view = DisplayView(archive.View),
                        version = archive.Version,
                        etag = archive.ETag,
                        etagFile = eTagPath,
                        files = paths,
                    },
                    compact ? CompactJsonOptions : PrettyJsonOptions));
            return 0;
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

    internal static string DescribeDownload(XboxLiveGetDocumentsRequest request) =>
        XboxLiveConfigurationScope.ScopeOf(request.DocumentType, request.Sandbox, IsPublished(request)) switch
        {
            XboxLiveScope.Account => $"Downloading {DisplayView(request.View)} account documents",
            XboxLiveScope.Title => $"Downloading {DisplayView(request.View)} title-wide documents",
            _ => $"Downloading {DisplayView(request.View)} documents from {request.Sandbox}",
        };

    private static bool IsPublished(XboxLiveGetDocumentsRequest request) =>
        string.Equals(request.View, "published", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>--view</c> name for a service view: the service calls the draft set "working".</summary>
    internal static string DisplayView(string view) =>
        string.Equals(view, "working", StringComparison.OrdinalIgnoreCase) ? "draft" : view;

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
    };

    internal static async Task<string[]> ExtractArchiveAsync(
        byte[] content,
        string directory,
        long maxExtractedBytes,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExtractedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > maxEntries)
        {
            throw new InvalidDataException(
                $"The document archive contained more than {maxEntries} documents.");
        }

        var entries = new List<(ZipArchiveEntry Entry, string Name)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (LocalFileNameValidator.IsInvalid(name, enforceWindowsRules: true) ||
                !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
                !names.Add(name))
            {
                throw new InvalidDataException(
                    "The document archive contained an invalid or duplicate document name.");
            }

            entries.Add((entry, name));
        }

        directory = SafeOutputDirectory.Create(directory);
        string stagingDirectory = Path.Combine(
            directory,
            $".xboxdevcli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            long totalExtractedBytes = 0;
            foreach ((ZipArchiveEntry entry, string name) in entries)
            {
                string stagedPath = Path.Combine(stagingDirectory, name);
                await using Stream source = entry.Open();
                await using var target = new FileStream(
                    stagedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                totalExtractedBytes += await CopyWithLimitAsync(
                    source,
                    target,
                    maxExtractedBytes - totalExtractedBytes,
                    maxExtractedBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            string[] paths = entries
                .Select(entry => Path.Combine(directory, entry.Name))
                .ToArray();
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    throw new IOException(
                        $"Cannot replace document '{Path.GetFileName(path)}' because the " +
                        "destination is a directory.");
                }

                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException(
                            $"Cannot replace document '{Path.GetFileName(path)}' because the " +
                            "destination is a reparse point.");
                    }
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            for (int index = 0; index < entries.Count; index++)
            {
                string name = entries[index].Name;
                string path = paths[index];
                File.Move(Path.Combine(stagingDirectory, name), path, overwrite: true);
            }

            return paths;
        }
        finally
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static async Task<long> CopyWithLimitAsync(
        Stream source,
        Stream target,
        long remainingBytes,
        long maxExtractedBytes,
        CancellationToken cancellationToken)
    {
        if (remainingBytes < 0)
        {
            throw ExtractedArchiveTooLarge(maxExtractedBytes);
        }

        byte[] buffer = new byte[81920];
        long bytesWritten = 0;
        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return bytesWritten;
            }

            if (bytesRead > remainingBytes - bytesWritten)
            {
                throw ExtractedArchiveTooLarge(maxExtractedBytes);
            }

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
            bytesWritten += bytesRead;
        }
    }

    private static InvalidDataException ExtractedArchiveTooLarge(long maxExtractedBytes) =>
        new(
            $"The document archive expands beyond " +
            $"{maxExtractedBytes} bytes.");

    private static void ValidateEtagPath(string eTagPath)
    {
        if (Directory.Exists(eTagPath))
        {
            throw new IOException("The existing ETag path is a directory.");
        }

        if (!File.Exists(eTagPath))
        {
            return;
        }
        FileAttributes attributes = File.GetAttributes(eTagPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The existing ETag path is a reparse point.");
        }
    }

    private static async Task WriteEtagAsync(
        string eTagPath,
        string eTag,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(eTagPath)!,
            $".xboxdevcli-etag-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, eTag, cancellationToken)
                .ConfigureAwait(false);
            FileAttributes? originalAttributes = null;
            if (File.Exists(eTagPath))
            {
                originalAttributes = File.GetAttributes(eTagPath);
                if ((originalAttributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The existing ETag path is a reparse point.");
                }

                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(
                        eTagPath,
                        originalAttributes.Value & ~FileAttributes.Hidden);
                }
            }

            try
            {
                File.Move(temporaryPath, eTagPath, overwrite: true);
            }
            catch
            {
                if (originalAttributes.HasValue && File.Exists(eTagPath))
                {
                    File.SetAttributes(eTagPath, originalAttributes.Value);
                }

                throw;
            }

            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(
                    eTagPath,
                    File.GetAttributes(eTagPath) | FileAttributes.Hidden);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>Whether a sandbox ID can name its ETag file: no path characters, and not <c>.</c> or <c>..</c>.</summary>
    internal static bool IsValidEtagSandbox(string? sandbox)
    {
        string value = sandbox?.Trim() ?? string.Empty;
        return value.IndexOfAny(['/', '\\', ':', '\0']) < 0 &&
            value is not "." and not "..";
    }

    private static void ValidateAllowedValue(
        OptionResult result,
        params string[] allowedValues)
    {
        if (result.Tokens.Count == 0)
        {
            return;
        }

        string value = result.Tokens[0].Value;
        if (!allowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            result.AddError(
                $"Invalid value '{value}' for option '{result.Option.Name}'. " +
                $"Allowed values: {string.Join(", ", allowedValues)}.");
        }
    }
}
