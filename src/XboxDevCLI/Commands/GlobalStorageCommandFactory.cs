// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Builds <c>xbox xbl global-storage</c>, which reads a sandbox's global title storage usage, and lists,
/// downloads, uploads, and deletes its files: the files every player of the game reads in that sandbox.
/// </summary>
/// <remarks>
/// The service names a sandbox's storage by SCID, so a title ID is first looked up through the sandbox's
/// configuration status, as the configuration commands resolve it.
/// </remarks>
internal static class GlobalStorageCommandFactory
{
    private const string FileExistsCode = "global_storage_file_exists";

    private const string Scope =
        "One sandbox of an XBOX title: --scid or --title-id, and --sandbox-id. --seller-id selects the seller, "
        + "or your only seller.";

    private const string FileOperationOutput =
        " Messages are for people and may change, so scripts should check the exit code. --output only "
        + "changes how an error is written, and --output table is refused.";

    private static readonly string[] FileTypes = ["binary", "json", "config"];

    private static readonly TableView UsageTable = TableView.Record(
    [
        new TableColumn("USED BYTES", Camel(nameof(XboxLiveGlobalStorageUsage.UsedBytes)), 12),
        new TableColumn("QUOTA BYTES", Camel(nameof(XboxLiveGlobalStorageUsage.QuotaBytes)), 12),
    ]);

    // `list` prints a bare array; the collection name only names it in errors.
    private static readonly TableView FilesTable = new(
        "files",
        [
            new TableColumn("PATH", Camel(nameof(XboxLiveGlobalStorageFile.Path)), 60),
            new TableColumn("TYPE", Camel(nameof(XboxLiveGlobalStorageFile.Type)), 6),
            new TableColumn("SIZE", Camel(nameof(XboxLiveGlobalStorageFile.Size)), 10),
            new TableColumn("UPLOADED", Camel(nameof(XboxLiveGlobalStorageFile.ClientFileTime)), 25),
        ]);

    public static Command Create(
        XboxPartnerJobDependencies? dependencies = null,
        Func<Stream>? openStandardOutput = null,
        Func<Stream>? openStandardInput = null)
    {
        var storage = new Command(
            "global-storage",
            "Get usage, and list, download, upload, and delete the files every player of a game reads in a sandbox");
        storage.Subcommands.Add(CreateGet());
        storage.Subcommands.Add(CreateList());
        storage.Subcommands.Add(CreateDownload(openStandardOutput ?? Console.OpenStandardOutput));
        storage.Subcommands.Add(CreateUpload(openStandardInput ?? Console.OpenStandardInput));
        storage.Subcommands.Add(CreateDelete(dependencies ?? XboxPartnerJobDependencies.Default));
        return storage;
    }

    private static Command CreateGet()
    {
        var target = new Target();
        var command = new Command("get", "Show how much of its quota a sandbox's global storage uses");
        target.AddTo(command);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, target, async (client, selection) =>
            {
                Resolved storage = await ResolveAsync(result, client, selection, change: false, cancellationToken);
                XboxLiveGlobalStorageUsage usage;
                using (RequestProgress.Start($"Reading the global storage usage of {storage.Sandbox}"))
                {
                    usage = await client.GetGlobalStorageUsageAsync(
                        storage.SellerId, storage.Scid, storage.Sandbox, cancellationToken);
                }

                var output = OutputOptions.From(result);
                if (output.TableView is { } view)
                {
                    using JsonDocument document = JsonDocument.Parse(usage.RawResponse);
                    Console.WriteLine(TableFormatter.Format(document.RootElement, view));
                }
                else
                {
                    Console.WriteLine(PlayFabApiClient.FormatJson(usage.RawResponse, output.IsCompact));
                }

                return 0;
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: Scope,
            Output: "A JSON object with usedBytes, the bytes the sandbox's files use, and quotaBytes, its quota: "
                + "once the files use that many bytes, uploads fail. The upload that crosses the quota succeeds, so "
                + "usedBytes can be higher.",
            Examples:
            [
                "xbox xbl global-storage get --title-id 1234ABCD --sandbox-id XDKS.1",
                "xbox xbl global-storage get --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --output table",
            ],
            Table: UsageTable));
    }

    private static Command CreateList()
    {
        var target = new Target();
        var path = new Option<string?>("--path")
        {
            Description = "Only files whose path starts with this folder, such as news/. Default: every file.",
        };
        var command = new Command("list", "List a sandbox's global storage files");
        target.AddTo(command);
        command.Options.Add(path);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, target, async (client, selection) =>
            {
                Resolved storage = await ResolveAsync(result, client, selection, change: false, cancellationToken);
                string files;
                using (var progress = ListProgress.Start($"Reading the global storage files of {storage.Sandbox}"))
                {
                    files = await client.ListGlobalStorageFilesAsync(
                        storage.SellerId, storage.Scid, storage.Sandbox, result.GetValue(path), cancellationToken,
                        progress.PageRead);
                }

                var output = OutputOptions.From(result);
                if (output.TableView is { } view)
                {
                    using JsonDocument document = JsonDocument.Parse(files);
                    Console.WriteLine(TableFormatter.FormatCollection(document.RootElement, view));
                }
                else
                {
                    Console.WriteLine(PlayFabApiClient.FormatJson(files, output.IsCompact));
                }

                return 0;
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: Scope,
            Output: "A JSON array of files, or [] when there are none. Each has its path; type, binary, json, or config; "
                + "size in bytes; etag; and clientFileTime, when it was uploaded, if known. " + ListProgress.HelpOutput,
            Examples:
            [
                "xbox xbl global-storage list --title-id 1234ABCD --sandbox-id XDKS.1",
                "xbox xbl global-storage list --scid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee --sandbox-id XDKS.1 --path news/ --output table",
            ],
            Table: FilesTable));
    }

    private static Command CreateDownload(Func<Stream> openStandardOutput)
    {
        var target = new Target();
        var path = CreatePathOption("The file's path, such as news/today.json.");
        var type = CreateFileTypeOption();
        var destination = new Option<string?>("--dest")
        {
            Description = "Where to save the file: a file, a directory to save it in under its name, or - for stdout. "
                + "Default: its name, in the current directory. An existing file is replaced.",
        };
        var command = new Command("download", "Download a global storage file");
        target.AddTo(command);
        command.Options.Add(path);
        command.Options.Add(type);
        command.Options.Add(destination);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            RunAsync(result, target, async (client, selection) =>
            {
                string filePath = result.GetValue(path)!;
                string? dest = result.GetValue(destination);
                Resolved storage = await ResolveAsync(result, client, selection, change: false, cancellationToken);
                if (dest == "-")
                {
                    await using Stream stdout = openStandardOutput();
                    long written = await DownloadAsync(client, storage, filePath, result.GetValue(type)!, stdout, cancellationToken);
                    Console.Error.WriteLine($"Downloaded '{filePath}' ({written} bytes).");
                    return 0;
                }

                string file = ResolveDestination(dest, filePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                // Written beside the destination first, so a failed download leaves an existing file as it was.
                string staged = Path.Combine(Path.GetDirectoryName(file)!, $".xboxdevcli-global-storage-{Guid.NewGuid():N}");
                var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                try
                {
                    long written;
                    await using (stream)
                    {
                        written = await DownloadAsync(client, storage, filePath, result.GetValue(type)!, stream, cancellationToken);
                    }

                    File.Move(staged, file, overwrite: true);
                    Console.Error.WriteLine($"Downloaded '{filePath}' to '{file}' ({written} bytes).");
                    return 0;
                }
                finally
                {
                    File.Delete(staged);
                }
            }, cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.ReadOnly,
            Scope: Scope,
            Output: "Writes nothing to stdout, except the file with --dest -. On stderr, writes a line such as "
                + "\"Downloaded 'news/today.json' to '<path>' (512 bytes).\"" + FileOperationOutput,
            SafetyDetails: "Writes the file to the local destination, replacing an existing file there.",
            Examples:
            [
                "xbox xbl global-storage download --title-id 1234ABCD --sandbox-id XDKS.1 --path news/today.json --file-type json",
                "xbox xbl global-storage download --title-id 1234ABCD --sandbox-id XDKS.1 --path news/today.json --file-type json --dest -",
            ]));
    }

    private static async Task<long> DownloadAsync(
        XboxPartnerClient client, Resolved storage, string path, string type, Stream destination,
        CancellationToken cancellationToken)
    {
        using (RequestProgress.Start($"Downloading {path}"))
        {
            return await client.DownloadGlobalStorageFileAsync(
                storage.SellerId, storage.Scid, storage.Sandbox, path, type, destination, cancellationToken);
        }
    }

    /// <summary>
    /// The file to save to: <c>--dest</c>, or the file's name in it when it's a directory or ends with a
    /// separator, or in the current directory without it.
    /// </summary>
    private static string ResolveDestination(string? destination, string path)
    {
        if (destination is not null && !Directory.Exists(destination) && !Path.EndsInDirectorySeparator(destination))
        {
            return Path.GetFullPath(destination);
        }

        string name = Path.GetFileName(path);
        return name.Length == 0
            ? throw new InvalidOperationException("--path must name a file, such as news/today.json.")
            : Path.GetFullPath(Path.Combine(destination ?? ".", name));
    }

    private static Command CreateUpload(Func<Stream> openStandardInput)
    {
        var target = new Target();
        var file = new Option<string>("--file")
        {
            Description = "The local file to upload, at most 4 MiB, or - to read it from stdin.",
            Required = true,
        };
        var path = CreatePathOption("The path to store the file at, such as news/today.json.");
        var type = CreateFileTypeOption();
        var overwrite = new Option<bool>("--overwrite")
        {
            Description = "Replace the file at --path, whatever its type. Default: false, which fails when the path holds a file.",
        };
        var command = new Command("upload", "Upload a file to a sandbox's global storage");
        target.AddTo(command);
        command.Options.Add(file);
        command.Options.Add(path);
        command.Options.Add(type);
        command.Options.Add(overwrite);
        command.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
        {
            string source = result.GetValue(file)!;
            string filePath = result.GetValue(path)!;
            string fileType = result.GetValue(type)!;
            ReadOnlyMemory<byte> content;
            try
            {
                content = await ReadUploadAsync(source, openStandardInput, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CanceledBeforeSending(filePath, "uploaded");
            }

            return await RunAsync(result, target, async (client, selection) =>
            {
                try
                {
                    return await ChangeAsync(
                        result, client, selection, filePath, "uploaded",
                        (storage, correlationId, onSending) => client.UploadGlobalStorageFileAsync(
                            storage.SellerId, storage.Scid, storage.Sandbox, filePath, fileType,
                            result.GetValue(overwrite), content, correlationId, cancellationToken, onSending),
                        $"Uploaded {DescribeSource(source)} to '{filePath}' ({fileType}, {content.Length} bytes).",
                        cancellationToken,
                        sending: $"Uploading {DescribeSource(source)} to '{filePath}'.");
                }
                catch (XboxPartnerApiException exception) when (
                    TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code") == FileExistsCode)
                {
                    Console.Error.WriteLine(XboxPartnerCommandErrorFormatter.Format(
                        exception, OutputOptions.From(result).IsCompact, "Pass --overwrite to replace it."));
                    return 1;
                }
            }, cancellationToken);
        });
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: Scope,
            Output: "Writes nothing to stdout. On stderr, writes a line such as \"Uploading '<path>' to "
                + "'news/today.json'.\" before it sends the file, and \"Uploaded '<path>' to 'news/today.json' (json, "
                + "512 bytes).\" when it succeeds." + FileOperationOutput,
            SafetyDetails: "Players in the sandbox get the file at once; nothing is published. RETAIL is read-only: "
                + "change a development sandbox's files, then publish that sandbox to RETAIL. The request is sent once "
                + "and never retried. Agent approval is still required.",
            Examples:
            [
                "xbox xbl global-storage upload --title-id 1234ABCD --sandbox-id XDKS.1 --file today.json --path news/today.json --file-type json",
                "xbox xbl global-storage upload --title-id 1234ABCD --sandbox-id XDKS.1 --file nod_arena.bin --path maps/nod_arena.bin --file-type binary --overwrite",
            ],
            LearnMore:
            [
                "XBOX services check a json or config file's content when it's uploaded.",
                "Games read these files only when the sandbox's published titlestorage.xml has "
                    + "<StorageType ownerOnly=\"false\">global</StorageType>, which Partner Center saves when Global is "
                    + "Active on its Title Storage page. To turn it on, add that element with 'xbox xbl config save', "
                    + "then publish the sandbox.",
                ChangeExitCodes("upload", "uploaded", "upload it again"),
            ]));
    }

    private static Command CreateDelete(XboxPartnerJobDependencies dependencies)
    {
        var target = new Target();
        var path = CreatePathOption("The file's path, such as news/today.json.");
        var type = CreateFileTypeOption();
        var confirm = new Option<bool>("--confirm", "--yes", "-y")
        {
            Description = "Confirm deleting the file without prompting.",
        };
        var command = new Command("delete", "Delete a global storage file");
        target.AddTo(command);
        command.Options.Add(path);
        command.Options.Add(type);
        command.Options.Add(confirm);
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
        {
            string filePath = result.GetValue(path)!;
            string fileType = result.GetValue(type)!;
            bool confirmed = result.GetResult(confirm) is { Implicit: false }
                ? result.GetValue(confirm)
                : result.GetValue(Program.ConfirmOption);
            return RunAsync(
                result,
                target,
                (client, selection) => ChangeAsync(
                    result, client, selection, filePath, "deleted",
                    (storage, correlationId, onSending) => client.DeleteGlobalStorageFileAsync(
                        storage.SellerId, storage.Scid, storage.Sandbox, filePath, fileType, correlationId,
                        cancellationToken, onSending),
                    $"Deleted '{filePath}' ({fileType}).",
                    cancellationToken,
                    storage => PlaytestCommandFactory.ConfirmAsync(
                        confirmed,
                        dependencies,
                        $"This deletes '{filePath}' ({fileType}) from the global storage of {storage.Sandbox}, "
                            + $"for SCID {storage.Scid:D}"
                            + (storage.SellerId is { } seller
                                ? $" and seller {seller.ToString(CultureInfo.InvariantCulture)}"
                                : string.Empty)
                            + ". Players there stop getting it at once, and it can't be recovered.",
                        "Delete the file?",
                        "Canceled. Nothing was deleted.",
                        cancellationToken)),
                cancellationToken);
        });
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Scope: Scope,
            Output: "Writes nothing to stdout. On stderr, writes a line such as \"Deleted 'news/today.json' (json).\""
                + FileOperationOutput,
            SafetyDetails: "Asks first at a terminal, and fails without --confirm otherwise. Players in the sandbox stop "
                + "getting the file at once, and it can't be recovered. Deletes nothing unless the path holds a file of "
                + "that type. RETAIL is read-only. The request is sent once and never retried. Agent approval is still "
                + "required.",
            Examples:
            [
                "xbox xbl global-storage delete --title-id 1234ABCD --sandbox-id XDKS.1 --path news/today.json --file-type json",
            ],
            LearnMore: [ChangeExitCodes("delete", "deleted", "try again")]));
    }

    private static string ChangeExitCodes(string verb, string done, string retry) =>
        $"Exit codes: 0 {done}; 1 not {done}; 3 the {verb} was sent, but its outcome is unknown. After 3, check with "
        + $"the 'xbox xbl global-storage list' command that stderr shows before you {retry}.";

    private static Option<string> CreatePathOption(string description) => new("--path")
    {
        Description = description,
        Required = true,
    };

    private static Option<string> CreateFileTypeOption()
    {
        var option = new Option<string>("--file-type")
        {
            HelpName = "type",
            Description = "The file's type: binary, json, or config.",
            Required = true,
        };
        option.AcceptOnlyFromAmong(FileTypes);
        return option;
    }

    /// <summary>
    /// The file to upload, or stdin for <c>-</c>. A pipe, such as stdin or <c>/dev/stdin</c>, can't say how long it
    /// is, so this reads one byte past the limit to tell a file that's too large.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>> ReadUploadAsync(
        string source, Func<Stream> openStandardInput, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[XboxPartnerClient.MaxGlobalStorageUploadBytes + 1];
        int length;
        await using (Stream input = source == "-" ? openStandardInput() : File.OpenRead(source))
        {
            length = await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        }

        return length <= XboxPartnerClient.MaxGlobalStorageUploadBytes
            ? buffer.AsMemory(0, length)
            : throw new InvalidOperationException(
                $"{DescribeSource(source)} is larger than 4 MiB. A global storage file can be at most 4 MiB "
                + "(4,194,304 bytes).");
    }

    private static string DescribeSource(string source) => source == "-" ? "stdin" : $"'{Path.GetFullPath(source)}'";

    /// <summary>
    /// Selects the sandbox and runs <paramref name="action"/>. The service's failures, and a request timeout,
    /// are written to stderr; everything else, including the caller's cancellation, reaches the shared boundary.
    /// </summary>
    private static async Task<int> RunAsync(
        ParseResult result,
        Target target,
        Func<XboxPartnerClient, Selection, Task<int>> action,
        CancellationToken cancellationToken)
    {
        var configuration = Program.GetConfiguration(result);
        _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
        Selection selection = await target.SelectAsync(result);
        using var client = Program.XboxPartnerClientFactory is null
            ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(result))
            : Program.XboxPartnerClientFactory(configuration);
        try
        {
            return await action(client, selection);
        }
        catch (Exception exception) when (exception is XboxPartnerApiException or XboxPartnerResponseException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(
                exception, OutputOptions.From(result).IsCompact, Console.Error, cancellationToken);
            return 1;
        }
    }

    /// <summary>
    /// Looks up the SCID, asks <paramref name="confirm"/> when there is one, writes <paramref name="sending"/> to stderr
    /// when there is one, and sends an upload or delete once. A failure after it was sent, other than a refusal (4xx
    /// other than 408), leaves its outcome unknown: exit 3, with the list command that checks.
    /// </summary>
    private static async Task<int> ChangeAsync(
        ParseResult result,
        XboxPartnerClient client,
        Selection selection,
        string path,
        string done,
        Func<Resolved, string, Action, Task> send,
        string succeeded,
        CancellationToken cancellationToken,
        Func<Resolved, Task<bool>>? confirm = null,
        string? sending = null)
    {
        string correlationId = Guid.NewGuid().ToString();
        Resolved? storage = null;
        var sent = false;
        try
        {
            storage = await ResolveAsync(result, client, selection, change: true, cancellationToken);
            if (confirm is not null && !await confirm(storage))
            {
                return 1;
            }

            if (sending is not null)
            {
                Console.Error.WriteLine(sending);
            }

            using (RequestProgress.Start(done == "deleted" ? $"Deleting {path}" : $"Uploading {path}"))
            {
                await send(storage, correlationId, () => sent = true);
            }
        }
        catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
        {
            return CanceledBeforeSending(path, done);
        }
        catch (Exception exception) when (sent && XboxPartnerCommandSupport.IsUncertainSubmission(exception))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(
                exception, OutputOptions.From(result).IsCompact, Console.Error, cancellationToken, correlationId);
            Console.Error.WriteLine($"It is unknown whether '{path}' was {done}. Check before you try again: "
                + CheckCommand(result, storage!, path));
            return XboxPartnerCommandSupport.OutcomeUnknownExitCode;
        }

        Console.Error.WriteLine(succeeded);
        return 0;
    }

    /// <summary>Ctrl+C before a change is sent: nothing changed, which is exit 1, not a quiet 130.</summary>
    private static int CanceledBeforeSending(string path, string done)
    {
        Console.Error.WriteLine($"Canceled before the request was sent, so '{path}' wasn't {done}.");
        return 1;
    }

    /// <summary>The read-only <c>list</c> command that shows the file's folder, with the caller's sign-in options.</summary>
    private static string CheckCommand(ParseResult result, Resolved storage, string path)
    {
        var arguments = RecoveryCommandArguments.Create(result);
        arguments.AddRange(
        [
            "xbl", "global-storage", "list", "--scid", storage.Scid.ToString("D"),
            "--sandbox-id", ShellQuoting.Quote(storage.Sandbox),
        ]);
        int folder = path.LastIndexOf('/');
        if (folder > 0)
        {
            arguments.AddRange(["--path", ShellQuoting.Quote(path[..(folder + 1)])]);
        }

        if (storage.SellerId is { } seller)
        {
            arguments.AddRange(["--seller-id", seller.ToString(CultureInfo.InvariantCulture)]);
        }

        return string.Join(' ', arguments);
    }

    /// <summary>
    /// The SCID and seller to use: <c>--scid</c> as given, or the SCID a title ID resolves to. A change by a
    /// bound title's ID is refused, because its files are its primary title's.
    /// </summary>
    private static async Task<Resolved> ResolveAsync(
        ParseResult result,
        XboxPartnerClient client,
        Selection selection,
        bool change,
        CancellationToken cancellationToken)
    {
        if (selection.Scid is { } scid)
        {
            return new Resolved(selection.SellerId, scid, selection.Sandbox);
        }

        XboxLiveResolvedConfigurationTarget? resolved = null;
        using (RequestProgress.Start($"Looking up XBOX title {selection.TitleId}"))
        {
            await client.GetXboxLiveConfigurationStatusAsync(
                selection.SellerId, XboxLiveScope.Sandbox, null, selection.Sandbox, cancellationToken,
                selection.TitleId, target => resolved = target);
        }

        // A title ID request always names the SCID it used, or fails. A change names it, as config save does.
        if (change || result.GetValue(Program.VerboseOption))
        {
            XboxLiveConfigurationTarget.WriteResolved(resolved!, selection.SellerId, Console.Error);
        }

        if (change && resolved!.ResolvedTitleId is { } primary
            && !string.Equals(primary, resolved.TitleId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"XBOX title {resolved.TitleId} is bound to {primary}, so its global storage files are {primary}'s. "
                + $"To change them, pass --scid {resolved.Scid:D} instead of --title-id.");
        }

        return new Resolved(resolved!.SellerId ?? selection.SellerId, resolved.Scid, selection.Sandbox);
    }

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>The seller, title, and sandbox the options name, before a title ID is looked up.</summary>
    private sealed record Selection(long? SellerId, Guid? Scid, string? TitleId, string Sandbox);

    /// <summary>The seller, SCID, and sandbox the requests use.</summary>
    private sealed record Resolved(long? SellerId, Guid Scid, string Sandbox);

    /// <summary>The <c>--seller-id</c>, <c>--scid</c> or <c>--title-id</c>, and <c>--sandbox-id</c> options.</summary>
    private sealed class Target
    {
        private readonly XdtsSellerOption _seller = new();
        private readonly XboxLiveConfigurationTarget _title = new();
        private readonly Option<string?> _sandbox;

        public Target()
        {
            _sandbox = new Option<string?>("--sandbox-id")
            {
                HelpName = "id",
                Description = "The case-sensitive ID of a sandbox games run in, such as XDKS.1 or RETAIL. "
                    + "ALL, HIDDEN, and HISTORY hold only configuration, so they fail.",
            }.AsContextValue(ContextEndpoint.XboxPartner);
            _sandbox.Validators.Add(result =>
            {
                XboxLiveConfigurationScope.ValidateSandbox(result);
                // The sandbox goes in the request's path, where "." or ".." would change the route.
                if (result.Tokens.Count > 0 && result.Tokens[0].Value.Trim() is "." or "..")
                {
                    result.AddError("--sandbox-id must name a sandbox, such as XDKS.1.");
                }
            });
        }

        public void AddTo(Command command)
        {
            _seller.AddTo(command);
            _title.AddTo(command);
            command.Options.Add(_sandbox);
        }

        /// <summary>The selection, failing with how to set the title or sandbox when either is missing.</summary>
        public async Task<Selection> SelectAsync(ParseResult result)
        {
            long? sellerId = _seller.GetSellerId(result);
            XboxLiveConfigurationTarget.SelectedTarget title = await _title.SelectAsync(result, sellerId);
            string? sandbox = result.GetValue(_sandbox)?.Trim();
            return string.IsNullOrEmpty(sandbox)
                ? throw new InvalidOperationException(await ContextResolver.GetMissingMessageAsync(result, _sandbox))
                : new Selection(sellerId, title.Scid, title.TitleId, sandbox);
        }
    }
}
