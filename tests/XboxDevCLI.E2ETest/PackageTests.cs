// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XboxDevCLI.E2ETest;

public class PackageTests
{
    // A seller the account doesn't belong to.
    private const string OtherSellerId = "1";

    private static E2ESettings Settings => E2ESettings.Current;

    // The game needn't have packages, so the list can be empty.
    [LiveFact]
    public async Task List_ReturnsThePackages()
    {
        CliResult result = await Cli.RunForSellerAsync("package", "list", "--store-id", Settings.StoreId);

        result.Check(result.Json().Field("packages").ValueKind == JsonValueKind.Array, "Expected a packages array.");
    }

    // Package commands can leave the seller to the service, so only a refusal shows that --seller-id is sent.
    [LiveFact]
    public async Task List_OtherSeller_IsRefused()
    {
        CliResult result = await Cli.RunAsync("package", "list", "--store-id", Settings.StoreId, "--seller-id", OtherSellerId);

        result.CheckServiceError(403);
        result.Check(
            result.Stderr.Contains("You don't belong to that seller.", StringComparison.Ordinal),
            "Expected the explanation that you don't belong to the seller.");
    }
}
