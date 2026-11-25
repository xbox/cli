// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using XboxDevCLI.CommandLine;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class CommandParsingTests
{
    [Fact]
    public void RootCommand_HasExpectedAuthenticationHierarchy()
    {
        var rootCommand = Program.BuildRootCommand();
        var authCommand = Assert.Single(
            rootCommand.Subcommands,
            command => command.Name == "auth");

        Assert.Contains(authCommand.Subcommands, command => command.Name == "login");
        Assert.Contains(authCommand.Subcommands, command => command.Name == "logout");
        Assert.Contains(authCommand.Subcommands, command => command.Name == "status");
        Assert.DoesNotContain(
            rootCommand.Subcommands,
            command => command.Name is "login" or "logout" or "status");
    }

    /// <summary>
    /// A command's validators run before the parser checks its options, and GetValue throws for an
    /// option given without its value, or twice when it takes one, and for a flag given twice with
    /// values. Parsing must report those instead, whatever comes first: nothing, one other option,
    /// or a Help example, which reaches the branches a validator takes only for a valid value, such
    /// as --dest with a .pfx file.
    /// </summary>
    [Fact]
    public void Parse_NeverThrows_ForAMissingOrRepeatedOptionValue()
    {
        var root = Program.BuildRootCommand();
        var failures = new List<string>();

        void Check(string[] args)
        {
            try
            {
                root.Parse(args, Program.NoResponseFileConfig);
            }
            catch (Exception exception)
            {
                failures.Add($"xbox {string.Join(' ', args)}: {exception.Message}");
            }
        }

        void Walk(Command command, string[] path, Option[] inherited)
        {
            Option[] options = [.. inherited, .. command.Options];
            Option[] valued = [.. options.Where(option => option.Arity.MinimumNumberOfValues > 0)];
            string[][] before =
            [
                path,
                .. options.Except(valued).Select(flag => (string[])[.. path, flag.Name]),
                .. valued.SelectMany(option => new string[][] { [.. path, option.Name, "-"], [.. path, option.Name, "1"] }),
                .. (CommandMetadataRegistry.GetMetadata(command).Examples ?? [])
                    .Select(example => CommandSurfaceTests.SplitHelpExample(example)[1..]),
            ];
            foreach (var option in valued)
            {
                foreach (var args in before)
                {
                    Check([.. args, option.Name]);
                    if (option.Arity.MaximumNumberOfValues == 1)
                    {
                        Check([.. args, option.Name, "a", option.Name, "b"]);
                    }
                }
            }

            foreach (var flag in options.Except(valued))
            {
                foreach (var args in before)
                {
                    Check([.. args, flag.Name, "true", flag.Name, "false"]);
                }
            }

            foreach (var subcommand in command.Subcommands)
            {
                Walk(
                    subcommand,
                    [.. path, subcommand.Name],
                    [.. inherited, .. command.Options.Where(option => option.Recursive)]);
            }
        }

        Walk(root, [], []);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task AuthLogin_Command_ParsesSuccessfully()
    {
        var loginCommand = new Command("login", "Sign in");
        var wasInvoked = false;
        loginCommand.SetAction((ParseResult _) => { wasInvoked = true; return 0; });

        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(loginCommand);
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("auth login").InvokeAsync();

        Assert.True(wasInvoked);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task AuthStatus_Command_ParsesSuccessfully()
    {
        var statusCommand = new Command("status", "Show status");
        var wasInvoked = false;
        statusCommand.SetAction((ParseResult _) => { wasInvoked = true; return 0; });

        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(statusCommand);
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("auth status").InvokeAsync();

        Assert.True(wasInvoked);
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData("feedback")]
    [InlineData("report-issue")]
    public async Task Feedback_Command_PrintsIssueUrlWithoutOpeningBrowser(string command)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var result = await Program.Main(["--disable-telemetry", command, "--no-browser"]);

            Assert.Equal(0, result);
            Assert.Contains(ProjectLinks.NewIssue, writer.ToString());
            Assert.Contains("environment=", writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task UnknownCommand_ReturnsNonZero()
    {
        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(new Command("login"));
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("nonexistent").InvokeAsync();

        Assert.NotEqual(0, result);
    }

    [Fact]
    public async Task GlobalOption_ClientId_IsAccepted()
    {
        var clientIdOption = new Option<string?>("--client-id") { Recursive = true };
        var loginCommand = new Command("login", "Sign in");
        var wasInvoked = false;
        loginCommand.SetAction((ParseResult _) => { wasInvoked = true; return 0; });

        var rootCommand = new RootCommand();
        var authCommand = new Command("auth");
        authCommand.Subcommands.Add(loginCommand);
        rootCommand.Options.Add(clientIdOption);
        rootCommand.Subcommands.Add(authCommand);

        var result = await rootCommand.Parse("--client-id some-id auth login").InvokeAsync();

        Assert.True(wasInvoked);
        Assert.Equal(0, result);
    }
}
