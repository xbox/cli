// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace XboxDevCLI.Tests;

/// <summary>
/// Every <c>xbox</c> command line in the docs names commands and options the CLI has, so a
/// rename that misses a page fails here instead of in a reader's terminal. It reads the docs
/// at the repository root and under <c>docs/</c>, in code spans and in code blocks, and checks
/// only the names: placeholders like <c>&lt;id&gt;</c> stand for any value.
/// </summary>
public sealed partial class DocumentedCommandsTests
{
    [Fact]
    public void EveryDocumentedCommandAndOptionExists()
    {
        var root = Program.BuildRootCommand();
        var problems = DocumentedCommands()
            .Select(documented => (documented, Problem: Problem(root, documented.Text)))
            .Where(result => result.Problem is not null)
            .Select(result => $"{result.documented.File}:{result.documented.Line}: `{result.documented.Text}` {result.Problem}")
            .ToList();

        Assert.True(problems.Count == 0, "The docs name commands or options the CLI doesn't have:\n" + string.Join('\n', problems));
    }

    [Theory]
    [InlineData("XBOX_PLAYFAB_TITLE_ID=DEF34 xbox contxt get")]
    [InlineData(".\\xbox contxt get")]
    [InlineData("./xbox contxt get")]
    [InlineData("& \"$cliDir\\xbox.exe\" contxt get")]
    public void ChecksCommandLinesThatSetVariablesOrNameAPath(string commandLine) =>
        Assert.Equal("has unknown `contxt get`.", Problem(Program.BuildRootCommand(), commandLine));

    [Theory]
    [InlineData("'{\"Keys\":[\"MyKey\"]}' | xbox contxt get")]
    [InlineData("echo '{\"Keys\":[\"MyKey\"]}' | xbox contxt get")]
    public void ChecksCommandsThatInputIsPipedInto(string commandLine) =>
        Assert.Equal("has unknown `contxt get`.", Problem(Program.BuildRootCommand(), commandLine));

    [Theory]
    [InlineData("xbox-win-x64.zip")]
    [InlineData("xbox.xml")]
    [InlineData("unzip xbox-linux-x64.zip")]
    public void IgnoresTextThatDoesntRunTheCli(string text) =>
        Assert.Null(CommandLine(text));

    [Theory]
    [InlineData("xbox context get", null)]
    [InlineData("xbox context set seller-id <id>", null)]
    [InlineData("xbox --playfab-title-id <TITLE_ID> playfab api /Admin/GetTitleData", null)]
    [InlineData("xbox game list --output table --verbose", null)]
    [InlineData("xbox playfab <service> --help", null)]
    [InlineData("xbox xbl config download --scope account --view draft --dest out --document a.xml b.xml", null)]
    [InlineData("xbox playfab mps build get --body-file -", null)]
    [InlineData("xbox playfab api -- /Admin/GetTitleData", null)]
    [InlineData("xbox playfab api -- --not-an-option", null)]
    [InlineData("xbox --playfab-title-id <TITLE_ID> playfab contxt", "has unknown `contxt`.")]
    [InlineData("xbox context set seller-id <id> --format table", "has unknown `--format table`.")]
    [InlineData("xbox playfab api /Admin/GetTitleData --body-file=<file> --format table", "has unknown `--format table`.")]
    [InlineData("xbox playfab api /Admin/GetTitleData < body.json", null)]
    [InlineData("xbox game list > games.json", null)]
    [InlineData("xbox context get; echo done", null)]
    [InlineData("xbox game list 2>/dev/null", null)]
    [InlineData("xbox game list --output table # a comment", null)]
    [InlineData("xbox game list --output '#table' --format table", "has unknown `--format table`.")]
    [InlineData("xbox game list --format=<format>", "has unknown `--format=<format>`.")]
    [InlineData("xbox help game list --format=<format>", "has unknown `--format=<format>`.")]
    [InlineData("xbox help", null)]
    [InlineData("xbox help [command...]", null)]
    [InlineData("xbox help playfab economy inventory collection list", null)]
    [InlineData("xbox help playfab contxt", "has unknown `contxt`.")]
    [InlineData("xbox contxt get", "has unknown `contxt get`.")]
    [InlineData("xbox game list --format table", "has unknown `--format table`.")]
    public void ChecksCommandsAndOptionsButNotValues(string commandLine, string? problem) =>
        Assert.Equal(problem, Problem(Program.BuildRootCommand(), commandLine));

    [Theory]
    [InlineData("xbox game list \\")]
    [InlineData("xbox game list `")]
    [InlineData("jq -n '{}' \\")]
    [InlineData("xbox game list | \\")]
    [InlineData("xbox game list > games.json \\")]
    public void FailsOnLineContinuations(string line) =>
        Assert.Equal("continues on the next line. Keep each command on one line, so it works in every shell.", Problem(Program.BuildRootCommand(), line));

    /// <summary>
    /// What's wrong with a documented line: that it continues on the next line, or the tokens
    /// of its <c>xbox</c> command the CLI's parser doesn't recognize, other than placeholders.
    /// <see langword="null"/> if nothing is. <c>xbox help</c> is checked as the command it
    /// names, with <c>--help</c>.
    /// </summary>
    private static string? Problem(Command root, string text)
    {
        if (ContinuesOnNextLine(text))
        {
            return "continues on the next line. Keep each command on one line, so it works in every shell.";
        }

        if (CommandLine(text) is not { } tokens)
        {
            return null;
        }

        var args = tokens.Skip(1).ToList();
        if (args is ["help", ..])
        {
            args = [.. args.Skip(1).Where(token => !IsPlaceholder(token)), "--help"];
        }

        var unmatched = root.Parse(args, Program.NoResponseFileConfig).UnmatchedTokens.Where(token => !IsPlaceholder(token)).ToList();
        return unmatched.Count == 0 ? null : $"has unknown `{string.Join(' ', unmatched)}`.";
    }

    // An option with a placeholder value, like `--format=<format>`, is still an option to check.
    private static bool IsPlaceholder(string token) =>
        !token.StartsWith('-') && (token.IndexOfAny(['<', '[', '{', '$', '*']) >= 0 || token.Contains("...", StringComparison.Ordinal));

    // A line ending in a space and `\` or a backtick continues in bash or PowerShell, but not in the other.
    private static bool ContinuesOnNextLine(string text) =>
        text.TrimEnd() is var trimmed && (trimmed.EndsWith(" \\", StringComparison.Ordinal) || trimmed.EndsWith(" `", StringComparison.Ordinal));

    /// <summary>
    /// Every <c>xbox</c> command line in the docs, and every line in a code block that continues
    /// on the next line, with where it is.
    /// </summary>
    private static IEnumerable<(string File, int Line, string Text)> DocumentedCommands()
    {
        var repository = RepositoryRoot();

        // The development repository keeps the user docs in docs/public, and the released source
        // keeps them in docs.
        var docs = Path.Combine(repository, "docs", "public");
        if (!Directory.Exists(docs))
        {
            docs = Path.Combine(repository, "docs");
        }

        var files = Directory.GetFiles(repository, "*.md").Concat(Directory.GetFiles(docs, "*.md", SearchOption.AllDirectories));
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(repository, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            string? fence = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (Fence().Match(trimmed) is { Success: true } opening && (fence is null || trimmed.StartsWith(fence, StringComparison.Ordinal)))
                {
                    fence = fence is null ? opening.Value : null;
                    continue;
                }

                if (fence is not null)
                {
                    var command = Prompt().Replace(trimmed, "");
                    if (ContinuesOnNextLine(command) || CommandLine(command) is not null)
                    {
                        yield return (relative, i + 1, command);
                    }

                    continue;
                }

                foreach (Match span in CodeSpan().Matches(line))
                {
                    if (CommandLine(span.Groups[1].Value) is not null)
                    {
                        yield return (relative, i + 1, span.Groups[1].Value);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The words of a command line that runs <c>xbox</c>, starting with <c>xbox</c>, or
    /// <see langword="null"/> if it doesn't run it. Environment variables set before it, a path
    /// to it, like <c>./xbox</c> or <c>.\xbox.exe</c>, and a command piped into it are left out.
    /// </summary>
    private static List<string>? CommandLine(string text)
    {
        text = PipedIntoXbox().Replace(text, "");
        return Invocation().Match(text) is { Success: true } invocation ? ["xbox", .. Tokenize(text[invocation.Length..])] : null;
    }

    /// <summary>Splits a shell command line into words, honoring quotes, up to an unquoted pipe, redirection, separator, or comment.</summary>
    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var started = false;
        char? quote = null;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var character = commandLine[i];
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    token.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                started = true;
            }
            else if (character == '<' && Placeholder().Match(commandLine, i) is { Success: true } placeholder)
            {
                // A placeholder, like `<id>`, not a redirection.
                token.Append(placeholder.Value);
                started = true;
                i += placeholder.Length - 1;
            }
            else if (character is ';' or '|' or '&' or '<' or '>' || (character == '#' && !started))
            {
                // A file descriptor before a redirection, like `2>`, isn't a word.
                if (character == '>' && started && token.ToString().All(char.IsAsciiDigit))
                {
                    started = false;
                }

                break;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (started)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    started = false;
                }
            }
            else
            {
                token.Append(character);
                started = true;
            }
        }

        if (started)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XboxDevCLI.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root from the test directory.");
        return directory!.FullName;
    }

    [GeneratedRegex("""^(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*(?:&\s*)?(?:"[^"]*[\\/]|'[^']*[\\/]|[^\s"']*[\\/])?xbox(?:\.exe)?["']?(?:\s+|$)""")]
    private static partial Regex Invocation();

    [GeneratedRegex("""^(?:[^'"|]|'[^']*'|"[^"]*")*\|\s*(?=(?:\S*[\\/])?xbox\b)""")]
    private static partial Regex PipedIntoXbox();

    [GeneratedRegex(@"\G<[^\s<>]+>")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^(```+|~~~+)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^(\$|>|PS>|PS [^>]*>)\s+")]
    private static partial Regex Prompt();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpan();
}
