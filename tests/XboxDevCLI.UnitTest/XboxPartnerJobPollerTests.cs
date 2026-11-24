// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerJobPollerTests
{
    [Fact]
    public async Task PollAsync_ReturnsInitialResponseWhenAlreadyCompleted()
    {
        var initial = CreateJob(status: "completed", result: "succeeded");
        var statusCalls = 0;
        var delayCalls = 0;

        var result = await XboxPartnerJobPoller.PollAsync(
            initial,
            _ =>
            {
                statusCalls++;
                return Task.FromResult(initial);
            },
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            () => DateTimeOffset.UnixEpoch);

        Assert.Same(initial, result);
        Assert.Equal(0, statusCalls);
        Assert.Equal(0, delayCalls);
    }

    [Fact]
    public async Task PollAsync_UsesDefaultIntervalThenRetryAfter()
    {
        var initial = CreateJob();
        var responses = new Queue<XboxPartnerGameCreationJob>(
        [
            CreateJob(retryAfter: TimeSpan.FromSeconds(7)),
            CreateJob(status: "completed", result: "succeeded"),
        ]);
        var clock = new FakeClock();

        var result = await XboxPartnerJobPoller.PollAsync(
            initial,
            _ => Task.FromResult(responses.Dequeue()),
            clock.DelayAsync,
            CancellationToken.None,
            () => clock.UtcNow);

        Assert.NotNull(result);
        Assert.True(result.Completed);
        Assert.True(result.Succeeded);
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7)],
            clock.Delays);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task PollAsync_BoundsRetryAfterAtDefaultDeadline()
    {
        var initial = CreateJob();
        var statusCalls = 0;
        var clock = new FakeClock();

        var result = await XboxPartnerJobPoller.PollAsync(
            initial,
            _ =>
            {
                statusCalls++;
                return Task.FromResult(
                    CreateJob(retryAfter: TimeSpan.FromSeconds(40)));
            },
            clock.DelayAsync,
            CancellationToken.None,
            () => clock.UtcNow);

        Assert.Null(result);
        Assert.Equal(TimeSpan.FromSeconds(120), XboxPartnerJobPoller.DefaultTimeout);
        Assert.Equal(3, statusCalls);
        Assert.Equal(
            [
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(35),
            ],
            clock.Delays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(500)]
    public async Task PollAsync_WaitsAtLeastTheMinimumDelayForAShortRetryAfter(int retryAfterMilliseconds)
    {
        var initial = CreateJob(retryAfter: TimeSpan.FromMilliseconds(retryAfterMilliseconds));
        var running = CreateJob(retryAfter: TimeSpan.Zero);
        var completed = CreateJob(status: "completed", result: "succeeded");
        var responses = new Queue<XboxPartnerGameCreationJob>([running, completed]);
        var clock = new FakeClock();

        var result = await XboxPartnerJobPoller.PollAsync(
            initial,
            _ => Task.FromResult(responses.Dequeue()),
            clock.DelayAsync,
            CancellationToken.None,
            () => clock.UtcNow);

        Assert.Same(completed, result);
        Assert.Equal(TimeSpan.FromSeconds(1), XboxPartnerJobPoller.MinimumDelay);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], clock.Delays);
    }

    [Fact]
    public async Task PollAsync_RetriesTransientReadFailuresAsTheyAsk()
    {
        var clock = new FakeClock();
        var completed = CreateJob(status: "completed", result: "succeeded");
        var outcomes = new Queue<Func<XboxPartnerGameCreationJob>>(
        [
            () => throw Api(HttpStatusCode.BadGateway, TimeSpan.FromSeconds(7)),
            () => throw new HttpRequestException("reset"),
            () => completed,
        ]);

        var result = await XboxPartnerJobPoller.PollAsync(
            CreateJob(),
            _ => Task.FromResult(outcomes.Dequeue()()),
            clock.DelayAsync,
            CancellationToken.None,
            () => clock.UtcNow);

        Assert.Same(completed, result);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(5)], clock.Delays);
    }

    private static XboxPartnerApiException Api(HttpStatusCode statusCode, TimeSpan? retryAfter) =>
        new(statusCode, null, string.Empty, new Uri("http://localhost/job"), retryAfter: retryAfter);

    private static XboxPartnerGameCreationJob CreateJob(
        string? status = "running",
        string? result = "pending",
        TimeSpan? retryAfter = null) =>
        new()
        {
            RawResponse = "{}",
            JobId = "job-1",
            Status = status,
            Result = result,
            RetryAfter = retryAfter,
        };

    private sealed class FakeClock
    {
        internal DateTimeOffset UtcNow { get; private set; } =
            DateTimeOffset.UnixEpoch;

        internal List<TimeSpan> Delays { get; } = [];

        internal Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow = UtcNow.Add(delay);
            return Task.CompletedTask;
        }
    }
}
