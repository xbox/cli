// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class GlobalStorageCommandTests : IDisposable
{
    private const string Scid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private const string Storage = $"/xboxpartner/v1/xboxlive/scids/{Scid}/sandboxes/XDKS.1/global-storage";

    private const string Check =
        $"xbox xbl global-storage list --scid {Scid} --sandbox-id XDKS.1 --path news/ --seller-id 1234567";

    private const string SandboxStatus = """{"scope":"sandbox","sandbox":"XDKS.1","documents":[]}""";

    private readonly string _directory = Directory.CreateTempSubdirectory("xbox-global-storage-").FullName;

    private static readonly string[] Target = ["--scid", Scid, "--sandbox-id", "XDKS.1", "--seller-id", "1234567"];

    private static readonly string[] NewsFile = ["--path", "news/today.json", "--file-type", "json"];

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Get_WritesTheUsageAsTheServiceSentIt()
    {
        const string Usage = """{"usedBytes":9437696,"quotaBytes":268435456}""";
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(Usage));

        var run = await InvokeAsync(handler, ["get", .. Target, "--output", "json-compact"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Usage, run.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{Storage}?sellerId=1234567", request.Uri.PathAndQuery);
    }

    [Fact]
    public async Task List_ReadsEveryPageUnderThePath()
    {
        var handler = new RoutingHttpHandler(request => RoutingHttpHandler.Json(
            request.Uri.Query.Contains("continuationToken", StringComparison.Ordinal)
                ? """{"files":[{"path":"news/old.json","type":"json","size":20}]}"""
                : """{"files":[{"path":"news/today.json","type":"json","size":512}],"continuationToken":"page2"}"""));

        var run = await InvokeAsync(handler, ["list", .. Target, "--path", "news/", "--output", "json-compact"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            """[{"path":"news/today.json","type":"json","size":512},{"path":"news/old.json","type":"json","size":20}]""",
            run.Stdout.Trim());
        Assert.Equal(
            [
                $"{Storage}/files?sellerId=1234567&path=news%2F&pageSize=100",
                $"{Storage}/files?sellerId=1234567&path=news%2F&continuationToken=page2",
            ],
            handler.Requests.Select(request => request.Uri.PathAndQuery));
    }

    [Theory]
    [InlineData(
        "get",
        """{"usedBytes":512,"quotaBytes":268435456}""",
        "^USED BYTES +QUOTA BYTES *$",
        "^512 +268435456 *$")]
    [InlineData(
        "list",
        """{"files":[{"path":"news/today.json","type":"json","size":512,"clientFileTime":"2026-10-28T17:02:11+00:00"}]}""",
        "^PATH +TYPE +SIZE +UPLOADED *$",
        @"^news/today\.json +json +512 +2026-10-28T17:02:11\+00:00 *$")]
    public async Task TableOutput_ShowsTheDeclaredColumns(string command, string response, string header, string row)
    {
        var run = await InvokeAsync(
            new RoutingHttpHandler(_ => RoutingHttpHandler.Json(response)), [command, .. Target, "--output", "table"]);

        Assert.Equal(0, run.ExitCode);
        string[] lines = run.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches(header, lines[0]);
        Assert.Matches(row, lines[^1]);
    }

    [Fact]
    public async Task TitleId_UsesTheScidAndSellerItsSandboxConfigurationResolvesTo()
    {
        var handler = new RoutingHttpHandler(request => request.Path.EndsWith("/configurations/get", StringComparison.Ordinal)
            ? Resolved(primaryTitleId: "1234ABCD")
            : RoutingHttpHandler.Json("""{"usedBytes":0,"quotaBytes":268435456}"""));

        var run = await InvokeAsync(handler, ["get", "--title-id", "1234abcd", "--sandbox-id", "XDKS.1"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("\"titleId\":\"1234ABCD\"", handler.Requests[0].Text, StringComparison.Ordinal);
        Assert.Contains("\"sandbox\":\"XDKS.1\"", handler.Requests[0].Text, StringComparison.Ordinal);
        Assert.Equal($"{Storage}?sellerId=1234567", handler.Requests[1].Uri.PathAndQuery);
    }

    [Fact]
    public async Task Upload_SendsTheFileOnceAsItsBody()
    {
        byte[] content = Encoding.UTF8.GetBytes("""{"headline":"Nod Arena season 3 starts Friday"}""");
        string file = WriteTemporaryFile(content);
        var handler = new RoutingHttpHandler(_ =>
            RoutingHttpHandler.Json("""{"path":"news/today.json","type":"json","etag":"\"0x8DDF1A2B3C4D5E6\""}"""));

        var run = await InvokeAsync(handler, ["upload", .. Target, "--file", file, .. NewsFile, "--overwrite"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains($"Uploaded '{file}' to 'news/today.json' (json, {content.Length} bytes).", run.Stderr, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(
            $"{Storage}/files/content?sellerId=1234567&path=news%2Ftoday.json&type=json&overwrite=true",
            request.Uri.PathAndQuery);
        Assert.Equal(content, request.Body);
        Assert.Equal("application/octet-stream", request.Header("Content-Type"));
        Assert.Equal(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Header("Content-Length"));
    }

    [Fact]
    public async Task Upload_NamesTheLocalFileBeforeItSendsIt()
    {
        string file = WriteTemporaryFile([1]);
        using var stderr = new StringWriter();
        string? beforeSending = null;
        var handler = new RoutingHttpHandler(_ =>
        {
            beforeSending = stderr.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var run = await InvokeAsync(handler, ["upload", .. Target, "--file", file, .. NewsFile], stderr: stderr);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains($"Uploading '{file}' to 'news/today.json'.", beforeSending, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_FromStdin_ReadsAPipeOfUpTo4MiB()
    {
        byte[] content = new byte[4 * 1024 * 1024];
        content.AsSpan().Fill(0x4E);
        using var stdin = new NonSeekableStream(content);
        var handler = new RoutingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var run = await InvokeAsync(
            handler,
            ["upload", .. Target, "--file", "-", "--path", "maps/nod_arena.bin", "--file-type", "binary"],
            stdinStream: stdin);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Uploading stdin to 'maps/nod_arena.bin'.", run.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            $"Uploaded stdin to 'maps/nod_arena.bin' (binary, {content.Length} bytes).", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(content, Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Upload_CanceledWhileReadingStdin_SaysNothingWasUploaded()
    {
        using var cancellation = new CancellationTokenSource();
        using var stdin = new CancelingStream(cancellation);
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        var run = await InvokeAsync(
            handler, ["upload", .. Target, "--file", "-", .. NewsFile], stdinStream: stdin, cancellationToken: cancellation.Token);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            "Canceled before the request was sent, so 'news/today.json' wasn't uploaded.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Upload_OfAFileOver4MiB_SendsNothing()
    {
        string file = WriteTemporaryFile(new byte[(4 * 1024 * 1024) + 1]);
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        var run = await InvokeAsync(
            handler, ["upload", .. Target, "--file", file, "--path", "maps/nod_arena.bin", "--file-type", "binary"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            $"'{file}' is larger than 4 MiB. A global storage file can be at most 4 MiB (4,194,304 bytes).",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Upload_ToAPathThatHoldsAFile_SaysToPassOverwrite()
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(
            """{"status":409,"title":"Global storage file already exists.","detail":"A global storage file already exists at 'news/today.json'. Send overwrite=true to replace it.","code":"global_storage_file_exists"}""",
            HttpStatusCode.Conflict));

        var run = await InvokeAsync(
            handler, ["upload", .. Target, "--file", WriteTemporaryFile([1]), .. NewsFile, "--output", "json-compact"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stdout);
        // As the xbl Errors docs tell scripts to: other lines come first.
        JsonElement error = JsonDocument.Parse(
            run.Stderr.Split(Environment.NewLine).Single(line => line.StartsWith('{'))).RootElement;
        Assert.Equal("global_storage_file_exists", error.GetProperty("code").GetString());
        Assert.EndsWith("Pass --overwrite to replace it.", error.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("delete")]
    public async Task AChangeByABoundTitleId_IsRefusedBeforeItAsksOrSends(string command)
    {
        var handler = new RoutingHttpHandler(_ => Resolved(primaryTitleId: "5678ABCD"));
        // No --confirm, so a delete that asked first would fail for that instead.
        string[] input = command == "upload" ? ["--file", WriteTemporaryFile([1])] : [];

        var run = await InvokeAsync(
            handler, [command, "--title-id", "1234ABCD", "--sandbox-id", "XDKS.1", .. NewsFile, .. input]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            $"Effective configuration target: seller 1234567 (only authorized seller), title 1234ABCD, primary title 5678ABCD, SCID {Scid}",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.Contains(
            $"XBOX title 1234ABCD is bound to 5678ABCD, so its global storage files are 5678ABCD's. To change them, pass --scid {Scid} instead of --title-id.",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("This deletes", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Uploading '", run.Stderr, StringComparison.Ordinal);
        Assert.EndsWith("/configurations/get", Assert.Single(handler.Requests).Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("upload", "uploaded")]
    [InlineData("delete", "deleted")]
    public async Task AChangeThatFailsAfterItIsSent_HasAnUnknownOutcome(string command, string done)
    {
        var handler = new RoutingHttpHandler(_ => RoutingHttpHandler.Json(
            """{"status":502,"title":"XBOX services failed."}""", HttpStatusCode.BadGateway));
        string[] input = command == "upload" ? ["--file", WriteTemporaryFile([1])] : ["--confirm"];

        var run = await InvokeAsync(handler, [command, .. Target, .. NewsFile, .. input]);

        Assert.Equal(XboxPartnerCommandSupport.OutcomeUnknownExitCode, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains(
            $"It is unknown whether 'news/today.json' was {done}. Check before you try again: {Check}",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Download_SavesTheFileUnderItsNameInTheDestinationDirectory()
    {
        byte[] content = [0x4E, 0x6F, 0x64, 0x00, 0xFF];
        string directory = _directory;
        var handler = new RoutingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });

        var run = await InvokeAsync(handler, ["download", .. Target, .. NewsFile, "--dest", directory]);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Stdout);
        string saved = Path.Combine(directory, "today.json");
        Assert.Equal(content, File.ReadAllBytes(saved));
        Assert.Equal([saved], Directory.GetFiles(directory));
        Assert.Contains($"Downloaded 'news/today.json' to '{saved}' (5 bytes).", run.Stderr, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Storage}/files/content?sellerId=1234567&path=news%2Ftoday.json&type=json", request.Uri.PathAndQuery);
        Assert.Equal("application/octet-stream", request.Header("Accept"));
    }

    [Theory]
    [InlineData("backup/today.json")]
    [InlineData("backup/")]
    public async Task Download_ToAFolderThatDoesNotExist_CreatesIt(string destination)
    {
        byte[] content = [1, 2, 3];
        var handler = new RoutingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });

        var run = await InvokeAsync(handler, ["download", .. Target, .. NewsFile, "--dest", Path.Combine(_directory, destination)]);

        Assert.Equal(0, run.ExitCode);
        string saved = Path.Combine(_directory, "backup", "today.json");
        Assert.Equal(content, File.ReadAllBytes(saved));
        Assert.Equal([saved], Directory.GetFiles(Path.Combine(_directory, "backup")));
    }

    [Fact]
    public async Task Download_ThatBreaksOff_LeavesTheExistingFileAsItWas()
    {
        string directory = _directory;
        string existing = Path.Combine(directory, "today.json");
        await File.WriteAllTextAsync(existing, "old");
        var handler = new RoutingHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream([1, 2, 3])) });

        var run = await InvokeAsync(handler, ["download", .. Target, .. NewsFile, "--dest", existing]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("The response ended prematurely.", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("old", await File.ReadAllTextAsync(existing));
        Assert.Equal([existing], Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Download_ToStdout_WritesOnlyTheFileThere()
    {
        byte[] content = Encoding.UTF8.GetBytes("""{"headline":"Nod Arena season 3 starts Friday"}""");
        using var stdout = new MemoryStream();
        var handler = new RoutingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });

        var run = await InvokeAsync(handler, ["download", .. Target, .. NewsFile, "--dest", "-"], stdout);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(content, stdout.ToArray());
        Assert.Empty(run.Stdout);
        Assert.Contains($"Downloaded 'news/today.json' ({content.Length} bytes).", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_WithoutConfirmOutsideATerminal_SendsNothing()
    {
        var handler = new RoutingHttpHandler(_ => throw new InvalidOperationException("No request is expected."));

        var run = await InvokeAsync(handler, ["delete", .. Target, .. NewsFile]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            $"This deletes 'news/today.json' (json) from the global storage of XDKS.1, for SCID {Scid} and seller 1234567.",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("Run again with --confirm (--yes, -y)", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Delete_WithConfirm_DeletesTheFileOfThatType()
    {
        // Only the status counts: the service's response repeats the path and type the request sent.
        var handler = new RoutingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var run = await InvokeAsync(handler, ["delete", .. Target, .. NewsFile, "--confirm"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("Deleted 'news/today.json' (json).", run.Stderr, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"{Storage}/files?sellerId=1234567&path=news%2Ftoday.json&type=json", request.Uri.PathAndQuery);
    }

    /// <summary>A configuration status response that resolved a title ID, whose configuration is <paramref name="primaryTitleId"/>'s.</summary>
    private static HttpResponseMessage Resolved(string primaryTitleId)
    {
        HttpResponseMessage response = RoutingHttpHandler.Json(SandboxStatus);
        response.Headers.Add("X-XboxPartner-Scid", Scid);
        response.Headers.Add("X-XboxPartner-Seller-Id", "1234567");
        response.Headers.Add("X-XboxPartner-Resolved-Title-Id", primaryTitleId);
        return response;
    }

    private string WriteTemporaryFile(byte[] content)
    {
        string path = Path.Combine(_directory, "upload.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static async Task<Run> InvokeAsync(
        HttpMessageHandler handler,
        string[] arguments,
        Stream? stdoutStream = null,
        Stream? stdinStream = null,
        CancellationToken cancellationToken = default,
        StringWriter? stderr = null)
    {
        RootCommand root = Program.BuildRootCommand();
        Command xbl = root.Subcommands.Single(command => command.Name == "xbl");
        xbl.Subcommands.Remove(xbl.Subcommands.Single(command => command.Name == "global-storage"));
        xbl.Subcommands.Add(GlobalStorageCommandFactory.Create(
            new XboxPartnerJobDependencies((_, _) => Task.CompletedTask, () => DateTimeOffset.UtcNow, () => false),
            () => stdoutStream ?? throw new InvalidOperationException("No stdout stream is expected."),
            () => stdinStream ?? throw new InvalidOperationException("No stdin stream is expected.")));
        CommandErrorBoundary.Install(root);

        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using StringWriter error = stderr ?? new StringWriter();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        Console.SetOut(stdout);
        Console.SetError(error);
        Program.XboxPartnerClientFactory = _ => new XboxPartnerClient(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XboxPartner:BaseUrl"] = "https://gateway.example" })
                .Build(),
            httpClient,
            () => Task.FromResult<string?>("caller-token"));
        try
        {
            int exitCode = await root.Parse(["xbl", "global-storage", .. arguments], Program.NoResponseFileConfig)
                .InvokeAsync(cancellationToken: cancellationToken);
            return new Run(exitCode, stdout.ToString(), error.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    /// <summary>A response body that ends with a dropped connection after its first bytes.</summary>
    private sealed class BreakingStream(byte[] start) : MemoryStream(start)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new IOException("The response ended prematurely.");
        }
    }

    /// <summary>A pipe, such as stdin, which can't seek or say how long it is.</summary>
    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException("Stream does not support seeking.");
    }

    /// <summary>A pipe the user presses Ctrl+C on while the CLI reads it.</summary>
    private sealed class CancelingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }
}
