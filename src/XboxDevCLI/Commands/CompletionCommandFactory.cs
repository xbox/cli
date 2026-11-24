// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using XboxDevCLI.CommandLine;

namespace XboxDevCLI.Commands;

/// <summary>
/// Prints a shell completion script. Each script asks the CLI for candidates with
/// System.CommandLine's <c>[suggest]</c> directive, which answers from the command tree, so
/// completion stays offline and never signs in.
/// </summary>
internal static class CompletionCommandFactory
{
    internal static readonly IReadOnlyDictionary<string, string> Scripts = new Dictionary<string, string>
    {
        ["bash"] = "xbox.bash",
        ["zsh"] = "xbox.zsh",
        ["fish"] = "xbox.fish",
        ["powershell"] = "xbox.ps1",
    };

    internal static Command Create()
    {
        var shellArgument = new Argument<string>("shell")
        {
            Description = "The shell to print a script for: bash, zsh, fish, or powershell.",
        };
        shellArgument.AcceptOnlyFromAmong([.. Scripts.Keys]);

        var command = new Command("completion", "Print a shell completion script");
        command.Arguments.Add(shellArgument);
        command.WithMetadata(new CommandMetadata(
            Category: HelpCategory.Support,
            Safety: CommandSafety.ReadOnly,
            Scope: "Your shell. Makes no network request and needs no sign-in.",
            Output:
                "The script on stdout. It completes commands, options, and values from a fixed "
                + "list, such as --output formats and context keys. Does not "
                + "support --output table.",
            Examples:
            [
                "xbox completion bash",
                "xbox completion zsh",
                "xbox completion fish",
                "xbox completion powershell",
            ],
            LearnMore:
            [
                "To enable it, add the line for your shell to its startup file:",
                "  Bash, in ~/.bashrc: eval \"$(xbox completion bash)\"",
                "  Zsh, in ~/.zshrc after compinit: source <(xbox completion zsh)",
                "  fish, in config.fish: xbox completion fish | source",
                "  PowerShell, in $PROFILE: xbox completion powershell | Out-String | Invoke-Expression",
            ]));
        command.SetAction(parseResult =>
        {
            Console.Out.Write(Read(Scripts[parseResult.GetRequiredValue(shellArgument)]));
            return 0;
        });

        return command.Requires(CommandRequirement.None).Calls(ServiceEndpoints.None);
    }

    private static string Read(string fileName)
    {
        var name = $"XboxDevCLI.Resources.Completion.{fileName}";
        using var stream = typeof(CompletionCommandFactory).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded completion script '{name}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
