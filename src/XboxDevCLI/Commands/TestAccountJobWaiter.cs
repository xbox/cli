// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// Polls a test account creation job until it stops running or the deadline passes. XboxPartner
/// never polls the job itself, so the CLI owns the cadence.
/// </summary>
/// <remarks>
/// Runs on <see cref="XboxPartnerPoller"/>, like <see cref="XboxPartnerJobPoller"/>, but keeps its
/// own cadence and result: test account jobs can run for days, so the caller sets the deadline, and
/// the last running read is returned on timeout so the user still sees how far the job got. The
/// interval bounds here also pace the batch and playtest waits.
/// </remarks>
internal static class TestAccountJobWaiter
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(5);

    public static async Task<TestAccountJobResult> WaitAsync(
        TestAccountJobResult initial,
        Func<CancellationToken, Task<TestAccountJobResult>> read,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTimeOffset> getUtcNow,
        Action<TestAccountJobResult>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initial);

        TestAccountJobResult current = initial;
        if (!current.Job.IsRunning)
        {
            return current;
        }

        await XboxPartnerPoller.PollAsync(
            read,
            job =>
            {
                current = job;
                onProgress?.Invoke(job);
                return !job.Job.IsRunning;
            },
            failedReadRetryAfter => Clamp(failedReadRetryAfter ?? current.RetryAfter ?? DefaultInterval),
            timeout,
            delayAsync,
            getUtcNow,
            cancellationToken).ConfigureAwait(false);
        return current;
    }

    internal static TimeSpan Clamp(TimeSpan value) =>
        value < MinimumInterval ? MinimumInterval : value > MaximumInterval ? MaximumInterval : value;
}
