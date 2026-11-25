// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XboxDevCLI.E2ETest;

/// <summary>
/// Runs the CLI as a personal Microsoft account (MSA), the only user of its own Partner Center seller. The tests read the
/// seller's data with each kind of command, and check that a seller the account doesn't belong to is refused.
/// </summary>
public class MsaTests
{
    private static MsaSettings Settings => MsaSettings.Current;

    [MsaFact]
    public async Task AuthStatus_ShowsTheAccount()
    {
        CliResult result = await RunAsync("auth", "status", "--output", "json-compact");

        JsonElement status = result.Json();
        result.Check(status.Flag("signedIn") == true, "Expected to be signed in. Run 'xbox auth login' first.");
        result.Check(status.Is("username", Settings.Username), $"Expected to be signed in as {Settings.Username}.");
        result.Check(
            status.Text("tokenExpiresAt") is not null,
            "Expected a token from the cache, which every gateway command needs.");
    }

    [MsaFact]
    public async Task SellerList_IsOnlyTheAccountsSeller()
    {
        CliResult result = await RunAsync("seller", "list", "--output", "json-compact");

        JsonElement[] sellers = result.Json().Items("sellers");
        result.Check(
            sellers.Length == 1 && sellers[0].Is("sellerId", Settings.SellerId), $"Expected only seller {Settings.SellerId}.");
    }

    [MsaFact]
    public async Task GameGet_ByStoreId_ReturnsTheGame()
    {
        CliResult result = await RunForSellerAsync(Settings.SellerId, "game", "get", "--store-id", Settings.StoreId);

        JsonElement[] games = result.Json().Items("games");
        result.Check(
            games.Length == 1 && games[0].Is("storeId", Settings.StoreId),
            $"Expected only the game with Store ID {Settings.StoreId}.");
        result.Check(
            games[0].Text("xboxTitleId") == Settings.TitleId,
            $"Expected XBOX title ID {Settings.TitleId}, as eight uppercase hexadecimal digits.");
    }

    // The seller needn't have sandboxes of its own, so the list can hold only global ones.
    [MsaFact]
    public async Task SandboxList_HasOnlyGlobalSandboxesAndTheSellers()
    {
        CliResult result = await RunForSellerAsync(Settings.SellerId, "sandbox", "list");

        JsonElement[] sandboxes = result.Json().Items("sandboxes");
        result.Check(sandboxes.Any(sandbox => sandbox.Is("sandboxId", "RETAIL")), "Expected RETAIL.");
        result.Check(
            sandboxes.All(sandbox => sandbox.Flag("global") == true || sandbox.Is("sellerId", Settings.SellerId)),
            $"Expected only global sandboxes and those of seller {Settings.SellerId}.");
    }

    // The game needn't have XBOX services configuration, so the title's can be empty.
    [MsaFact]
    public async Task XblConfigList_ByTitleId_IncludesTheTitle()
    {
        CliResult result = await RunForSellerAsync(
            Settings.SellerId, "xbl", "config", "list", "--title-id", Settings.TitleId);

        result.Check(result.Json().Items().Any(set => set.Is("scope", "title")), "Expected the title's configuration.");
    }

    // The seller needn't have test accounts, playtests, or audiences, and the game needn't have packages, so these
    // lists can be empty.
    [MsaFact]
    public async Task TestAccountBatchList_ReturnsTheBatches()
    {
        CliResult result = await RunForSellerAsync(Settings.SellerId, "test-account", "batch", "list");

        result.Check(result.Json().Field("batches").ValueKind == JsonValueKind.Array, "Expected a batches array.");
    }

    [MsaFact]
    public async Task PackageList_ReturnsTheMainBranch()
    {
        CliResult result = await RunForSellerAsync(Settings.SellerId, "package", "list", "--store-id", Settings.StoreId);

        JsonElement listing = result.Json();
        result.Check(listing.Text("packageSetId") is { Length: > 0 }, "Expected the Main branch's packageSetId.");
        result.Check(listing.Field("packages").ValueKind == JsonValueKind.Array, "Expected a packages array.");
    }

    [MsaFact]
    public async Task PlaytestList_ReturnsThePlaytests()
    {
        CliResult result = await RunForSellerAsync(Settings.SellerId, "playtest", "list", "--store-id", Settings.StoreId);

        result.Check(result.Json().Field("playtests").ValueKind == JsonValueKind.Array, "Expected a playtests array.");
    }

    [MsaFact]
    public async Task AudienceList_ReturnsTheAudiences()
    {
        CliResult result = await RunForSellerAsync(Settings.SellerId, "audience", "list");

        result.Check(result.Json().Field("audiences").ValueKind == JsonValueKind.Array, "Expected an audiences array.");
    }

    // The game needn't have a listing in en-us, so the command can say it has none instead.
    [MsaFact]
    public async Task StoreListingGet_ReturnsTheListingOrSaysThereIsNone()
    {
        CliResult result = await RunForSellerAsync(
            Settings.SellerId, "store-listing", "get", "--store-id", Settings.StoreId, "--language", "en-us");

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

    [MsaFact]
    public async Task TestAccountBatchList_OtherSeller_IsRefused() =>
        CheckRefusedAsJson(await RunForSellerAsync(Settings.OtherSellerId, "test-account", "batch", "list"));

    [MsaFact]
    public async Task PackageList_OtherSeller_IsRefused()
    {
        CliResult result = await RunForSellerAsync(
            Settings.OtherSellerId, "package", "list", "--store-id", Settings.OtherStoreId);

        result.CheckServiceError(403);
        result.Check(
            result.Stderr.Contains("You don't belong to that seller.", StringComparison.Ordinal),
            "Expected the explanation that you don't belong to the seller.");
    }

    [MsaFact]
    public async Task PlaytestList_OtherSeller_IsRefused() =>
        (await RunForSellerAsync(Settings.OtherSellerId, "playtest", "list", "--store-id", Settings.OtherStoreId))
            .CheckServiceError(403);

    [MsaFact]
    public async Task AudienceList_OtherSeller_IsRefused() =>
        (await RunForSellerAsync(Settings.OtherSellerId, "audience", "list")).CheckServiceError(403);

    [MsaFact]
    public async Task XblConfigStatus_OtherSeller_IsRefused() =>
        CheckRefusedAsJson(
            await RunForSellerAsync(Settings.OtherSellerId, "xbl", "config", "status", "--scope", "account"));

    [MsaFact]
    public async Task XblConfigPublish_ValidateOnly_OtherSeller_IsRefused() =>
        CheckRefusedAsJson(await RunForSellerAsync(
            Settings.OtherSellerId, "xbl", "config", "publish", "--scope", "account", "--validate-only"));

    // The service refuses the seller first, and no game has this Store ID, so neither test can bind or unbind anything.
    [MsaFact]
    public async Task XblBindingCreate_OtherSeller_IsRefused() =>
        CheckRefusedAsJson(await RunForSellerAsync(
            Settings.OtherSellerId, "xbl", "binding", "create", "--store-id", PlaytestTests.UnknownStoreId,
            "--primary-title-id", Settings.TitleId, "--confirm"));

    [MsaFact]
    public async Task XblBindingDelete_OtherSeller_IsRefused() =>
        CheckRefusedAsJson(await RunForSellerAsync(
            Settings.OtherSellerId, "xbl", "binding", "delete", "--store-id", PlaytestTests.UnknownStoreId,
            "--confirm"));

    [MsaFact]
    public async Task XblWebServiceList_OtherSeller_IsRefused() =>
        CheckRefusedAsJson(await RunForSellerAsync(Settings.OtherSellerId, "xbl", "web-service", "list"));

    [MsaFact]
    public async Task XblRelyingPartyList_OtherSeller_IsRefused() =>
        CheckRefusedAsJson(await RunForSellerAsync(Settings.OtherSellerId, "xbl", "relying-party", "list"));

    [MsaFact]
    public async Task XblBusinessPartnerList_OtherSeller_IsRefused() =>
        CheckRefusedAsJson(await RunForSellerAsync(Settings.OtherSellerId, "xbl", "business-partner", "list"));

    private static Task<CliResult> RunAsync(params string[] arguments) => Cli.RunAsync(Settings.Cli, arguments);

    // Runs a command for a seller, with compact JSON output.
    private static Task<CliResult> RunForSellerAsync(string sellerId, params string[] arguments) =>
        RunAsync([.. arguments, "--seller-id", sellerId, "--output", "json-compact"]);

    // Test account and XBOX services commands write the service's refusal to stderr as JSON.
    private static void CheckRefusedAsJson(CliResult result)
    {
        result.Check(result.ExitCode == 1, "Expected exit code 1.");
        result.Check(string.IsNullOrWhiteSpace(result.Stdout), "Expected nothing on stdout.");
        result.Check(
            result.Stderr.Contains("\"status\":403", StringComparison.Ordinal)
                && result.Stderr.Contains("\"code\":\"identity_override_not_authorized\"", StringComparison.Ordinal),
            "Expected the service's 403 refusal, identity_override_not_authorized, on stderr.");
    }
}
