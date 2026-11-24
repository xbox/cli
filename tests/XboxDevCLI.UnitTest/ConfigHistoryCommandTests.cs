// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class ConfigHistoryCommandTests
{
    private const string ScidText = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private const string SandboxStatus =
        """{"scope":"sandbox","sandbox":"XDKS.1","workingVersion":55,"workingCommitTime":"2026-10-29T21:40:12Z","publishedVersion":52,"documents":[]}""";

    private const string FirstPage =
        """
        {
          "entries": [
            { "action": "publish", "version": 60, "sandbox": "XDKS.1", "time": "2026-10-30T09:12:00+00:00", "author": { "id": "service", "name": null }, "message": null, "documents": [] },
            { "action": "save", "version": 55, "sandbox": "XDKS.1", "time": "2026-10-29T21:40:12+00:00", "author": { "id": "1001", "name": "Nod Developer" }, "message": "Add Win a race",
              "documents": [
                { "documentType": "achievements2017", "displayName": "Achievements 2017", "description": "1 achievements added. Total gamerscore: 1000." },
                { "documentType": "leaderboards", "displayName": "Leaderboards", "description": "Leaderboard FastestLap has been added." }
              ] }
          ],
          "continuationToken": "page-2"
        }
        """;

    private const string SecondPage =
        """
        {
          "entries": [
            { "action": "publish", "version": 52, "sandbox": "XDKS.1", "time": "2026-10-27T18:05:00+00:00", "author": { "id": "1003", "name": "Lucerne Developer" }, "message": null,
              "documents": [ { "documentType": "achievements2017", "displayName": "Achievements 2017", "description": "12 achievements. Total gamerscore: 1000." } ] },
            { "action": "copy", "version": 50, "sandbox": "XDKS.1", "time": "2026-10-26T18:05:00+00:00", "author": { "id": "1003", "name": "Lucerne Developer" }, "message": null, "documents": [] }
          ]
        }
        """;

    [Fact]
    public async Task Sandbox_ReadsEveryPageNewestFirstAndNamesEachFeature()
    {
        RouteHandler handler = Routes();

        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "history", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1"],
            handler);

        Assert.True(exitCode == 0, stderr);
        Assert.Empty(stderr);
        Assert.Equal(
            [
                $$"""{"sellerId":1234567,"scid":"{{ScidText}}","sandbox":"XDKS.1","scope":"sandbox"}""",
                $$"""{"sellerId":1234567,"scid":"{{ScidText}}","sandbox":"XDKS.1","pageSize":100}""",
                $$"""{"sellerId":1234567,"scid":"{{ScidText}}","sandbox":"XDKS.1","continuationToken":"page-2"}""",
            ],
            handler.Requests.Select(request => request.Body));
        Assert.Single(handler.Requests.Skip(1).Select(request => request.CorrelationId).Distinct());
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.False(root.TryGetProperty("scope", out _));
        Assert.Equal(ScidText, root.GetProperty("scid").GetString());
        Assert.Equal("XDKS.1", root.GetProperty("sandbox").GetString());
        Assert.Equal([60UL, 55UL, 52UL, 50UL], root.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("version").GetUInt64()));
        JsonElement save = root.GetProperty("entries")[1];
        Assert.Equal("Add Win a race", save.GetProperty("message").GetString());
        Assert.Equal("Nod Developer", save.GetProperty("author").GetProperty("name").GetString());
        JsonElement document = save.GetProperty("documents")[0];
        Assert.Equal("achievements2017", document.GetProperty("documentType").GetString());
        Assert.Equal("Achievements", document.GetProperty("feature").GetString());
        Assert.Equal("1 achievements added. Total gamerscore: 1000.", document.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Limit_StopsReadingOnceEnoughEntriesMatch()
    {
        RouteHandler handler = Routes();

        var (exitCode, stdout, _) = await InvokeAsync(
            ["xbl", "config", "history", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1", "--limit", "1", "--output", "json-compact"],
            handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(60UL, Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("version").GetUInt64());
    }

    [Fact]
    public async Task ActionAndFeature_KeepOnlyMatchingEntriesAndTheirMatchingDocuments()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "history", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1",
             "--action", "save", "--feature", "Leaderboards", "--feature", "sets.xml"],
            Routes());

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "XBOX services don't describe changes to 'sets.xml', so --feature 'sets.xml' matches no entry." + Environment.NewLine,
            stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement entry = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal(55UL, entry.GetProperty("version").GetUInt64());
        Assert.Equal("leaderboards", Assert.Single(entry.GetProperty("documents").EnumerateArray()).GetProperty("documentType").GetString());
    }

    [Theory]
    [InlineData("--scope", "title")]
    [InlineData("--sandbox-id", "ALL")]
    [InlineData("--sandbox-id", "HIDDEN")]
    [InlineData("--sandbox-id", "HISTORY")]
    public async Task Title_HasNoHistoryAndPointsToItsStatus(string option, string value)
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "history", "--seller-id", "1234567", "--title-id", "1234ABCD", option, value],
            new RouteHandler(_ => throw new InvalidOperationException("No request is expected.")));

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains(
            "XBOX services keep no history of the title configuration. For its state, run " +
                $"'{CliIdentity.CommandName} xbl config status --scope title --title-id 1234ABCD'.",
            stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TitleId_ReadsTheHistoryByTheResolvedScidAndSeller()
    {
        var handler = new RouteHandler(request =>
        {
            if (request.Route != "get")
            {
                return Ok(request.Body.Contains("continuationToken", StringComparison.Ordinal) ? SecondPage : FirstPage);
            }

            HttpResponseMessage response = Ok(SandboxStatus);
            response.Headers.Add("X-XboxPartner-Scid", ScidText);
            response.Headers.Add("X-XboxPartner-Seller-Id", "1234567");
            return response;
        });

        var (exitCode, stdout, _) = await InvokeAsync(
            ["xbl", "config", "history", "--title-id", "1234ABCD", "--sandbox-id", "XDKS.1", "--limit", "1"],
            handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            $$"""{"sellerId":1234567,"scid":"{{ScidText}}","sandbox":"XDKS.1","pageSize":100}""",
            handler.Requests[^1].Body);
        Assert.Equal(ScidText, JsonDocument.Parse(stdout).RootElement.GetProperty("scid").GetString());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("publish", false)]
    public async Task ARecentSaveMissingFromTheHistory_IsReportedWhenSavesAreShown(string? action, bool reported)
    {
        string justSaved = SandboxStatus
            .Replace("55", "61", StringComparison.Ordinal)
            .Replace("2026-10-29T21:40:12Z", DateTimeOffset.UtcNow.AddSeconds(-10).ToString("O"), StringComparison.Ordinal);
        string[] args = ["xbl", "config", "history", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1", "--limit", "1"];

        var (exitCode, _, stderr) = await InvokeAsync(action is null ? args : [.. args, "--action", action], Routes(status: justSaved));

        Assert.Equal(0, exitCode);
        Assert.Equal(
            reported,
            stderr.Contains("The latest save, version 61, isn't in the history yet.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARecentSaveMissingFromAWholeHistoryReadToItsLimit_IsReported()
    {
        string justSaved = SandboxStatus
            .Replace("55", "61", StringComparison.Ordinal)
            .Replace("2026-10-29T21:40:12Z", DateTimeOffset.UtcNow.AddSeconds(-10).ToString("O"), StringComparison.Ordinal);
        const string OnlyALaterPublish = """{"entries":[{"action":"publish","version":62,"sandbox":"XDKS.1","documents":[]}]}""";

        var (exitCode, _, stderr) = await InvokeAsync(
            ["xbl", "config", "history", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1", "--limit", "1"],
            Routes(status: justSaved, firstPage: OnlyALaterPublish));

        Assert.Equal(0, exitCode);
        Assert.Contains("The latest save, version 61, isn't in the history yet.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Table_ShowsARowPerFeatureAndOneForAnEntryWithoutDescriptions()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "history", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1", "--output", "table"],
            Routes());

        Assert.True(exitCode == 0, stderr);
        string[] lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("VERSION", lines[0], StringComparison.Ordinal);
        Assert.Equal(7, lines.Length);
        Assert.Matches(@"^60\s+publish\s+XDKS\.1\s+2026-10-30 09:12:00 Z\s+-\s+-\s+-$", lines[2]);
        Assert.Matches(@"^55\s+save\s+XDKS\.1\s+2026-10-29 21:40:12 Z\s+Nod Developer\s+Achievements\s+1 achievements added", lines[3]);
        Assert.Matches(@"^55\s+save\s+XDKS\.1\s+.+Leaderboards\s+Leaderboard FastestLap has been added\.$", lines[4]);
        Assert.Matches(@"^50\s+copy\s+XDKS\.1\s+", lines[6]);
    }

    [Fact]
    public async Task AFailedPageWritesTheErrorAndNoResults()
    {
        var handler = new RouteHandler(request => request.Route == "get"
            ? Ok(SandboxStatus)
            : request.Body.Contains("continuationToken", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                {
                    Content = new StringContent("""{"status":504,"title":"Configuration history timed out.","code":"upstream_timeout"}"""),
                }
                : Ok(FirstPage));

        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "history", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1"],
            handler);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("upstream_timeout", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xbl config history --scope account", "account")]
    [InlineData("xbl config history --scid " + ScidText + " --sandbox-id XDKS.1 --limit 0", "--limit must be a positive number.")]
    [InlineData("xbl config history --scid " + ScidText + " --sandbox-id XDKS.1 --action other", "other")]
    [InlineData("xbl config history --scope title --scid " + ScidText + " --sandbox-id XDKS.1", "--scope title uses the title-wide configuration, so it takes no --sandbox-id.")]
    public void Options_RejectWhatTheHistoryCantShow(string command, string expected)
    {
        var errors = Program.BuildRootCommand().Parse(command, Program.NoResponseFileConfig).Errors;

        Assert.Contains(errors, error => error.Message.Contains(expected, StringComparison.Ordinal));
    }

    private static RouteHandler Routes(string status = SandboxStatus, string firstPage = FirstPage) =>
        new(request => Ok(
            request.Route == "get" ? status
            : request.Body.Contains("continuationToken", StringComparison.Ordinal) ? SecondPage
            : firstPage));

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(string[] args, RouteHandler handler)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = configuration => new XboxPartnerClient(
            configuration,
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));
        try
        {
            int exitCode = await Program.Main(args);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed record RecordedRequest(string Path, string Body, string? CorrelationId = null)
    {
        public string Route => Path[(Path.LastIndexOf('/') + 1)..];
    }

    private sealed class RouteHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                body,
                request.Headers.TryGetValues(XboxPartnerClient.CorrelationIdHeader, out var ids) ? ids.Single() : null);
            lock (_requests)
            {
                _requests.Add(recorded);
            }

            return respond(recorded);
        }
    }
}
