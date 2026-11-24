// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Services;

/// <summary>
/// Waits for a game or sandbox creation job, with a short fixed deadline. Runs on
/// <see cref="XboxPartnerPoller"/>, so brief failures of the status read are retried; the create
/// itself is never resent.
/// </summary>
internal static class XboxPartnerJobPoller
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The shortest wait between polls. A <c>Retry-After</c> of zero or a date in the past would
    /// otherwise poll the gateway in a tight loop.
    /// </summary>
    internal static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(1);

    internal static async Task<TJob?> PollAsync<TJob>(
        TJob initialResponse,
        Func<CancellationToken, Task<TJob>> getStatusAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken,
        Func<DateTimeOffset> getUtcNow)
        where TJob : XboxPartnerCreationJob
    {
        if (initialResponse.Completed)
        {
            return initialResponse;
        }

        var currentResponse = initialResponse;
        var completed = await XboxPartnerPoller.PollAsync(
            getStatusAsync,
            job =>
            {
                currentResponse = job;
                return job.Completed;
            },
            failedReadRetryAfter => (failedReadRetryAfter ?? currentResponse.RetryAfter) is { } retryAfter
                ? (retryAfter > MinimumDelay ? retryAfter : MinimumDelay)
                : DefaultInterval,
            DefaultTimeout,
            delayAsync,
            getUtcNow,
            cancellationToken).ConfigureAwait(false);
        return completed ? currentResponse : null;
    }
}
