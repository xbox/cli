// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal static partial class AudienceCommandFactory
{
    /// <summary>The most members an audience's member list can hold.</summary>
    internal const int MaxMembers = 1000;

    /// <summary>The longest audience name, in UTF-16 code units.</summary>
    internal const int MaxNameLength = 200;

    /// <summary>The most members-file problems an error lists before summarizing the rest.</summary>
    private const int MaxListedProblems = 10;

    /// <summary>The most added or removed members the confirmation prompt lists; --dry-run lists all.</summary>
    internal const int MaxPromptedMembers = 10;

    private const string ExitCodesNote =
        "Exit codes: 0 written; 1 not written (rejected, or failed before the request was sent); 3 the request " +
        "was sent but its outcome is unknown, and stderr shows its correlation ID. The request is never retried.";

    private const string MembersFileNote =
        "A members file is UTF-8 or UTF-16 text with one email address per line. " +
        "Blank lines and spaces around each line are ignored. " +
        "A member listed more than once, even with different capitalization, is sent once, as first written. " +
        "It can list up to 1000 members. " +
        "Each line must be a plain email address, without a name, angle brackets, or invisible characters. " +
        "If a line isn't, the command sends nothing and names the lines to fix.";

    private const string PermissionNote =
        "Requires Partner Center's customer groups Write permission for the seller.";

    private const string LastWriterNote =
        "Changes aren't checked against other changes to the same audience: the last write wins.";

    private static Command CreateCreateCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var name = CreateNameOption($"Name of the audience, up to {MaxNameLength} characters.");
        name.Required = true;
        var membersFile = CreateMembersFileOption(
            "File listing the audience's members, one per line. Without it, the audience starts with no members.");
        var confirm = CreateConfirmOption("Confirm creating the audience without prompting.");
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("create", "Create a known-user audience")
        {
            name, membersFile, confirm, seller, baseUrl,
        };
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
            ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    var audienceName = result.GetValue(name)!;
                    var members = result.GetValue(membersFile) is { } path ? ReadMembersFile(path) : [];
                    if (!await PlaytestCommandFactory.ConfirmAsync(
                        IsConfirmed(result, confirm),
                        dependencies,
                        $"Creating the known-user audience '{XboxPartnerCommandSupport.ForTerminal(audienceName)}' "
                            + $"with {CountMembers(members.Count)} under seller {XboxPartnerCommandSupport.ForTerminal(sellerId)}.",
                        "Create this audience?",
                        "Audience creation canceled.",
                        cancellationToken))
                    {
                        return 1;
                    }

                    var recovery = XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result,
                        XboxPartnerCommandSupport.ForTerminal(audienceName) == audienceName
                            ? $"audience list --name {ShellQuoting.Quote(audienceName)}"
                            : "audience list",
                        sellerId);
                    return await WriteAsync(
                        result,
                        "Creating audience",
                        "created",
                        $"Audience creation was interrupted or its response could not be verified; it is unknown whether "
                            + $"the audience '{XboxPartnerCommandSupport.ForTerminal(audienceName)}' was created.",
                        $"Don't run the create again until you've checked. An audience with the same name is only a hint, "
                            + $"because names don't have to be unique. To look for it, run: {recovery}",
                        (correlationId, onSending) => client.CreateAudienceAsync(
                            url, sellerId, audienceName, members, correlationId, cancellationToken, onSending),
                        cancellationToken);
                },
                cancellationToken));
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Stage: CommandStage.Experimental,
            Scope: "Creates an audience under the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "The created audience as a JSON object as the service returns it, such as name, id, members, "
                + "dnaTags, sellerId, groupType, and groupUsage. Table output has ID, NAME, TYPE, MEMBERS, and MEMBER columns, "
                + "with a row per member, where MEMBERS is the number of members returned. An audience with no "
                + "members has one row, with 0 under MEMBERS and '-' under MEMBER. "
                + "Nothing is written to stdout when the create fails or its outcome is unknown.",
            SafetyDetails: "Creates an audience in Partner Center. The CLI can't delete audiences. Requires "
                + "--confirm (--yes, -y) or terminal confirmation. The request is sent once and never retried. "
                + "Agent approval is still required.",
            Examples:
            [
                "xbox audience create --name \"Nod Arena testers\" --members-file testers.txt --confirm",
                "xbox audience create --name \"Nod Arena testers\" --seller-id 1234567 --confirm",
            ],
            LearnMore:
            [
                MembersFileNote,
                StoredMembersNote,
                PermissionNote,
                ExitCodesNote,
                "After exit 3, don't create the audience again until you've checked whether it exists: stderr "
                    + "shows an 'xbox audience list' command to look for it.",
            ],
            Table: XboxPartnerCommandSupport.AudienceDetailTableView));
    }

    private static Command CreateUpdateCommand(
        Func<IConfiguration, XboxPartnerClient>? clientFactory,
        XboxPartnerJobDependencies dependencies)
    {
        var audienceId = CreateAudienceIdArgument();
        var name = CreateNameOption($"New name of the audience, up to {MaxNameLength} characters. Without it, the name is kept.");
        var membersFile = CreateMembersFileOption(
            "File listing the audience's complete new member list, one per line. An empty file removes every "
                + "member. Without it, the members are kept.");
        var confirm = CreateConfirmOption("Confirm changing the audience without prompting.");
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show the changes the update would make as JSON, without making them.",
        };
        var seller = XboxPartnerCommandSupport.CreateSellerIdOption();
        var baseUrl = XboxPartnerCommandSupport.CreateBaseUrlOption();
        var command = new Command("update", "Rename a known-user audience or replace its member list")
        {
            audienceId, name, membersFile, confirm, dryRun, seller, baseUrl,
        };
        command.Validators.Add(commandResult =>
        {
            if (commandResult.GetResult(name) is null && commandResult.GetResult(membersFile) is null)
            {
                commandResult.AddError("Pass --name, --members-file, or both: there's nothing to change.");
            }
        });
        command.SetAction((ParseResult result, CancellationToken cancellationToken) =>
        {
            var preview = result.GetValue(dryRun);
            if (preview && OutputOptions.From(result).IsTable)
            {
                Console.Error.WriteLine("--dry-run writes JSON only. Omit --output table, or pass --output json.");
                return Task.FromResult(1);
            }

            return ExecuteAsync(
                result, seller, baseUrl, clientFactory,
                async (client, url, sellerId) =>
                {
                    var id = result.GetValue(audienceId)!;
                    var newName = result.GetValue(name);
                    var members = result.GetValue(membersFile) is { } path ? ReadMembersFile(path) : null;
                    var confirmed = IsConfirmed(result, confirm);
                    var target = $"audience {XboxPartnerCommandSupport.ForTerminal(id)} under seller "
                        + XboxPartnerCommandSupport.ForTerminal(sellerId);
                    string summary;

                    // Only a dry run or a terminal prompt reads the audience, so members are shown
                    // only where they were asked for, never in a script's stderr.
                    if (preview || (!confirmed && dependencies.IsInteractive()))
                    {
                        Program.WriteVerbose(result.GetValue(Program.VerboseOption),
                            () => $"Reading {target} to preview the update.");
                        JsonObject current;
                        using (RequestProgress.Start("Reading audience"))
                        {
                            current = await client.GetAudienceAsync(url, sellerId, id, cancellationToken);
                        }

                        var changes = UpdatePreview.From(id, current, newName, members);
                        if (preview)
                        {
                            Write(changes.ToJson(), OutputOptions.From(result));
                            Console.Error.WriteLine($"Dry run: {target} wasn't changed.");
                            return 0;
                        }

                        summary = changes.Describe(target);
                    }
                    else
                    {
                        summary = DescribeUpdateWithoutReading(target, newName, members);
                    }

                    if (!await PlaytestCommandFactory.ConfirmAsync(
                        confirmed,
                        dependencies,
                        summary,
                        "Change this audience?",
                        "Audience update canceled.",
                        cancellationToken))
                    {
                        return 1;
                    }

                    var recovery = XboxPartnerCommandSupport.BuildRecoveryCommand(
                        result,
                        XboxPartnerCommandSupport.ForTerminal(id) == id
                            ? $"audience get {ShellQuoting.Quote(id)}"
                            : "audience list",
                        sellerId);
                    return await WriteAsync(
                        result,
                        "Updating audience",
                        "changed",
                        "Audience update was interrupted or its response could not be verified; it is unknown whether "
                            + $"audience {XboxPartnerCommandSupport.ForTerminal(id)} was changed.",
                        $"To check its stored name and members, run: {recovery}",
                        (correlationId, onSending) => client.UpdateAudienceAsync(
                            url, sellerId, id, newName, members, correlationId, cancellationToken, onSending),
                        cancellationToken);
                },
                cancellationToken);
        });
        return command.WithMetadata(new CommandMetadata(
            Safety: CommandSafety.RemoteMutation,
            Stage: CommandStage.Experimental,
            Scope: "One audience of the seller selected by --seller-id, XBOX_SELLER_ID, or 'context set'.",
            Output: "The updated audience as a JSON object as the service returns it, such as name, id, members, "
                + "dnaTags, sellerId, groupType, and groupUsage. Table output has ID, NAME, TYPE, MEMBERS, and MEMBER columns, "
                + "with a row per member, where MEMBERS is the number of members returned. An audience with no "
                + "members has one row, with 0 under MEMBERS and '-' under MEMBER. "
                + "Nothing is written to stdout when the update fails or its outcome is unknown. --dry-run writes "
                + "JSON only: the id, name (current and new) with --name, and members (current and new counts, "
                + "added, and removed) with --members-file.",
            SafetyDetails: "Renames the audience, replaces its complete member list in Partner Center, or both. With "
                + "--members-file, members not in the file are removed, and an empty file removes every member. "
                + "The type isn't changed. Requires --confirm (--yes, -y) or terminal confirmation. In a terminal "
                + "without --confirm, it reads the audience first and shows the rename and the members added and "
                + "removed before it asks. The request is sent once and never retried. Agent approval is still "
                + "required. --dry-run only reads the audience and never changes it.",
            Examples:
            [
                "xbox audience update 1152921504606846976 --members-file testers.txt --dry-run",
                "xbox audience update 1152921504606846976 --members-file testers.txt --confirm",
                "xbox audience update 1152921504606846976 --name \"Nod Arena beta testers\" --confirm",
                "xbox audience update 1152921504606846976 --members-file testers.txt --seller-id 1234567 --output table",
            ],
            LearnMore:
            [
                MembersFileNote,
                "Pass --name, --members-file, or both; what you leave out is kept. Only known-user audiences can be "
                    + "updated. To add or remove one member, save the list with 'xbox audience get --members-only', "
                    + "change it, and pass the whole list.",
                StoredMembersNote,
                PermissionNote,
                LastWriterNote,
                ExitCodesNote,
                "--dry-run exits 0 when it has shown the changes. The service doesn't check the update.",
            ],
            Table: XboxPartnerCommandSupport.AudienceDetailTableView));
    }

    private static string DescribeUpdateWithoutReading(string target, string? newName, IReadOnlyList<string>? members)
    {
        var changes = new List<string>();
        if (newName is not null)
        {
            changes.Add($"renaming it to '{XboxPartnerCommandSupport.ForTerminal(newName)}'");
        }

        if (members is not null)
        {
            changes.Add(members.Count == 0
                ? "removing every member"
                : $"replacing its members with {CountMembers(members.Count)}");
        }

        return $"Changing {target}: {string.Join(" and ", changes)}.";
    }

    /// <summary>What an update changes, compared with the audience as it was read.</summary>
    private sealed record UpdatePreview(
        string Id,
        string? CurrentName,
        string? NewName,
        int CurrentMemberCount,
        IReadOnlyList<string>? NewMembers,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Removed)
    {
        public static UpdatePreview From(
            string id, JsonObject current, string? newName, IReadOnlyList<string>? newMembers)
        {
            List<string> currentMembers = newMembers is null ? [] : MembersOf(current);
            var newSet = new HashSet<string>(newMembers ?? [], StringComparer.OrdinalIgnoreCase);
            var currentSet = new HashSet<string>(currentMembers, StringComparer.OrdinalIgnoreCase);
            return new UpdatePreview(
                id,
                current["name"]?.GetValue<string>(),
                newName,
                currentMembers.Count,
                newMembers,
                (newMembers ?? []).Where(member => !currentSet.Contains(member)).ToList(),
                currentMembers.Where(member => !newSet.Contains(member)).ToList());
        }

        public JsonObject ToJson()
        {
            var json = new JsonObject { ["id"] = Id };
            if (NewName is not null)
            {
                json["name"] = new JsonObject { ["current"] = CurrentName, ["new"] = NewName };
            }

            if (NewMembers is not null)
            {
                json["members"] = new JsonObject
                {
                    ["currentCount"] = CurrentMemberCount,
                    ["newCount"] = NewMembers.Count,
                    ["added"] = new JsonArray([.. Added.Select(member => (JsonNode?)member)]),
                    ["removed"] = new JsonArray([.. Removed.Select(member => (JsonNode?)member)]),
                };
            }

            return json;
        }

        public string Describe(string target)
        {
            var text = new StringBuilder($"Changing {target}:");
            if (NewName is not null)
            {
                var current = CurrentName is null ? "no name" : $"'{XboxPartnerCommandSupport.ForTerminal(CurrentName)}'";
                text.Append($"\n  Renaming it from {current} to '{XboxPartnerCommandSupport.ForTerminal(NewName)}'.");
            }

            if (NewMembers is not null)
            {
                text.Append($"\n  Replacing its {CountMembers(CurrentMemberCount)} with {CountMembers(NewMembers.Count)}: "
                    + $"{Added.Count} added, {Removed.Count} removed.");
                AppendMembers(text, '+', Added);
                AppendMembers(text, '-', Removed);
                if (Added.Count > MaxPromptedMembers || Removed.Count > MaxPromptedMembers)
                {
                    text.Append("\n  Run with --dry-run to list every change.");
                }
            }

            return text.ToString();
        }

        private static void AppendMembers(StringBuilder text, char sign, IReadOnlyList<string> members)
        {
            foreach (var member in members.Take(MaxPromptedMembers))
            {
                text.Append($"\n    {sign} {XboxPartnerCommandSupport.ForTerminal(member)}");
            }

            if (members.Count > MaxPromptedMembers)
            {
                text.Append($"\n    {sign} ...and {members.Count - MaxPromptedMembers} more");
            }
        }
    }

    /// <summary>
    /// Sends one audience write and writes its result. A failure the service reports before the write
    /// reached Partner Center is a definite failure; any other failure after sending leaves the outcome unknown.
    /// </summary>
    private static async Task<int> WriteAsync(
        ParseResult result,
        string progress,
        string done,
        string unknownOutcome,
        string recovery,
        Func<string, Action, Task<JsonObject>> write,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString();
        Program.WriteVerbose(result.GetValue(Program.VerboseOption),
            () => $"{progress} with correlation ID {correlationId}.");
        var sent = false;
        JsonObject audience;
        try
        {
            using (RequestProgress.Start(progress))
            {
                audience = await write(correlationId, () => sent = true);
            }
        }
        catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Canceled before the request was sent, so the audience wasn't {done}.");
            return 1;
        }
        catch (XboxPartnerApiException exception) when (DescribeNotWritten(exception, done) is { } diagnosis)
        {
            XboxPartnerCommandSupport.WriteCorrelationId(exception.CorrelationId ?? correlationId, Console.Error);
            XboxPartnerCommandSupport.WriteGatewayError(exception, result, diagnosis);
            return 1;
        }
        catch (Exception exception) when (sent && XboxPartnerCommandSupport.IsUncertainSubmission(exception))
        {
            Console.Error.WriteLine(unknownOutcome);
            Console.Error.WriteLine(recovery);
            WriteUnknownOutcomeError(exception, result, cancellationToken, correlationId);
            return XboxPartnerCommandSupport.OutcomeUnknownExitCode;
        }

        Write(ForOutput(audience, "memberCount"), OutputOptions.From(result));
        return 0;
    }

    /// <summary>
    /// The explanation of a failure the service reports only when the write didn't reach Partner Center
    /// or was refused, or null for any other failure.
    /// </summary>
    private static string? DescribeNotWritten(XboxPartnerApiException exception, string done) =>
        ((int)exception.StatusCode, TestAccountCommandFactory.ReadProblemString(exception.ResponseBody, "code")) switch
        {
            (502, "downstream_request_rejected") =>
                $"Partner Center rejected the request, so the audience wasn't {done}.",
            (502, "audience_update_preflight_failed") =>
                $"The audience couldn't be checked before the change, so it wasn't {done}. Try again later.",
            (503, "audience_write_not_available") =>
                $"Audience changes aren't available right now, so the audience wasn't {done}. Try again later.",
            (504, "audience_write_not_sent") =>
                $"The request timed out before it reached Partner Center, so the audience wasn't {done}. Try again.",
            (403, "audience_write_forbidden" or "playtest_access_not_enabled") =>
                $"Playtests aren't enabled for this seller, or audience changes aren't available for it, so the "
                + $"audience wasn't {done}.",
            (403, "audience_permission_denied") =>
                $"You don't have Partner Center's customer groups Write permission for this seller, so the audience "
                + $"wasn't {done}. Ask a manager of the Partner Center account for access, or pass --seller-id if you "
                + "meant another seller.",
            (409, "audience_write_rejected") when done == "changed" =>
                "Partner Center rejected the change, so the audience wasn't changed. The audience may not be a "
                + "known-user audience (check its groupType with 'xbox audience get'), or the change conflicted with "
                + "another one. Rerun with --verbose to see the service's reason.",
            (409, "audience_write_rejected") =>
                $"Partner Center rejected the request, so the audience wasn't {done}.",
            _ => null,
        };

    // Unlike WriteRequestError, never writes an unreadable response, which can hold the audience's members.
    private static void WriteUnknownOutcomeError(
        Exception exception, ParseResult result, CancellationToken cancellationToken, string correlationId)
    {
        switch (exception)
        {
            case XboxPartnerApiException api:
                XboxPartnerCommandSupport.WriteCorrelationId(api.CorrelationId ?? correlationId, Console.Error);
                XboxPartnerCommandSupport.WriteGatewayError(api, result);
                break;
            case XboxPartnerResponseException response:
                XboxPartnerCommandSupport.WriteCorrelationId(response.CorrelationId ?? correlationId, Console.Error);
                Console.Error.WriteLine($"The response could not be read: {TerminalText.SingleLine(response.Message)}");
                break;
            default:
                XboxPartnerCommandSupport.WriteRequestError(exception, result, cancellationToken, correlationId);
                break;
        }
    }

    private static Argument<string> CreateAudienceIdArgument()
    {
        var audienceId = new Argument<string>("audience-id")
        {
            Description = "Audience ID, exactly as 'xbox audience list' returns it, such as 1152921504606846976.",
        };
        audienceId.Validators.Add(argument =>
        {
            if (argument.Tokens.Count == 0)
            {
                return;
            }

            var value = argument.Tokens[0].Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                argument.AddError("Invalid audience ID: it can't be empty.");
            }
            else if (XboxPartnerClient.IsDotSegment(value))
            {
                argument.AddError($"Invalid audience ID '{value}': it can't be '.' or '..'.");
            }
        });
        return audienceId;
    }

    private static Option<string> CreateNameOption(string description)
    {
        var option = new Option<string>("--name") { HelpName = "text", Description = description };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count == 0)
            {
                return;
            }

            var value = result.Tokens[0].Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                result.AddError("Invalid --name: it can't be empty.");
            }
            else if (value.Length > MaxNameLength)
            {
                result.AddError($"Invalid --name: it can't be longer than {MaxNameLength} characters.");
            }
        });
        return option;
    }

    private static Option<string?> CreateMembersFileOption(string description)
    {
        var option = new Option<string?>("--members-file") { HelpName = "path", Description = description };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0 && string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                result.AddError("Invalid --members-file: the path can't be empty.");
            }
        });
        return option;
    }

    private static Option<bool> CreateConfirmOption(string description) =>
        new("--confirm", "--yes", "-y") { Description = description };

    private static bool IsConfirmed(ParseResult result, Option<bool> confirm) =>
        result.GetResult(confirm) is { Implicit: false }
            ? result.GetValue(confirm)
            : result.GetValue(Program.ConfirmOption);

    private static string CountMembers(int count) => count == 1 ? "1 member" : $"{count} members";

    /// <summary>
    /// Reads a members file: strict UTF-8, or UTF-16 with a byte order mark, one member per line, each
    /// trimmed, with blank lines and repeats (ignoring case) dropped and case kept. Lines that aren't plain
    /// email addresses are refused, before the member limit is checked. Errors name the file and line
    /// numbers, never its contents.
    /// </summary>
    internal static IReadOnlyList<string> ReadMembersFile(string path)
    {
        var shown = TerminalText.SingleLine(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw new IOException($"The members file '{shown}' could not be read: {TerminalText.SingleLine(exception.Message)}", exception);
        }

        // Windows PowerShell 5.1 redirects output to a file as UTF-16 with a byte order mark.
        Encoding encoding = bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })
            ? new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true)
            : bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })
                ? new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        var preamble = encoding.Preamble;
        var start = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        string text;
        try
        {
            text = encoding.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The members file '{shown}' isn't valid UTF-8 or UTF-16 text.", exception);
        }

        var members = new List<string>();
        var problems = new List<string>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var member = lines[index].Trim();
            if (member.Length == 0)
            {
                continue;
            }

            var lineNumber = index + 1;
            members.Add(member);
            if (member.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.Format))
            {
                problems.Add($"line {lineNumber}: has an invisible character");
            }
            // MailAddress parses "Name <address>" too, so the address must be the whole line.
            else if (!MailAddress.TryCreate(member, out var address) || address.Address != member)
            {
                problems.Add($"line {lineNumber}: not a plain email address");
            }
        }

        if (problems.Count > 0)
        {
            var message = new List<string> { $"The members file '{shown}' has lines to fix, so nothing was sent:" };
            message.AddRange(problems.Take(MaxListedProblems).Select(problem => "  " + problem));
            if (problems.Count > MaxListedProblems)
            {
                message.Add($"  and {problems.Count - MaxListedProblems} more");
            }

            throw new InvalidDataException(string.Join(Environment.NewLine, message));
        }

        members = members.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (members.Count > MaxMembers)
        {
            throw new InvalidDataException(
                $"The members file '{shown}' lists {members.Count} members; an audience can have at most {MaxMembers}.");
        }

        return members;
    }
}
