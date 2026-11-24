// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.E2ETest;

public class StoreListingTests
{
    private static E2ESettings Settings => E2ESettings.Current;

    // The game needn't have a listing in en-us, so the command can say it has none instead.
    [LiveFact]
    public async Task Get_ReturnsTheListingOrSaysThereIsNone()
    {
        CliResult result = await Cli.RunForSellerAsync(
            "store-listing", "get", "--store-id", Settings.StoreId, "--language", "en-us");

        if (result.ExitCode == 0)
        {
            result.Check(result.Json().Is("language", "en-us"), "Expected the en-us listing.");
            return;
        }

        result.CheckServiceError(404);
        result.Check(
            result.Stderr.Contains($"Game {Settings.StoreId} has no Store listing in en-us.", StringComparison.Ordinal),
            "Expected the explanation that the game has no listing in en-us.");
    }
}
