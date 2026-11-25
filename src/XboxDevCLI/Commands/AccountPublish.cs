// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json.Nodes;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// <c>xbl config publish --scope account</c>: publishes the seller account's configuration, such as its
/// relying parties and business partners. XBOX services report no status for an account publish, so the
/// wait reads the account's published version instead, which a publish that fails leaves unchanged. Any
/// publish of the account raises it, so a rise says only that this publish or another one committed.
/// </summary>
internal static class AccountPublish
{
    private const string RunningStatus = "Running";

    /// <summary>
    /// Asks first unless <paramref name="validateOnly"/> or <paramref name="confirmed"/>, sends the publish, and
    /// writes its result. When <paramref name="follow"/> is set and a publish starts, says how to check it, and
    /// waits for it unless <see cref="PublishFollowUp.Timeout"/> is null.
    /// </summary>
    internal static async Task<int> RunAsync(
        XboxPartnerClient client,
        long? sellerId,
        bool validateOnly,
        bool confirmed,
        PublishConfirmationDependencies confirmation,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        string recoveryPrefix,
        PublishFollowUp? follow,
        CancellationToken cancellationToken)
    {
        if (!validateOnly && !confirmed)
        {
            try
            {
                if (!await ConfirmAsync(sellerId, confirmation, stderr, cancellationToken).ConfigureAwait(false))
                {
                    return 1;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stderr.WriteLine();
                stderr.WriteLine("Publish canceled.");
                return 1;
            }
        }

        string check = $"{recoveryPrefix} xbl config status --scope account" +
            (sellerId is { } seller ? $" --seller-id {seller.ToString(CultureInfo.InvariantCulture)}" : "");
        var sent = false;
        string? sentCorrelationId = null;
        var answered = false;
        try
        {
            XboxPartnerOperationResult<XboxLiveAccountPublishResponse> response;
            using (RequestProgress.Start(Describe(validateOnly), stderr))
            {
                response = await client.PublishXboxLiveAccountConfigurationAsync(
                    sellerId,
                    validateOnly,
                    cancellationToken,
                    onSending: correlationId =>
                    {
                        sent = true;
                        sentCorrelationId = correlationId;
                    }).ConfigureAwait(false);
            }

            answered = true;
            XboxLiveAccountPublishResponse result = response.Value;
            PublishCommandFactory.WriteDiagnostics(result.Validation, stderr);
            if (string.Equals(result.Status, PublishCommandFactory.AlreadyPublishedStatus, StringComparison.OrdinalIgnoreCase))
            {
                stderr.WriteLine("Nothing changed in the account configuration since its last publish, so there's nothing to publish.");
            }

            string body = response.Body;
            string status = result.Status!;
            if (follow is not null &&
                string.Equals(result.Status, RunningStatus, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.JobId))
            {
                string job = XboxPartnerCommandSupport.ForTerminal(result.JobId);
                stderr.WriteLine($"Publish job {job} started.");
                if (follow.Timeout is not { } timeout)
                {
                    stderr.WriteLine($"Publishing continues on the service. Check it with: {check}");
                }
                else
                {
                    ulong? published = await WaitAsync(
                        client, sellerId, result.PreviousPublishedVersion, job, timeout, follow.Dependencies, check,
                        stderr, cancellationToken).ConfigureAwait(false);
                    if (published is null)
                    {
                        return PublishCommandFactory.PublishNotFinishedExitCode;
                    }

                    // The job's own outcome is unknown, so status stays the publish's Running.
                    JsonObject finished = JsonNode.Parse(body)!.AsObject();
                    finished["publishedVersion"] = published.Value;
                    body = finished.ToJsonString();
                    stderr.WriteLine(
                        $"The account's published version rose to {published.Value.ToString(CultureInfo.InvariantCulture)}, " +
                        "so this publish or another one committed. XBOX services start using the published " +
                        "configuration once it reaches them, which the CLI can't see.");
                }
            }

            stdout.WriteLine(output.TableView is not null
                ? XboxPartnerTableFormatter.FormatRows(
                    output.TableView,
                    [
                        [
                            status,
                            "account",
                            "account",
                            validateOnly.ToString(),
                            result.SourceVersion?.ToString(CultureInfo.InvariantCulture) ?? "latest",
                        ],
                    ])
                : PlayFabApiClient.FormatJson(body, output.IsCompact));
            return 0;
        }
        catch (OperationCanceledException) when (!validateOnly && !sent && cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine("Canceled before the request was sent, so the account publish didn't start.");
            return 1;
        }
        catch (Exception ex) when (!validateOnly && sent && !answered && XboxPartnerCommandSupport.IsUncertainSubmission(ex))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(ex, output.IsCompact, stderr, cancellationToken, sentCorrelationId);
            stderr.WriteLine("It is unknown whether the account publish started.");
            WriteHowToCheck(stderr, check);
            return PublishCommandFactory.OutcomeUnknownExitCode;
        }
    }

    /// <summary>
    /// Says how to tell whether the draft is live once the CLI lost track of a publish. The published version
    /// can't tell, because another publish of the account raises it too, and a save since the publish keeps
    /// hasChanges true even when the publish landed. Running it again is safe, because the service publishes only
    /// what isn't live yet, and starts nothing once it is.
    /// </summary>
    private static void WriteHowToCheck(TextWriter stderr, string check)
    {
        stderr.WriteLine($"Check with: {check}");
        stderr.WriteLine(
            "The draft is live if hasChanges is false. If not, running the publish again is safe, because it " +
            "publishes only what isn't live yet.");
    }

    private static string Describe(bool validateOnly) =>
        validateOnly ? "Validating the account publish" : "Publishing the account configuration";

    /// <summary>
    /// Says what the publish changes, and asks at a terminal. Without one it refuses. The account's
    /// configuration reaches RETAIL, as the title-wide configuration does.
    /// </summary>
    private static Task<bool> ConfirmAsync(
        long? sellerId,
        PublishConfirmationDependencies dependencies,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        stderr.WriteLine(
            "This publishes the account's latest working configuration" +
            (sellerId is { } seller ? $" for seller {seller.ToString(CultureInfo.InvariantCulture)}." : "."));
        stderr.WriteLine("The account's configuration applies to the whole seller account, in every sandbox, RETAIL included.");
        return PublishCommandFactory.AskAsync(
            "Publish the account configuration? [y/N]: ", dependencies, stderr, cancellationToken);
    }

    /// <summary>
    /// Reads the account's published version, as config status does, until it passes
    /// <paramref name="previous"/>, the one before the publish, or <paramref name="timeout"/> passes. Returns
    /// the new version, or null after saying on stderr why waiting ended and how to tell whether the draft is live.
    /// </summary>
    private static async Task<ulong?> WaitAsync(
        XboxPartnerClient client,
        long? sellerId,
        ulong? previous,
        string job,
        TimeSpan timeout,
        XboxPartnerJobDependencies dependencies,
        string check,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        stderr.WriteLine($"Waiting up to {TestAccountCommandFactory.FormatDuration(timeout)} for it to finish...");
        ulong? published = null;
        try
        {
            using (RequestProgress.Start(Describe(validateOnly: false), stderr))
            {
                await XboxPartnerPoller.PollAsync(
                    async token => XboxPartnerCommandSupport.DeserializeResponse<XboxLiveConfigurationGetResponse>(
                        await PublishCommandFactory.ReadStatusAsync(() => client.GetXboxLiveConfigurationStatusAsync(
                            sellerId, XboxLiveScope.Account, null, null, token)).ConfigureAwait(false),
                        "configuration status").PublishedVersion,
                    version =>
                    {
                        published = version > (previous ?? 0) ? version : null;
                        return published is not null;
                    },
                    retryAfter => retryAfter is { } after
                        ? TestAccountJobWaiter.Clamp(after)
                        : TestAccountJobWaiter.DefaultInterval,
                    timeout,
                    dependencies.DelayAsync,
                    dependencies.GetUtcNow,
                    cancellationToken).ConfigureAwait(false);
            }

            if (published is not null)
            {
                return published;
            }

            stderr.WriteLine(
                $"Publish job {job} didn't land within {TestAccountCommandFactory.FormatDuration(timeout)}. " +
                "It might still be running, or it might have failed; XBOX services don't report which.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine("Stopped waiting.");
        }
        catch (XboxPartnerApiException exception)
        {
            stderr.WriteLine(XboxPartnerCommandErrorFormatter.Format(exception, compact: false));
            stderr.WriteLine("Could not read the account's published version.");
        }
        catch (Exception exception) when (XboxPartnerCommandSupport.IsRequestError(exception))
        {
            XboxPartnerCommandSupport.WriteCorrelationId(
                (exception as XboxPartnerResponseException)?.CorrelationId ?? XboxPartnerClient.TransportCorrelationId(exception),
                stderr);
            stderr.WriteLine($"Request failed: {TerminalText.SingleLine(exception.Message)}");
            stderr.WriteLine("Could not read the account's published version.");
        }

        WriteHowToCheck(stderr, check);
        return null;
    }
}
