// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class CloudScriptCommandTests
{
    [Fact]
    public void FunctionRegister_RequiresAnExplicitTriggerType()
    {
        var register = CloudScriptFunctionCommandFactory.Create()
            .Subcommands.First(c => c.Name == "register");

        var typeOption = Assert.Single(register.Options, o => o.Name == "--type");
        Assert.True(typeOption.Required);
    }

    [Fact]
    public void FunctionRegisterAndUnregister_SayTheyAffectThePlayFabRegistration()
    {
        var function = CloudScriptFunctionCommandFactory.Create();

        foreach (var name in new[] { "register", "unregister" })
        {
            var description = function.Subcommands.First(c => c.Name == name).Description;
            Assert.NotNull(description);
            Assert.Contains("does not", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Azure Function itself", description, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("https://nodpublishers.azurewebsites.net/api/MatchWebhook?code=SECRETKEY0==")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/MatchWebhook?clientId=default&CODE=SECRETKEY0==")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/MatchWebhook?%63ode=SECRETKEY0==")]
    public void FunctionRegister_RefusesAKeyInTheUrlWithoutEchoingIt(string url)
    {
        var parsed = Program.BuildRootCommand().Parse(
            ["playfab", "cloudscript", "function", "register", "--type", "http", "--name", "MatchWebhook", "--url", url],
            Program.NoResponseFileConfig);

        var error = Assert.Single(parsed.Errors);
        Assert.Equal(CloudScriptFunctionCommandFactory.FunctionKeyInUrlMessage, error.Message);
        Assert.Contains("--key-file", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETKEY0", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://nodpublishers.azurewebsites.net/api/PublicStatus")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/PublicStatus?codec=h264")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/PublicStatus#code=fragment")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/PublicStatus#help?code=example")]
    public void FunctionRegister_AcceptsAUrlWithoutAKey(string url)
    {
        var parsed = Program.BuildRootCommand().Parse(
            ["playfab", "cloudscript", "function", "register", "--type", "http", "--name", "PublicStatus", "--url", url],
            Program.NoResponseFileConfig);

        Assert.Empty(parsed.Errors);
    }

    [Theory]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn", "abc/DEF==",
        "https://nodpublishers.azurewebsites.net/api/Fn?code=abc/DEF==")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn?clientId=default", "abc==",
        "https://nodpublishers.azurewebsites.net/api/Fn?clientId=default&code=abc==")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn?", "abc",
        "https://nodpublishers.azurewebsites.net/api/Fn?code=abc")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn#top", "abc",
        "https://nodpublishers.azurewebsites.net/api/Fn?code=abc#top")]
    [InlineData("https://nodpublishers.azurewebsites.net/api/Fn", "a+b&c",
        "https://nodpublishers.azurewebsites.net/api/Fn?code=a%2Bb%26c")]
    public void AppendFunctionKey_AddsTheKeyAsTheCodeParameter(string url, string key, string expected)
    {
        Assert.Equal(expected, CloudScriptFunctionCommandFactory.AppendFunctionKey(url, key));
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_ReadsAFileAndTrimsItsNewline()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc/DEF==\r\n");

            var key = await CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                path,
                () => throw new InvalidOperationException("stdin must not be checked"),
                () => throw new InvalidOperationException("stdin must not be read"),
                CancellationToken.None);

            Assert.Equal("abc/DEF==", key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_ReadsPipedStdinForDash()
    {
        var key = await CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
            "-", () => true, () => Utf8Stream("abc/DEF==\n"), CancellationToken.None);

        Assert.Equal("abc/DEF==", key);
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_RefusesStdinThatIsATerminal()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                "-",
                () => false,
                () => throw new InvalidOperationException("a terminal must not be read"),
                CancellationToken.None));

        Assert.Equal(CloudScriptFunctionCommandFactory.FunctionKeyFromTerminalMessage, exception.Message);
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_StopsWhenCanceledWhileThePipeStaysOpen()
    {
        var writer = new AnonymousPipeServerStream(PipeDirection.Out);
        var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            // Task.Run and the bound make a read that ignores cancellation fail with a
            // TimeoutException instead of hanging the test, even if it blocks before returning a task.
            var read = Task.Run(() => CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                "-", () => true, () => reader, cancellation.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            // Closing the writer first ends any read still waiting, so disposing the reader can't block.
            writer.Dispose();
            reader.Dispose();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n")]
    public async Task ReadFunctionKeyAsync_RefusesAnEmptyKey(string content)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                "-", () => true, () => Utf8Stream(content), CancellationToken.None));

        Assert.Contains("empty", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFunctionKeyAsync_NamesTheOptionWhenTheFileIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "function-key.txt");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CloudScriptFunctionCommandFactory.ReadFunctionKeyAsync(
                path, () => true, () => Stream.Null, CancellationToken.None));

        Assert.Contains("--key-file", exception.Message, StringComparison.Ordinal);
    }

    private static MemoryStream Utf8Stream(string content) => new(Encoding.UTF8.GetBytes(content));

    #region Run Method Tests (SetAction lambda logic)

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    // ----- classic revision get -----

    [Fact]
    public async Task RunGetRevisionAsync_Success_SendsVersionAndRevisionAndPrintsData()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Version":2,"Revision":3,"IsPublished":true}}""";
        var handler = new MockHttpMessageHandler(Ok(responseJson));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await CloudScriptClassicCommandFactory.RunGetRevisionAsync(
            client, 2, 3, OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetCloudScriptRevision", handler.LastRequestUri);
        Assert.NotNull(handler.LastRequestBody);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(2, body.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(3, body.RootElement.GetProperty("Revision").GetInt32());
        Assert.Contains("IsPublished", stdout.ToString());
    }

    [Fact]
    public async Task RunGetRevisionAsync_WithoutVersionOrRevision_SendsNeither()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await CloudScriptClassicCommandFactory.RunGetRevisionAsync(
            client, null, null, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Empty(body.RootElement.EnumerateObject());
    }

    // ----- classic version list -----

    [Fact]
    public async Task RunListVersionsAsync_Success_CallsVersionsEndpoint()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Versions":[{"Version":1}]}}""";
        var handler = new MockHttpMessageHandler(Ok(responseJson));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await CloudScriptClassicCommandFactory.RunListVersionsAsync(
            client, OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/GetCloudScriptVersions", handler.LastRequestUri);
        Assert.Contains("Versions", stdout.ToString());
    }

    // ----- classic revision create -----

    [Fact]
    public async Task RunUpdateAsync_LocalFileMissing_ReturnsOneWithoutCallingApi()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.js");

        var exit = await CloudScriptClassicCommandFactory.RunUpdateAsync(
            client, missing, publish: false, OutputOptions.Json, new StringWriter(), stderr, default);

        Assert.Equal(1, exit);
        Assert.Contains("File not found", stderr.ToString());
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task RunUpdateAsync_Success_UploadsFileContentsAndPublishFlag()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var filePath = Path.Combine(tempDir, "handlers.js");
            await File.WriteAllTextAsync(filePath, "handlers.foo = function() {};");
            var responseJson = """{"code":200,"status":"OK","data":{"Version":9,"Revision":1}}""";
            var handler = new MockHttpMessageHandler(Ok(responseJson));
            using var client = PlayFabTestClient.Create(handler);
            var stdout = new StringWriter();

            var exit = await CloudScriptClassicCommandFactory.RunUpdateAsync(
                client, filePath, publish: true, OutputOptions.Json, stdout, new StringWriter(), default);

            Assert.Equal(0, exit);
            Assert.Contains("/Admin/UpdateCloudScript", handler.LastRequestUri);
            Assert.NotNull(handler.LastRequestBody);
            using var body = JsonDocument.Parse(handler.LastRequestBody!);
            Assert.True(body.RootElement.GetProperty("Publish").GetBoolean());
            var file = body.RootElement.GetProperty("Files").EnumerateArray().Single();
            Assert.Equal("handlers.js", file.GetProperty("Filename").GetString());
            Assert.Equal("handlers.foo = function() {};", file.GetProperty("FileContents").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ----- classic revision publish -----

    [Fact]
    public async Task RunPublishAsync_Success_SendsVersionAndRevision()
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Version":4}}""";
        var handler = new MockHttpMessageHandler(Ok(responseJson));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await CloudScriptClassicCommandFactory.RunPublishAsync(
            client, 4, 7, OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/Admin/SetPublishedRevision", handler.LastRequestUri);
        Assert.NotNull(handler.LastRequestBody);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(4, body.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(7, body.RootElement.GetProperty("Revision").GetInt32());
    }

    // ----- functions list -----

    [Theory]
    [InlineData(null, "/CloudScript/ListFunctions")]
    [InlineData("http", "/CloudScript/ListHttpFunctions")]
    [InlineData("queued", "/CloudScript/ListQueuedFunctions")]
    [InlineData("eventhub", "/CloudScript/ListEventHubFunctions")]
    public async Task RunListAsync_SelectsEndpointByType(string? type, string expectedPath)
    {
        var responseJson = """{"code":200,"status":"OK","data":{"Functions":[]}}""";
        var handler = new MockHttpMessageHandler(Ok(responseJson));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await CloudScriptFunctionCommandFactory.RunListAsync(
            client, type, OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(expectedPath, handler.LastRequestUri);
    }

    // ----- function register --type http -----

    [Theory]
    [InlineData(null, "https://nodpublishers.azurewebsites.net/api/MatchWebhook")]
    [InlineData("abc/DEF==", "https://nodpublishers.azurewebsites.net/api/MatchWebhook?code=abc/DEF==")]
    public async Task RunRegisterHttpAsync_Success_SendsNameAndCompleteUrl(string? functionKey, string expectedUrl)
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await CloudScriptFunctionCommandFactory.RunRegisterHttpAsync(
            client, "MatchWebhook", "https://nodpublishers.azurewebsites.net/api/MatchWebhook", functionKey,
            OutputOptions.Json, new StringWriter(), new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/CloudScript/RegisterHttpFunction", handler.LastRequestUri);
        Assert.NotNull(handler.LastRequestBody);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("MatchWebhook", body.RootElement.GetProperty("FunctionName").GetString());
        Assert.Equal(expectedUrl, body.RootElement.GetProperty("FunctionUrl").GetString());
    }

    // ----- functions unregister -----

    [Fact]
    public async Task RunUnregisterAsync_Success_SendsName()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{}}"""));
        using var client = PlayFabTestClient.Create(handler);

        var exit = await CloudScriptFunctionCommandFactory.RunUnregisterAsync(
            client, "OldFunc", OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("/CloudScript/UnregisterFunction", handler.LastRequestUri);
        Assert.NotNull(handler.LastRequestBody);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("OldFunc", body.RootElement.GetProperty("FunctionName").GetString());
    }

    #endregion
}
