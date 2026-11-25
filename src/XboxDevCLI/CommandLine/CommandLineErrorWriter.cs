// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.Output;

namespace XboxDevCLI.CommandLine;

internal static class CommandLineErrorWriter
{
    internal static bool TryWriteUnknownCommand(
        ParseResult parseResult,
        RootCommand rootCommand,
        string cliName,
        TextWriter errorWriter,
        bool requireParseError = true)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);
        ArgumentNullException.ThrowIfNull(errorWriter);

        // Each unmatched token is one error. A leaf with any other error, such as a missing
        // required option, or with an unknown option, is left to the parser, which reports every
        // error.
        if (requireParseError &&
            (parseResult.Errors.Count == 0 ||
             (parseResult.CommandResult.Command.Subcommands.Count == 0 &&
              (parseResult.Errors.Count != parseResult.UnmatchedTokens.Count ||
               parseResult.UnmatchedTokens.Any(IsOption)))))
        {
            return false;
        }

        var unknownCommand = FindUnknownCommand(parseResult.UnmatchedTokens);
        if (unknownCommand is null)
        {
            return false;
        }

        WriteUnknownCommand(
            rootCommand,
            parseResult.CommandResult.Command,
            unknownCommand,
            cliName,
            errorWriter);
        return true;
    }

    /// <summary>
    /// Writes the contextual "unknown command" error for <paramref name="unknownCommand"/> under
    /// <paramref name="command"/>, including a usage line derived from that command's real depth.
    /// </summary>
    internal static void WriteUnknownCommand(
        RootCommand rootCommand,
        Command command,
        string unknownCommand,
        string cliName,
        TextWriter errorWriter)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(unknownCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);
        ArgumentNullException.ThrowIfNull(errorWriter);

        var commandPath = GetCommandPath(rootCommand, command, cliName);

        errorWriter.WriteLine(
            $"unknown command {Quote(unknownCommand)} for {Quote(commandPath)}");
        errorWriter.WriteLine();
        errorWriter.WriteLine($"Usage:  {CommandHelpWriter.BuildUsage(command, commandPath)}");

        var visibleSubcommands = command.Subcommands
            .Where(subcommand => !subcommand.Hidden)
            .OrderBy(subcommand => subcommand.Name, StringComparer.Ordinal)
            .ToArray();
        if (visibleSubcommands.Length > 0)
        {
            errorWriter.WriteLine();
            errorWriter.WriteLine("Available commands:");

            foreach (var subcommand in visibleSubcommands)
            {
                errorWriter.WriteLine($"  {subcommand.Name}");
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="parseResult"/>'s parse and validation errors, and returns exit code 1.
    /// Everything goes to <paramref name="errorWriter"/>, so stdout carries nothing on a failure:
    /// each error, the likely option for a mistyped one, the command's usage line, and how to see
    /// its Help.
    /// </summary>
    internal static int WriteParseErrors(
        ParseResult parseResult,
        RootCommand rootCommand,
        string cliName,
        TextWriter errorWriter)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliName);
        ArgumentNullException.ThrowIfNull(errorWriter);

        var command = parseResult.CommandResult.Command;
        foreach (var error in parseResult.Errors)
        {
            errorWriter.WriteLine(TerminalText.MultiLine(error.Message));
        }

        foreach (var token in parseResult.UnmatchedTokens)
        {
            if (SuggestOption(command, token) is { } suggestion)
            {
                errorWriter.WriteLine($"Did you mean {suggestion}?");
            }
        }

        errorWriter.WriteLine();
        WriteUsage(rootCommand, command, cliName, errorWriter);
        WriteHelpHint(rootCommand, command, cliName, errorWriter);
        return 1;
    }

    /// <summary>
    /// The option <paramref name="token"/> was probably meant to be: the closest of the options
    /// <paramref name="command"/> takes, within two edits, or <c>null</c>.
    /// </summary>
    private static string? SuggestOption(Command command, string token)
    {
        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        var name = token.Split('=', 2)[0];
        var options = new List<Option>(command.Options);
        for (var parent = command.Parents.OfType<Command>().FirstOrDefault();
             parent is not null;
             parent = parent.Parents.OfType<Command>().FirstOrDefault())
        {
            options.AddRange(parent.Options.Where(option => option.Recursive));
        }

        return options
            .Where(option => !option.Hidden)
            .Select(option => (option.Name, Distance: EditDistance(name, option.Name)))
            .Where(candidate => candidate.Distance is > 0 and <= 2)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();
    }

    private static int EditDistance(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        for (var i = 1; i <= first.Length; i++)
        {
            var current = new int[second.Length + 1];
            current[0] = i;
            for (var j = 1; j <= second.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (first[i - 1] == second[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[second.Length];
    }

    /// <summary>Writes the usage line for <paramref name="command"/> at its real depth.</summary>
    internal static void WriteUsage(
        RootCommand rootCommand,
        Command command,
        string cliName,
        TextWriter errorWriter) =>
        errorWriter.WriteLine(
            $"Usage:  {CommandHelpWriter.BuildUsage(command, GetCommandPath(rootCommand, command, cliName))}");

    /// <summary>Writes the <c>help</c> command that shows <paramref name="command"/>'s Help.</summary>
    internal static void WriteHelpHint(
        RootCommand rootCommand,
        Command command,
        string cliName,
        TextWriter errorWriter) =>
        errorWriter.WriteLine(
            $"Run '{string.Join(' ', [cliName, "help", .. CommandPath.Resolve(rootCommand, command)])}' for more.");

    private static string GetCommandPath(RootCommand rootCommand, Command command, string cliName)
    {
        var segments = CommandPath.Resolve(rootCommand, command);
        return segments.Count == 0
            ? cliName
            : $"{cliName} {string.Join(' ', segments)}";
    }

    private static string? FindUnknownCommand(IEnumerable<string> unmatchedTokens)
    {
        var previousWasOption = false;
        foreach (var token in unmatchedTokens)
        {
            if (IsOption(token))
            {
                previousWasOption = true;
                continue;
            }

            if (previousWasOption)
            {
                previousWasOption = false;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static bool IsOption(string token) =>
        !string.IsNullOrWhiteSpace(token) &&
        (token.StartsWith("-", StringComparison.Ordinal) ||
         token.StartsWith("/", StringComparison.Ordinal));

    private static string Quote(string value) =>
        $"\"{value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)}\"";
}
