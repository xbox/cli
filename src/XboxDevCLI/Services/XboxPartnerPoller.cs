// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;

namespace XboxDevCLI.Services;

/// <summary>
/// The loop every XboxPartner wait runs: wait, read, and repeat until the caller says the operation
/// finished or the deadline passes.
/// </summary>
/// <remarks>
/// The caller supplies what to read, when the wait is over, and how long to wait between reads,
/// including its own Retry-After bounds. The loop owns the deadline, which also bounds each read,
/// cancellation, and retrying reads that fail briefly. Reads are safe to repeat, so fewer than
/// <see cref="MaxConsecutiveReadFailures"/> transient failures in a row are retried; the request
/// that started the operation is never sent from here.
/// </remarks>
internal static class XboxPartnerPoller
{
    public const int MaxConsecutiveReadFailures = 3;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="onRead"/> ends the wait, or
    /// <see langword="false"/> when the deadline passes first. Throws when the caller cancels, or a
    /// read fails in a way that waiting won't fix.
    /// </summary>
    /// <param name="read">Reads the operation's current state once.</param>
    /// <param name="onRead">Takes each successful read, such as to report progress, and returns whether the wait is over.</param>
    /// <param name="nextDelay">
    /// How long to wait before the next read, given the Retry-After of the read that just failed, or
    /// <see langword="null"/> after a successful read or a failure without one.
    /// </param>
    /// <param name="timeout">How long to keep reading, counted from the call.</param>
    /// <param name="delayAsync">Waits between reads.</param>
    /// <param name="getUtcNow">The clock the deadline is measured on.</param>
    /// <param name="cancellationToken">Stops the wait, such as on Ctrl+C.</param>
    public static async Task<bool> PollAsync<T>(
        Func<CancellationToken, Task<T>> read,
        Func<T, bool> onRead,
        Func<TimeSpan?, TimeSpan> nextDelay,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTimeOffset> getUtcNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(onRead);
        ArgumentNullException.ThrowIfNull(nextDelay);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(getUtcNow);

        DateTimeOffset deadline = getUtcNow() + timeout;
        TimeSpan? failedReadRetryAfter = null;
        int failures = 0;
        while (true)
        {
            TimeSpan remaining = deadline - getUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            TimeSpan delay = nextDelay(failedReadRetryAfter);
            await delayAsync(delay < remaining ? delay : remaining, cancellationToken).ConfigureAwait(false);
            remaining = deadline - getUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            // The deadline bounds the read too, so a slow answer can't stretch the wait past it.
            // WaitAsync abandons a read that doesn't observe its token.
            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCancellation.CancelAfter(remaining);
            T value;
            try
            {
                value = await read(readCancellation.Token)
                    .WaitAsync(readCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (readCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception) when (
                IsTransient(exception, cancellationToken) && ++failures < MaxConsecutiveReadFailures)
            {
                failedReadRetryAfter = (exception as XboxPartnerApiException)?.RetryAfter;
                continue;
            }

            failures = 0;
            failedReadRetryAfter = null;
            if (onRead(value))
            {
                return true;
            }
        }
    }

    internal static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            XboxPartnerApiException { StatusCode: HttpStatusCode.TooManyRequests } => true,
            // Reads are safe to repeat, so any 502 or 504 is retried: that covers XboxPartner's
            // downstream_* transport codes and the codeless answers from the network in front of it.
            XboxPartnerApiException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout } => true,
            // A 503 with a code such as feature_disabled or service_configuration_invalid does not
            // clear by waiting a few seconds; one without a code came from the network in front of XboxPartner.
            XboxPartnerApiException { StatusCode: HttpStatusCode.ServiceUnavailable } api =>
                Commands.TestAccountCommandFactory.ReadProblemString(api.ResponseBody, "code") is null or "downstream_throttled",
            // DNS and TLS failures do not clear by waiting a few seconds; other network failures, such
            // as a dropped or refused connection, may.
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError } => false,
            HttpRequestException or IOException => true,
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false,
        };
}
