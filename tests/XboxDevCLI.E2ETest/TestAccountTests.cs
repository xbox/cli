// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Xunit;

namespace XboxDevCLI.E2ETest;

/// <remarks>
/// The test account service allows few calls a minute, so the list that finds the tagged batch, and
/// the read of that batch, each run once for every test that needs them.
/// </remarks>
public class TestAccountTests
{
    private static readonly Lazy<Task<CliResult>> TaggedBatches = new(() =>
        Cli.RunForSellerAsync("test-account", "batch", "list", "--mine", "--tag", Settings.BatchKeyword));

    // Checked for passwords before any test reads it, without showing the command or its output, which
    // would copy them into the run's logs and test results.
    private static readonly Lazy<Task<CliResult>> TaggedBatch = new(async () =>
    {
        CliResult result = await Cli.RunForSellerAsync(
            "test-account", "batch", "get", (await TaggedBatchIdAsync()).ToString(CultureInfo.InvariantCulture));
        Assert.False(
            result.Stdout.Contains("password", StringComparison.OrdinalIgnoreCase),
            "Expected no passwords in the output, which isn't shown so it can't repeat them.");
        return result;
    });

    private static E2ESettings Settings => E2ESettings.Current;

    [LiveFact]
    public async Task BatchList_Mine_FindsTheTaggedBatch() => await TaggedBatchIdAsync();

    [LiveFact]
    public async Task BatchList_Limit_ReturnsAtMostThatMany()
    {
        CliResult result = await Cli.RunForSellerAsync("test-account", "batch", "list", "--limit", "4");

        int count = result.Json().Items("batches").Length;
        result.Check(count is >= 1 and <= 4, "Expected one to four batches.");
    }

    [LiveFact]
    public async Task BatchGet_ShowsTheBatchAndItsAccounts()
    {
        long batchId = await TaggedBatchIdAsync();
        CliResult result = await TaggedBatch.Value;

        JsonElement output = result.Json();
        JsonElement batch = output.Field("batch");
        result.Check(batch.Number("id") == batchId, $"Expected batch {batchId}.");
        result.Check(batch.Items("keywords").HasString(Settings.BatchKeyword), $"Expected the tag {Settings.BatchKeyword}.");
        result.Check(output.Items("accounts").Length > 0, "Expected the batch's accounts.");
    }

    // The batch already has the tag, so the service changes nothing and starts no job. Had it found a change, it would
    // have sent the test account service the batch's own tags, the same set.
    [LiveWriteFact]
    public async Task BatchUpdate_WithATagItHas_ChangesNothing()
    {
        long batchId = await TaggedBatchIdAsync();
        CliResult result = await Cli.RunForSellerAsync(
            "test-account", "batch", "update", batchId.ToString(CultureInfo.InvariantCulture),
            "--tag", Settings.BatchKeyword, "--confirm");

        JsonElement update = result.Json();
        result.Check(update.Number("batchId") == batchId, $"Expected batch {batchId}.");
        result.Check(
            update.Is("state", "unchanged") && update.Field("jobId").ValueKind == JsonValueKind.Null,
            "Expected the batch unchanged, with no job.");
    }

    [LiveFact]
    public async Task Get_FindsTheAccountInItsBatch()
    {
        long batchId = await TaggedBatchIdAsync();
        CliResult batch = await TaggedBatch.Value;
        string? email = batch.Json().Items("accounts").Select(account => account.Text("email")).FirstOrDefault();
        batch.Check(email is not null, "Expected an account with an email.");

        CliResult result = await Cli.RunForSellerAsync("test-account", "get", email);

        JsonElement found = result.Json();
        result.Check(found.Field("account").Is("email", email), $"Expected account {email}.");
        result.Check(found.Field("batch").Number("id") == batchId, $"Expected batch {batchId}.");
    }

    // When the service ignores the xuid filter, the lookup falls back to reading the newest
    // batches. It says on stderr which ones it searched only when the seller has more than 10
    // batches, so this test catches an ignored filter only for such a seller.
    [LiveFact]
    public async Task Get_ByXuid_FindsTheAccountThroughTheXuidFilter()
    {
        long batchId = await TaggedBatchIdAsync();
        CliResult batch = await TaggedBatch.Value;
        string? xuid = batch.Json().Items("accounts").Select(account => account.Text("xuid")).FirstOrDefault();
        batch.Check(xuid is not null, "Expected an account with a XUID.");

        CliResult result = await Cli.RunForSellerAsync("test-account", "get", xuid);

        JsonElement found = result.Json();
        result.Check(found.Field("account").Is("xuid", xuid), $"Expected account {xuid}.");
        result.Check(found.Field("batch").Number("id") == batchId, $"Expected batch {batchId}.");
        result.Check(
            !result.Stderr.Contains("searched", StringComparison.Ordinal),
            "Expected the service to filter by XUID, not a search of the newest batches.");
    }

    private static async Task<long> TaggedBatchIdAsync()
    {
        CliResult result = await TaggedBatches.Value;

        JsonElement[] batches = result.Json().Items("batches");
        result.Check(batches.Length == 1, $"Expected exactly one of your batches tagged {Settings.BatchKeyword}.");
        long id = batches[0].Number("id") ?? 0;
        result.Check(id > 0, "Expected the batch's ID.");
        return id;
    }
}
