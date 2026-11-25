// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Net;
using System.Text.Json;
using XboxDevCLI.PlayFab.Commands;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Coverage for the <c>playfab economy inventory</c> command group: command/option structure, the
/// entity/count/item/operations build helpers and their validation branches, the exact request URI
/// and serialized body each verb produces, and the malformed-JSON handling for the
/// <c>--item</c>/<c>--operations</c> escape hatches. Actions take an injected client and captured
/// writers, so no <c>Console</c> redirection (and no <c>[Collection("ConsoleRedirect")]</c>) is needed.
/// </summary>
public class EconomyInventoryCommandTests
{
    #region Command structure

    [Fact]
    public void Inventory_Verbs_HaveExpectedOptions()
    {
        var inventory = EconomyCommandFactory.Create().Subcommands.First(c => c.Name == "inventory");

        Command Verb(string path)
        {
            var current = inventory;
            foreach (var segment in path.Split(' '))
            {
                current = current.Subcommands.First(c => c.Name == segment);
            }

            return current;
        }

        void AssertHas(Command c, params string[] names)
        {
            foreach (var n in names)
            {
                Assert.Contains(c.Options, o => o.Name == n);
            }
        }

        AssertHas(Verb("list"), "--entity-id", "--entity-type", "--collection-id", "--limit", "--continuation-token", "--filter");
        AssertHas(Verb("collection list"), "--entity-id", "--entity-type", "--limit", "--continuation-token");
        AssertHas(Verb("transaction list"), "--entity-id", "--entity-type", "--collection-id", "--limit", "--continuation-token", "--filter", "--order-by");
        AssertHas(Verb("add"), "--entity-id", "--entity-type", "--collection-id", "--item-id", "--item", "--stack-id", "--amount", "--idempotency-id");
        AssertHas(Verb("operation execute"), "--entity-id", "--entity-type", "--collection-id", "--operations", "--operations-file", "--idempotency-id");
        AssertHas(Verb("operation status"), "--entity-id", "--entity-type", "--collection-id", "--operation-token");

        Assert.True(Verb("operation status").Options.First(o => o.Name == "--operation-token").Required);

        // Without an entity PlayFab targets the caller, the title, which has no inventory.
        foreach (var verb in new[] { "list", "collection list", "transaction list", "add", "operation execute", "operation status" })
        {
            Assert.True(Verb(verb).Options.First(o => o.Name == "--entity-id").Required, verb);
            Assert.True(Verb(verb).Options.First(o => o.Name == "--entity-type").Required, verb);
        }

        // Paging vocabulary is normalized; the retired spelling must be gone everywhere.
        foreach (var paged in new[] { "list", "collection list", "transaction list" })
        {
            Assert.DoesNotContain(Verb(paged).Options, o => o.Name == "--count");
        }
    }

    #endregion

    #region TryBuildEntity

    [Fact]
    public void TryBuildEntity_BothOmitted_ReturnsError()
    {
        Assert.False(EconomyCommandFactory.TryBuildEntity(null, "  ", out var entity, out var err));
        Assert.Null(entity);
        Assert.Contains("--entity-id", err);
        Assert.Contains("--entity-type", err);
    }

    #endregion

    #region TryBuildItemReference

    [Fact]
    public void TryBuildItemReference_Json_Deserializes_CaseInsensitive()
    {
        Assert.True(EconomyCommandFactory.TryBuildItemReference(null, """{"id":"abc","stackId":"s1"}""", null, out var item, out var err));
        Assert.Null(err);
        Assert.Equal("abc", item!.Id);
        Assert.Equal("s1", item.StackId);
    }

    [Fact]
    public void TryBuildItemReference_BothForms_ReturnsError()
    {
        Assert.False(EconomyCommandFactory.TryBuildItemReference("sword", """{"Id":"abc"}""", null, out var item, out var err));
        Assert.Null(item);
        Assert.Contains("not both", err);
    }

    [Fact]
    public void TryBuildItemReference_StackIdWithJson_ReturnsError()
    {
        Assert.False(EconomyCommandFactory.TryBuildItemReference(null, """{"Id":"abc"}""", "s1", out var item, out var err));
        Assert.Null(item);
        Assert.Contains("--stack-id", err);
    }

    #endregion

    #region TryParseOperations

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void TryParseOperations_EmptyOrBlank_ReturnsError(string json)
    {
        Assert.False(EconomyCommandFactory.TryParseOperations(json, out var ops, out var err));
        Assert.Null(ops);
        Assert.Contains("--operations", err);
    }

    #endregion

    #region list

    [Fact]
    public async Task RunListAsync_SendsRequestToCorrectPathAndBody()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        var exit = await EconomyCommandFactory.RunListAsync(
            client, "E1", "title_player_account", "coll1", 25, "tok", "type eq 'currency'",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.GetInventoryItemsPath, handler.LastRequestUri);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("E1", root.GetProperty("Entity").GetProperty("Id").GetString());
        Assert.Equal("title_player_account", root.GetProperty("Entity").GetProperty("Type").GetString());
        Assert.Equal("coll1", root.GetProperty("CollectionId").GetString());
        Assert.Equal(25, root.GetProperty("Count").GetDouble());
        Assert.Equal("tok", root.GetProperty("ContinuationToken").GetString());
        Assert.Equal("type eq 'currency'", root.GetProperty("Filter").GetString());
    }

    [Fact]
    public async Task RunListAsync_OmitsOptionalFieldsAndDefaultsCount()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        var exit = await EconomyCommandFactory.RunListAsync(
            client, "E1", "title_player_account", null, null, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("CollectionId", out _));
        Assert.False(root.TryGetProperty("Filter", out _));
        Assert.Equal(EconomyCommandFactory.DefaultCount, root.GetProperty("Count").GetDouble());
    }

    [Fact]
    public async Task RunListAsync_HalfEntity_ReturnsOneWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunListAsync(
            client, "E1", null, null, null, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--entity-type", stderr.ToString());
    }

    [Fact]
    public async Task RunListAsync_NonPositiveCount_ReturnsOneWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunListAsync(
            client, "E1", "title_player_account", null, 0, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--limit", stderr.ToString());
    }

    #endregion

    #region collections

    [Fact]
    public async Task RunCollectionsAsync_SendsRequestToCorrectPath()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        var exit = await EconomyCommandFactory.RunCollectionsAsync(
            client, "E1", "title_player_account", 5, "tok",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.GetInventoryCollectionIdsPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(5, doc.RootElement.GetProperty("Count").GetDouble());
        Assert.Equal("tok", doc.RootElement.GetProperty("ContinuationToken").GetString());
    }

    #endregion

    #region history

    [Fact]
    public async Task RunHistoryAsync_SendsFilterAndOrderBy()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        var exit = await EconomyCommandFactory.RunHistoryAsync(
            client, "E1", "title_player_account", "coll1", 15, null,
            "apiname eq 'AddInventoryItems'", "timestamp desc",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.GetTransactionHistoryPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("apiname eq 'AddInventoryItems'", root.GetProperty("Filter").GetString());
        Assert.Equal("timestamp desc", root.GetProperty("OrderBy").GetString());
        Assert.Equal("coll1", root.GetProperty("CollectionId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunHistoryAsync_WithoutCollectionId_OmitsCollectionId(string? collectionId)
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        var exit = await EconomyCommandFactory.RunHistoryAsync(
            client, "E1", "title_player_account", collectionId, null, null, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("CollectionId", out _));
    }

    #endregion

    #region add

    [Fact]
    public async Task RunAddAsync_ItemIdAndAmount_SendsItemReference()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        var exit = await EconomyCommandFactory.RunAddAsync(
            client, "E1", "title_player_account", "coll1", "sword", null, "stack7", 3, "idem1",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.AddInventoryItemsPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("sword", root.GetProperty("Item").GetProperty("Id").GetString());
        Assert.Equal("stack7", root.GetProperty("Item").GetProperty("StackId").GetString());
        Assert.Equal(3, root.GetProperty("Amount").GetDouble());
        Assert.Equal("idem1", root.GetProperty("IdempotencyId").GetString());
        Assert.Equal("coll1", root.GetProperty("CollectionId").GetString());
    }

    [Fact]
    public async Task RunAddAsync_ItemJson_SendsParsedReference()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        var exit = await EconomyCommandFactory.RunAddAsync(
            client, "E1", "title_player_account", null, null, """{"Id":"potion","StackId":"s1"}""", null, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("potion", doc.RootElement.GetProperty("Item").GetProperty("Id").GetString());
        Assert.False(doc.RootElement.TryGetProperty("Amount", out _));
    }

    [Fact]
    public async Task RunAddAsync_MalformedItemJson_ReturnsOneWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunAddAsync(
            client, "E1", "title_player_account", null, null, "{bad json", null, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--item", stderr.ToString());
    }

    [Fact]
    public async Task RunAddAsync_NoItem_ReturnsOneWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunAddAsync(
            client, "E1", "title_player_account", null, null, null, null, 1, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--item-id", stderr.ToString());
    }

    #endregion

    #region ops

    [Fact]
    public async Task RunOpsAsync_InlineOperations_SendsBatch()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var json = """[{"Add":{"Item":{"Id":"sword"},"Amount":1}},{"Subtract":{"Item":{"Id":"gold"},"Amount":5}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", "coll1", json, null, "idem2",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.ExecuteInventoryOperationsPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var ops = doc.RootElement.GetProperty("Operations");
        Assert.Equal(2, ops.GetArrayLength());
        Assert.Equal("sword", ops[0].GetProperty("Add").GetProperty("Item").GetProperty("Id").GetString());
        Assert.Equal("idem2", doc.RootElement.GetProperty("IdempotencyId").GetString());
    }

    [Fact]
    public async Task RunOpsAsync_OperationsFile_ReadsAndSendsBatch()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        // Write to the process working directory and delete it in the finally below, so no file
        // is left behind wherever the test host happens to run.
        var path = Path.Combine(Directory.GetCurrentDirectory(), $"ops-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """[{"Add":{"Item":{"Id":"sword"},"Amount":1}}]""");
        try
        {
            var exit = await EconomyCommandFactory.RunOpsAsync(
                client, "E1", "title_player_account", null, null, path, null,
                OutputOptions.Json, new StringWriter(), new StringWriter(),
                CancellationToken.None);

            Assert.Equal(0, exit);
            using var doc = JsonDocument.Parse(handler.LastRequestBody!);
            Assert.Equal(1, doc.RootElement.GetProperty("Operations").GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunOpsAsync_BothSources_ReturnsOneWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", null, "[{\"Add\":{}}]", "some-file.json", null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("exactly one", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_MalformedInlineJson_ReturnsOneWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", null, "[{bad}]", null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--operations", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_MissingFile_ReturnsOneWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();
        var missing = Path.Combine(Directory.GetCurrentDirectory(), $"missing-{Guid.NewGuid():N}.json");

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", null, null, missing, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--operations-file", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_DeleteOperationWithoutConfirm_IsRefusedBeforeCalling()
    {
        // ExecuteInventoryOperations is not a Delete*/Shutdown* path, so the name-based guard in
        // PlayFabApiClient never fires for it. A Delete carried inside the batch must still be
        // confirmed, otherwise deleting via a batch would be easier than via a dedicated verb.
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();
        var json = """[{"Delete":{"Item":{"Id":"sword"}}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", null, json, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None, confirmed: false);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--confirm", stderr.ToString());
    }

    [Fact]
    public async Task RunOpsAsync_DeleteOperationWithConfirm_Proceeds()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var json = """[{"Delete":{"Item":{"Id":"sword"}}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", null, json, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None, confirmed: true);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.ExecuteInventoryOperationsPath, handler.LastRequestUri);
    }

    [Fact]
    public async Task RunOpsAsync_DeleteMixedIntoBatch_IsAlsoRefused()
    {
        // The guard must inspect every operation, not just the first one.
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();
        var json = """[{"Add":{"Item":{"Id":"gold"},"Amount":1}},{"Delete":{"Item":{"Id":"sword"}}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", null, json, null, null,
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None, confirmed: false);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task RunOpsAsync_NonDeleteBatch_DoesNotRequireConfirm()
    {
        // Only Delete is gated; ordinary batches must not regress into needing --confirm.
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var json = """[{"Add":{"Item":{"Id":"sword"},"Amount":1}},{"Subtract":{"Item":{"Id":"gold"},"Amount":5}}]""";

        var exit = await EconomyCommandFactory.RunOpsAsync(
            client, "E1", "title_player_account", null, json, null, null,
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None, confirmed: false);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.ExecuteInventoryOperationsPath, handler.LastRequestUri);
    }

    #endregion

    #region status

    [Fact]
    public async Task RunStatusAsync_SendsOperationToken()
    {
        var handler = new MockHttpMessageHandler(Ok("""{"code":200,"status":"OK","data":{"OperationStatus":"Completed"}}"""));
        using var client = PlayFabTestClient.Create(handler);
        var stdout = new StringWriter();

        var exit = await EconomyCommandFactory.RunStatusAsync(
            client, "E1", "title_player_account", "coll1", "op-token-123",
            OutputOptions.Json, stdout, new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(EconomyCommandFactory.GetInventoryOperationStatusPath, handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("op-token-123", doc.RootElement.GetProperty("OperationToken").GetString());
        Assert.Equal("coll1", doc.RootElement.GetProperty("CollectionId").GetString());
        Assert.Contains("OperationStatus", stdout.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunStatusAsync_WithoutCollectionId_OmitsCollectionId(string? collectionId)
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);

        var exit = await EconomyCommandFactory.RunStatusAsync(
            client, "E1", "title_player_account", collectionId, "op-token-123",
            OutputOptions.Json, new StringWriter(), new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("CollectionId", out _));
    }

    [Fact]
    public async Task RunStatusAsync_BlankToken_ReturnsOneWithoutCalling()
    {
        var handler = new MockHttpMessageHandler(Ok());
        using var client = PlayFabTestClient.Create(handler);
        var stderr = new StringWriter();

        var exit = await EconomyCommandFactory.RunStatusAsync(
            client, "E1", "title_player_account", null, "   ",
            OutputOptions.Json, new StringWriter(), stderr,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Null(handler.LastRequestUri);
        Assert.Contains("--operation-token", stderr.ToString());
    }

    #endregion

    #region Test helpers

    private static HttpResponseMessage Ok(string json = """{"code":200,"status":"OK","data":{"ok":true}}""") =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    #endregion
}
