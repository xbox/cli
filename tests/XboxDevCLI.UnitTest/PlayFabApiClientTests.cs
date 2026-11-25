// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Safety;
using XboxDevCLI.Services;
using XboxDevCLI.Telemetry;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class PlayFabApiClientTests
{
    private static IConfiguration CreateTestConfiguration(
        string titleId = "ABC123",
        string? baseUrlTemplate = "https://{0}.playfabapi.com",
        string? hostHeader = null,
        long? maxResponseBytes = null,
        bool auditLogEnabled = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["PlayFab:TitleId"] = titleId,
            ["PlayFab:BaseUrlTemplate"] = baseUrlTemplate,
            ["AuditLog:Enabled"] = auditLogEnabled.ToString(),
            ["PlayFab:HostHeader"] = hostHeader,
        };

        if (maxResponseBytes.HasValue)
        {
            settings["PlayFab:MaxResponseBytes"] = maxResponseBytes.Value.ToString();
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    [Fact]
    public void Constructor_MissingBaseUrlTemplateUsesTheShippedDefault()
    {
        var config = CreateTestConfiguration(baseUrlTemplate: null);
        var auth = new FakeAuthenticationService(token: null);
        using var httpClient = new HttpClient();

        using var client = new PlayFabApiClient(config, auth, httpClient);

        Assert.Equal(
            new Uri(EndpointOverrideWarning.DefaultBaseUrlTemplate + "/"),
            httpClient.BaseAddress);
    }

    /// <summary>
    /// Callers catch the client's failures and report them themselves, so the client records the
    /// status and the reason for telemetry.
    /// </summary>
    [Theory]
    [InlineData("ok", 200, null)]
    [InlineData("rejected", 400, "service_rejected")]
    [InlineData("error", 503, "service_error")]
    [InlineData("transport", null, "transport")]
    [InlineData("timeout", null, "transport")]
    [InlineData("no-token", null, "auth")]
    public async Task CallApiAsync_RecordsTheStatusAndFailureReasonForTelemetry(
        string scenario, int? status, string? errorType)
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(scenario == "no-token" ? null : FakeAuthenticationService.CreateToken());
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage((HttpStatusCode)(status ?? 200))
            {
                Content = new StringContent(status is null or 200
                    ? "{\"data\":{}}"
                    : $"{{\"code\":{status},\"status\":\"Failed\",\"error\":\"Failed\",\"errorMessage\":\"failed\"}}"),
            })
        {
            OnSend = _ =>
            {
                if (scenario == "transport")
                {
                    throw new HttpRequestException("connection refused");
                }

                // A wait the caller didn't cancel, which ended because it timed out.
                if (scenario == "timeout")
                {
                    throw new OperationCanceledException();
                }
            },
        };
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://ABC123.playfabapi.com/"),
        };
        using var client = new PlayFabApiClient(config, auth, httpClient);
        var recorded = TelemetryRecorder.Start();

        try
        {
            try
            {
                await client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None);
            }
            catch (PlayFabApiException) when (errorType is not null)
            {
            }

            Assert.Equal(status, recorded.HttpStatusCode);
            Assert.Equal(errorType, recorded.ErrorType);
        }
        finally
        {
            TelemetryRecorder.Stop();
        }
    }

    [Fact]
    public async Task CallApiAsync_PassesTheTokenToAuthenticationAndTransport()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());
        using var cts = new CancellationTokenSource();
        CancellationToken transportToken = default;
        var handler = new FailingHttpMessageHandler(ct =>
        {
            transportToken = ct;
            cts.Cancel();
            throw new OperationCanceledException(ct);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var error = await Assert.ThrowsAsync<PlayFabApiException>(
            () => client.CallApiAsync("/Admin/GetTitleData", null, cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);

        Assert.Equal(1, auth.TokenRequests);
        Assert.Equal(cts.Token, auth.TokenRequestCancellationToken);
        Assert.True(transportToken.IsCancellationRequested);
    }

    /// <summary>
    /// Ctrl-C that arrives while signing in, with the token returned anyway, means the write was
    /// never sent, so it must not be reported as an unknown outcome.
    /// </summary>
    [Fact]
    public async Task CallApiAsync_WriteCanceledBeforeItIsSent_IsNotAnUnknownOutcome()
    {
        var config = CreateTestConfiguration();
        using var cts = new CancellationTokenSource();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken())
        {
            OnTokenRequest = _ => cts.Cancel(),
        };
        var sent = false;
        var handler = new FailingHttpMessageHandler(ct =>
        {
            sent = true;
            throw new OperationCanceledException(ct);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var exception = await Record.ExceptionAsync(() => client.CallApiAsync("/Admin/SetTitleData", "{}", cts.Token));

        var error = Assert.IsType<PlayFabApiException>(exception);
        Assert.Equal(PlayFabCallOutcome.NotSent, error.Outcome);
        Assert.Null(error.OutcomeWarning);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.False(sent);
    }

    /// <summary>
    /// Every failure is classified once, by how far the call got. Only a write that may have
    /// reached PlayFab without a conclusive answer (no response, or a 5xx) warns that it may have
    /// been applied; a read never warns.
    /// </summary>
    [Theory]
    [InlineData("/Admin/SetTitleData", "signin", PlayFabCallOutcome.NotSent, null)]
    [InlineData("/Admin/SetTitleData", "dns", PlayFabCallOutcome.NotSent, null)]
    [InlineData("/Admin/SetTitleData", "tls", PlayFabCallOutcome.NotSent, null)]
    [InlineData("/Admin/SetTitleData", "cancel", PlayFabCallOutcome.NoResponse, "the request was canceled")]
    [InlineData("/Admin/SetTitleData", "timeout", PlayFabCallOutcome.NoResponse, "the request timed out")]
    [InlineData("/Admin/SetTitleData", "reset", PlayFabCallOutcome.NoResponse, "the connection failed (connection reset)")]
    [InlineData("/Admin/SetTitleData", "500", PlayFabCallOutcome.Inconclusive, "the request failed with HTTP 500")]
    [InlineData("/Admin/SetTitleData", "400", PlayFabCallOutcome.Rejected, null)]
    [InlineData("/Admin/GetTitleData", "cancel", PlayFabCallOutcome.NoResponse, null)]
    [InlineData("/Admin/GetTitleData", "500", PlayFabCallOutcome.Inconclusive, null)]
    public async Task CallApiAsync_ClassifiesEveryFailureByHowFarItGot(
        string apiPath, string failure, PlayFabCallOutcome expectedOutcome, string? expectedReason)
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken())
        {
            TokenRequestException = failure == "signin" ? new InvalidOperationException("sign-in failed") : null,
        };

        using var cts = new CancellationTokenSource();
        var handler = new FailingHttpMessageHandler(_ => failure switch
        {
            "cancel" => Cancel(),
            "timeout" => throw new TaskCanceledException("timed out", new TimeoutException()),
            "reset" => throw new HttpRequestException("connection reset"),
            "dns" => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"),
            "tls" => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "handshake failed"),
            "500" => Respond(HttpStatusCode.InternalServerError, new StringContent("""{"errorMessage":"oops"}""")),
            "400" => Respond(HttpStatusCode.BadRequest, new StringContent("""{"errorMessage":"bad"}""")),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var exception = await Record.ExceptionAsync(() => client.CallApiAsync(apiPath, "{}", cts.Token));

        var error = Assert.IsType<PlayFabApiException>(exception);
        Assert.Equal(apiPath, error.ApiPath);
        Assert.Equal(expectedOutcome, error.Outcome);
        Assert.Equal(
            expectedOutcome is PlayFabCallOutcome.NotSent or PlayFabCallOutcome.NoResponse,
            error.HttpStatusCode is null);
        if (expectedReason is null)
        {
            Assert.Null(error.OutcomeWarning);
            Assert.DoesNotContain("outcome is unknown", error.Message);
            return;
        }

        Assert.Equal(
            $"{apiPath} may have reached PlayFab, but {expectedReason}, so its outcome is unknown. " +
            "Check whether it was applied before retrying.",
            error.OutcomeWarning);
        Assert.EndsWith(error.OutcomeWarning, error.Message);

        HttpResponseMessage Cancel()
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        static HttpResponseMessage Respond(HttpStatusCode status, HttpContent content) => new(status) { Content = content };
    }

    /// <summary>
    /// An error status whose body fails to arrive keeps that status: a 4xx write is still a
    /// definitive rejection, not an unknown outcome, and a 5xx write still warns.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task CallApiAsync_ErrorStatusWhoseBodyFails_KeepsTheStatus(HttpStatusCode status, bool expectWarning)
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());
        var handler = new FailingHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StreamContent(new FailingStream(new IOException("response lost"))),
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var error = await Assert.ThrowsAsync<PlayFabApiException>(
            () => client.CallApiAsync("/Admin/SetTitleData", "{}", CancellationToken.None));

        Assert.Equal((int)status, error.HttpStatusCode);
        Assert.Contains("the response body could not be read (response lost)", error.Message);
        Assert.Equal(expectWarning, error.OutcomeWarning is not null);
    }

    /// <summary>HttpClient's timeout ends at the headers, so the body read has its own bound.</summary>
    [Fact]
    public async Task CallApiAsync_StalledErrorBody_TimesOut()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());
        var handler = new FailingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(new StalledStream()),
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://ABC123.playfabapi.com/"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var error = await Assert.ThrowsAsync<PlayFabApiException>(
            () => client.CallApiAsync("/Admin/SetTitleData", "{}", CancellationToken.None));

        Assert.Equal(400, error.HttpStatusCode);
        Assert.Contains("reading the response body timed out", error.Message);
        Assert.Null(error.OutcomeWarning);
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class FailingHttpMessageHandler(Func<CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(cancellationToken));
    }

    private sealed class FailingStream(Exception failure) : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(failure);
    }

    [Fact]
    public async Task CallApiAsync_SendsBearerTokenAndJsonBody()
    {
        var config = CreateTestConfiguration();
        var token = FakeAuthenticationService.CreateToken();
        var auth = new FakeAuthenticationService(token);

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"Keys\":[\"Foo\"]}}"),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var result = await client.CallApiAsync("/Admin/GetTitleData", "{\"Keys\":[\"Foo\"]}", CancellationToken.None);

        Assert.Contains("Keys", result);
        Assert.Equal("Bearer", handler.LastRequest?.Headers.Authorization?.Scheme);
        Assert.Equal(token.AccessToken, handler.LastRequest?.Headers.Authorization?.Parameter);
        Assert.Equal(CliIdentity.UserAgent, handler.LastRequest?.Headers.UserAgent.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Contains("/Admin/GetTitleData", handler.LastRequest?.RequestUri?.ToString());
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("", "")]
    public async Task CallApiAsync_DefaultsOnlyAnOmittedBody(string? body, string expectedBody)
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        await client.CallApiAsync("/Catalog/SearchItems", body, CancellationToken.None);

        Assert.Equal(expectedBody, handler.LastRequestBody);
    }

    [Fact]
    public async Task CallApiAsync_ThrowsPlayFabApiExceptionOnErrorResponse()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var errorJson = "{\"code\":401,\"status\":\"Unauthorized\",\"error\":\"NotAuthorized\",\"errorCode\":1089,\"errorMessage\":\"Developer does not have admin permission for this title.\"}";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(errorJson),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var ex = await Assert.ThrowsAsync<PlayFabApiException>(
            () => client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None));

        Assert.Equal(401, ex.HttpStatusCode);
        Assert.Equal(1089, ex.ApiErrorCode);
        Assert.Equal("NotAuthorized", ex.ApiError);
        Assert.Contains("admin permission", ex.ApiErrorMessage);
        Assert.Contains("errorCode=1089", ex.Message);
    }

    [Fact]
    public async Task CallApiAsync_ThrowsWhenNoToken()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(token: null);

        using var httpClient = new HttpClient();
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var ex = await Assert.ThrowsAsync<PlayFabApiException>(
            () => client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None));

        Assert.Equal(PlayFabCallOutcome.NotSent, ex.Outcome);
        Assert.Contains(
            $"'{CliIdentity.CommandName} auth login'",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallApiAsync_TitlePathWithoutATitle_FailsBeforeSigningIn()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "",
            })
            .Build();

        var auth = new FakeAuthenticationService(token: null);
        using var client = new PlayFabApiClient(config, auth);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None));

        Assert.StartsWith("No playfab-title-id is set.", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, auth.TokenRequests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC123")]
    public async Task CallApiAsync_StudioPath_GoesToTheStudioEndpointWithoutTheTitleHeader(string titleId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = titleId,
                ["PlayFab:BaseUrlTemplate"] = "https://{0}.playfabapi.com",
                ["PlayFab:HostHeader"] = "{titleId}.playfabapi.com",
                ["PlayFab:StudioBaseUrl"] = "https://studio.nodpublishers.com",
            })
            .Build();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler);
        using var client = new PlayFabApiClient(config, auth, httpClient);

        await client.CallApiAsync("/Studio/ListServicePrincipals", null, CancellationToken.None);

        Assert.Equal(
            new Uri("https://studio.nodpublishers.com/Studio/ListServicePrincipals"),
            handler.LastRequest!.RequestUri);
        Assert.False(handler.LastRequest.Headers.Contains("X-PlayFab-Host"));
    }

    [Theory]
    [InlineData("/Studio/ListServicePrincipals", true)]
    [InlineData("studio/listserviceprincipals", true)]
    [InlineData("/%53tudio/ListServicePrincipals", true)]
    [InlineData("/Studio/../Admin/DeleteTitle", false)]
    [InlineData("/Admin/Studio/GetTitleData", false)]
    [InlineData("/StudioX/List", false)]
    [InlineData("/\u00A0Studio/ListServicePrincipals", false)]
    public void IsStudioPath_ReadsThePathThatIsSent(string apiPath, bool expected) =>
        Assert.Equal(expected, PlayFabApiClient.IsStudioPath(apiPath));

    [Fact]
    public void Constructor_BuildsCorrectBaseUrl()
    {
        var config = CreateTestConfiguration(titleId: "MYID");
        var auth = new FakeAuthenticationService(token: null);

        using var httpClient = new HttpClient();
        using var client = new PlayFabApiClient(config, auth, httpClient);

        Assert.Equal("https://myid.playfabapi.com/", httpClient.BaseAddress?.ToString());
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("http://attacker.com/Admin/GetTitleData")]
    [InlineData("https://other.playfabapi.com/Admin/GetTitleData")]
    [InlineData("//evil.example/path")]
    [InlineData("///evil.example/path")]
    [InlineData("\\\\evil.example\\path")]
    public async Task CallApiAsync_RejectsNonRelativeUri(string maliciousPath)
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(token: null);

        using var httpClient = new HttpClient();
        using var client = new PlayFabApiClient(config, auth, httpClient);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CallApiAsync(maliciousPath, null, CancellationToken.None));
    }

    /// <summary>
    /// A path that would resolve to another host is refused before a token is acquired, so the
    /// bearer token and body never leave for it. <c>HttpClient</c> folds a backslash to '/' when it
    /// combines a relative path with its base address, so <c>\/host</c> becomes <c>//host</c>.
    /// </summary>
    [Theory]
    [InlineData("/\\/evil.example/Admin/GetTitleData")]
    [InlineData("\\/evil.example/Admin/GetTitleData")]
    [InlineData("/\\\\evil.example/Admin/GetTitleData")]
    [InlineData("/\\/\\evil.example/Admin/GetTitleData")]
    [InlineData("/\\/evil.example\\Admin\\GetTitleData")]
    [InlineData(" //evil.example/Admin/GetTitleData")]
    [InlineData("\t//evil.example/Admin/GetTitleData")]
    [InlineData("\r\n//evil.example/Admin/GetTitleData")]
    [InlineData("/ \\/evil.example/Admin/GetTitleData")]
    [InlineData("/\t\\/evil.example/Admin/GetTitleData")]
    [InlineData("/\\/evil.example:443/Admin/GetTitleData")]
    [InlineData("/\\/evil.example:8443/Admin/GetTitleData")]
    [InlineData(" //ABC123.playfabapi.com:8443/Admin/GetTitleData")]
    [InlineData("/\\/ABC123.playfabapi.com@evil.example/Admin/GetTitleData")]
    [InlineData("/\\/user:pass@evil.example/Admin/GetTitleData")]
    [InlineData(" //user@ABC123.playfabapi.com/Admin/GetTitleData")]
    public async Task CallApiAsync_SendsNothingOffTheBaseAddress(string apiPath)
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var exception = await Record.ExceptionAsync(
            () => client.CallApiAsync(apiPath, null, CancellationToken.None));

        Assert.Null(handler.LastRequest?.RequestUri?.AbsoluteUri);
        var argumentException = Assert.IsType<ArgumentException>(exception);
        Assert.DoesNotContain("evil.example", argumentException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, auth.TokenRequests);
    }

    /// <summary>
    /// Legitimate paths, and encoded or control characters that <see cref="Uri"/> escapes into the
    /// path, are sent to the base address exactly as before.
    /// </summary>
    [Theory]
    [InlineData("https://ABC123.playfabapi.com/", "/Admin/GetTitleData", "https://abc123.playfabapi.com/Admin/GetTitleData")]
    [InlineData("https://ABC123.playfabapi.com/", "Admin/GetTitleData", "https://abc123.playfabapi.com/Admin/GetTitleData")]
    [InlineData("https://ABC123.playfabapi.com/", "/Catalog/SearchItems?x=1", "https://abc123.playfabapi.com/Catalog/SearchItems?x=1")]
    [InlineData("https://ABC123.playfabapi.com/", " /Admin/GetTitleData", "https://abc123.playfabapi.com/Admin/GetTitleData")]
    [InlineData("https://ABC123.playfabapi.com/", "/%5C/evil.example/x", "https://abc123.playfabapi.com/%5C/evil.example/x")]
    [InlineData("https://ABC123.playfabapi.com/", "\u0001//evil.example/x", "https://abc123.playfabapi.com/%01//evil.example/x")]
    [InlineData("http://localhost:5000/", "/Admin/GetTitleData", "http://localhost:5000/Admin/GetTitleData")]
    public async Task CallApiAsync_SendsToTheBaseAddress(string baseAddress, string apiPath, string expectedUri)
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        await client.CallApiAsync(apiPath, null, CancellationToken.None);

        Assert.Equal(expectedUri, handler.LastRequest?.RequestUri?.AbsoluteUri);
    }

    /// <summary>
    /// The destructive gate reads <c>Admin\DeleteTitle</c> as <c>DeleteTitle</c>, and the client
    /// refuses the backslash, so such a path never goes out, confirmed or not.
    /// </summary>
    [Theory]
    [InlineData("Admin\\DeleteTitle", false)]
    [InlineData("/Admin\\DeleteTitle", false)]
    [InlineData("Admin\\DeleteTitle", true)]
    [InlineData("/Admin\\DeleteTitle", true)]
    public async Task CallApiAsync_BackslashDestructivePath_IsNeverSent(string apiPath, bool confirmedDestructive)
    {
        Assert.True(DestructiveOperations.IsDestructive(apiPath));
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => client.CallApiAsync(apiPath, null, CancellationToken.None, confirmedDestructive));

        Assert.Contains("backslash", ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
        Assert.Equal(0, auth.TokenRequests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("   ")]
    public async Task CallApiAsync_RejectsEmptyPath(string emptyPath)
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(token: null);

        using var httpClient = new HttpClient();
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => client.CallApiAsync(emptyPath, null, CancellationToken.None));

        Assert.Contains("non-empty", ex.Message);
    }

    [Theory]
    [InlineData("/Admin/DeleteTitle")]
    [InlineData("/MultiplayerServer/DeleteBuild")]
    [InlineData("/MultiplayerServer/ShutdownMultiplayerServer")]
    public async Task CallApiAsync_DestructiveOperation_WithoutConfirmation_ThrowsAndSendsNothing(string apiPath)
    {
        // Fail-closed: a destructive op with confirmedDestructive left at its default (false)
        // is refused at the chokepoint before any token is acquired or request is sent.
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(token: null);

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var ex = await Assert.ThrowsAsync<DestructiveOperationNotConfirmedException>(
            () => client.CallApiAsync(apiPath, null, CancellationToken.None));

        Assert.Equal(apiPath, ex.ApiPath);
        Assert.Null(handler.LastRequest);
        Assert.Equal(0, auth.TokenRequests);
    }

    [Fact]
    public async Task CallApiAsync_Typed_DestructiveOperation_WithoutConfirmation_Throws()
    {
        // The typed overload is the path curated commands use; it must also fail closed so a
        // destructive curated command can't bypass the gate by skipping Program.InvokeApiAsync.
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(token: null);

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{}}") });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        await Assert.ThrowsAsync<DestructiveOperationNotConfirmedException>(
            () => client.CallApiAsync<object, object>("/Admin/DeleteTitle", new object(), CancellationToken.None));

        Assert.Null(handler.LastRequest);
        Assert.Equal(0, auth.TokenRequests);
    }

    [Fact]
    public async Task CallApiAsync_DestructiveOperation_WithConfirmation_Proceeds()
    {
        // With confirmedDestructive: true the gate is bypassed and the request is sent,
        // proving the confirmation threads through to the actual call.
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{}}") });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var result = await client.CallApiAsync("/Admin/DeleteTitle", "{}", CancellationToken.None, confirmedDestructive: true);

        Assert.Contains("data", result);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/Admin/DeleteTitle", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task CallApiAsync_AuditLogDisabled_DoesNotCreateFile()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var auditLogPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "audit.jsonl");

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient, auditLogPath);

        await client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None);

        Assert.False(File.Exists(auditLogPath));
    }

    [Fact]
    public async Task CallApiAsync_AuditLogEnabled_AppendsMetadataWithoutSecrets()
    {
        const string bodySecret = "body-super-secret";
        var token = FakeAuthenticationService.CreateToken();
        var config = CreateTestConfiguration(auditLogEnabled: true);
        var auth = new FakeAuthenticationService(token);
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var auditLogPath = Path.Combine(directory, "audit.jsonl");

        try
        {
            using (var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") })
            using (var client = new PlayFabApiClient(config, auth, httpClient, auditLogPath))
            {
                await client.CallApiAsync(
                    "/Admin/UpdateTitleData?ignored=query",
                    $"{{\"Secret\":\"{bodySecret}\"}}",
                    CancellationToken.None);
            }

            var contents = await File.ReadAllTextAsync(ReadAuditFile(directory));
            var line = Assert.Single(
                contents.Split('\n', StringSplitOptions.RemoveEmptyEntries));

            Assert.DoesNotContain(bodySecret, contents);
            Assert.DoesNotContain(token.AccessToken, contents);

            using var entry = JsonDocument.Parse(line);
            Assert.Equal("/Admin/UpdateTitleData", entry.RootElement.GetProperty("ResolvedApiPath").GetString());
            Assert.False(entry.RootElement.TryGetProperty("Classification", out _));
            Assert.Equal("ABC123", entry.RootElement.GetProperty("TitleId").GetString());
            Assert.Equal(0, entry.RootElement.GetProperty("ExitStatus").GetInt32());
            // CompactJsonFormatter records the timestamp as "@t".
            Assert.True(entry.RootElement.TryGetProperty("@t", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CallApiAsync_AuditLogEnabled_RecordsFailedDestructiveAttempt()
    {
        var config = CreateTestConfiguration(auditLogEnabled: true);
        var auth = new FakeAuthenticationService(token: null);
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var auditLogPath = Path.Combine(directory, "audit.jsonl");

        try
        {
            using (var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") })
            using (var client = new PlayFabApiClient(config, auth, httpClient, auditLogPath))
            {
                await Assert.ThrowsAsync<DestructiveOperationNotConfirmedException>(
                    () => client.CallApiAsync("/Admin/DeleteTitle", null, CancellationToken.None));
            }

            var line = Assert.Single(await File.ReadAllLinesAsync(ReadAuditFile(directory)));
            using var entry = JsonDocument.Parse(line);
            Assert.Equal("/Admin/DeleteTitle", entry.RootElement.GetProperty("ResolvedApiPath").GetString());
            Assert.Equal(1, entry.RootElement.GetProperty("ExitStatus").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AuditLogger_ConcurrentInstances_AppendEveryEntry()
    {
        // Serilog's shared-file mode coordinates writers across processes, which is what several
        // concurrent CLI invocations amount to. Every entry must survive.
        var config = CreateTestConfiguration(auditLogEnabled: true);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var auditLogPath = Path.Combine(directory, "audit.jsonl");
        var loggers = Enumerable.Range(0, 20)
            .Select(_ => new AuditLogger(config, "ABC123", auditLogPath))
            .ToArray();

        try
        {
            Parallel.ForEach(loggers, logger => logger.WriteInvocation("/Admin/SetTitleData", 0));

            foreach (var logger in loggers)
            {
                logger.Dispose();
            }

            Assert.Equal(loggers.Length, File.ReadAllLines(ReadAuditFile(directory)).Length);
        }
        finally
        {
            foreach (var logger in loggers)
            {
                logger.Dispose();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AuditLogger_RollsDaily_SoTheFileNameCarriesTheDate()
    {
        // Retention is by day: Serilog cannot roll on size for a shared file, because each
        // process tracks the size in its own memory and the roll races. The date suffix is
        // therefore part of the contract with anyone reading these logs.
        var config = CreateTestConfiguration(auditLogEnabled: true);
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var beforeWrite = DateTime.Now;
            using (var logger = new AuditLogger(config, "ABC123", Path.Combine(directory, "audit.jsonl")))
            {
                logger.WriteInvocation("/Admin/SetTitleData", 0);
            }
            var afterWrite = DateTime.Now;

            var file = Path.GetFileName(ReadAuditFile(directory));
            Assert.Contains(file, new[]
            {
                $"audit{beforeWrite:yyyyMMdd}.jsonl",
                $"audit{afterWrite:yyyyMMdd}.jsonl"
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Resolves the single audit file in <paramref name="directory"/>. Serilog's daily rolling
    /// inserts a date before the extension, so the path handed to the logger is a template
    /// rather than the literal file name.
    /// </summary>
    private static string ReadAuditFile(string directory) =>
        Assert.Single(Directory.GetFiles(directory, "audit*.jsonl"));

    [Fact]
    public async Task CallApiAsync_Typed_DeserializesDataWrapper()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var responseJson = """{"code":200,"status":"OK","data":{"Title":"MyGame","Version":"1.0"}}""";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var result = await client.CallApiAsync<TestRequest, TestResponse>("Admin/GetTitleData", new TestRequest { Key = "x" }, CancellationToken.None);

        Assert.Equal("MyGame", result.Title);
        Assert.Equal("1.0", result.Version);
    }

    [Fact]
    public async Task CallApiAsync_Typed_FallsBackToWholeResponseWhenNoDataField()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        // Response without a "data" wrapper — just the payload directly
        var responseJson = """{"Title":"DirectGame","Version":"2.0"}""";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var result = await client.CallApiAsync<TestRequest, TestResponse>("Admin/GetTitleData", new TestRequest { Key = "x" }, CancellationToken.None);

        Assert.Equal("DirectGame", result.Title);
        Assert.Equal("2.0", result.Version);
    }

    [Fact]
    public async Task CallApiAsync_Typed_ThrowsWhenDataFieldIsNull()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        // "data" field is present but null
        var responseJson = """{"code":200,"status":"OK","data":null}""";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var ex = await Assert.ThrowsAsync<PlayFabApiException>(
            () => client.CallApiAsync<TestRequest, TestResponse>("Admin/GetTitleData", new TestRequest { Key = "x" }, CancellationToken.None));

        Assert.Equal(PlayFabCallOutcome.Succeeded, ex.Outcome);
        Assert.Equal(200, ex.HttpStatusCode);
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallApiAsync_DoesNotSendSecretKeyOrEntityTokenHeaders()
    {
        var config = CreateTestConfiguration();
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        await client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None);

        // Verify no X-SecretKey or X-EntityToken headers are sent
        Assert.False(handler.LastRequest!.Headers.Contains("X-SecretKey"),
            "Request must not contain X-SecretKey header — it would short-circuit the Entra ID auth path.");
        Assert.False(handler.LastRequest.Headers.Contains("X-EntityToken"),
            "Request must not contain X-EntityToken header.");

        // Verify Bearer token IS present
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
    }

    [Theory]
    [InlineData("DEF456.playfabapi.com")]
    // The selected title must be the whole first label.
    [InlineData("ABC1234.playfabapi.com")]
    // A list, or anything else that isn't one host name, is refused even when it starts with the title.
    [InlineData("ABC123.playfabapi.com,DEF456.playfabapi.com")]
    [InlineData("{titleId}.playfabapi.com/path")]
    public void Constructor_RefusesHostHeaderForAnotherTitle(string value)
    {
        var config = CreateTestConfiguration(hostHeader: value);
        var auth = new FakeAuthenticationService(token: null);
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new PlayFabApiClient(config, auth, httpClient));

        Assert.Equal(
            $"The X-PlayFab-Host header is '{value.Replace("{titleId}", "ABC123")}', but it must be a single host " +
            "name whose first label is the selected PlayFab title 'ABC123'. Make {titleId} the first label of " +
            "PlayFab:HostHeader (XBOX_PLAYFAB__HOSTHEADER), as in the built-in '{titleId}.playfabapi.com', or set " +
            "it to 'none' to send no X-PlayFab-Host header.",
            ex.Message);
        Assert.Equal(0, auth.TokenRequests);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData("{titleId}.playfabapi.com", "ABC123.playfabapi.com")]
    [InlineData("abc123.playfabapi.com", "abc123.playfabapi.com")]
    [InlineData(" {titleId}.example.com ", "ABC123.example.com")]
    public async Task CallApiAsync_SendsHostHeaderForTheSelectedTitle(string value, string sent)
    {
        var handler = await SendWithAsync(CreateTestConfiguration(hostHeader: value));

        Assert.Equal(sent, handler.LastRequest!.Headers.GetValues("X-PlayFab-Host").Single());
    }

    /// <summary>
    /// <c>none</c>, in any case, sends no host header; so does a configuration without the
    /// setting. A word rather than an empty value, because an empty environment variable is dropped.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData(null)]
    public async Task CallApiAsync_SendsNoHostHeaderWhenTurnedOff(string? value)
    {
        var handler = await SendWithAsync(CreateTestConfiguration(hostHeader: value));

        Assert.False(handler.LastRequest!.Headers.Contains("X-PlayFab-Host"));
    }

    /// <summary>The built-in settings send the host header for the selected title.</summary>
    [Fact]
    public async Task CallApiAsync_BuiltInSettingsSendTheHostHeader()
    {
        var config = new ConfigurationBuilder()
            .AddConfiguration(TestSettings.BuiltIn())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlayFab:TitleId"] = "ABC123",
                ["Telemetry:Enabled"] = "false",
            })
            .Build();

        var handler = await SendWithAsync(config);

        Assert.Equal("ABC123.playfabapi.com", handler.LastRequest!.Headers.GetValues("X-PlayFab-Host").Single());
    }

    private static async Task<MockHttpMessageHandler> SendWithAsync(IConfiguration config)
    {
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        await client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None);
        return handler;
    }

    [Fact]
    public async Task CallApiAsync_ResponseBodyWithinConfiguredLimit_Succeeds()
    {
        var responseBody = "{\"data\":{\"value\":\"1234567890\"}}";
        var maxResponseBytes = System.Text.Encoding.UTF8.GetByteCount(responseBody);
        var config = CreateTestConfiguration(maxResponseBytes: maxResponseBytes);
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var result = await client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None);

        Assert.Equal(responseBody, result);
    }

    [Fact]
    public async Task CallApiAsync_ResponseBodyOverConfiguredLimit_ThrowsHelpfulError()
    {
        using var client = PlayFabTestClient.Create(
            new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"value\":\"too-large\"}}"),
            }),
            maxResponseBytes: 8);

        var ex = await Assert.ThrowsAsync<PlayFabApiException>(
            () => client.CallApiAsync("/Admin/GetTitleData", null, CancellationToken.None));

        Assert.Equal(PlayFabCallOutcome.Succeeded, ex.Outcome);
        Assert.Equal(
            "PlayFab API request succeeded with HTTP 200 OK, but PlayFab response exceeded the configured maximum " +
            "size of 8 bytes. Increase 'PlayFab:MaxResponseBytes' to allow larger responses.",
            ex.Message);
    }

    /// <summary>
    /// A write's 5xx whose body is over the limit keeps its status and still warns that the write
    /// may have been applied, rather than reporting only the size error.
    /// </summary>
    [Fact]
    public async Task CallApiAsync_OversizedServerErrorOnAWrite_KeepsStatusAndOutcomeWarning()
    {
        var config = CreateTestConfiguration(maxResponseBytes: 8);
        var auth = new FakeAuthenticationService(FakeAuthenticationService.CreateToken());

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html>an oversized gateway error page</html>"),
            });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://ABC123.playfabapi.com/") };
        using var client = new PlayFabApiClient(config, auth, httpClient);

        var ex = await Assert.ThrowsAsync<PlayFabApiException>(
            () => client.CallApiAsync("/Admin/SetTitleData", "{}", CancellationToken.None));

        Assert.Equal(502, ex.HttpStatusCode);
        Assert.Contains("PlayFab response exceeded the configured maximum size", ex.Message);
        Assert.StartsWith(
            "/Admin/SetTitleData may have reached PlayFab, but the request failed with HTTP 502",
            ex.OutcomeWarning);

        var stderr = Program.FormatErrorOutput(ex, compact: true);
        Assert.Contains("HTTP 502", stderr);
        Assert.Contains("PlayFab:MaxResponseBytes", stderr);
        Assert.Contains("outcome is unknown", stderr);
        Assert.False(stderr.EndsWith(Environment.NewLine, StringComparison.Ordinal));
    }
}

internal class TestRequest
{
    public string? Key { get; set; }
}

internal class TestResponse
{
    public string? Title { get; set; }
    public string? Version { get; set; }
}
