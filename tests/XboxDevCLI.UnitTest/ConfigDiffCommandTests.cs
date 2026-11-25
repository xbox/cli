// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class ConfigDiffCommandTests
{
    private const string ScidText = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly Guid Scid = Guid.Parse(ScidText);

    private static readonly byte[] AchievementsLive = Utf8("<Achievements>\n  <Achievement Id=\"1\" />\n</Achievements>\n");
    private static readonly byte[] AchievementsDraft = Utf8("<Achievements>\n  <Achievement Id=\"1\" />\n  <Achievement Id=\"7\" />\n</Achievements>\n");
    private static readonly byte[] Leaderboards = Utf8("<Leaderboards />\n");
    private static readonly byte[] Sets = Utf8("<Sets />\n");
    private static readonly byte[] Presence = Utf8("<Presence />\n");
    private static readonly byte[] DataPlatformLive = Utf8("<DataPlatform Version=\"1\" />\n");
    private static readonly byte[] DataPlatformDraft = Utf8("<DataPlatform Version=\"2\" />\n");
    private static readonly byte[] ProductDeveloper = Utf8("<ProductDeveloper />\n");
    private static readonly byte[] ProductDeveloperLive = Utf8("<ProductDeveloper Old=\"1\" />\n");
    private static readonly byte[] ProductSystem = Utf8("<ProductSystem />\n");

    // Published documents that are UTF-8 bytes declaring UTF-16, as XBOX services still serve them.
    private static readonly byte[] TitleNamesLive = Utf8("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<Names>\n  <Name>Nod Arena</Name>\n</Names>\n");
    private static readonly byte[] TitleNamesDraft = Utf8("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<Names>\n  <Name>Nod Arène</Name>\n</Names>\n");

    private const string History =
        """
        {
          "entries": [
            { "action": "save", "version": 55, "sandbox": "XDKS.1", "time": "2026-10-29T21:40:12+00:00", "author": { "id": "1001", "name": "Nod Developer" }, "message": "Add Win a race",
              "documents": [ { "documentType": "achievements2017", "displayName": "Achievements 2017", "description": "1 achievements added. Total gamerscore: 1000." } ] },
            { "action": "publish", "version": 52, "sandbox": "XDKS.1", "time": "2026-10-27T18:05:00+00:00", "author": { "id": "1003", "name": "Publishing Developer" }, "message": null, "documents": [] }
          ],
          "continuationToken": null
        }
        """;

    [Fact]
    public async Task DraftAgainstLive_IsTheStatusByFeature_WithEachChangeDescribed()
    {
        var service = new FakeService();

        var (exitCode, stdout, stderr) = await RunAsync(service, Request());

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Equal(["get", "get", "history"], service.Routes.Order());
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.Equal("sandbox", root.GetProperty("scope").GetString());
        Assert.True(root.GetProperty("hasDifferences").GetBoolean());
        Assert.Equal("published", root.GetProperty("old").GetProperty("view").GetString());
        Assert.Equal("XDKS.1", root.GetProperty("old").GetProperty("sandbox").GetString());
        Assert.Equal(52UL, root.GetProperty("old").GetProperty("version").GetUInt64());
        Assert.Equal("Publishing Developer", root.GetProperty("old").GetProperty("userName").GetString());
        Assert.Equal("draft", root.GetProperty("new").GetProperty("view").GetString());
        Assert.Equal(55UL, root.GetProperty("new").GetProperty("version").GetUInt64());

        JsonElement achievements = Document(root, "achievements2017.xml", out string feature);
        Assert.Equal("Achievements", feature);
        Assert.Equal("changed", achievements.GetProperty("state").GetString());
        Assert.Equal(Hash(AchievementsLive), achievements.GetProperty("oldHash").GetString());
        Assert.Equal(Hash(AchievementsDraft), achievements.GetProperty("newHash").GetString());
        Assert.Equal("ready", achievements.GetProperty("descriptionStatus").GetString());
        Assert.Equal(
            "1 achievements added. Total gamerscore: 1000.",
            Assert.Single(achievements.GetProperty("changes").EnumerateArray()).GetProperty("description").GetString());
        Assert.False(achievements.TryGetProperty("lines", out _));

        JsonElement sets = Document(root, "sets.xml", out string setsFeature);
        Assert.Equal("Set definitions", setsFeature);
        Assert.Equal("added", sets.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, sets.GetProperty("oldHash").ValueKind);
        Assert.Equal("unavailable", sets.GetProperty("descriptionStatus").GetString());
        Assert.Equal("removed", Document(root, "presence.xml", out _).GetProperty("state").GetString());
        Assert.False(Document(root, "leaderboards.xml", out _).TryGetProperty("descriptionStatus", out _));
        JsonElement dataPlatform = Document(root, "dataplatformconfig.xml", out _);
        Assert.Equal("title", dataPlatform.GetProperty("scope").GetString());
        Assert.Equal("changed", dataPlatform.GetProperty("state").GetString());
        Assert.Equal(
            ["achievements2017.xml", "leaderboards.xml", "presence.xml", "sets.xml", "titlename-localization.xml", "dataplatformconfig.xml"],
            DocumentNames(root));
    }

    [Fact]
    public async Task DraftAgainstLive_WithLines_DownloadsOnlyTheDifferingDocumentsAtTheStatusVersions()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await RunAsync(service, Request(lines: true));

        Assert.Equal(0, exitCode);
        Assert.Contains("\"+  <Achievement Id=\\\"7\\\" />\"", stdout, StringComparison.Ordinal);
        string[] downloads = [.. service.Bodies("documents").Select(Describe).Order()];
        Assert.Equal(
            [
                "ALL working 60 dataplatformconfig.xml",
                "XDKS.1 published 52 source-achievements2017.xml,source-presence.xml,source-dataplatformconfig.xml",
                "XDKS.1 working 55 achievements2017.xml,sets.xml,titlename-localization.xml",
            ],
            downloads);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(
            [
                "--- achievements2017.xml (XDKS.1 published version 52)",
                "+++ achievements2017.xml (XDKS.1 draft version 55)",
                "@@ -1,3 +1,4 @@",
                " <Achievements>",
                "   <Achievement Id=\"1\" />",
                "+  <Achievement Id=\"7\" />",
                " </Achievements>",
            ],
            Lines(Document(json.RootElement, "achievements2017.xml", out _)));
        Assert.Equal(
            ["--- /dev/null", "+++ sets.xml (XDKS.1 draft version 55)", "@@ -0,0 +1 @@", "+<Sets />"],
            Lines(Document(json.RootElement, "sets.xml", out _)));
        Assert.Equal(
            ["--- presence.xml (XDKS.1 published version 52)", "+++ /dev/null", "@@ -1 +0,0 @@", "-<Presence />"],
            Lines(Document(json.RootElement, "presence.xml", out _)));
        Assert.False(Document(json.RootElement, "leaderboards.xml", out _).TryGetProperty("lines", out _));
    }

    [Fact]
    public async Task AnEarlierLiveVersion_IsReadFromTheSandboxAndComparedThroughItsSourceCopies()
    {
        var service = new FakeService();

        var (exitCode, stdout, stderr) = await RunAsync(service, Request(sandbox: "CERT", oldVersion: 38));

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Equal(["CERT working 38 ", "CERT published 38 "], service.Bodies("documents").Select(Describe));
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.Equal("published", root.GetProperty("old").GetProperty("view").GetString());
        Assert.Equal("CERT", root.GetProperty("old").GetProperty("sandbox").GetString());
        Assert.Equal(38UL, root.GetProperty("old").GetProperty("version").GetUInt64());
        Assert.False(root.GetProperty("old").TryGetProperty("time", out _));
        Assert.Equal(43UL, root.GetProperty("new").GetProperty("version").GetUInt64());

        // The title's own documents, internal documents, and XBOX services' generated output aren't compared.
        Assert.Equal(
            ["achievements2017.xml", "leaderboards.xml", "titlename-localization.xml", "dataplatformconfig.xml"],
            DocumentNames(root));
        Assert.Equal("same", Document(root, "leaderboards.xml", out _).GetProperty("state").GetString());
        Assert.Equal("changed", Document(root, "dataplatformconfig.xml", out _).GetProperty("state").GetString());
        Assert.Equal(Hash(AchievementsLive), Document(root, "achievements2017.xml", out _).GetProperty("oldHash").GetString());
        Assert.All(
            root.GetProperty("features").EnumerateArray().SelectMany(feature => feature.GetProperty("documents").EnumerateArray()),
            document => Assert.False(document.TryGetProperty("descriptionStatus", out _)));
    }

    [Fact]
    public async Task ADocumentDeclaringUtf16_IsComparedByItsBytesAndDiffedAsUtf8()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await RunAsync(
            service,
            Request(sandbox: "CERT", oldVersion: 38, lines: true, features: ["titlename-localization.xml"]));

        Assert.Equal(0, exitCode);
        Assert.Contains("<Name>Nod Arène</Name>", stdout, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement document = Document(json.RootElement, "titlename-localization.xml", out _);
        Assert.Equal("changed", document.GetProperty("state").GetString());
        Assert.Equal(Hash(TitleNamesLive), document.GetProperty("oldHash").GetString());
        Assert.Equal(
            [
                "--- titlename-localization.xml (CERT published version 38)",
                "+++ titlename-localization.xml (CERT draft version 43)",
                "@@ -1,4 +1,4 @@",
                " <?xml version=\"1.0\" encoding=\"utf-16\"?>",
                " <Names>",
                "-  <Name>Nod Arena</Name>",
                "+  <Name>Nod Arène</Name>",
                " </Names>",
            ],
            Lines(document));
    }

    [Fact]
    public async Task AnEarlierDraft_LeavesOutTitleDocumentsAndSaysSo()
    {
        var service = new FakeService();

        var (exitCode, stdout, stderr) = await RunAsync(service, Request(sandbox: "CERT", oldVersion: 41));

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "Not compared: dataplatformconfig.xml. Title documents are saved in the title's draft, so CERT draft version 41 doesn't hold them." +
            Environment.NewLine,
            stderr);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("draft", json.RootElement.GetProperty("old").GetProperty("view").GetString());
        Assert.DoesNotContain("dataplatformconfig.xml", DocumentNames(json.RootElement));
        Assert.Equal(["dataplatformconfig.xml"], NotCompared(json.RootElement));
    }

    [Fact]
    public async Task TwoEarlierVersions_AreBothReadFromTheSandbox()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await RunAsync(service, Request(sandbox: "CERT", oldVersion: 38, newVersion: 41));

        Assert.Equal(0, exitCode);
        Assert.All(service.Bodies("documents"), body => Assert.Equal("CERT", body.GetProperty("sandbox").GetString()));
        Assert.Equal(
            [
                $$"""{"sellerId":12345,"scid":"{{ScidText}}","sandbox":"CERT","scope":"sandbox"}""",
                $$"""{"sellerId":12345,"scid":"{{ScidText}}","scope":"title"}""",
            ],
            service.Requests.Where(request => request.Route == "get").Select(request => request.Body).Distinct());
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.Equal("CERT", root.GetProperty("new").GetProperty("sandbox").GetString());
        Assert.Equal("draft", root.GetProperty("new").GetProperty("view").GetString());
        Assert.Equal(["achievements2017.xml", "leaderboards.xml", "titlename-localization.xml"], DocumentNames(root));
        Assert.Equal("changed", Document(root, "achievements2017.xml", out _).GetProperty("state").GetString());
    }

    [Fact]
    public async Task TheCurrentLiveAndDraftByNumber_AreTheDefaultComparison()
    {
        var service = new FakeService();

        var (exitCode, stdout, stderr) = await RunAsync(service, Request(sandbox: "CERT", oldVersion: 40, newVersion: 43));

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.DoesNotContain("documents", service.Routes);
        Assert.Contains(service.Requests, request => request.Route == "history" && request.Body.Contains("\"CERT\"", StringComparison.Ordinal));
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.Equal(40UL, root.GetProperty("old").GetProperty("version").GetUInt64());
        Assert.Equal(43UL, root.GetProperty("new").GetProperty("version").GetUInt64());
        JsonElement dataPlatform = Document(root, "dataplatformconfig.xml", out _);
        Assert.Equal("changed", dataPlatform.GetProperty("state").GetString());
        Assert.Equal("unavailable", dataPlatform.GetProperty("descriptionStatus").GetString());
    }
    [Fact]
    public async Task LocalFiles_AreComparedWithTheDraftByNameOnly()
    {
        var service = new FakeService();
        byte[] leaderboards = Utf8("<Leaderboards>\n  <Leaderboard Id=\"FastestLap\" />\n</Leaderboards>\n");

        var (exitCode, stdout, _) = await RunAsync(
            service,
            Request(
                files:
                [
                    new XboxDocumentUpload("achievements2017.xml", AchievementsDraft),
                    new XboxDocumentUpload("leaderboards.xml", leaderboards),
                    new XboxDocumentUpload("multiplayer.xml", Sets),
                ],
                lines: true));

        Assert.Equal(0, exitCode);
        Assert.Equal(["XDKS.1 working 55 leaderboards.xml"], service.Bodies("documents").Select(Describe));
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.Equal("draft", root.GetProperty("old").GetProperty("view").GetString());
        Assert.Equal("files", root.GetProperty("new").GetProperty("view").GetString());
        Assert.Equal(3, root.GetProperty("new").GetProperty("files").GetArrayLength());
        Assert.Equal(["achievements2017.xml", "leaderboards.xml", "multiplayer.xml"], DocumentNames(root));
        Assert.Equal("same", Document(root, "achievements2017.xml", out _).GetProperty("state").GetString());
        Assert.Equal("added", Document(root, "multiplayer.xml", out _).GetProperty("state").GetString());
        JsonElement changed = Document(root, "leaderboards.xml", out _);
        Assert.Equal(Hash(leaderboards), changed.GetProperty("newHash").GetString());
        Assert.Contains("+  <Leaderboard Id=\"FastestLap\" />", Lines(changed));
        Assert.Equal("+++ leaderboards.xml (your files)", Lines(changed)[1]);
    }

    [Fact]
    public async Task LocalFiles_AgainstAVersion_ReadOnlyThatVersion()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await RunAsync(
            service,
            Request(sandbox: "CERT", oldVersion: 38, files: [new XboxDocumentUpload("titlename-localization.xml", TitleNamesLive)]));

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("same", Document(json.RootElement, "titlename-localization.xml", out _).GetProperty("state").GetString());
        Assert.False(json.RootElement.GetProperty("hasDifferences").GetBoolean());
    }

    [Theory]
    [InlineData("achievements", "achievements2017.xml")]
    [InlineData("SETS.XML", "sets.xml")]
    [InlineData("dataplatformconfig", "dataplatformconfig.xml")]
    public async Task Feature_MatchesTheFeatureNameTheDocumentNameOrItsType(string feature, string expected)
    {
        var (exitCode, stdout, _) = await RunAsync(new FakeService(), Request(features: [feature]));

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal([expected], DocumentNames(json.RootElement));
    }

    [Fact]
    public async Task Feature_SaysWhenItMatchesNothing()
    {
        var (exitCode, stdout, stderr) = await RunAsync(new FakeService(), Request(features: ["stats"]));

        Assert.Equal(0, exitCode);
        Assert.Contains("--feature 'stats' matches no compared document.", stderr, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Empty(json.RootElement.GetProperty("features").EnumerateArray());
        Assert.False(json.RootElement.GetProperty("hasDifferences").GetBoolean());
    }

    [Fact]
    public async Task Table_ShowsEachDifferenceAndNamesBothSidesOnStderr()
    {
        var (exitCode, stdout, stderr) = await RunAsync(new FakeService(), Request(), OutputOptions.Table(ConfigDiffCommandFactory.DiffTableView));

        Assert.Equal(0, exitCode);
        string[] rows = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("SCOPE", rows[0], StringComparison.Ordinal);
        Assert.Contains(rows, row => row.Contains("Achievements ", StringComparison.Ordinal)
            && row.Contains("achievements2017.xml", StringComparison.Ordinal)
            && row.Contains("1 achievements added. Total gamerscore: 1000.", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith("sandbox", StringComparison.Ordinal) && row.Contains("presence.xml", StringComparison.Ordinal) && row.Contains("removed", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith("title", StringComparison.Ordinal) && row.Contains("dataplatformconfig.xml", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("leaderboards.xml", StringComparison.Ordinal));
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Old: XDKS.1 published version 52, published 2026-10-27 18:05:00 Z by Publishing Developer.",
                "New: XDKS.1 draft version 55, saved 2026-10-29 21:40:12 Z by Nod Developer.",
                "5 documents differ.",
                "1 document is the same.",
                string.Empty),
            stderr);
    }

    [Fact]
    public async Task Table_WithLinesWritesEachLineDiffAfterTheTable()
    {
        var (exitCode, stdout, _) = await RunAsync(
            new FakeService(),
            Request(lines: true, features: ["achievements2017"]),
            OutputOptions.Table(ConfigDiffCommandFactory.DiffTableView));

        Assert.Equal(0, exitCode);
        int table = stdout.IndexOf("SCOPE", StringComparison.Ordinal);
        int diff = stdout.IndexOf("--- achievements2017.xml (XDKS.1 published version 52)", StringComparison.Ordinal);
        Assert.True(table >= 0 && diff > table);
        Assert.Contains("+  <Achievement Id=\"7\" />", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_SaysWhenThereAreNoDifferencesAndWhenASideWasNeverPublished()
    {
        var diff = new XboxLiveConfigurationDiff
        {
            Scope = "title",
            Old = new XboxLiveDiffSide { View = XboxLiveSideViews.Published },
            New = new XboxLiveDiffSide { View = XboxLiveSideViews.Files, Files = ["a.xml"] },
            Features = [],
        };

        (string table, IReadOnlyList<string> notes) = ConfigDiffCommandFactory.FormatTable(diff, ConfigDiffCommandFactory.DiffTableView);

        Assert.Equal(TableFormatter.EmptyNotice, table);
        Assert.Equal(["Old: title: never published.", "New: your files (1 file).", "No differences."], notes);
    }

    [Fact]
    public async Task TitleScope_ReadsTheTitlesDraftAndLiveSets()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await RunAsync(
            service,
            Request(scope: XboxLiveScope.Title, sandbox: null, lines: true));

        Assert.Equal(0, exitCode);
        Assert.Equal(["get", "documents", "documents"], service.Routes);
        Assert.Equal(
            ["HIDDEN published 58 source-productdeveloper.xml", "ALL working 60 productdeveloper.xml,productsystem.xml"],
            service.Bodies("documents").Select(Describe));
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("old").TryGetProperty("sandbox", out _));

        // dataplatformconfig.xml goes live with each sandbox's own publish, so a publish to HIDDEN doesn't change it.
        Assert.Equal(["productdeveloper.xml", "productsystem.xml"], DocumentNames(json.RootElement).Order());
        Assert.Equal("--- productdeveloper.xml (title published version 58)", Lines(Document(json.RootElement, "productdeveloper.xml", out _))[0]);
    }

    [Fact]
    public async Task AVersion_IsReadFromTheDraftFirst()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await RunAsync(service, Request(sandbox: "CERT", oldVersion: 41));

        Assert.Equal(0, exitCode);
        Assert.Equal(
            $$"""{"sellerId":12345,"documentType":"sandbox","scid":"{{ScidText}}","sandbox":"CERT","view":"working","version":41,"documents":[]}""",
            Assert.Single(service.Requests, request => request.Route == "documents").Body);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("draft", json.RootElement.GetProperty("old").GetProperty("view").GetString());
        Assert.Equal(41UL, json.RootElement.GetProperty("old").GetProperty("version").GetUInt64());
    }

    [Fact]
    public async Task AVersionInNeitherView_IsNotFoundAndSaysToNameItsSandbox()
    {
        var service = new FakeService();

        var (exitCode, stdout, stderr) = await RunAsync(service, Request(oldVersion: 38));

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.EndsWith(
            $"Version 38 wasn't found in XDKS.1's draft or published versions. Run '{CliIdentity.CommandName} xbl config status --scid {ScidText} --sandbox-id XDKS.1' " +
            "to see its version numbers, and name another sandbox's with that sandbox's --sandbox-id." + Environment.NewLine,
            stderr);
        Assert.Equal(["XDKS.1 working 38 ", "XDKS.1 published 38 "], service.Bodies("documents").Select(Describe));
    }

    [Fact]
    public async Task TitleScope_AVersionIsReadFromTheTitlesDraftSetThenItsPublishedSet()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await RunAsync(service, Request(scope: XboxLiveScope.Title, sandbox: null, oldVersion: 57));

        Assert.Equal(0, exitCode);
        Assert.Equal(["ALL working 57 ", "HIDDEN published 57 "], service.Bodies("documents").Select(Describe));
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement root = json.RootElement;
        Assert.Equal("published", root.GetProperty("old").GetProperty("view").GetString());
        Assert.False(root.GetProperty("old").TryGetProperty("sandbox", out _));
        Assert.Equal(["productdeveloper.xml", "productsystem.xml"], DocumentNames(root));
        Assert.Equal("same", Document(root, "productdeveloper.xml", out _).GetProperty("state").GetString());
        Assert.Equal("added", Document(root, "productsystem.xml", out _).GetProperty("state").GetString());
    }

    [Fact]
    public async Task TheSameVersionTwice_IsDownloadedOnce()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await RunAsync(service, Request(sandbox: "CERT", oldVersion: 38, newVersion: 38));

        Assert.Equal(0, exitCode);
        Assert.Equal(["CERT working 38 ", "CERT published 38 "], service.Bodies("documents").Select(Describe));
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("hasDifferences").GetBoolean());
    }
    [Fact]
    public async Task TheLiveVersionsNumber_ReadsTheStatusOnce()
    {
        var service = new FakeService();

        var (exitCode, _, _) = await RunAsync(service, Request(oldVersion: 52));

        Assert.Equal(0, exitCode);
        Assert.Single(service.Requests, request => request.Route == "get" && request.Body.Contains("\"scope\":\"sandbox\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADownloadOfAnotherVersion_FailsTheComparison()
    {
        var service = new FakeService { DocumentsVersionOverride = 99 };

        var (exitCode, stdout, stderr) = await RunAsync(service, Request(lines: true));

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("version 99, not", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADownloadWhoseBytesDontMatchTheirHash_FailsTheComparison()
    {
        var service = new FakeService { CorruptDocuments = true };

        var (exitCode, stdout, stderr) = await RunAsync(service, Request(lines: true));

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("doesn't match XDKS.1 published version 52: its hash differs", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyLineEndingsDiffering_SaysSoInsteadOfAnEmptyDiff()
    {
        var (exitCode, stdout, _) = await RunAsync(
            new FakeService(),
            Request(files: [new XboxDocumentUpload("leaderboards.xml", [0xEF, 0xBB, 0xBF, .. Utf8("<Leaderboards />\r\n")])], lines: true));

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        JsonElement document = Document(json.RootElement, "leaderboards.xml", out _);
        Assert.Equal("changed", document.GetProperty("state").GetString());
        Assert.Equal(XboxLiveConfigurationComparison.OnlyLineEndingsDiffer, Lines(document)[^1]);
    }

    [Fact]
    public async Task InternalFiles_AreNotComparedAndStderrSaysSo()
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            new FakeService(),
            Request(files: [new XboxDocumentUpload("leaderboards.xml", Leaderboards), new XboxDocumentUpload("xmsinternal.xml", Sets)]));

        Assert.Equal(0, exitCode);
        Assert.Contains("Not compared: xmsinternal.xml.", stderr, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(["leaderboards.xml"], DocumentNames(json.RootElement));
        Assert.Equal(["xmsinternal.xml"], NotCompared(json.RootElement));
    }

    [Fact]
    public async Task SandboxFiles_LeaveOutTitleWideDocuments()
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            new FakeService(),
            Request(files: [new XboxDocumentUpload("leaderboards.xml", Leaderboards), new XboxDocumentUpload("productdeveloper.xml", ProductDeveloper)]));

        Assert.Equal(0, exitCode);
        Assert.Contains("Not compared: productdeveloper.xml. They're title-wide documents, so compare them with --scope title.", stderr, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(["leaderboards.xml"], DocumentNames(json.RootElement));
        Assert.Equal(["productdeveloper.xml"], NotCompared(json.RootElement));
    }

    [Fact]
    public async Task TitleScopeFiles_LeaveOutTheDocumentsEachSandboxPublishes()
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            new FakeService(),
            Request(
                scope: XboxLiveScope.Title,
                sandbox: null,
                files:
                [
                    new XboxDocumentUpload("productdeveloper.xml", ProductDeveloper),
                    new XboxDocumentUpload("dataplatformconfig.xml", DataPlatformDraft),
                    new XboxDocumentUpload("achievements2017.xml", AchievementsDraft),
                ]));

        Assert.Equal(0, exitCode);
        Assert.Contains("Not compared: achievements2017.xml, dataplatformconfig.xml. They go live with each sandbox's own publish, so compare them with --sandbox-id.", stderr, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(["productdeveloper.xml"], DocumentNames(json.RootElement));
        Assert.Equal(["achievements2017.xml", "dataplatformconfig.xml"], NotCompared(json.RootElement));
        Assert.False(json.RootElement.GetProperty("hasDifferences").GetBoolean());
    }
    [Fact]
    public async Task Table_LinesCantWriteEscapeSequences()
    {
        byte[] hostile = Utf8("<Leaderboards>\u001b]0;owned\u0007</Leaderboards>\n");

        var (exitCode, stdout, _) = await RunAsync(
            new FakeService(),
            Request(files: [new XboxDocumentUpload("leaderboards.xml", hostile)], lines: true),
            OutputOptions.Table(ConfigDiffCommandFactory.DiffTableView));

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain('\u001b', stdout);
        Assert.DoesNotContain('\u0007', stdout);
        Assert.Contains("+<Leaderboards>]0;owned</Leaderboards>", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xbl config diff --scid " + ScidText + " --sandbox-id XDKS.1")]
    [InlineData("xbl config diff 52 --scid " + ScidText + " --sandbox-id XDKS.1 --lines")]
    [InlineData("xbl config diff 52 55 --scid " + ScidText + " --sandbox-id CERT --feature achievements2017 --feature leaderboards")]
    [InlineData("xbl config diff --scid " + ScidText + " --file a.xml --file b.xml")]
    [InlineData("xbl config diff 52 --scid " + ScidText + " --file a.xml")]
    [InlineData("xbl config diff 7 --scope account")]
    [InlineData("xbl config diff --title-id 1234ABCD --sandbox-id XDKS.1")]
    public void Options_Parse(string command) =>
        Assert.Empty(Program.BuildRootCommand().Parse(command, Program.NoResponseFileConfig).Errors);

    [Theory]
    [InlineData("xbl config diff 52 55 --scid " + ScidText + " --file a.xml", "--file can't be used with two versions")]
    [InlineData("xbl config diff live --scid " + ScidText, "'live' isn't a version number. Run 'xbox xbl config status'")]
    [InlineData("xbl config diff 0 --scid " + ScidText, "'0' isn't a version number.")]
    [InlineData("xbl config diff --scid " + ScidText + " --file \" \"", "Each --file requires one non-empty path or glob pattern.")]
    public void Options_RejectAnIncompleteOrContradictoryComparison(string command, string expected)
    {
        var errors = Program.BuildRootCommand().Parse(command, Program.NoResponseFileConfig).Errors;

        Assert.Contains(errors, error => error.Message.Contains(expected.Replace("'xbox ", $"'{CliIdentity.CommandName} ", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Action_ReadsAllAndHiddenAsTheTitleScope()
    {
        var service = new FakeService();

        var (exitCode, stdout, _) = await InvokeAsync(
            ["xbl", "config", "diff", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "HIDDEN", "--output", "json-compact"],
            service.CreateHandler());

        Assert.Equal(0, exitCode);
        Assert.Equal("title", JsonDocument.Parse(stdout).RootElement.GetProperty("scope").GetString());
        Assert.Equal("title", service.Bodies("get")[0].GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Action_RefersHistoryToTheTitleScope()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "diff", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "HISTORY"],
            new FakeService.Handler(_ => throw new InvalidOperationException("No request is expected.")));

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains(
            $"HISTORY holds the title's configuration, not one sandbox's. Run '{CliIdentity.CommandName} xbl config diff --scope title --scid {ScidText}'.",
            stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Action_ReadsTheFilesAsSaveDoes()
    {
        string directory = Directory.CreateTempSubdirectory("xbox-config-diff-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "achievements2017.xml"), AchievementsLive);
            await File.WriteAllBytesAsync(Path.Combine(directory, "notes.txt"), Sets);
            var service = new FakeService();

            var (exitCode, stdout, stderr) = await InvokeAsync(
                ["xbl", "config", "diff", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1",
                 "--file", Path.Combine(directory, "*.xml"), "--output", "json-compact"],
                service.CreateHandler());

            Assert.Equal(0, exitCode);
            Assert.Empty(stderr);
            using JsonDocument json = JsonDocument.Parse(stdout);
            Assert.Equal([Path.Combine(directory, "achievements2017.xml")], json.RootElement.GetProperty("new").GetProperty("files").EnumerateArray().Select(file => file.GetString()));
            Assert.Equal("changed", Document(json.RootElement, "achievements2017.xml", out _).GetProperty("state").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Action_LeavesAMissingFileToTheErrorBoundary()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync(
            ["xbl", "config", "diff", "--seller-id", "1234567", "--scid", ScidText, "--sandbox-id", "XDKS.1",
             "--file", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "*.xml")],
            new FakeService.Handler(_ => throw new InvalidOperationException("No request is expected.")));

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Document directory does not exist", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_RejectsANestedOrDuplicateName()
    {
        Assert.Throws<InvalidDataException>(() => XboxLiveDocumentArchives.Read(Zip([new("folder/a.xml", Sets)])));
        Assert.Throws<InvalidDataException>(() => XboxLiveDocumentArchives.Read(Zip([new("a.xml", Sets), new("a.xml", Sets)])));
        Assert.Throws<InvalidDataException>(() => XboxLiveDocumentArchives.Read(Zip([new("A.xml", Sets), new("a.xml", Sets)])));
        Assert.Throws<InvalidDataException>(() => XboxLiveDocumentArchives.Read(
            Zip([.. Enumerable.Range(0, XboxLiveDocumentArchives.MaxEntries + 1).Select(index => new KeyValuePair<string, byte[]>($"{index}.xml", Sets))])));
    }

    private static ConfigDiffCommandFactory.ConfigDiffRequest Request(
        XboxLiveScope scope = XboxLiveScope.Sandbox,
        string? sandbox = "XDKS.1",
        ulong? oldVersion = null,
        ulong? newVersion = null,
        IReadOnlyList<XboxDocumentUpload>? files = null,
        bool lines = false,
        IReadOnlyList<string>? features = null) =>
        new(
            scope,
            scope == XboxLiveScope.Account ? null : Scid,
            sandbox,
            oldVersion,
            newVersion,
            [.. (files ?? []).Select(file => Path.Combine("nod-arena", file.Name))],
            files ?? [],
            new XboxLiveComparisonOptions { Lines = lines, Features = features ?? [] });

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        FakeService service,
        ConfigDiffCommandFactory.ConfigDiffRequest request,
        OutputOptions? output = null)
    {
        using var client = CreateClient(service);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = await ConfigDiffCommandFactory.RunAsync(
            client, 12345, request, output ?? OutputOptions.Json, stdout, stderr, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(string[] args, HttpMessageHandler handler)
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

    private static JsonElement Document(JsonElement diff, string name, out string feature)
    {
        foreach (JsonElement group in diff.GetProperty("features").EnumerateArray())
        {
            foreach (JsonElement document in group.GetProperty("documents").EnumerateArray())
            {
                if (document.GetProperty("name").GetString() == name)
                {
                    feature = group.GetProperty("name").GetString()!;
                    return document;
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"No document {name}.");
    }

    private static string[] DocumentNames(JsonElement diff) =>
        [.. diff.GetProperty("features").EnumerateArray()
            .SelectMany(feature => feature.GetProperty("documents").EnumerateArray())
            .Select(document => document.GetProperty("name").GetString()!)];

    private static string[] Lines(JsonElement document) =>
        [.. document.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!)];

    private static string[] NotCompared(JsonElement diff) =>
        [.. diff.GetProperty("notCompared").EnumerateArray().Select(name => name.GetString()!)];

    private static string Describe(JsonElement body) =>
        $"{(body.TryGetProperty("sandbox", out JsonElement sandbox) && sandbox.ValueKind == JsonValueKind.String ? sandbox.GetString() : "account")} " +
        $"{body.GetProperty("view").GetString()} {(body.GetProperty("version").ValueKind == JsonValueKind.Number ? body.GetProperty("version").GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "latest")} " +
        string.Join(',', body.GetProperty("documents").EnumerateArray().Select(name => name.GetString()));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Hash(byte[] content) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(content));

    private static byte[] Zip(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                using Stream entry = archive.CreateEntry(name).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static XboxPartnerClient CreateClient(FakeService service) => CreateClient(service.CreateHandler());

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
        public string Route => Path[(Path.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// XBOX services with recorded configuration sets: each sandbox's draft (<c>working</c>) and
    /// published versions, as the status, history, documents, and version routes
    /// serve them.
    /// </summary>
    private sealed class FakeService
    {
        private readonly Dictionary<(string Set, string View, ulong Version), Dictionary<string, byte[]>> _sets = new()
        {
            [("XDKS.1", "working", 55)] = new()
            {
                ["achievements2017.xml"] = AchievementsDraft,
                ["leaderboards.xml"] = Leaderboards,
                ["sets.xml"] = Sets,
                ["titlename-localization.xml"] = TitleNamesDraft,
                ["xmsinternal.xml"] = Sets,
            },
            [("ALL", "working", 60)] = new()
            {
                ["dataplatformconfig.xml"] = DataPlatformDraft,
                ["productdeveloper.xml"] = ProductDeveloper,
                ["productsystem.xml"] = ProductSystem,
            },
            [("XDKS.1", "published", 52)] = new()
            {
                ["source-achievements2017.xml"] = AchievementsLive,
                ["source-leaderboards.xml"] = Leaderboards,
                ["source-presence.xml"] = Presence,
                ["source-dataplatformconfig.xml"] = DataPlatformLive,
                ["source-productdeveloper.xml"] = ProductDeveloper,
                ["achievements2017.xml"] = Utf8("transformed"),
            },
            [("CERT", "published", 40)] = new()
            {
                ["source-achievements2017.xml"] = AchievementsLive,
                ["source-leaderboards.xml"] = Leaderboards,
                ["source-titlename-localization.xml"] = TitleNamesLive,
                ["source-dataplatformconfig.xml"] = DataPlatformLive,
                ["source-productdeveloper.xml"] = ProductDeveloper,
                ["source-productsystem.xml"] = ProductSystem,
                ["source-xmsinternal.xml"] = Sets,
                ["mpsdtitleinfo.xml"] = TitleNamesLive,
            },
            [("CERT", "published", 38)] = new()
            {
                ["source-achievements2017.xml"] = AchievementsLive,
                ["source-leaderboards.xml"] = Leaderboards,
                ["source-titlename-localization.xml"] = TitleNamesLive,
                ["source-dataplatformconfig.xml"] = DataPlatformLive,
                ["source-productdeveloper.xml"] = ProductDeveloper,
                ["source-productsystem.xml"] = ProductSystem,
                ["source-xmsinternal.xml"] = Sets,
                ["mpsdtitleinfo.xml"] = TitleNamesLive,
            },
            [("CERT", "working", 43)] = new()
            {
                ["achievements2017.xml"] = AchievementsDraft,
                ["leaderboards.xml"] = Leaderboards,
                ["titlename-localization.xml"] = TitleNamesDraft,
            },
            [("CERT", "working", 41)] = new()
            {
                ["achievements2017.xml"] = AchievementsDraft,
                ["leaderboards.xml"] = Leaderboards,
                ["titlename-localization.xml"] = TitleNamesDraft,
            },
            [("HIDDEN", "published", 57)] = new()
            {
                ["source-productdeveloper.xml"] = ProductDeveloper,
                ["source-dataplatformconfig.xml"] = DataPlatformLive,
            },
            [("account", "working", 7)] = new(),
            [("HIDDEN", "published", 58)] = new()
            {
                ["source-productdeveloper.xml"] = ProductDeveloperLive,
                ["source-dataplatformconfig.xml"] = DataPlatformLive,
            },
        };

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

        public IReadOnlyList<string> Routes => [.. Requests.Select(request => request.Route)];

        /// <summary>A version the documents route claims instead of the one asked for.</summary>
        public ulong? DocumentsVersionOverride { get; init; }

        /// <summary>Whether the documents route returns bytes other than the ones the status hashed.</summary>
        public bool CorruptDocuments { get; init; }

        public IReadOnlyList<JsonElement> Bodies(string route) =>
            [.. Requests.Where(request => request.Route == route).Select(request => JsonDocument.Parse(request.Body).RootElement.Clone())];

        public HttpMessageHandler CreateHandler() => new Handler(request =>
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return Respond(request);
        });

        private HttpResponseMessage Respond(RecordedRequest request)
        {
            using JsonDocument body = JsonDocument.Parse(request.Body);
            JsonElement root = body.RootElement;
            return request.Route switch
            {
                "get" => Json(root.GetProperty("scope").GetString() == "title" ? TitleStatus() : IsCert(root) ? CertStatus() : SandboxStatus()),
                "history" => Json(IsCert(root)
                    ? """{ "entries": [ { "action": "publish", "version": 40, "sandbox": "CERT", "time": "2026-10-20T10:00:00+00:00", "author": { "id": "1003", "name": "Publishing Developer" }, "message": null, "documents": [] } ], "continuationToken": null }"""
                    : History),
                "documents" => Documents(root),
                "status" => Json("""{ "status": "Succeeded", "jobId": "job-1", "statusMessage": null }"""),
                _ => throw new InvalidOperationException($"Unexpected route {request.Path}."),
            };
        }

        /// <summary>
        /// The documents route: reads the named view, and is a 404 when that view of the sandbox (or
        /// the account) doesn't hold the version.
        /// </summary>
        private HttpResponseMessage Documents(JsonElement body)
        {
            string set = body.TryGetProperty("sandbox", out JsonElement sandbox) && sandbox.ValueKind == JsonValueKind.String ? sandbox.GetString()! : "account";
            string view = body.GetProperty("view").GetString()!;
            ulong version = body.GetProperty("version") is { ValueKind: JsonValueKind.Number } pinned
                ? pinned.GetUInt64()
                : _sets.Keys.Where(key => key.Set == set && key.View == view).Max(key => key.Version);
            string[] names = body.TryGetProperty("documents", out JsonElement filter)
                ? [.. filter.EnumerateArray().Select(name => name.GetString()!)]
                : [];
            if (_sets.TryGetValue((set, view, version), out Dictionary<string, byte[]>? documents))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Zip(documents
                        .Where(document => names.Length == 0 || names.Contains(document.Key))
                        .Select(document => CorruptDocuments ? new KeyValuePair<string, byte[]>(document.Key, Utf8("<Other />")) : document))),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"etag\"");
                response.Headers.Add("X-Version", (DocumentsVersionOverride ?? version).ToString(System.Globalization.CultureInfo.InvariantCulture));
                response.Headers.Add("X-Document-View", view);
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"status":404,"title":"Not Found","detail":"Config Set Not Found","code":"not_found"}""",
                    Encoding.UTF8,
                    "application/problem+json"),
            };
        }
        private static string SandboxStatus() => JsonSerializer.Serialize(new
        {
            scope = "sandbox",
            scid = ScidText,
            sandbox = "XDKS.1",
            workingVersion = 55,
            workingCommitTime = "2026-10-29T21:40:12Z",
            publishedVersion = 52,
            publishedCommitTime = "2026-10-27T18:05:00Z",
            documents = new object[]
            {
                StatusDocument("achievements2017.xml", "sandbox", "changed", AchievementsDraft, AchievementsLive),
                StatusDocument("leaderboards.xml", "sandbox", "same", Leaderboards, Leaderboards),
                StatusDocument("sets.xml", "sandbox", "draft-only", Sets, null),
                StatusDocument("titlename-localization.xml", "sandbox", "draft-only", TitleNamesDraft, null),
                StatusDocument("presence.xml", "sandbox", "published-only", null, Presence),
                StatusDocument("dataplatformconfig.xml", "title", "changed", DataPlatformDraft, DataPlatformLive),
            },
            lastPublish = new { status = "Success" },
            hasChanges = true,
        });

        private static bool IsCert(JsonElement body) =>
            body.TryGetProperty("sandbox", out JsonElement sandbox) && sandbox.GetString() == "CERT";

        private static string CertStatus() => JsonSerializer.Serialize(new
        {
            scope = "sandbox",
            scid = ScidText,
            sandbox = "CERT",
            workingVersion = 43,
            publishedVersion = 40,
            documents = new object[]
            {
                StatusDocument("achievements2017.xml", "sandbox", "changed", AchievementsDraft, AchievementsLive),
                StatusDocument("leaderboards.xml", "sandbox", "same", Leaderboards, Leaderboards),
                StatusDocument("titlename-localization.xml", "sandbox", "changed", TitleNamesDraft, TitleNamesLive),
                StatusDocument("dataplatformconfig.xml", "title", "changed", DataPlatformDraft, DataPlatformLive),
            },
            lastPublish = new { status = "Success" },
            hasChanges = true,
        });

        private static string TitleStatus() => JsonSerializer.Serialize(new
        {
            scope = "title",
            scid = ScidText,
            workingVersion = 60,
            publishedVersion = 58,
            documents = new object[]
            {
                StatusDocument("productdeveloper.xml", "title", "changed", ProductDeveloper, ProductDeveloperLive),
                StatusDocument("productsystem.xml", "title", "draft-only", ProductSystem, null),
                StatusDocument("dataplatformconfig.xml", "title", "changed", DataPlatformDraft, DataPlatformLive),
            },
            hasChanges = true,
        });

        private static object StatusDocument(string name, string scope, string state, byte[]? draft, byte[]? published) => new
        {
            name,
            scope,
            state,
            draftHash = draft is null ? null : Hash(draft),
            publishedHash = published is null ? null : Hash(published),
        };

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

        /// <summary>Records each request and answers it; requests can arrive concurrently.</summary>
        internal sealed class Handler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                return respond(new RecordedRequest(request.RequestUri!.AbsolutePath, body));
            }
        }
    }
}
