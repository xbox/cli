// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class XboxLiveBindingCommandTests
{
    private const string Store = "9N0000000000";

    private const string Check = $"xbox xbl binding get --store-id {Store} --seller-id 1234567";

    private static string Binding(string? primary, string? status, string? message = null, string[]? bound = null) =>
        JsonSerializer.Serialize(new
        {
            storeId = Store,
            titleId = "12345678",
            primaryTitleId = primary,
            boundStoreIds = bound ?? [],
            status,
            statusMessage = message,
        });

    [Theory]
    [InlineData("?sellerId=1234567", "--seller-id", "1234567")]
    [InlineData("")]
    public async Task Get_WritesTheBindingAsTheServiceSentIt(string query, params string[] seller)
    {
        var body = Binding("1234ABCD", "Success");
        var handler = Handler(Response(body));

        var result = await InvokeAsync(handler, ["get", "--store-id", "9n0000000000", "--output", "json-compact", .. seller]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(body, result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/xboxpartner/v1/games/{Store}/xboxlive/binding{query}", request.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData("1234ABCD", "Success", "1234ABCD +Success +-")]
    [InlineData(null, null, "- +- +9N0000000001, 9N0000000002")]
    public async Task Get_TableOutput_ShowsTheBindingAndTheGamesBoundToIt(string? primary, string? status, string row)
    {
        string[]? bound = primary is null ? ["9N0000000001", "9N0000000002"] : null;
        var result = await InvokeAsync(
            Handler(Response(Binding(primary, status, bound: bound))), "get", "--store-id", Store, "--output", "table");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches("^STORE ID +TITLE ID +PRIMARY TITLE ID +STATUS +BOUND GAMES *$", lines[0]);
        Assert.Matches($"^{Store} +12345678 +{row} *$", lines[^1]);
    }

    [Theory]
    [InlineData("Invalid Store ID '9N00'.", "get", "--store-id", "9N00")]
    [InlineData("Invalid XBOX title ID 'XYZ'.", "create", "--store-id", Store, "--primary-title-id", "XYZ")]
    [InlineData("--no-wait and --timeout cannot be combined.", "delete", "--store-id", Store, "--no-wait", "--timeout", "5m")]
    public async Task InvalidInvocations_AreRejectedWithoutARequest(string expected, params string[] arguments)
    {
        var handler = Handler();

        var result = await InvokeAsync(handler, arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(
        "Once it's bound, game 9N0000000000 uses 1234ABCD's XBOX services configuration, so you change and publish its configuration through 1234ABCD.",
        "Binding game 9N0000000000 to 1234ABCD publishes both games' title-wide configuration",
        "create",
        "--primary-title-id",
        "1234ABCD")]
    [InlineData(
        "Unbinding game 9N0000000000 returns it to its own XBOX services configuration as it was before it was bound, without the changes made while it was bound.",
        "Unbinding also publishes its title-wide configuration",
        "delete")]
    public async Task WithoutATerminal_IsRefusedUnlessConfirmed(
        string configuration, string publish, string verb, params string[] options)
    {
        var handler = Handler();

        var result = await InvokeAsync(handler, [verb, "--store-id", Store, .. options]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains(configuration, result.Stderr, StringComparison.Ordinal);
        Assert.Contains(publish, result.Stderr, StringComparison.Ordinal);
        Assert.Contains("every sandbox uses, RETAIL included", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Run again with --confirm (--yes, -y)", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("y", 0, 1)]
    [InlineData("n", 1, 0)]
    public async Task AtATerminal_AsksBeforeBinding(string answer, int exitCode, int requests)
    {
        var handler = Handler(Response(Binding("1234ABCD", "Success")));
        var dependencies = new XboxPartnerJobDependencies(
            (_, _) => Task.CompletedTask, () => DateTimeOffset.UtcNow, () => true);
        TextReader originalIn = Console.In;
        Console.SetIn(new StringReader(answer + Environment.NewLine));
        Run result;
        try
        {
            result = await InvokeAsync(
                handler, dependencies, CancellationToken.None,
                "create", "--store-id", Store, "--primary-title-id", "1234ABCD");
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(requests, handler.Requests.Count);
        Assert.Contains($"Bind game {Store} to 1234ABCD? [y/N]:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            answer == "y" ? $"Game {Store} is bound to 1234ABCD." : $"Game {Store} wasn't bound to 1234ABCD.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_SendsThePrimaryAndWaitsUntilTheBindSucceeds()
    {
        var bound = Binding("1234ABCD", "Success");
        var handler = Handler(
            Response(Binding("1234ABCD", "Running"), HttpStatusCode.Accepted),
            Response(Binding("1234ABCD", "Running")),
            Response(bound));

        var result = await InvokeAsync(
            handler, "create", "--store-id", Store, "--primary-title-id", "0x1234abcd", "--seller-id", "1234567",
            "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(bound, result.Stdout.Trim());
        Assert.Contains($"Game {Store} is bound to 1234ABCD.", result.Stderr, StringComparison.Ordinal);
        Assert.Equal([HttpMethod.Put, HttpMethod.Get, HttpMethod.Get], handler.Requests.Select(request => request.Method));
        Assert.Equal("""{"primaryTitleId":"1234ABCD"}""", handler.Requests[0].Body);
        Assert.Single(handler.Requests[0].Headers["x-ms-correlation-id"]);
    }

    [Theory]
    [InlineData("create", "1234ABCD", "Failed", 2, "The bind of game 9N0000000000 failed: The platform is unsupported.")]
    [InlineData("delete", "1234ABCD", "Failed", 2, "The unbind of game 9N0000000000 failed: The platform is unsupported.")]
    [InlineData("delete", null, null, 0, "Game 9N0000000000 is unbound.")]
    public async Task Wait_EndsWhenTheChangeIsOver(string verb, string? primary, string? status, int exitCode, string expected)
    {
        var final = Binding(primary, status, status == "Failed" ? "The platform is unsupported." : null);
        var handler = Handler(Response(Binding("1234ABCD", "Running"), HttpStatusCode.Accepted), Response(final));
        string[] primaryOption = verb == "create" ? ["--primary-title-id", "1234ABCD"] : [];

        var result = await InvokeAsync(
            handler, [verb, "--store-id", Store, .. primaryOption, "--confirm", "--output", "json-compact"]);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(final, result.Stdout.Trim());
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Create_WhenTheWaitEndsWithTheGameNotBoundToThePrimary_ExitsWithTwo()
    {
        // The game was unbound between the bind finishing and the next read.
        var unbound = Binding(null, null);
        var handler = Handler(Response(Binding("1234ABCD", "Running"), HttpStatusCode.Accepted), Response(unbound));

        var result = await InvokeAsync(
            handler, "create", "--store-id", Store, "--primary-title-id", "1234ABCD", "--seller-id", "1234567",
            "--confirm", "--output", "json-compact");

        Assert.Equal(XboxLiveBindingCommandFactory.FailedExitCode, result.ExitCode);
        Assert.Equal(unbound, result.Stdout.Trim());
        Assert.Contains(
            $"Game {Store} isn't bound to 1234ABCD, maybe because of another change to its binding. Before you run "
                + $"'xbox xbl binding create' again, check it with: {Check}",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Equal([HttpMethod.Put, HttpMethod.Get], handler.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task Delete_OfAGameThatIsNotBound_FinishesWithoutWaiting()
    {
        var handler = Handler(Response(Binding(null, null)));

        var result = await InvokeAsync(handler, "delete", "--store-id", Store, "--confirm");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HttpMethod.Delete, Assert.Single(handler.Requests).Method);
        Assert.Contains($"Game {Store} is unbound.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoWait_WritesTheBindingAndHowToCheckIt()
    {
        var running = Binding("1234ABCD", "Running");
        var handler = Handler(Response(running, HttpStatusCode.Accepted));

        var result = await InvokeAsync(
            handler, "create", "--store-id", Store, "--primary-title-id", "1234ABCD", "--seller-id", "1234567",
            "--no-wait", "--confirm", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Equal(running, result.Stdout.Trim());
        Assert.Contains($"The bind of game {Store} started. Check it with: {Check}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitEndsAtTheTimeout_ExitsWithFour()
    {
        var now = DateTimeOffset.UnixEpoch;
        var dependencies = new XboxPartnerJobDependencies(
            (delay, _) => { now += delay; return Task.CompletedTask; }, () => now, () => false);
        var handler = Handler(
            Response(Binding("1234ABCD", "Running"), HttpStatusCode.Accepted),
            Response(Binding("1234ABCD", "Running")),
            Response(Binding("1234ABCD", "Running")));

        // Reads at 10s and 20s; the wait ends at 25s without a third read.
        var result = await InvokeAsync(
            handler, dependencies, CancellationToken.None, "delete", "--store-id", Store, "--seller-id", "1234567",
            "--timeout", "25s", "--confirm");

        Assert.Equal(XboxLiveBindingCommandFactory.NotFinishedExitCode, result.ExitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(result.Stdout);
        Assert.Contains($"Game {Store} wasn't unbound within 25 seconds.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"Check it with: {Check}", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, """{"status":502}""")]
    [InlineData(HttpStatusCode.Accepted, "not json")]
    public async Task AFailureAfterSending_ExitsWithOutcomeUnknown(HttpStatusCode status, string body)
    {
        var handler = Handler(Response(body, status));

        var result = await InvokeAsync(
            handler, "create", "--store-id", Store, "--primary-title-id", "1234ABCD", "--seller-id", "1234567",
            "--confirm");

        Assert.Equal(XboxPartnerCommandSupport.OutcomeUnknownExitCode, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        Assert.Contains($"It is unknown whether the bind of game {Store} started. Don't resubmit it.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"Check with: {Check}", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("response-correlation", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, XboxPartnerCommandSupport.OutcomeUnknownExitCode, "It is unknown whether the bind of game 9N0000000000 started. Don't resubmit it.")]
    [InlineData(true, XboxLiveBindingCommandFactory.NotFinishedExitCode, "Stopped waiting. The bind may still be running.")]
    public async Task CtrlCAfterSending_ExitsWithHowToCheckTheGame(bool whileWaiting, int exitCode, string expected)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = whileWaiting
            ? Handler(Response(Binding("1234ABCD", "Running"), HttpStatusCode.Accepted))
            : new XboxPartnerHttpMessageHandler(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        var dependencies = new XboxPartnerJobDependencies(
            (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            () => DateTimeOffset.UtcNow,
            () => false);

        var result = await InvokeAsync(
            handler, dependencies, cancellation.Token,
            "create", "--store-id", Store, "--primary-title-id", "1234ABCD", "--seller-id", "1234567", "--confirm");

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Contains(Check, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusal_ExitsWithOneAndWritesTheServiceError()
    {
        var handler = Handler(Response(
            """{"status":409,"title":"Conflict","detail":"Game 9N0000000000 is already bound to 5678ABCD.","code":"xbl_binding_conflict"}""",
            HttpStatusCode.Conflict));

        var result = await InvokeAsync(handler, "create", "--store-id", Store, "--primary-title-id", "1234ABCD", "--confirm");

        Assert.Equal(1, result.ExitCode);
        Assert.Single(handler.Requests);
        Assert.Empty(result.Stdout);
        var error = JsonDocument.Parse(result.Stderr).RootElement;
        Assert.Equal(409, error.GetProperty("status").GetInt32());
        Assert.Equal("xbl_binding_conflict", error.GetProperty("code").GetString());
        Assert.Equal("Game 9N0000000000 is already bound to 5678ABCD.", error.GetProperty("detail").GetString());
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.Add("x-ms-correlation-id", "response-correlation");
        return response;
    }

    private static XboxPartnerHttpMessageHandler Handler(params HttpResponseMessage[] responses) => new(responses);

    private static Task<Run> InvokeAsync(HttpMessageHandler handler, params string[] arguments) =>
        InvokeAsync(
            handler,
            new XboxPartnerJobDependencies((_, _) => Task.CompletedTask, () => DateTimeOffset.UtcNow, () => false),
            CancellationToken.None,
            arguments);

    private static async Task<Run> InvokeAsync(
        HttpMessageHandler handler,
        XboxPartnerJobDependencies dependencies,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        RootCommand root = Program.BuildRootCommand();
        Command xbl = root.Subcommands.Single(command => command.Name == "xbl");
        xbl.Subcommands.Remove(xbl.Subcommands.Single(command => command.Name == "binding"));
        xbl.Subcommands.Add(XboxLiveBindingCommandFactory.Create(dependencies));
        CommandErrorBoundary.Install(root);

        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Program.XboxPartnerClientFactory = _ => new XboxPartnerClient(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["XboxPartner:BaseUrl"] = "https://gateway.example" })
                .Build(),
            httpClient,
            () => Task.FromResult<string?>("caller-token"));
        try
        {
            int exitCode = await root.Parse(["xbl", "binding", .. arguments], Program.NoResponseFileConfig)
                .InvokeAsync(cancellationToken: cancellationToken);
            return new Run(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.XboxPartnerClientFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);
}
