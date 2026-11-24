// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Coverage for the <c>playfab privacy</c> command group: command/option structure, and the exact
/// request URI path, serialized JSON body and output for both <c>export</c> and
/// <c>played-titles</c>. Uses an injected client with a fake transport and captured writers, so no
/// console redirection is needed.
/// </summary>
public class PrivacyCommandTests
{
    #region Command structure

    [Fact]
    public void Privacy_Subcommands_RequirePlayFabId()
    {
        var privacy = PrivacyCommandFactory.Create();

        foreach (var name in new[] { "export", "played-titles" })
        {
            var sub = privacy.Subcommands.First(c => c.Name == name);
            var option = sub.Options.FirstOrDefault(o => o.Name == "--playfab-id");
            Assert.NotNull(option);
            Assert.True(option!.Required);
        }
    }

    [Fact]
    public void Privacy_HasNoWaitOption()
    {
        // No job-status endpoint exists in the bundled metadata, so --wait must not be offered.
        var export = PrivacyCommandFactory.Create().Subcommands.First(c => c.Name == "export");
        Assert.DoesNotContain(export.Options, o => o.Name == "--wait");
    }

    [Fact]
    public void Export_HelpSafety_SaysWhoReceivesThePlayersData()
    {
        var export = PrivacyCommandFactory.Create().Subcommands.First(c => c.Name == "export");

        var safety = CommandMetadataRegistry.DescribeSafety(CommandMetadataRegistry.GetMetadata(export));

        Assert.NotNull(safety);
        Assert.Contains("Mutates", safety, StringComparison.Ordinal);
        Assert.Contains("doesn't return the data", safety, StringComparison.Ordinal);
        Assert.Contains("notification email address configured for the title", safety, StringComparison.Ordinal);
        Assert.Contains("every title of the publisher", safety, StringComparison.Ordinal);
        Assert.Contains("can't be recalled", safety, StringComparison.Ordinal);
    }

    #endregion

    #region export

    [Fact]
    public async Task Export_SendsPlayFabIdToExportEndpoint()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{"JobReceiptId":"job-123"}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await PrivacyCommandFactory.RunExportAsync(
            client, "ABC123", OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("/Admin/ExportMasterPlayerData", handler.LastRequestUri);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("ABC123", body.RootElement.GetProperty("PlayFabId").GetString());

        using var outDoc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("job-123", outDoc.RootElement.GetProperty("data").GetProperty("JobReceiptId").GetString());
    }

    #endregion

    #region played-titles

    [Fact]
    public async Task PlayedTitles_SendsPlayFabIdToPlayedTitleListEndpoint()
    {
        var handler = new MockHttpMessageHandler(Ok(
            """{"code":200,"status":"OK","data":{"TitleIds":["AAA","BBB"]}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await PrivacyCommandFactory.RunPlayedTitlesAsync(
            client, "XYZ", OutputOptions.JsonCompact, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("/Admin/GetPlayedTitleList", handler.LastRequestUri);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("XYZ", body.RootElement.GetProperty("PlayFabId").GetString());

        // json-compact writes single-line JSON.
        var output = stdout.ToString().TrimEnd();
        Assert.DoesNotContain('\n', output);
        using var outDoc = JsonDocument.Parse(output);
        var titles = outDoc.RootElement.GetProperty("data").GetProperty("TitleIds")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "AAA", "BBB" }, titles);
    }

    #endregion

    #region Test helpers

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    #endregion
}
