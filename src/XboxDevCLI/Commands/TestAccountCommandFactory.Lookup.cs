// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static partial class TestAccountCommandFactory
{
    private static readonly JsonSerializerOptions LookupJsonOptions = new(JsonSerializerDefaults.Web);

    private static Command CreateLookupCommand()
    {
        var sellerOption = new XdtsSellerOption();
        var accountArgument = new Argument<TestAccountKey>("email-or-xuid")
        {
            Description = $"The test account's email, for example ABCD-tester00000@{TestAccountLookup.Domain}, or its XUID, for example 2814600000000001.",
            CustomParser = result =>
            {
                string? problem = TestAccountLookup.TryParseKey(result.Tokens[0].Value, out var parsed);
                if (problem is not null)
                {
                    result.AddError(problem);
                }

                return parsed!;
            },
        };

        var command = new Command("get", "Show which XBOX test account batch holds an account and whether its access expired");
        command.WithMetadata(XboxPartnerCommandSupport.ReadOnlyMetadata(
            scope: "One XBOX test account and its batch for the selected seller.",
            output:
                "A JSON object with account, batch, expired, and expiredOn. Expired means the batch's " +
                "sandbox access expiry has passed. Table output shows one row.",
            examples:
            [
                $"xbox test-account get ABCD-tester00000@{TestAccountLookup.Domain}",
                $"xbox test-account get ABCD-tester00000@{TestAccountLookup.Domain} --output table",
                "xbox test-account get 2814600000000001",
            ],
            table: XboxPartnerCommandSupport.TestAccountLookupView));
        command.Arguments.Add(accountArgument);
        sellerOption.AddTo(command);
        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var key = parseResult.GetValue(accountArgument)!;
            return InvokeAsync(
                parseResult,
                sellerOption,
                $"Finding the test account batch for {key.Display} using the signed in Entra ID token",
                (client, sellerId, stdout, stderr, output, token) =>
                    RunLookupAsync(client, sellerId, key, parseResult, output, stdout, stderr, token),
                cancellationToken);
        });
        return command;
    }

    internal static async Task<int> RunLookupAsync(
        XboxPartnerClient client,
        long? sellerId,
        TestAccountKey key,
        ParseResult parseResult,
        OutputOptions output,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        TestAccountLookupOutcome outcome;
        try
        {
            outcome = await TestAccountLookup.FindAsync(
                client,
                sellerId,
                key,
                Dependencies.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (XboxPartnerApiException ex)
        {
            stderr.WriteLine(FormatError(
                ex,
                output.IsCompact,
                DescribeFailure(ex.StatusCode, ex.ResponseBody, TestAccountRequest.Lookup)));
            return 1;
        }
        catch (XboxPartnerResponseException ex)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(ex.CorrelationId, stderr);
            stderr.WriteLine($"Request failed: {TerminalText.Message(ex.Message)}");
            return 1;
        }

        string account = key.Display;
        string withTemplate = key.Email is null ? string.Empty : " with this email template";
        if (outcome.Matches.Count == 0)
        {
            if (outcome.CandidateCount > outcome.BatchesRead)
            {
                var commands = new RecoveryCommands(parseResult, sellerId);
                stderr.WriteLine(
                    $"{account} was not found in the newest {outcome.BatchesRead} of " +
                    $"{outcome.CandidateCount} batches{withTemplate}; the older ones were not searched. " +
                    $"List the batches, then inspect the older ones{(key.Email is null ? string.Empty : " with this template")}:");
                stderr.WriteLine("  " + commands.Build("batch", "list"));
                stderr.WriteLine("  " + commands.Template("batch", "get", RecoveryCommands.BatchIdPlaceholder));
            }
            else
            {
                stderr.WriteLine(
                    $"{account} was not found among the batches this seller can list. " +
                    "Batches created by other developers may not be listed; check --seller-id.");
            }

            return 1;
        }

        if (outcome.CandidateCount > outcome.BatchesRead)
        {
            stderr.WriteLine(
                $"Only the newest {outcome.BatchesRead} of {outcome.CandidateCount} batches{withTemplate} " +
                "were searched.");
        }

        if (outcome.Matches.Count > 1)
        {
            var commands = new RecoveryCommands(parseResult, sellerId);
            stderr.WriteLine($"{account} appears in more than one batch. Inspect each with:");
            foreach (TestAccountLookupResult match in outcome.Matches)
            {
                stderr.WriteLine("  " + commands.Build(
                    "batch",
                    "get",
                    match.Batch.Id.ToString(CultureInfo.InvariantCulture)));
            }

            return 1;
        }

        if (outcome.Matches[0].Account.IsDeleted)
        {
            stderr.WriteLine($"{account} is deleted; its batch still lists it.");
        }

        string json = JsonSerializer.Serialize(outcome.Matches[0], LookupJsonOptions);
        return await RunAsync(
            () => Task.FromResult(json),
            XboxPartnerTableFormatter.FormatTestAccountLookup,
            _ => null,
            output,
            stdout,
            stderr,
            cancellationToken).ConfigureAwait(false);
    }
}
