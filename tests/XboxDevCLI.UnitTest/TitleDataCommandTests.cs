// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using Moq;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Services;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class TitleDataCommandTests
{
    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpResponseMessage Error(HttpStatusCode statusCode, string message) =>
        new(statusCode)
        {
            Content = new StringContent($$"""{"code":400,"status":"BadRequest","error":"BadRequest","errorCode":1,"errorMessage":"{{message}}"}"""),
        };

    [Fact]
    public void BuildGetCall_MapsClientAndInternalEndpoints()
    {
        var (clientPath, clientRequest) = TitleDataCommandFactory.BuildGetCall(internalTier: false, new List<string> { "a", "b" });
        var (internalPath, internalRequest) = TitleDataCommandFactory.BuildGetCall(internalTier: true, null);

        Assert.Equal("/Admin/GetTitleData", clientPath);
        Assert.Equal(new List<string> { "a", "b" }, clientRequest.Keys);
        Assert.Equal("/Admin/GetTitleInternalData", internalPath);
        Assert.Null(internalRequest.Keys);
    }

    [Fact]
    public async Task RunGetAsync_SingleKey_PrintsBareValueByDefault()
    {
        using var client = PlayFabTestClient.Create(new RoutingHttpHandler(_ =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"motd":"hello players"}}}""")));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // --key with no explicit structured-output request prints just the value.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, "motd", null, explicitStructuredOutput: false, OutputOptions.Json, stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("hello players", stdout.ToString().Trim());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Theory]
    [InlineData("playfab title-data get DemoKey", false)]
    [InlineData("playfab title-data get --key DemoKey", false)]
    [InlineData("playfab title-data get DemoKey -o json", true)]
    [InlineData("playfab title-data get DemoKey --data-only", true)]
    public void Get_Parse_DistinguishesOmittedOutputFromExplicitStructuredRequest(string args, bool explicitStructuredOutput)
    {
        var parseResult = Program.BuildRootCommand().Parse(args, Program.NoResponseFileConfig);

        Assert.Empty(parseResult.Errors);
        Assert.Equal(explicitStructuredOutput, TitleDataCommandFactory.RequestsExplicitStructuredOutput(parseResult));
    }

    [Fact]
    public async Task RunGetAsync_SingleKey_ExplicitOutput_PrintsEnvelope()
    {
        using var client = PlayFabTestClient.Create(new RoutingHttpHandler(_ =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"motd":"hello players"}}}""")));
        var stdout = new StringWriter();

        // An explicit -o json request wins over the bare-value default: the full envelope is printed.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, explicitStructuredOutput: true, OutputOptions.Json, stdout, new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("\"code\"", stdout.ToString());
        Assert.Contains("\"Data\"", stdout.ToString());
    }

    [Fact]
    public async Task RunGetAsync_SingleKey_MissingKeyWithStructuredOutput_ReturnsOne()
    {
        using var client = PlayFabTestClient.Create(new RoutingHttpHandler(_ =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"other":"x"}}}""")));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "motd", null, null, explicitStructuredOutput: true,
            OutputOptions.Json, stdout, stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("not found", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_PositionalAndKeyOption_Rejected()
    {
        using var client = PlayFabTestClient.Create(new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "a", "b", null, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("positional key or --key", stderr.ToString());
    }

    [Fact]
    public async Task RunGetAsync_PositionalKeyAndKeys_Rejected()
    {
        using var client = PlayFabTestClient.Create(new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "a", null, "b,c", explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("single key or --keys", stderr.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunGetAsync_PositionalKeyAndBlankKeys_Rejected(string blankKeys)
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, "a", null, blankKeys, explicitStructuredOutput: false,
            OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("single key or --keys", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunGetAsync_BlankPositionalKey_Rejected(string blankKey)
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        // A present-but-blank positional key must be rejected, not silently treated as an all-keys read.
        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, blankKey, null, null, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("Key must not be blank", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ")]
    public async Task RunGetAsync_BlankKeys_Rejected(string blankKeys)
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, null, blankKeys, explicitStructuredOutput: false,
            OutputOptions.Json, new StringWriter(), stderr, cancellationToken: CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal("--keys must contain at least one key." + Environment.NewLine, stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunGetAsync_MultiKey_SendsKeys()
    {
        var handler = new RoutingHttpHandler(_ =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"a":"1","b":"2"}}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, null, "a, b", explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        using var body = JsonDocument.Parse(handler.Requests.Single().Text);
        var keys = body.RootElement.GetProperty("Keys").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new List<string?> { "a", "b" }, keys);
    }

    [Fact]
    public async Task RunGetAsync_AllKeys_OmitsKeys()
    {
        var handler = new RoutingHttpHandler(_ =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"a":"1"}}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, null, null, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        using var body = JsonDocument.Parse(handler.Requests.Single().Text);
        Assert.False(body.RootElement.TryGetProperty("Keys", out _));
    }

    [Fact]
    public async Task RunGetAsync_Internal_UsesInternalEndpoint()
    {
        var handler = new RoutingHttpHandler(_ =>
            Ok("""{"code":200,"status":"OK","data":{"Data":{"secret":"s"}}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, true, "secret", null, null, explicitStructuredOutput: false, OutputOptions.Json, new StringWriter(), new StringWriter(), cancellationToken: CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetTitleInternalData", handler.Requests.Single().Path);
    }

    [Fact]
    public async Task RunGetAsync_OutputRendering_HonorsJsonCompactAndDataOnly()
    {
        const string responseJson = """{"code":200,"status":"OK","data":{"Data":{"a":"1"}}}""";
        using var prettyClient = PlayFabTestClient.Create(new RoutingHttpHandler(_ => Ok(responseJson)));
        var pretty = new StringWriter();
        await TitleDataCommandFactory.RunGetAsync(prettyClient, false, null, null, null, false, OutputOptions.Json, pretty, new StringWriter(), CancellationToken.None);

        using var compactClient = PlayFabTestClient.Create(new RoutingHttpHandler(_ => Ok(responseJson)));
        var compact = new StringWriter();
        await TitleDataCommandFactory.RunGetAsync(compactClient, false, null, null, null, false, OutputOptions.JsonCompact, compact, new StringWriter(), CancellationToken.None);

        using var dataOnlyClient = PlayFabTestClient.Create(new RoutingHttpHandler(_ => Ok(responseJson)));
        var dataOnly = new StringWriter();
        await TitleDataCommandFactory.RunGetAsync(dataOnlyClient, false, null, null, null, true, new OutputOptions("json-compact", true), dataOnly, new StringWriter(), CancellationToken.None);

        Assert.Contains("\n", pretty.ToString().TrimEnd());
        Assert.DoesNotContain("\n", compact.ToString().TrimEnd());
        Assert.Contains("\"code\"", compact.ToString());
        Assert.DoesNotContain("\"code\"", dataOnly.ToString());
        Assert.Contains("\"Data\"", dataOnly.ToString());
    }

    [Fact]
    public async Task RunGetAsync_ApiError_ReturnsOneAndWritesError()
    {
        var handler = new RoutingHttpHandler(_ => Error(HttpStatusCode.BadRequest, "bad keys"));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunGetAsync(
            client, false, null, null, null, false, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("bad keys", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_KeyValue_SetsSingleKey()
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        // The value is stored verbatim, including '=' characters.
        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, "motd", "some value=3", null, OutputOptions.JsonCompact, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/SetTitleData", handler.Requests.Single().Path);
        AssertSetBody(handler.Requests[0].Text, "motd", "some value=3");
        using var summary = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(new[] { "motd" }, summary.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task RunSetAsync_Internal_UsesInternalEndpoint()
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, true, "secret", "1", null, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/SetTitleInternalData", handler.Requests.Single().Path);
    }

    [Fact]
    public async Task RunSetAsync_DataObject_StringifiesValues()
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        const string dataJson = """{"s":"text","n":5,"b":true,"o":{"x":1},"a":[1,2]}""";

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, dataJson, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(5, handler.Requests.Count);
        AssertSetBody(handler.Requests[0].Text, "s", "text");
        AssertSetBody(handler.Requests[1].Text, "n", "5");
        AssertSetBody(handler.Requests[2].Text, "b", "true");
        AssertSetBody(handler.Requests[3].Text, "o", """{"x":1}""");
        AssertSetBody(handler.Requests[4].Text, "a", "[1,2]");
    }

    [Fact]
    public async Task RunSetAsync_DataObject_NullRejects()
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":null}""", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("title-data delete", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""["a","b"]""")]
    [InlineData("\"hi\"")]
    public async Task RunSetAsync_DataNonObject_ReturnsFriendlyError(string dataJson)
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, dataJson, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("expected a JSON object", stderr.ToString());
        Assert.DoesNotContain("Dictionary", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunSetAsync_KeyValueAndData_Rejected()
    {
        using var client = PlayFabTestClient.Create(new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, "a", "1", """{"b":"2"}""", OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("not both", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_NothingToSet_ReturnsOne()
    {
        using var client = PlayFabTestClient.Create(new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}""")));
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("Nothing to set", stderr.ToString());
    }

    [Fact]
    public async Task RunSetAsync_PartialFailure_StopsAndReportsSummary()
    {
        int calls = 0;
        var handler = new RoutingHttpHandler(_ =>
            ++calls == 1 ? Ok("""{"code":200,"status":"OK","data":{}}""") : Error(HttpStatusCode.BadRequest, "bad b"));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.Equal("b", summary.RootElement.GetProperty("failed").GetProperty("key").GetString());
        Assert.Contains("bad b", summary.RootElement.GetProperty("failed").GetProperty("error").GetRawText());
    }

    /// <summary>
    /// Ctrl-C on a later key keeps the summary: the earlier keys are applied, and the in-flight
    /// key is reported as unknown rather than failed, because PlayFab may have applied it.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_CanceledMidway_ReportsTheInFlightKeyAsUnknown()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var handler = new RoutingHttpHandler(_ =>
        {
            if (++calls == 1)
            {
                return Ok("""{"code":200,"status":"OK","data":{}}""");
            }

            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            cts.Token);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("failed", out _));
        var unknown = summary.RootElement.GetProperty("unknown");
        Assert.Equal("b", unknown.GetProperty("key").GetString());
        Assert.Contains("canceled", unknown.GetProperty("error").GetString());
        Assert.Contains("the request was canceled", unknown.GetProperty("outcomeUnknown").GetString());
    }

    /// <summary>
    /// A server error on a later key keeps the summary and reports that key as unknown, not
    /// failed: a 5xx such as a gateway timeout can come after PlayFab wrote it.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_ServerErrorOnALaterKey_ReportsItAsUnknown()
    {
        int calls = 0;
        var handler = new RoutingHttpHandler(_ =>
            ++calls == 1 ? Ok("""{"code":200,"status":"OK","data":{}}""") : Error(HttpStatusCode.GatewayTimeout, "timeout"));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("failed", out _));
        var unknown = summary.RootElement.GetProperty("unknown");
        Assert.Equal("b", unknown.GetProperty("key").GetString());
        Assert.Equal("timeout", unknown.GetProperty("error").GetProperty("errorMessage").GetString());
        Assert.StartsWith(
            "/Admin/SetTitleData may have reached PlayFab, but the request failed with HTTP 504",
            unknown.GetProperty("outcomeUnknown").GetString());
    }

    /// <summary>
    /// A 2xx means the key was written, even when its response is too large to read, so the
    /// key counts as applied and the remaining keys are still written.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_OversizedSuccessOnALaterKey_CountsItAsApplied()
    {
        int calls = 0;
        var handler = new RoutingHttpHandler(_ =>
            ++calls == 2
                ? Ok("""{"data":{"padding":"PADDING"}}""".Replace("PADDING", new string('x', 64)))
                : Ok("""{"data":{}}"""));
        using var client = PlayFabTestClient.Create(handler, maxResponseBytes: 32);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, stdout, stderr,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(stderr.ToString());
        using var summary = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(
            ["a", "b", "c"],
            summary.RootElement.GetProperty("applied").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>
    /// A sign-in failure on a later key keeps the summary, and that key is reported as not sent.
    /// </summary>
    [Fact]
    public async Task RunSetAsync_SignInFailsOnALaterKey_ReportsItAsNotSent()
    {
        var mockAuth = new Mock<AuthenticationService>(PlayFabTestClient.CreateConfiguration()) { CallBase = false };
        mockAuth
            .SetupSequence(a => a.AcquireTokenSilentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakeAuthenticationService.CreateToken())
            .ThrowsAsync(new InvalidOperationException("sign-in expired"));
        var handler = new RoutingHttpHandler(_ => Ok("""{"data":{}}"""));
        using var client = PlayFabTestClient.Create(handler, mockAuth.Object);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, null, """{"a":"1","b":"2","c":"3"}""", OutputOptions.JsonCompact, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Single(handler.Requests);
        using var summary = JsonDocument.Parse(stderr.ToString());
        Assert.Equal("a", summary.RootElement.GetProperty("applied").EnumerateArray().Single().GetString());
        Assert.False(summary.RootElement.TryGetProperty("unknown", out _));
        var notSent = summary.RootElement.GetProperty("notSent");
        Assert.Equal("b", notSent.GetProperty("key").GetString());
        Assert.Equal("sign-in expired", notSent.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunSetAsync_BlankKey_Rejected(string blankKey)
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, blankKey, "v", null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("--key must not be blank", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunSetAsync_KeyWithoutValue_Rejected()
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, "a", null, null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("--key and --value must be used together", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunSetAsync_ValueWithoutKey_Rejected()
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunSetAsync(
            client, false, null, "v", null, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("--key and --value must be used together", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TryParseDataObject_BlankKey_Rejected()
    {
        var ok = TitleDataCommandFactory.TryParseDataObject("""{"":"value"}""", out _, out var error);

        Assert.False(ok);
        Assert.Contains("keys must not be blank", error);
    }

    [Fact]
    public async Task RunDeleteAsync_LoopsSetTitleDataWithNullValues()
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await TitleDataCommandFactory.RunDeleteAsync(
            client, false, "a,b", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("/Admin/SetTitleData", r.Path));
        AssertDeleteBody(handler.Requests[0].Text, "a");
        AssertDeleteBody(handler.Requests[1].Text, "b");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ")]
    public async Task RunDeleteAsync_EmptyKeys_ReturnsOne(string blankKeys)
    {
        var handler = new RoutingHttpHandler(_ => Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await TitleDataCommandFactory.RunDeleteAsync(
            client, false, blankKeys, OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("at least one key", stderr.ToString());
        Assert.Empty(handler.Requests);
    }

    private static void AssertSetBody(string bodyJson, string expectedKey, string expectedValue)
    {
        using var body = JsonDocument.Parse(bodyJson);
        Assert.Equal(expectedKey, body.RootElement.GetProperty("Key").GetString());
        Assert.Equal(expectedValue, body.RootElement.GetProperty("Value").GetString());
    }

    private static void AssertDeleteBody(string bodyJson, string expectedKey)
    {
        using var body = JsonDocument.Parse(bodyJson);
        Assert.Equal(expectedKey, body.RootElement.GetProperty("Key").GetString());
        Assert.True(body.RootElement.TryGetProperty("Value", out var value));
        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }
}
