// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class CompletionCommandTests
{
    [Theory]
    [InlineData("bash", "complete -o default -F _xbox_complete xbox")]
    [InlineData("zsh", "compdef _xbox xbox")]
    [InlineData("fish", "complete -c xbox")]
    [InlineData("powershell", "Register-ArgumentCompleter -Native -CommandName xbox")]
    public async Task Completion_PrintsAScriptThatAsksTheCliForCandidates(string shell, string registration)
    {
        var (exitCode, stdout, stderr) = await MainAsync(["completion", shell]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(registration, stdout, StringComparison.Ordinal);
        Assert.Contains("[suggest:", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pla", "playfab", "playtest")]
    [InlineData("playtest ", "list", "--output")]
    [InlineData("--output ", "json-compact", "table")]
    [InlineData("context set ", "sandbox-id", "seller-id")]
    [InlineData("completion ", "bash", "powershell")]
    [InlineData("xbl config download --scope ", "sandbox", "account")]
    [InlineData("xbl config download --view ", "draft", "published")]
    [InlineData("game create --supported-device ", "xbox", "desktop")]
    public async Task CompletionQuery_ListsCandidatesWithoutPreparingAnything(string line, string first, string second)
    {
        // A service-principal mode with none of its settings fails anything that prepares sign-in.
        using var automation = TestEnvironment.UseAuthenticationMode("clientsecret");

        var (exitCode, stdout, stderr) = await MainAsync([$"[suggest:{line.Length}]", line]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        var candidates = stdout.Split(Environment.NewLine);
        Assert.Contains(first, candidates);
        Assert.Contains(second, candidates);
    }

    [Fact]
    public async Task CompletionQuery_ForAContextValue_OffersNoKeys()
    {
        var (exitCode, stdout, _) = await MainAsync(["[suggest:26]", "context set sandbox-id sa"]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("sandbox-id", stdout.Split(Environment.NewLine));
    }

    [Theory]
    [InlineData("xbl", "config", "download", "--view", "draft", "--sandbox-id")]
    [InlineData("xbl", "config", "save", "--sandbox-id")]
    public async Task SandboxIdWithoutAValue_IsAUsageError(params string[] args)
    {
        var (exitCode, stdout, stderr) = await MainAsync(args);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.StartsWith("Required argument missing for option: '--sandbox-id'.", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> MainAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exitCode = await Program.Main(args);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
