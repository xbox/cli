// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XboxDevCLI.E2ETest;

/// <remarks>
/// No test lists audiences: listing a seller with many audiences can time out.
/// </remarks>
public class AudienceTests
{
    private static E2ESettings Settings => E2ESettings.Current;

    [LiveFact]
    public async Task Get_ReturnsTheAudienceAndItsMembers()
    {
        CliResult result = await Cli.RunForSellerAsync("audience", "get", Settings.AudienceId);

        JsonElement audience = result.Json();
        result.Check(audience.Is("id", Settings.AudienceId), $"Expected audience {Settings.AudienceId}.");
        result.Check(audience.Items("members").Length > 0, "Expected its members.");
    }

    // The file lists the audience's own members, so even a dry run that wrote would change nothing.
    [LiveFact]
    public async Task UpdateDryRun_ComparesTheMembersFileWithTheAudience()
    {
        string[] members = [.. (await Cli.RunForSellerAsync("audience", "get", Settings.AudienceId)).Json()
            .Items("members").Select(member => member.GetString()!)];
        DirectoryInfo directory = Directory.CreateTempSubdirectory("xbox-e2e-");
        try
        {
            string file = Path.Combine(directory.FullName, "members.txt");
            await File.WriteAllLinesAsync(file, members);

            CliResult result = await Cli.RunForSellerAsync(
                "audience", "update", Settings.AudienceId, "--members-file", file, "--dry-run");

            JsonElement changes = result.Json().Field("members");
            result.Check(
                changes.Number("currentCount") == members.Length && changes.Number("newCount") == members.Length,
                $"Expected {members.Length} current and new members.");
            result.Check(
                changes.Items("added").Length == 0 && changes.Items("removed").Length == 0,
                "Expected no members added or removed.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [LiveFact]
    public async Task GetMembersOnly_WritesTheSameMembersAsJson()
    {
        CliResult json = await Cli.RunForSellerAsync("audience", "get", Settings.AudienceId);
        CliResult text = await Cli.RunAsync("audience", "get", Settings.AudienceId, "--members-only", "--seller-id", Settings.SellerId);
        text.Check(text.ExitCode == 0, "Expected exit code 0.");

        string[] expected = json.Json().Items("members").Select(member => member.GetString()!).ToArray();
        string[] lines = text.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        text.Check(lines.SequenceEqual(expected), "Expected one line per member, matching the JSON members.");
    }
}
