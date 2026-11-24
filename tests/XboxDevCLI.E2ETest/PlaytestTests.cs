// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace XboxDevCLI.E2ETest;

public class PlaytestTests(ITestOutputHelper output)
{
    // A well-formed Store ID that names no game.
    internal const string UnknownStoreId = "9NQKQPMK6HNH";

    // The lifecycle test names each playtest this and the UTC time it was made, so a later run can find one it left.
    private const string LifecycleName = "XboxDevCLI e2e lifecycle ";

    private const string NameTime = "yyyyMMdd'T'HHmmss'Z'";

    private const string UpdatedDescription = "Updated by the XBOX CLI's end-to-end tests.";

    private static E2ESettings Settings => E2ESettings.Current;

    [LiveFact]
    public async Task List_IncludesThePlaytest()
    {
        CliResult result = await Cli.RunForSellerAsync("playtest", "list", "--store-id", Settings.StoreId);

        result.Check(
            result.Json().Items("playtests").Any(playtest => playtest.IsGuid("playtestId", Settings.PlaytestId)),
            $"Expected playtest {Settings.PlaytestId}.");
    }

    [LiveFact]
    public async Task Get_ReturnsThePlaytestAndItsEtag()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "playtest", "get", Settings.PlaytestId, "--store-id", Settings.StoreId);

        JsonElement playtest = result.Json();
        result.Check(
            playtest.Field("details").IsGuid("playtestId", Settings.PlaytestId), $"Expected playtest {Settings.PlaytestId}.");
        result.Check(playtest.Text("etag") is { Length: > 0 }, "Expected the etag that 'playtest update --if-match' takes.");
    }

    [LiveFact]
    public async Task Diff_ShowsThePlaytestAsItIsNow()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "playtest", "diff", Settings.PlaytestId, "--store-id", Settings.StoreId);

        result.Check(
            result.Json().Field("current").Field("details").IsGuid("playtestId", Settings.PlaytestId),
            $"Expected playtest {Settings.PlaytestId} as it is now.");
    }

    [LiveFact]
    public async Task List_UnknownGame_NamesTheStoreIdAndSeller()
    {
        CliResult result = await Cli.RunForSellerAsync("playtest", "list", "--store-id", UnknownStoreId);

        result.CheckServiceError(404);
        result.Check(
            result.Stderr.Contains(
                $"No game with Store ID {UnknownStoreId} was found for seller {Settings.SellerId}.", StringComparison.Ordinal),
            "Expected the explanation that names the Store ID and the seller.");
    }

    // A playtest that was never published is deleted at once, with everything stored with it. The test never publishes
    // and adds no audiences or packages, and it changes or deletes only its own playtest and the ones
    // DeleteLeftoversAsync finds.
    [LiveWriteFact]
    public async Task CreateUpdateAndDelete_LeavesNoPlaytest()
    {
        await DeleteLeftoversAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string[] createCommand =
        [
            "playtest", "create", "--store-id", Settings.StoreId,
            "--name", LifecycleName + now.UtcDateTime.ToString(NameTime, CultureInfo.InvariantCulture),
            "--description", "Created by the XBOX CLI's end-to-end tests.",
            "--start", Utc(now.AddDays(1)), "--end", Utc(now.AddDays(8)),
            "--idempotency-key", Guid.NewGuid().ToString(), "--confirm",
        ];
        string? id = null;
        bool mightBeLeft = false;
        try
        {
            CliResult create = await Cli.RunForSellerAsync(createCommand);

            // Exit 1 means the create was refused, so there's nothing to delete.
            mightBeLeft = create.ExitCode != 1;
            JsonElement created = create.Json();
            id = created.Field("details").Text("playtestId");
            create.Check(id is not null && created.Field("details").Is("status", "Draft"), "Expected a draft playtest.");
            create.Check(created.Text("etag") is { Length: > 0 }, "Expected the playtest's etag.");

            CliResult update = await Cli.RunForSellerAsync(
                "playtest", "update", id, "--store-id", Settings.StoreId, "--description", UpdatedDescription,
                "--if-match", created.Text("etag")!, "--confirm");
            JsonElement updated = update.Json();
            update.Check(
                updated.Field("details").Is("description", UpdatedDescription) &&
                    updated.Text("etag") is { Length: > 0 } etag && etag != created.Text("etag"),
                "Expected the new description and a new etag.");

            CliResult delete = await Cli.RunForSellerAsync(
                "playtest", "delete", id, "--store-id", Settings.StoreId, "--confirm");
            delete.Check(delete.Json().Is("status", "Deleted"), "Expected the playtest deleted at once.");

            CliResult get = await Cli.RunForSellerAsync("playtest", "get", id, "--store-id", Settings.StoreId);
            get.Check(IsNotFound(get), "Expected the game to have no playtest with that ID.");
            mightBeLeft = false;
        }
        finally
        {
            if (mightBeLeft)
            {
                await CleanUpAsync(id, createCommand);
            }
        }
    }

    // Deletes the drafts that runs which stopped early left, and fails if one is still there, so it's noticed.
    private static async Task DeleteLeftoversAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CliResult list = await Cli.RunForSellerAsync("playtest", "list", "--store-id", Settings.StoreId);
        List<string> failures = [];
        foreach (JsonElement playtest in list.Json().Items("playtests"))
        {
            if (IsLeftover(playtest, now) && await DeleteAsync(playtest.Text("playtestId")!) is { } failure)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            throw new XunitException(string.Join(Environment.NewLine, failures));
        }
    }

    // A draft named LifecycleName and a time over an hour ago, so a run that's still going keeps its own.
    private static bool IsLeftover(JsonElement playtest, DateTimeOffset now) =>
        playtest.Is("status", "Draft") &&
        playtest.Text("name") is { } name &&
        name.StartsWith(LifecycleName, StringComparison.Ordinal) &&
        DateTimeOffset.TryParseExact(
            name[LifecycleName.Length..], NameTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out DateTimeOffset made) &&
        now - made > TimeSpan.FromHours(1);

    // Keeps the test's own failure: a cleanup that fails is written to the output, and a later run deletes the playtest.
    // Without an ID, as when the create's outcome is unknown, repeating the create with its idempotency key returns the
    // playtest that the key made, or makes it, so there's exactly one to delete.
    private async Task CleanUpAsync(string? id, string[] createCommand)
    {
        try
        {
            if (id is null)
            {
                CliResult repeat = await Cli.RunForSellerAsync(createCommand);
                id = repeat.Json().Field("details").Text("playtestId");
                repeat.Check(id is not null, "Expected the playtest that the idempotency key made.");
            }

            if (await DeleteAsync(id) is { } failure)
            {
                output.WriteLine(failure);
            }
        }
        catch (XunitException exception)
        {
            output.WriteLine($"Deleting the test's playtest failed: {exception.Message}");
        }
    }

    /// <summary>Deletes a playtest, and returns why it couldn't, or null once it's gone.</summary>
    private static async Task<string?> DeleteAsync(string id)
    {
        CliResult delete = await Cli.RunForSellerAsync(
            "playtest", "delete", id, "--store-id", Settings.StoreId, "--confirm");
        return delete.ExitCode == 0 || IsNotFound(delete)
            ? null
            : $"Deleting playtest {id} failed.{Environment.NewLine}{delete}";
    }

    // Whether the game has no playtest with the ID, rather than another failure, such as a 404 for the game.
    private static bool IsNotFound(CliResult result) =>
        result.ExitCode == 1 && result.Stderr.Contains("The game has no playtest with that ID.", StringComparison.Ordinal);

    private static string Utc(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
