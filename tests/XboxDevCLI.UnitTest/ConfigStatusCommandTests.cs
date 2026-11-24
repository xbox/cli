// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class ConfigStatusCommandTests
{
    private const string ScidText = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly Guid Scid = Guid.Parse(ScidText);

    private const string SandboxStatus =
        """
        {
          "scope": "sandbox",
          "scid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
          "sandbox": "XDKS.1",
          "hasWorkingConfiguration": true,
          "workingVersion": 55,
          "workingCommitTime": "2026-10-29T21:40:12Z",
          "hasPublishedConfiguration": true,
          "publishedVersion": 52,
          "publishedCommitTime": "2026-10-27T18:05:00Z",
          "hasUnpublishedChanges": true,
          "documents": [
            { "name": "achievements2017.xml", "scope": "sandbox", "state": "changed", "draftHash": "9C1E", "publishedHash": "0A2B" },
            { "name": "leaderboards.xml", "scope": "sandbox", "state": "same", "draftHash": "77F0", "publishedHash": "77F0" },
            { "name": "sets.xml", "scope": "sandbox", "state": "draft-only", "draftHash": "1234", "publishedHash": null },
            { "name": "dataplatformconfig.xml", "scope": "title", "state": "changed", "draftHash": "5D01", "publishedHash": "C3A9" }
          ]
        }
        """;

    private const string History =
        """
        {
          "entries": [
            { "action": "save", "version": 60, "sandbox": "ALL", "time": "2026-10-30T09:12:00+00:00", "author": { "id": "1002", "name": "Lucerne Developer" }, "message": null, "documents": [] },
            { "action": "save", "version": 55, "sandbox": "XDKS.1", "time": "2026-10-29T21:40:12+00:00", "author": { "id": "1001", "name": "Nod Developer" }, "message": "Add Win a race",
              "documents": [ { "documentType": "achievements2017", "displayName": "Achievements 2017", "description": "1 achievements added. Total gamerscore: 1000." } ] },
            { "action": "save", "version": 54, "sandbox": "XDKS.1", "time": "2026-10-28T10:00:00+00:00", "author": { "id": "1004", "name": "Second Developer" }, "message": null,
              "documents": [ { "documentType": "leaderboards", "displayName": "Leaderboards", "description": "Leaderboard FastestLap has been added." } ] },
            { "action": "publish", "version": 52, "sandbox": "XDKS.1", "time": "2026-10-27T18:05:00+00:00", "author": { "id": "1003", "name": "Publishing Developer" }, "message": null, "documents": [] },
            { "action": "save", "version": 51, "sandbox": "XDKS.1", "time": "2026-10-26T18:05:00+00:00", "author": { "id": "1003", "name": "Publishing Developer" }, "message": null,
              "documents": [ { "documentType": "achievements2017", "displayName": "Achievements 2017", "description": "Already live." } ] }
          ],
          "continuationToken": null
        }
        """;

    [Fact]
    public async Task HistoryRead_SendsTheSandboxAndAFullPage()
    {
        var handler = new RouteHandler(_ => Ok("{}"));
        using var client = CreateClient(handler);

        await client.GetXboxLiveConfigurationHistoryAsync(12345, Scid, "XDKS.1", 100, CancellationToken.None);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/xboxpartner/v1/xboxlive/configurations/history", request.Path);
        Assert.Equal($$"""{"sellerId":12345,"scid":"{{ScidText}}","sandbox":"XDKS.1","pageSize":100}""", request.Body);
    }

    [Fact]
    public async Task SandboxStatus_ReadsItsStatusLastPublishTitleDraftAndHistory()
    {
        var (_, _, _, handler) = await RunStatusAsync(Routes(), OutputOptions.Json);

        Assert.Equal(
            [
                $$"""{"sellerId":12345,"scid":"{{ScidText}}","sandbox":"XDKS.1","scope":"sandbox"}""",
                $$"""{"sellerId":12345,"scid":"{{ScidText}}","scope":"title"}""",
                $$"""{"sellerId":12345,"scid":"{{ScidText}}","sandbox":"XDKS.1","pageSize":100}""",
                $$"""{"sellerId":12345,"scid":"{{ScidText}}","sandbox":"XDKS.1"}""",
            ],
            handler.Requests.Take(1).Concat(handler.Requests.Skip(1).OrderBy(request => request.Route)).Select(request => request.Body));
    }

    [Fact]
    public async Task SandboxStatus_FailsWhenThePublishStatusCantBeRead()
    {
        var routes = Routes();
        routes["status"] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"status":500,"title":"Internal Server Error","code":"publish_status_failed"}"""),
        };

        var (exitCode, stdout, stderr, _) = await RunStatusAsync(routes, OutputOptions.Json);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("publish_status_failed", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxStatus_APublishedOnlyDocumentAloneIsNothingToPublish()
    {
        string status = """{"scope":"sandbox","sandbox":"XDKS.1","workingVersion":55,"publishedVersion":52,"documents":[{"name":"leaderboards.xml","scope":"sandbox","state":"same"},{"name":"sets.xml","scope":"sandbox","state":"published-only"}]}""";

        var (_, stdout, _, _) = await RunStatusAsync(Routes(status: status), OutputOptions.Json);

        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("hasChanges").GetBoolean());
        Assert.Equal("published-only", Document(json.RootElement, "sets.xml").GetProperty("state").GetString());

        var (_, table, tableStderr, _) = await RunStatusAsync(
            Routes(status: status),
            OutputOptions.Table(XboxPartnerCommandSupport.ConfigurationStatusTableView));

        Assert.Contains(
            table.Split(Environment.NewLine),
            line => line.Contains("Set definitions", StringComparison.Ordinal) && line.Contains("not in draft", StringComparison.Ordinal));
        Assert.Contains($"{Environment.NewLine}Nothing to publish to XDKS.1.{Environment.NewLine}", tableStderr, StringComparison.Ordinal);
        Assert.DoesNotContain("differ", tableStderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxStatus_DescribesTheSavesSinceTheLastPublishAndNamesWhoSavedEachVersion()
    {
        var (exitCode, stdout, stderr, _) = await RunStatusAsync(Routes(), OutputOptions.Json);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.Equal("XDKS.1", root.GetProperty("sandbox").GetString());
        Assert.Equal(ScidText, root.GetProperty("scid").GetString());
        Assert.True(root.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(55UL, root.GetProperty("draft").GetProperty("version").GetUInt64());
        Assert.Equal("Nod Developer", root.GetProperty("draft").GetProperty("userName").GetString());
        Assert.Equal("1001", root.GetProperty("draft").GetProperty("user").GetString());
        Assert.Equal("Add Win a race", root.GetProperty("draft").GetProperty("message").GetString());
        Assert.Equal(52UL, root.GetProperty("published").GetProperty("version").GetUInt64());
        Assert.Equal("Publishing Developer", root.GetProperty("published").GetProperty("userName").GetString());
        Assert.Equal("1003", root.GetProperty("published").GetProperty("user").GetString());
        Assert.Equal(60UL, root.GetProperty("titleDraft").GetProperty("version").GetUInt64());
        Assert.False(root.TryGetProperty("workingVersion", out _));
        Assert.Equal("Success", root.GetProperty("lastPublish").GetProperty("status").GetString());

        JsonElement achievements = Document(root, "achievements2017.xml");
        Assert.Equal("Achievements", achievements.GetProperty("feature").GetString());
        Assert.Equal("ready", achievements.GetProperty("descriptionStatus").GetString());
        JsonElement change = Assert.Single(achievements.GetProperty("changes").EnumerateArray());
        Assert.Equal("1 achievements added. Total gamerscore: 1000.", change.GetProperty("description").GetString());
        Assert.Equal(55UL, change.GetProperty("version").GetUInt64());
        Assert.Equal("Nod Developer", change.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("Add Win a race", change.GetProperty("message").GetString());
        Assert.Equal("9C1E", achievements.GetProperty("draftHash").GetString());

        JsonElement leaderboards = Document(root, "leaderboards.xml");
        Assert.False(leaderboards.TryGetProperty("descriptionStatus", out _));
        Assert.False(leaderboards.TryGetProperty("changes", out _));

        Assert.Equal("unavailable", Document(root, "sets.xml").GetProperty("descriptionStatus").GetString());
        Assert.Empty(Document(root, "sets.xml").GetProperty("changes").EnumerateArray());
        Assert.Equal("unavailable", Document(root, "dataplatformconfig.xml").GetProperty("descriptionStatus").GetString());
    }

    /// <summary>A sandbox status whose draft was saved a moment ago, so its description can still be pending.</summary>
    private static string JustSavedStatus =>
        SandboxStatus.Replace("2026-10-29T21:40:12Z", DateTimeOffset.UtcNow.AddSeconds(-10).ToString("O"), StringComparison.Ordinal);

    private const string HistoryOlderThanTheDraft =
        """{"entries":[{"action":"save","version":54,"sandbox":"XDKS.1","time":"2026-10-28T10:00:00+00:00","documents":[{"documentType":"achievements2017","displayName":"Achievements 2017","description":"1 achievements added."}]},{"action":"publish","version":52,"sandbox":"XDKS.1","time":"2026-10-27T18:05:00+00:00","documents":[]}]}""";

    [Theory]
    [InlineData(HistoryOlderThanTheDraft)]
    [InlineData("""{"entries":[],"continuationToken":null}""")]
    [InlineData("""{"entries":[{"action":"save","version":60,"sandbox":"ALL","documents":[]},{"action":"save","version":54,"sandbox":"XDKS.1","documents":[]}]}""")]
    public async Task SandboxStatus_SaysDescriptionPendingUntilTheHistoryReachesTheDraft(string history)
    {
        var (exitCode, stdout, _, _) = await RunStatusAsync(Routes(status: JustSavedStatus, history: history), OutputOptions.Json);

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("pending", Document(json.RootElement, "achievements2017.xml").GetProperty("descriptionStatus").GetString());
        Assert.Equal("unavailable", Document(json.RootElement, "sets.xml").GetProperty("descriptionStatus").GetString());
        Assert.Equal("unavailable", Document(json.RootElement, "dataplatformconfig.xml").GetProperty("descriptionStatus").GetString());
    }

    [Fact]
    public async Task SandboxStatus_AnOldSaveTheHistoryNeverDescribedIsUnavailableNotPending()
    {
        var (_, stdout, _, _) = await RunStatusAsync(Routes(history: HistoryOlderThanTheDraft), OutputOptions.Json);

        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("unavailable", Document(json.RootElement, "achievements2017.xml").GetProperty("descriptionStatus").GetString());
    }

    [Fact]
    public async Task SandboxStatus_AnEntryWithoutItsSandboxStillDescribesTheDraft()
    {
        string history =
            """{"entries":[{"action":"save","version":55,"sandbox":null,"author":{"id":"1001","name":"Nod Developer"},"documents":[{"documentType":"achievements2017","displayName":"Achievements 2017","description":"1 achievements added."}]},{"action":"publish","version":52,"sandbox":"XDKS.1","documents":[]}]}""";

        var (_, stdout, _, _) = await RunStatusAsync(Routes(history: history), OutputOptions.Json);

        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement achievements = Document(json.RootElement, "achievements2017.xml");
        Assert.Equal("ready", achievements.GetProperty("descriptionStatus").GetString());
        Assert.Equal("1 achievements added.", achievements.GetProperty("changes")[0].GetProperty("description").GetString());
        Assert.Equal("Nod Developer", json.RootElement.GetProperty("draft").GetProperty("userName").GetString());
    }

    [Theory]
    [InlineData("""{"entries":[{"action":"save","version":60,"sandbox":"ALL","time":"2026-10-30T00:00:00+00:00","documents":[]}],"continuationToken":"next"}""")]
    public async Task SandboxStatus_APageOfOnlyNewerEntriesIsUnavailableNotPending(string history)
    {
        var (_, stdout, _, _) = await RunStatusAsync(Routes(history: history), OutputOptions.Json);

        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("unavailable", Document(json.RootElement, "achievements2017.xml").GetProperty("descriptionStatus").GetString());
    }

    [Fact]
    public async Task SandboxStatus_ACopyMeansTheDescriptionsAreIncomplete()
    {
        string history =
            """{"entries":[{"action":"save","version":55,"sandbox":"XDKS.1","documents":[{"documentType":"achievements2017","displayName":"Achievements 2017","description":"1 achievements added."}]},{"action":"copy","version":53,"sandbox":"XDKS.1","documents":[]},{"action":"publish","version":52,"sandbox":"XDKS.1","documents":[]}]}""";

        var (exitCode, stdout, stderr, _) = await RunStatusAsync(Routes(history: history), OutputOptions.Json);

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement achievements = Document(json.RootElement, "achievements2017.xml");
        Assert.Equal("unavailable", achievements.GetProperty("descriptionStatus").GetString());
        Assert.Single(achievements.GetProperty("changes").EnumerateArray());
        Assert.Contains("copied from another sandbox", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxStatus_ANeverPublishedSandboxsWholeHistoryIsComplete()
    {
        string status = SandboxStatus.Replace(
            "\"publishedVersion\": 52,",
            "\"publishedVersion\": 50,",
            StringComparison.Ordinal);
        string history =
            """{"entries":[{"action":"save","version":55,"sandbox":"XDKS.1","documents":[{"documentType":"achievements2017","description":"1 achievements added."}]}],"continuationToken":null}""";

        var (_, stdout, stderr, _) = await RunStatusAsync(Routes(status: status, history: history), OutputOptions.Json);

        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("ready", Document(json.RootElement, "achievements2017.xml").GetProperty("descriptionStatus").GetString());
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task SandboxStatus_AHistoryReadThatTimesOutOnlyRemovesDescriptions()
    {
        var routes = Routes();
        routes["history"] = () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var (exitCode, stdout, stderr, _) = await RunStatusAsync(routes, OutputOptions.Json);

        Assert.Equal(0, exitCode);
        Assert.Equal($"Change descriptions are unavailable. The request timed out.{Environment.NewLine}", stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("unavailable", Document(json.RootElement, "achievements2017.xml").GetProperty("descriptionStatus").GetString());
    }

    [Fact]
    public async Task Status_AStatusReadThatTimesOutSaysSo()
    {
        var routes = Routes();
        routes["get"] = () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var (exitCode, stdout, stderr, _) = await RunStatusAsync(routes, OutputOptions.Json);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.EndsWith($"Request timed out.{Environment.NewLine}", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_LetsCtrlCThrough()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RouteHandler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException();
        });
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfigStatusCommandFactory.RunAsync(
            client, 12345, XboxLiveScope.Title, Scid, null, OutputOptions.Json, stdout, stderr, cancellation.Token));

        Assert.Empty(stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task List_LetsCtrlCThrough()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = CreateClient(new RouteHandler(_ => throw new TaskCanceledException()));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ListConfigurationsCommandFactory.RunAsync(
            client, 12345, Scid, OutputOptions.Json, stdout, stderr, cancellation.Token));

        Assert.Empty(stdout.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Status":null}""")]
    public async Task Status_KeepsALastPublishWithoutAStatus(string publish)
    {
        string status = """{"scope":"title","sandbox":null,"workingVersion":60,"documents":[]}""";
        foreach (bool table in new[] { false, true })
        {
            using var client = CreateClient(new RouteHandler(request => Ok(request.Route == "status" ? publish : status)));
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = await ConfigStatusCommandFactory.RunAsync(
                client, 12345, XboxLiveScope.Title, Scid, null,
                table ? OutputOptions.Table(XboxPartnerCommandSupport.ConfigurationStatusTableView) : OutputOptions.JsonCompact,
                stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exitCode);
            if (!table)
            {
                using JsonDocument json = JsonDocument.Parse(stdout.ToString());
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("lastPublish").GetProperty("status").ValueKind);
            }
        }
    }

    [Fact]
    public async Task SandboxStatus_AMalformedHistoryRemovesDescriptionsWithoutHidingABug()
    {
        var (exitCode, _, stderr, _) = await RunStatusAsync(Routes(history: """{"entries":[null]}"""), OutputOptions.Json);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            $"Change descriptions are unavailable. The configuration history response must contain an entries array of objects.{Environment.NewLine}",
            stderr);
    }

    [Fact]
    public async Task SandboxStatus_StillWorksWhenDescriptionsAreUnavailable()
    {
        var routes = Routes();
        routes["history"] = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"status":503,"title":"Service Unavailable"}"""),
        };

        var (exitCode, stdout, stderr, _) = await RunStatusAsync(routes, OutputOptions.Json);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            $"Change descriptions are unavailable. The request failed with HTTP 503 (Service Unavailable).{Environment.NewLine}",
            stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement achievements = Document(json.RootElement, "achievements2017.xml");
        Assert.Equal("unavailable", achievements.GetProperty("descriptionStatus").GetString());
        Assert.Empty(achievements.GetProperty("changes").EnumerateArray());
        Assert.Null(json.RootElement.GetProperty("draft").GetProperty("userName").GetString());
    }

    [Theory]
    [InlineData("localization-en-US.xml", "Localized strings")]
    [InlineData("nodarena.xml", "nodarena.xml")]
    public void FeatureNames_NameEachDocumentByItsFeature(string name, string expected) =>
        Assert.Equal(expected, XboxLiveFeatureNames.NameOf(name));

    [Fact]
    public async Task SandboxStatus_ANotFoundPublishStatusMeansNoLastPublish()
    {
        var routes = Routes();
        routes["status"] = () => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"status":404,"title":"Not Found"}"""),
        };

        var (exitCode, stdout, stderr, _) = await RunStatusAsync(routes, OutputOptions.Json);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("lastPublish").ValueKind);
    }

    [Fact]
    public async Task SandboxStatus_FailsWithTheStatusReadsOwnError()
    {
        var routes = Routes();
        routes["get"] = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"status":400,"title":"Bad Request","code":"invalid_sandbox"}"""),
        };
        routes["history"] = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"status":503,"title":"Service Unavailable"}"""),
        };

        var (exitCode, stdout, stderr, handler) = await RunStatusAsync(routes, OutputOptions.Json);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("invalid_sandbox", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Service Unavailable", stderr, StringComparison.Ordinal);
        Assert.Single(handler.Requests, request => request.Route == "get");
    }

    [Fact]
    public async Task SandboxStatus_RejectsAStatusWithoutDocuments()
    {
        var routes = Routes();
        routes["get"] = () => Ok("""{"scope":"sandbox","workingVersion":55}""");

        var (exitCode, stdout, stderr, _) = await RunStatusAsync(routes, OutputOptions.Json);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("documents array", stderr, StringComparison.Ordinal);
        Assert.Contains($"{XboxPartnerClient.CorrelationIdHeader}: ", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxStatus_NotesWhenTheLastPublishIsOlderThanTheHistoryPage()
    {
        string history =
            """{"entries":[{"action":"save","version":55,"sandbox":"XDKS.1","documents":[]}],"continuationToken":"next"}""";

        var (_, stdout, stderr, _) = await RunStatusAsync(Routes(history: history), OutputOptions.Json);

        Assert.Equal(
            $"Change descriptions cover the latest 100 saves and publishes only, which don't reach the last publish.{Environment.NewLine}",
            stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("unavailable", Document(json.RootElement, "achievements2017.xml").GetProperty("descriptionStatus").GetString());
    }

    [Fact]
    public async Task SandboxStatus_LeavesOutSavesAndPublishesAfterTheStatusRead()
    {
        string later =
            """{ "action": "publish", "version": 58, "sandbox": "XDKS.1", "documents": [] }, """ +
            """{ "action": "save", "version": 57, "sandbox": "XDKS.1", "documents": [ { "documentType": "achievements2017", "description": "Saved after the status read." } ] }, """;
        string history = History.Replace("\"entries\": [", "\"entries\": [" + later, StringComparison.Ordinal);

        var (_, stdout, _, _) = await RunStatusAsync(Routes(history: history), OutputOptions.Json);

        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement achievements = Document(json.RootElement, "achievements2017.xml");
        Assert.Equal("ready", achievements.GetProperty("descriptionStatus").GetString());
        Assert.Equal([55UL], achievements.GetProperty("changes").EnumerateArray().Select(change => change.GetProperty("version").GetUInt64()));
    }

    [Fact]
    public async Task SandboxStatus_RightAfterAPublishReadsThePublishNewerThanTheDraft()
    {
        string status = SandboxStatus.Replace("\"publishedVersion\": 52", "\"publishedVersion\": 56", StringComparison.Ordinal);
        string history =
            """{"entries":[{"action":"publish","version":56,"sandbox":"XDKS.1","author":{"id":"1005","name":"Release Developer"},"documents":[]},""" +
            """{"action":"save","version":55,"sandbox":"XDKS.1","documents":[{"documentType":"achievements2017","description":"Already published."}]},""" +
            """{"action":"publish","version":52,"sandbox":"XDKS.1","documents":[]}]}""";

        var (_, stdout, stderr, _) = await RunStatusAsync(Routes(status: status, history: history), OutputOptions.Json);

        Assert.Empty(stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("Release Developer", json.RootElement.GetProperty("published").GetProperty("userName").GetString());
        Assert.Empty(Document(json.RootElement, "achievements2017.xml").GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task SandboxStatus_TableShowsEachChangeAndWritesTheVersionsToStderr()
    {
        var (exitCode, stdout, stderr, _) = await RunStatusAsync(
            Routes(),
            OutputOptions.Table(XboxPartnerCommandSupport.ConfigurationStatusTableView));

        Assert.Equal(0, exitCode);
        string[] lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("SCOPE", lines[0], StringComparison.Ordinal);
        Assert.Contains("FEATURE", lines[0], StringComparison.Ordinal);
        Assert.Contains("SAVED BY", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line =>
            line.Contains("Achievements", StringComparison.Ordinal)
            && line.Contains("changed", StringComparison.Ordinal)
            && line.Contains("1 achievements added", StringComparison.Ordinal)
            && line.Contains("Nod Developer", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Set definitions", StringComparison.Ordinal) && line.Contains("added", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("title", StringComparison.Ordinal) && line.Contains("Stats and achievements systems", StringComparison.Ordinal));
        Assert.DoesNotContain("leaderboards", stdout, StringComparison.Ordinal);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Published: version 52. Last publish: Success.",
                "Draft: version 55. Title draft: version 60 (ALL).",
                "3 documents differ for a publish to XDKS.1.",
                string.Empty),
            stderr);
    }

    [Fact]
    public async Task Status_TablePendingRowSaysDescriptionPending()
    {
        var (_, stdout, _, _) = await RunStatusAsync(
            Routes(status: JustSavedStatus, history: HistoryOlderThanTheDraft),
            OutputOptions.Table(XboxPartnerCommandSupport.ConfigurationStatusTableView));

        Assert.Contains(stdout.Split(Environment.NewLine), line =>
            line.Contains("Achievements", StringComparison.Ordinal) && line.Contains("description pending", StringComparison.Ordinal));
        Assert.Contains(stdout.Split(Environment.NewLine), line =>
            line.Contains("Set definitions", StringComparison.Ordinal) && !line.Contains("pending", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_TableSaysWhenTheDraftMatchesLiveOrWasNeverPublished()
    {
        var routes = new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["get"] = () => Ok("""{"scope":"account","sandbox":null,"workingVersion":3,"publishedVersion":null,"documents":[{"name":"relyingparty.xml","state":"same"}]}"""),
        };
        var handler = new RouteHandler(request => routes[request.Route]());
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await ConfigStatusCommandFactory.RunAsync(
            client, 12345, XboxLiveScope.Account, null, null,
            OutputOptions.Table(XboxPartnerCommandSupport.ConfigurationStatusTableView), stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(TableFormatter.EmptyNotice + Environment.NewLine, stdout.ToString());
        Assert.Equal(
            string.Join(Environment.NewLine, "Published: never.", "Draft: version 3.", "The draft matches what's published.", string.Empty),
            stderr.ToString());
        Assert.Equal("""{"sellerId":12345,"scope":"account"}""", Assert.Single(handler.Requests).Body);
    }

    /// <summary>
    /// The title's status: the embargo in productdeveloper is saved but not published to HIDDEN,
    /// and titlepriv's change was published only to a sandbox, so HIDDEN's copy differs.
    /// </summary>
    private const string TitleStatus =
        """
        {
          "scope": "title",
          "sandbox": null,
          "workingVersion": 60,
          "publishedVersion": 58,
          "documents": [
            { "name": "dataplatformconfig.xml", "scope": "title", "state": "same", "draftHash": "5D01", "publishedHash": "5D01" },
            { "name": "globalaccesspolicies.xml", "scope": "title", "state": "same", "draftHash": "1A2B", "publishedHash": "1A2B" },
            { "name": "nsal.xml", "scope": "title", "state": "same", "draftHash": "3C4D", "publishedHash": "3C4D" },
            { "name": "productdeveloper.xml", "scope": "title", "state": "changed", "draftHash": "9E8F", "publishedHash": "7A6B" },
            { "name": "productsystem.xml", "scope": "title", "state": "same", "draftHash": "5C4D", "publishedHash": "5C4D" },
            { "name": "titlepriv.xml", "scope": "title", "state": "changed", "draftHash": "2E3F", "publishedHash": "4A5B" }
          ]
        }
        """;

    /// <summary>Answers the title-scope read and HIDDEN's publish status, and fails any other request, such as a sandbox's.</summary>
    private static RouteHandler TitleOnly(string status = TitleStatus) => new(request =>
        request.Route == "get" && request.Body.Contains("\"scope\":\"title\"", StringComparison.Ordinal) ? Ok(status)
        : request.Route == "status" && request.Body.Contains("\"sandbox\":\"HIDDEN\"", StringComparison.Ordinal) ? Ok(PublishStatus)
        : throw new InvalidOperationException($"No sandbox is read: {request.Path} {request.Body}"));

    [Theory]
    [InlineData("--scope", "title")]
    [InlineData("--sandbox-id", "HIDDEN")]
    [InlineData("--sandbox-id", "ALL")]
    [InlineData("--sandbox-id", "all")]
    public async Task TitleStatus_EverySelectorListsTheSixTitleDocumentsAndReadsNoSandbox(string option, string value)
    {
        RouteHandler handler = TitleOnly();

        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "status", "--seller-id", "1234567", "--scid", ScidText, option, value],
            handler);

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(
            [
                $$"""{"sellerId":1234567,"scid":"{{ScidText}}","scope":"title"}""",
                $$"""{"sellerId":1234567,"scid":"{{ScidText}}","sandbox":"HIDDEN"}""",
            ],
            handler.Requests.Select(request => request.Body));
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.Equal("title", root.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sandbox").ValueKind);
        Assert.True(root.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(
            [
                "productsystem.xml:same:HIDDEN", "globalaccesspolicies.xml:same:HIDDEN", "productdeveloper.xml:changed:HIDDEN",
                "titlepriv.xml::sandbox", "nsal.xml::sandbox", "dataplatformconfig.xml::sandbox",
            ],
            root.GetProperty("documents").EnumerateArray().Select(document =>
                $"{document.GetProperty("name").GetString()}:{document.GetProperty("state").GetString()}:{document.GetProperty("publishesWith").GetString()}"));
        Assert.Equal(JsonValueKind.Null, Document(root, "titlepriv.xml").GetProperty("publishedHash").ValueKind);
        Assert.False(Document(root, "titlepriv.xml").TryGetProperty("descriptionStatus", out _));
        Assert.Equal("unavailable", Document(root, "productdeveloper.xml").GetProperty("descriptionStatus").GetString());
    }

    [Fact]
    public async Task TitleStatus_AChangePublishedOnlyToASandboxIsNeitherPendingForHiddenNorUpToDate()
    {
        RouteHandler handler = TitleOnly(TitleStatus.Replace(
            """"state": "changed", "draftHash": "9E8F", "publishedHash": "7A6B"""",
            """"state": "same", "draftHash": "9E8F", "publishedHash": "9E8F"""",
            StringComparison.Ordinal));

        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "status", "--seller-id", "1234567", "--scid", ScidText, "--scope", "title", "--output", "table"],
            handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Published: version 58 (HIDDEN). Last publish: Success.",
                "Draft: version 60 (ALL).",
                "The draft matches what's published to HIDDEN.",
                "3 documents publish with each sandbox. Check them with config status --sandbox-id <sandbox>.",
                string.Empty),
            stderr);
        string[] lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8, lines.Length);
        Assert.Contains(lines, line =>
            line.Contains("Privileges", StringComparison.Ordinal)
            && line.Contains("Publishes with each sandbox", StringComparison.Ordinal)
            && !line.Contains("changed", StringComparison.Ordinal)
            && !line.Contains("same", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("Title names and platforms", StringComparison.Ordinal)
            && line.Contains("same", StringComparison.Ordinal)
            && line.Contains("Publishes through HIDDEN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_WritesTheCliShapeWithoutTheServicesOwnFields()
    {
        string status =
            """{"scope":"title","sandbox":null,"hasWorkingConfiguration":true,"workingVersion":60,"hasUnpublishedChanges":false,"titleId":"1234ABCD","documents":[{"name":"productdeveloper.xml","scope":"title","state":"same","size":12}]}""";
        using var client = CreateClient(new RouteHandler(request => Ok(request.Route == "status"
            ? """{"Status":"Running","JobId":"","StatusMessage":null,"StartedAt":"2026-10-29T21:40:12Z"}"""
            : status)));
        using var stdout = new StringWriter();

        await ConfigStatusCommandFactory.RunAsync(
            client, 12345, XboxLiveScope.Title, Scid, null, OutputOptions.JsonCompact, stdout, TextWriter.Null, CancellationToken.None);

        Assert.Equal(
            $$"""{"scope":"title","scid":"{{ScidText}}","sandbox":null,"draft":{"version":60,"time":null,"user":null,"userName":null,"message":null},"published":null,"documents":[{"name":"productdeveloper.xml","feature":"Title names and platforms","scope":"title","state":"same","draftHash":null,"publishedHash":null,"publishesWith":"HIDDEN"}],"lastPublish":{"status":"Running","jobId":null,"message":null},"hasChanges":false}""" + Environment.NewLine,
            stdout.ToString());
    }

    [Theory]
    [InlineData("xbl config status --scope sandbox --scid " + ScidText)]
    [InlineData("xbl config status --scope account")]
    public void StatusOptions_Parse(string command) =>
        Assert.Empty(Program.BuildRootCommand().Parse(command, Program.NoResponseFileConfig).Errors);

    [Theory]
    [InlineData("xbl config status --scope title --scid not-a-guid", "--scid must be a non-empty GUID")]
    [InlineData("xbl config status --scope account --scid " + ScidText, "--scope account uses the seller account's configuration, so it takes no --scid.")]
    [InlineData("xbl config status --scope title --scid " + ScidText + " --sandbox-id XDKS.1", "--scope title uses the title-wide configuration, so it takes no --sandbox-id.")]
    public void StatusOptions_RejectAnInvalidScidAndTargetsTheScopeDoesntTake(string command, string expected)
    {
        var errors = Program.BuildRootCommand().Parse(command, Program.NoResponseFileConfig).Errors;

        Assert.Contains(errors, error => error.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusAction_ByTitleWithoutASellerReadsTheHistoryAsTheResolvedSeller()
    {
        var handler = new RouteHandler(request =>
        {
            if (request.Route != "get")
            {
                return Ok(request.Route == "status" ? PublishStatus : History);
            }

            HttpResponseMessage response = Ok(SandboxStatus);
            response.Headers.Add("X-XboxPartner-Scid", ScidText);
            response.Headers.Add("X-XboxPartner-Seller-Id", "1234567");
            return response;
        });

        var (exitCode, _, _) = await InvokeAsync(
            ["xbl", "config", "status", "--title-id", "1234ABCD", "--sandbox-id", "XDKS.1"],
            handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            $$"""{"sellerId":1234567,"scid":"{{ScidText}}","sandbox":"XDKS.1","pageSize":100}""",
            handler.Requests[^1].Body);
    }

    [Fact]
    public async Task StatusAction_ByTitleReadsTheHistoryByTheResolvedScid()
    {
        var handler = new RouteHandler(request =>
        {
            if (request.Route != "get")
            {
                return Ok(request.Route == "status" ? PublishStatus : History);
            }

            HttpResponseMessage response = Ok(SandboxStatus);
            response.Headers.Add("X-XboxPartner-Scid", ScidText);
            response.Headers.Add("X-XboxPartner-Seller-Id", "1234567");
            return response;
        });

        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "status", "--seller-id", "1234567", "--title-id", "1234ABCD", "--sandbox-id", "XDKS.1"],
            handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["""{"sellerId":1234567,"titleId":"1234ABCD","sandbox":"XDKS.1","scope":"sandbox"}""",
             $$"""{"sellerId":1234567,"scid":"{{ScidText}}","scope":"title"}""",
             $$"""{"sellerId":1234567,"scid":"{{ScidText}}","sandbox":"XDKS.1","pageSize":100}""",
             $$"""{"sellerId":1234567,"scid":"{{ScidText}}","sandbox":"XDKS.1"}"""],
            handler.Requests.Take(1).Concat(handler.Requests.Skip(1).OrderBy(request => request.Route)).Select(request => request.Body));
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(ScidText, json.RootElement.GetProperty("scid").GetString());
    }

    [Fact]
    public async Task StatusAction_ByTitlePointsHistoryToTheTitleScopeByTitle()
    {
        var (exitCode, _, stderr) = await InvokeAsync(
            ["xbl", "config", "status", "--seller-id", "1234567", "--title-id", "1234ABCD", "--sandbox-id", "HISTORY"],
            new RouteHandler(_ => throw new InvalidOperationException("No request is expected.")));

        Assert.Equal(1, exitCode);
        Assert.Contains(
            $"Run '{CliIdentity.CommandName} xbl config status --scope title --title-id 1234ABCD'.",
            stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ReadsEverySetAndLeavesOutTheTitlesOwnSandboxes()
    {
        var handler = new RouteHandler(request => request.Route switch
        {
            "configurations" => Ok(SandboxList("XDKS.1", "ALL", "CERT", "HIDDEN")),
            "get" or "status" => Ok(StatusFor(request.Body)),
            _ => throw new InvalidOperationException(request.Path),
        });

        var (exitCode, stdout, stderr) = await RunListAsync(handler, OutputOptions.Json);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(
            ["sandbox:XDKS.1:True", "sandbox:CERT:False", "title::False", "account::False"],
            json.RootElement.EnumerateArray().Select(set =>
                $"{set.GetProperty("scope").GetString()}:{set.GetProperty("sandbox").GetString()}:{set.GetProperty("hasChanges").GetBoolean()}"));
        JsonElement first = json.RootElement[0];
        Assert.Equal(55UL, first.GetProperty("draft").GetProperty("version").GetUInt64());
        Assert.Equal("2026-10-29T21:40:12+00:00", first.GetProperty("draft").GetProperty("time").GetString());
        Assert.Equal(52UL, first.GetProperty("published").GetProperty("version").GetUInt64());
        Assert.Equal(
            "2026-10-27T18:05:00+00:00",
            first.GetProperty("published").GetProperty("time").GetString());
        Assert.Equal("Success", first.GetProperty("lastPublish").GetProperty("status").GetString());
        Assert.False(first.TryGetProperty("documents", out _));
        Assert.Equal(
            ["CERT", "HIDDEN", "XDKS.1"],
            handler.Requests
                .Where(request => request.Route == "status")
                .Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("sandbox").GetString())
                .Order());

        // A sandbox's row comes from the list: only the title and the account read their status.
        Assert.Equal(
            ["account", "title"],
            handler.Requests
                .Where(request => request.Route == "get")
                .Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("scope").GetString())
                .Order());
        Assert.Equal(6, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Sandbox", "sandbox:XDKS.1,sandbox:CERT")]
    [InlineData("Title", "title:")]
    public async Task List_ScopeKeepsOnlyThatScopesRows(string scope, string expected)
    {
        var handler = new RouteHandler(request => request.Route == "configurations"
            ? Ok("""{"configurations":[{"sandbox":"XDKS.1"},{"sandbox":"HIDDEN"},{"sandbox":"CERT"}]}""")
            : Ok(StatusFor(request.Body)));
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();

        int exitCode = await ListConfigurationsCommandFactory.RunAsync(
            client, 12345, Scid, OutputOptions.Json, stdout, TextWriter.Null, CancellationToken.None, scope: Enum.Parse<XboxLiveScope>(scope));

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(
            expected,
            string.Join(",", json.RootElement.EnumerateArray().Select(set => $"{set.GetProperty("scope").GetString()}:{set.GetProperty("sandbox").GetString()}")));
    }

    [Fact]
    public async Task List_TableHasOneRowPerSet()
    {
        var handler = new RouteHandler(request => request.Route == "configurations"
            ? Ok(SandboxList("XDKS.1"))
            : Ok(StatusFor(request.Body)));

        var (exitCode, stdout, _) = await RunListAsync(
            handler,
            OutputOptions.Table(XboxPartnerCommandSupport.ConfigurationTableView));

        Assert.Equal(0, exitCode);
        string[] lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Contains("LAST PUBLISH", lines[0], StringComparison.Ordinal);
        Assert.Contains("XDKS.1", lines[2], StringComparison.Ordinal);
        Assert.Contains("Yes", lines[2], StringComparison.Ordinal);
        Assert.Contains("2026-10-27 18:05:00 Z", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("title", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("account", lines[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_LeavesOutTheAccountWhenItsConfigurationIsDenied()
    {
        var handler = new RouteHandler(request => request.Route switch
        {
            "configurations" => Ok("""{"configurations":[{"sandbox":"XDKS.1"}]}"""),
            _ when request.Body.Contains("\"account\"", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"status":403,"title":"Forbidden"}"""),
            },
            _ => Ok(StatusFor(request.Body)),
        });

        var (exitCode, stdout, stderr) = await RunListAsync(handler, OutputOptions.Json);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, JsonDocument.Parse(stdout).RootElement.GetArrayLength());
        Assert.Equal(
            $"The seller account's configuration isn't listed: access to it was denied (HTTP 403).{Environment.NewLine}",
            stderr);
    }

    [Fact]
    public async Task List_PrintsNothingWhenASetCantBeRead()
    {
        var handler = new RouteHandler(request => request.Route switch
        {
            "configurations" => Ok("""{"configurations":[{"sandbox":"XDKS.1"},{"sandbox":"CERT"}]}"""),
            _ when request.Body.Contains("\"CERT\"", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"status":500,"title":"Internal Server Error","code":"cert_failed"}"""),
            },
            _ => Ok(StatusFor(request.Body)),
        });

        var (exitCode, stdout, stderr) = await RunListAsync(handler, OutputOptions.Json);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("cert_failed", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_StopsReadingAfterTheFirstFailure()
    {
        string sandboxes = "{\"configurations\":[" + string.Join(',', Enumerable.Range(1, 20).Select(i => $"{{\"sandbox\":\"S{i}\"}}")) + "]}";
        var handler = new RouteHandler(request => request.Route == "configurations"
            ? Ok(sandboxes)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"status":401,"title":"Unauthorized"}""") });

        var (exitCode, stdout, stderr) = await RunListAsync(handler, OutputOptions.Json);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("401", stderr, StringComparison.Ordinal);
        Assert.InRange(handler.Requests.Count(request => request.Route != "configurations"), 1, ListConfigurationsCommandFactory.MaxConcurrentReads + 1);
    }

    [Fact]
    public async Task List_RejectsASandboxWithNoName()
    {
        var handler = new RouteHandler(_ => Ok("""{"configurations":[{"sandbox":" "}]}"""));

        var (exitCode, stdout, stderr) = await RunListAsync(handler, OutputOptions.Json);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("invalid sandbox", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("XDKS.1", null, "Saved to the XDKS.1 draft. Not published yet. Run 'xbox --profile staging xbl config publish --scid " + ScidText + " --sandbox-id XDKS.1' to publish it.")]
    [InlineData("all", null, "Saved to the title draft. Not published yet. Run 'xbox --profile staging xbl config status --scope title --scid " + ScidText + "' to see where each document goes live. dataplatformconfig, titlepriv, and nsal go live with each sandbox's own publish, so check them with --sandbox-id <sandbox>.")]
    [InlineData("HIDDEN", null, "Saved to the HIDDEN draft. Not published yet. Run 'xbox --profile staging xbl config download --scid " + ScidText + " --sandbox-id HIDDEN --view draft --dest <directory>' to check it.")]
    [InlineData(null, 1234567L, "Saved to the account draft. Not published yet. Run 'xbox --profile staging xbl config publish --scope account --seller-id 1234567' to publish it.")]
    public void Save_EndsBySayingWhereTheDocumentsWent(string? sandbox, long? sellerId, string expected) =>
        Assert.Equal(
            expected,
            CommitDocumentsCommandFactory.DescribeSaved("xbox --profile staging", sellerId, sandbox is null ? null : Scid, sandbox));

    [Fact]
    public void Save_ByTitleSuggestsPublishingByTitle() =>
        Assert.Equal(
            "Saved to the XDKS.1 draft. Not published yet. Run 'xbox xbl config publish --title-id 1234ABCD --sandbox-id XDKS.1' to publish it.",
            CommitDocumentsCommandFactory.DescribeSaved("xbox", null, null, "XDKS.1", "1234ABCD"));

    /// <summary>
    /// The sandbox list, each sandbox with the versions <see cref="StatusFor"/> answers, and
    /// changes in <c>XDKS.1</c> only.
    /// </summary>
    private static string SandboxList(params string[] sandboxes) =>
        JsonSerializer.Serialize(new
        {
            configurations = sandboxes.Select(sandbox => new
            {
                sandbox,
                hasWorkingConfiguration = true,
                workingVersion = 55,
                workingCommitTime = "2026-10-29T21:40:12Z",
                hasPublishedConfiguration = true,
                publishedVersion = 52,
                publishedCommitTime = "2026-10-27T18:05:00Z",
                hasUnpublishedChanges = sandbox == "XDKS.1",
            }),
        });

    private static string StatusFor(string requestBody)
    {
        using JsonDocument request = JsonDocument.Parse(requestBody);
        if (!request.RootElement.TryGetProperty("scope", out JsonElement scopeValue))
        {
            return PublishStatus;
        }

        string scope = scopeValue.GetString()!;
        string? sandbox = request.RootElement.TryGetProperty("sandbox", out JsonElement value) ? value.GetString() : null;
        bool changed = sandbox == "XDKS.1";
        return JsonSerializer.Serialize(new
        {
            scope,
            sandbox,
            workingVersion = 55,
            publishedVersion = 52,
            publishedCommitTime = "2026-10-27T18:05:00Z",
            hasUnpublishedChanges = changed,
            documents = new[] { new { name = "achievements2017.xml", scope = "sandbox", state = changed ? "changed" : "same" } },
        });
    }

    /// <summary>
    /// Answers for a sandbox's status reads. <c>title</c> answers the title-scope read of the title
    /// draft's version.
    /// </summary>
    private static Dictionary<string, Func<HttpResponseMessage>> Routes(
        string status = SandboxStatus,
        string history = History) =>
        new()
        {
            ["get"] = () => Ok(status),
            ["title"] = () => Ok("""{"scope":"title","sandbox":null,"workingVersion":60,"documents":[]}"""),
            ["history"] = () => Ok(history),
            ["status"] = () => Ok(PublishStatus),
        };

    private const string PublishStatus =
        """{"Status":"Success","JobId":"11111111-2222-3333-4444-555555555555","StatusMessage":" "}""";

    private static async Task<(int ExitCode, string Stdout, string Stderr, RouteHandler Handler)> RunStatusAsync(
        Dictionary<string, Func<HttpResponseMessage>> routes,
        OutputOptions output)
    {
        var handler = new RouteHandler(request => routes[
            request.Route == "get" && request.Body.Contains("\"scope\":\"title\"", StringComparison.Ordinal) ? "title" : request.Route]());
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = await ConfigStatusCommandFactory.RunAsync(
            client, 12345, XboxLiveScope.Sandbox, Scid, "XDKS.1", output, stdout, stderr, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString(), handler);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunListAsync(RouteHandler handler, OutputOptions output)
    {
        using var client = CreateClient(handler);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = await ListConfigurationsCommandFactory.RunAsync(
            client, 12345, Scid, output, stdout, stderr, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

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

    private static JsonElement Document(JsonElement status, string name) =>
        status.GetProperty("documents").EnumerateArray().Single(document => document.GetProperty("name").GetString() == name);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static XboxPartnerClient CreateClient(HttpMessageHandler handler) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["XboxPartner:BaseUrl"] = "http://localhost:8080",
                    ["XboxPartner:TimeoutSeconds"] = "10",
                })
                .Build(),
            new HttpClient(handler),
            () => Task.FromResult<string?>("caller-token"));

    private sealed record RecordedRequest(string Path, string Body)
    {
        /// <summary>The route's last segment, such as <c>status</c> or <c>history</c>.</summary>
        public string Route => Path[(Path.LastIndexOf('/') + 1)..];
    }

    /// <summary>Answers each request by its route; requests can arrive concurrently.</summary>
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
            var recorded = new RecordedRequest(request.RequestUri!.AbsolutePath, body);
            lock (_requests)
            {
                _requests.Add(recorded);
            }

            return respond(recorded);
        }
    }
}
