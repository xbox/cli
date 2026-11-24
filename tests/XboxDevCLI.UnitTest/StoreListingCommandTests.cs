// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using XboxDevCLI.Commands;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public sealed class StoreListingCommandTests
{
    private const string Listing =
        """{"storeId":"9N0000000000","language":"en-us","title":"Nod Arena","description":"Race your friends around the arena.","features":["Up to eight players online"]}""";

    [Fact]
    public async Task Get_WritesTheListingOfTheSellersGame()
    {
        var (handler, result) = await InvokeAsync(
            RoutingHttpHandler.Json(Listing),
            "store-listing", "get", "--store-id", "9n0000000000", "--language", " EN-US ", "--output", "json-compact");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Listing, result.Stdout.Trim());
        Assert.Empty(TestEnvironment.WithoutEndpointWarnings(result.Stderr));
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/xboxpartner/v1/games/9N0000000000/store-listings/en-us", request.Path);
        Assert.Equal("1234567", request.Header("X-DeveloperAccountId"));
    }

    [Fact]
    public async Task Get_NamesTheTextThatIsNotSet()
    {
        var (_, result) = await InvokeAsync(
            RoutingHttpHandler.Json("""{"storeId":"9N0000000000","language":"en-us","title":"Nod Arena","description":"","features":[]}"""),
            "store-listing", "get", "--store-id", "9N0000000000", "--language", "en-us");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"title\": \"Nod Arena\"", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Not set in the en-us listing: description, features.", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_NoListingInTheLanguage_SaysSoAndFails()
    {
        var (_, result) = await InvokeAsync(
            RoutingHttpHandler.Json(
                """{"title":"Store listing not found.","status":404,"code":"store_listing_not_found"}""", HttpStatusCode.NotFound),
            "store-listing", "get", "--store-id", "9N0000000000", "--language", "fr-fr");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(
            "Game 9N0000000000 has no Store listing in fr-fr. Partner Center matches the language code exactly: "
                + $"en doesn't read an en-us listing, nor the reverse.{Environment.NewLine}HTTP 404.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_GameWithoutModularPublishing_SaysSoAndFails()
    {
        var (_, result) = await InvokeAsync(
            RoutingHttpHandler.Json(
                """{"title":"Game doesn't use modular publishing.","status":422,"code":"game_not_modular"}""",
                HttpStatusCode.UnprocessableEntity),
            "store-listing", "get", "--store-id", "9N0000000000", "--language", "en-us");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(
            "Game 9N0000000000 doesn't use Partner Center's modular publishing, which reading its Store listing needs. "
                + $"Read the listing in Partner Center instead.{Environment.NewLine}HTTP 422.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ReadFailure_PointsToTheLanguageCode()
    {
        var (_, result) = await InvokeAsync(
            RoutingHttpHandler.Json("""{"title":"Partner Center request failed.","status":502}""", HttpStatusCode.BadGateway),
            "store-listing", "get", "--store-id", "9N0000000000", "--language", "xx-xx");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains(
            "The Store listing couldn't be read. Partner Center fails this way for a language code it doesn't support. "
                + $"Otherwise, try again later.{Environment.NewLine}HTTP 502.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    // A page from a host that isn't the service, such as one a wrong --base-url names, is a failed request, not a crash.
    [Fact]
    public async Task Get_ABodyThatIsNotJson_IsReportedWithItsCorrelationId()
    {
        var (_, result) = await InvokeAsync(
            RoutingHttpHandler.Json("<html>Sign in</html>"),
            "store-listing", "get", "--store-id", "9N0000000000", "--language", "en-us");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("x-ms-correlation-id: response-correlation", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Request failed:", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en_US")]
    [InlineData("..")]
    public async Task Get_RefusesAValueThatIsNotALanguageCode(string language)
    {
        var (handler, result) = await InvokeAsync(
            RoutingHttpHandler.Json(Listing),
            "store-listing", "get", "--store-id", "9N0000000000", "--language", language);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"Invalid language code '{language}'.", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    private static async Task<(RoutingHttpHandler Handler, CapturedInvocation Result)> InvokeAsync(
        HttpResponseMessage response, params string[] arguments)
    {
        var handler = new RoutingHttpHandler(_ => response);
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var root = Program.BuildRootCommand();
        string[] args = [.. arguments, "--base-url", "https://gateway.example", "--seller-id", "1234567"];

        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Program.XboxPartnerClientFactory = configuration => new XboxPartnerClient(configuration, httpClient, auth);
            ContextResolver.ReadIdentityAsync = _ => SignedInIdentity.FromCacheAsync(auth, CancellationToken.None);
            var parseResult = await Program.ResolveContextAsync(root.Parse(args, Program.NoResponseFileConfig), args);
            // The default handler is off so that exceptions the command lets through reach the test.
            int exitCode = parseResult is null ? 1 : await parseResult.InvokeAsync(
                new System.CommandLine.InvocationConfiguration { EnableDefaultExceptionHandler = false });
            return (handler, new CapturedInvocation(exitCode, stdout.ToString(), stderr.ToString()));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Program.XboxPartnerClientFactory = null;
            TestEnvironment.Reset();
        }
    }

    private sealed record CapturedInvocation(int ExitCode, string Stdout, string Stderr);
}
