// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

/// <summary>
/// What <c>xbox xbl relying-party</c>, <c>business-partner</c>, and <c>web-service</c> share: each reads or changes
/// something a seller account owns.
/// </summary>
internal static class XboxLiveAccountCommandSupport
{
    /// <summary>How a write's messages name it.</summary>
    /// <param name="Activity">The write in progress, such as "Creating relying party rp://api.nodpublishers.com/".</param>
    /// <param name="NotDone">What didn't happen when it wasn't sent, such as "no relying party was created".</param>
    /// <param name="Unknown">
    /// What's unknown when its outcome is, such as "whether relying party rp://api.nodpublishers.com/ was created".
    /// </param>
    /// <param name="Check">The command that checks it then, and whether to run the write again.</param>
    internal sealed record Change(string Activity, string NotDone, string Unknown, string Check);

    public static Option<bool> CreateConfirmOption(string description) =>
        new("--confirm", "--yes", "-y") { Description = description };

    public static bool IsConfirmed(ParseResult result, Option<bool> confirm) =>
        result.GetResult(confirm) is { Implicit: false }
            ? result.GetValue(confirm)
            : result.GetValue(Program.ConfirmOption);

    /// <summary>
    /// Runs <paramref name="action"/> with a client and the seller. The service's failures, and a request timeout,
    /// are written to stderr; everything else, including the caller's cancellation, reaches the shared boundary.
    /// </summary>
    public static async Task<int> RunAsync(
        ParseResult result,
        XdtsSellerOption seller,
        Func<XboxPartnerClient, long?, Task<int>> action,
        CancellationToken cancellationToken)
    {
        var configuration = Program.GetConfiguration(result);
        _ = XboxPartnerCommandSupport.ResolveBaseUrl(configuration);
        using var client = Program.XboxPartnerClientFactory is null
            ? new XboxPartnerClient(configuration, authenticationService: Program.GetAuthentication(result))
            : Program.XboxPartnerClientFactory(configuration);
        try
        {
            return await action(client, seller.GetSellerId(result));
        }
        catch (Exception exception) when (exception is XboxPartnerApiException or XboxPartnerResponseException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(
                exception, OutputOptions.From(result).IsCompact, Console.Error, cancellationToken);
            return 1;
        }
    }

    /// <summary>Writes a response as the service sent it, or as the command's table: one record, or a bare array of them.</summary>
    public static void Write(ParseResult result, string body)
    {
        var output = OutputOptions.From(result);
        if (output.TableView is { } view)
        {
            using var document = JsonDocument.Parse(body);
            Console.WriteLine(view.IsRecord
                ? TableFormatter.Format(document.RootElement, view)
                : TableFormatter.FormatCollection(document.RootElement, view));
            return;
        }

        Console.WriteLine(PlayFabApiClient.FormatJson(body, output.IsCompact));
    }

    /// <summary>
    /// Asks <paramref name="confirm"/> first when there is one, then sends one write, and writes its result and what
    /// <paramref name="done"/> says about it. A failure after it was sent, other than a refusal (4xx other than 408),
    /// leaves its outcome unknown: exit 3, with the command that checks it. A name resolution or TLS failure means it
    /// never left the machine: exit 1. <paramref name="explain"/> returns what to add to a failure it knows, which
    /// then exits 1 whatever its status.
    /// </summary>
    public static async Task<int> ChangeAsync<T>(
        ParseResult result,
        Change change,
        Func<string, Action, Task<XboxPartnerOperationResult<T>>> send,
        Func<T, string> done,
        CancellationToken cancellationToken,
        Func<Task<bool>>? confirm = null,
        Func<XboxPartnerApiException, string?>? explain = null)
    {
        bool compact = OutputOptions.From(result).IsCompact;
        string correlationId = Guid.NewGuid().ToString();
        var sent = false;
        XboxPartnerOperationResult<T> changed;
        try
        {
            if (confirm is not null && !await confirm())
            {
                return 1;
            }

            Program.WriteVerbose(
                result.GetValue(Program.VerboseOption), () => $"{change.Activity} with correlation ID {correlationId}.");
            using (RequestProgress.Start(change.Activity))
            {
                changed = await send(correlationId, () => sent = true);
            }
        }
        catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Canceled before the request was sent, so {change.NotDone}.");
            return 1;
        }
        catch (XboxPartnerApiException exception) when (explain?.Invoke(exception) is { } explanation)
        {
            Console.Error.WriteLine(XboxPartnerCommandErrorFormatter.Format(exception, compact, explanation));
            return 1;
        }
        // The write is the command's last request, so a name resolution or TLS failure proves the write never left the
        // machine.
        catch (HttpRequestException exception) when (XboxPartnerClient.NeverReachedService(exception))
        {
            Console.Error.WriteLine(
                $"Request failed: {TerminalText.SingleLine(exception.Message).TrimEnd('.')}. The request didn't reach "
                + $"the service, so {change.NotDone}.");
            return 1;
        }
        catch (Exception exception) when (sent && XboxPartnerCommandSupport.IsUncertainSubmission(exception))
        {
            XboxPartnerCommandSupport.WriteUncertainCause(
                exception, compact, Console.Error, cancellationToken, correlationId);
            Console.Error.WriteLine($"It is unknown {change.Unknown}. {change.Check}");
            return XboxPartnerCommandSupport.OutcomeUnknownExitCode;
        }

        Write(result, changed.Body);
        Console.Error.WriteLine(done(changed.Value));
        return 0;
    }

    /// <summary>What stderr says after a write to the account draft: that it isn't published yet, and how to publish it.</summary>
    public static string Saved(ParseResult result, long? sellerId) =>
        CommitDocumentsCommandFactory.DescribeSaved(
            string.Join(' ', RecoveryCommandArguments.Create(result)), sellerId, scid: null, sandbox: null);

    /// <summary>
    /// A read-only command, with the caller's sign-in options and the seller. <paramref name="command"/>'s values
    /// must already be quoted.
    /// </summary>
    public static string CheckCommand(ParseResult result, long? sellerId, params string[] command)
    {
        var arguments = RecoveryCommandArguments.Create(result);
        arguments.AddRange(command);
        if (sellerId is { } seller)
        {
            arguments.AddRange(["--seller-id", seller.ToString(CultureInfo.InvariantCulture)]);
        }

        return string.Join(' ', arguments);
    }

    /// <summary>"for seller 1234567", or nothing when the service uses the developer's only seller.</summary>
    public static string ForSeller(long? sellerId) =>
        sellerId is { } seller ? $" for seller {seller.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

    public static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
}
