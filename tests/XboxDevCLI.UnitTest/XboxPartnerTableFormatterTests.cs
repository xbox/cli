// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerTableFormatterTests
{
    [Fact]
    public void FormatGames_GroupsGamesWithTheSameHexadecimalTitleId()
    {
        var table = XboxPartnerTableFormatter.FormatGames(
            """
            {
              "games": [
                {"productId":"abc","storeId":"9N0000000001","name":"Nod Arena","xboxTitleId":"12345678","packageFamilyName":"Nod.Arena_abc123","msaAppId":"0000000040ABCDEF"},
                {"productId":"def","storeId":"9N0000000000","name":"Nod Arena","xboxTitleId":"12345678","packageFamilyName":"Nod.Arena_abc123","msaAppId":"0000000040ABCDEF"},
                {"productId":"ghi","storeId":"9N0000000002","name":"Nod Arena","xboxTitleId":"12345678","packageFamilyName":"Nod.Arena_abc123","msaAppId":"0000000040ABCDEF"},
                {"productId":"jkl","storeId":"9N0000000003","name":"Nod Arena","xboxTitleId":"12345678","packageFamilyName":"Nod.Arena_abc123","msaAppId":"0000000040ABCDEF"}
              ]
            }
            """);

        Assert.Contains("TITLE ID", table, StringComparison.Ordinal);
        Assert.Contains("STORE IDS", table, StringComparison.Ordinal);
        Assert.Contains("PACKAGE FAMILY NAME", table, StringComparison.Ordinal);
        Assert.Contains("Nod.Arena_abc123", table, StringComparison.Ordinal);
        Assert.Contains("MSA APP ID", table, StringComparison.Ordinal);
        Assert.Contains("0000000040ABCDEF", table, StringComparison.Ordinal);
        Assert.Contains("12345678", table, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "9N0000000000, 9N0000000001, 9N0000000002, 9N0000000003",
            table,
            StringComparison.Ordinal);
        Assert.Contains("9N0000000000, 9N0000000001", table, StringComparison.Ordinal);
        Assert.Contains("...", table, StringComparison.Ordinal);
        Assert.Equal(1, table.Split(Environment.NewLine).Count(line =>
            line.Contains("Nod Arena", StringComparison.Ordinal)));
    }

    [Fact]
    public void FormatGames_DoesNotMergeGamesWhoseTitleIdsAreNull()
    {
        var table = XboxPartnerTableFormatter.FormatGames(
            """
            {
              "games": [
                {"productId":null,"storeId":"9N0000000000","xboxTitleId":null,"name":"Nod Arena"},
                {"productId":null,"storeId":"9N0000000001","xboxTitleId":null,"name":"Lucerne Rally"}
              ]
            }
            """);

        Assert.Equal(1, table.Split(Environment.NewLine).Count(line =>
            line.Contains("Nod Arena", StringComparison.Ordinal)));
        Assert.Equal(1, table.Split(Environment.NewLine).Count(line =>
            line.Contains("Lucerne Rally", StringComparison.Ordinal)));
    }

    [Fact]
    public void FormatGames_TruncatesMultipleLongGameNamesToTheDeclaredWidth()
    {
        const string firstName =
            "Lucerne Adventure Game With An Exceptionally Long Complete Product Name";
        const string secondName =
            "Nod Adventure Game With Another Exceptionally Long Complete Product Name";
        var table = XboxPartnerTableFormatter.FormatGames(
            $$"""
            {
              "games": [
                {"productId":"abc","storeId":"9N0000000000","name":"{{firstName}}","xboxTitleId":"12345678"},
                {"productId":"def","storeId":"9N0000000001","name":"{{secondName}}","xboxTitleId":"12345678"}
              ]
            }
            """);

        Assert.DoesNotContain(firstName, table, StringComparison.Ordinal);
        Assert.DoesNotContain(secondName, table, StringComparison.Ordinal);
        Assert.Contains("Lucerne Adventure Game", table, StringComparison.Ordinal);
        Assert.Contains("...", table, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"games":[123]}""")]
    [InlineData("""{"games":[{"productId":"abc","xboxTitleId":"305419896"}]}""")]
    [InlineData("""{"games":[{"xboxTitleId":305419896}]}""")]
    [InlineData("""{"games":[{"xboxTitleId":true}]}""")]
    [InlineData("""{"games":[{"xboxTitleId":"1234abcd"}]}""")]
    [InlineData("""{"games":[{"xboxTitleId":"0x12345678"}]}""")]
    [InlineData("""{"games":[{"xboxTitleId":"42"}]}""")]
    [InlineData("""{"games":[{"xboxTitleId":"1234567G"}]}""")]
    [InlineData("[]")]
    [InlineData("""{"games":"nope"}""")]
    public void FormatGames_RejectsInvalidGameFields(string responseBody)
    {
        Assert.Throws<InvalidDataException>(() =>
            XboxPartnerCommandSupport.ParseGameResponse(responseBody));

        Assert.Throws<InvalidDataException>(() =>
            XboxPartnerTableFormatter.FormatGames(responseBody));
        Assert.Throws<InvalidDataException>(() =>
            XboxPartnerGameDetailsFormatter.Format(responseBody));
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("00000042")]
    [InlineData("1234ABCD")]
    [InlineData("FFFFFFFF")]
    public void FormatGames_PreservesEightDigitHexadecimalTitleIds(string titleId)
    {
        var table = XboxPartnerTableFormatter.FormatGames(
            $$"""{"games":[{"xboxTitleId":"{{titleId}}","name":"Nod Arena"}]}""");

        Assert.Contains(titleId, table, StringComparison.Ordinal);
    }

    /// <summary>
    /// Seller names come from the gateway and are written straight to a terminal, so every
    /// control character must be neutralized rather than only the whitespace ones.
    /// </summary>
    [Fact]
    public void FormatSellers_ReplacesControlCharactersInCellValues()
    {
        var table = XboxPartnerTableFormatter.FormatSellers(
            """{"sellers":[{"sellerId":"1234567","name":"\u001b[31mRed\u0008Name"}]}""");

        Assert.DoesNotContain('\u001b', table);
        Assert.DoesNotContain('\u0008', table);
        Assert.Contains("Red", table, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSellers_MalformedJsonFailsClearly()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => XboxPartnerTableFormatter.FormatSellers("not-json"));

        Assert.Contains("was not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    private const string TestAccountBatchJson = """
        {"id":9007199254740993,"emailTemplate":"ABCDEF-smoke","batchSize":2,
         "createdBy":7654321,"familyPermission":"Adult",
         "creationDate":"2026-09-11T21:00:00-07:00","accessExpirationDate":"2027-03-11T20:00:00-08:00",
         "locale":"en-US","keywords":null,"sandboxAccess":null,"isComplete":true,"progress":null}
        """;

    [Fact]
    public void FormatTestAccountBatches_ShowsStateCreatorUtcDatesAndCounts()
    {
        var result = XboxPartnerTableFormatter.FormatTestAccountBatches(
            "{\"batches\":[" + TestAccountBatchJson + "]}",
            new TestAccountBatchFilter());
        string table = result.Text;

        string[] lines = table.Split(Environment.NewLine);
        Assert.Equal(
            ["BATCH ID", "EMAIL TEMPLATE", "SIZE", "STATE", "CREATED BY", "CREATED (UTC)", "EXPIRES (UTC)"],
            SplitColumns(lines[0]));
        Assert.Contains("9007199254740993", lines[2], StringComparison.Ordinal);
        Assert.Contains("ABCDEF-smoke", lines[2], StringComparison.Ordinal);
        Assert.Contains("complete", lines[2], StringComparison.Ordinal);
        Assert.Contains("7654321", lines[2], StringComparison.Ordinal);
        Assert.Contains("2026-09-12 04:00", lines[2], StringComparison.Ordinal);
        Assert.Contains("2027-03-12 04:00", lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("SANDBOXES", table, StringComparison.Ordinal);
        Assert.DoesNotContain("Returned", table, StringComparison.Ordinal);
        Assert.Equal("Returned 1 batches.", Assert.Single(result.Notes));
    }

    [Fact]
    public void FormatTestAccountBatches_IncludeSandboxesAddsColumnAndDashesForMissingValues()
    {
        string table = XboxPartnerTableFormatter.FormatTestAccountBatches(
            """
            {"batches":[
              {"id":2,"batchSize":1,"createdBy":5,"isComplete":false,"sandboxAccess":["ABCDEF.0","ABCDEF.1"]},
              {"id":1,"batchSize":1,"createdBy":5,"isComplete":true,"sandboxAccess":[]}
            ]}
            """,
            new TestAccountBatchFilter(IncludeSandboxes: true)).Text;

        string[] lines = table.Split(Environment.NewLine);
        Assert.Equal("SANDBOXES", SplitColumns(lines[0])[^1]);
        Assert.Contains("pending", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("ABCDEF.0, ABCDEF.1", lines[2].TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("-", lines[3].TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTestAccountBatches_EmptyListStillShowsCounts()
    {
        var result = XboxPartnerTableFormatter.FormatTestAccountBatches(
            """{"batches":[]}""",
            new TestAccountBatchFilter());

        Assert.Equal(TableFormatter.EmptyNotice, result.Text);
        Assert.Equal("Returned 0 batches.", Assert.Single(result.Notes));
    }

    [Theory]
    [InlineData("not-json", "was not valid JSON")]
    [InlineData("null", "was not valid JSON")]
    [InlineData("{}", "must contain a 'batches' array")]
    [InlineData("""{"batches":[null]}""", "must contain a 'batches' array")]
    public void FormatTestAccountBatches_RejectsMalformedResponses(string body, string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => XboxPartnerTableFormatter.FormatTestAccountBatches(body, new TestAccountBatchFilter()));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTestAccountBatch_ShowsSortedAccountsAndSummarizesBatchInNotes()
    {
        var result = XboxPartnerTableFormatter.FormatTestAccountBatch(
            """
            {"batch":{"id":42,"emailTemplate":"ABCDEF-smoke","batchSize":3,"createdBy":7654321,
              "familyPermission":"Adult","isComplete":true,"progress":"Done",
              "sandboxAccess":["ABCDEF.0","ABCDEF.1"]},
             "accounts":[
              {"xuid":"2814600000000002","email":"b@example.com","gamertag":"Beta","isDeleted":true},
              {"xuid":"2814600000000001","email":"a@example.com","gamertag":"Alpha","isDeleted":false}
             ]}
            """);

        string[] lines = result.Text.Split(Environment.NewLine);
        Assert.Equal(["EMAIL", "GAMERTAG", "XUID", "DELETED"], SplitColumns(lines[0]));
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("a@example.com", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("no", lines[2].TrimEnd(), StringComparison.Ordinal);
        Assert.StartsWith("b@example.com", lines[3], StringComparison.Ordinal);
        Assert.EndsWith("yes", lines[3].TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(
            [
                "Batch 42 (ABCDEF-smoke): complete, 2 accounts returned of batch size 3.",
                "Sandbox access: ABCDEF.0, ABCDEF.1",
            ],
            result.Notes);
    }

    [Fact]
    public void FormatTestAccountBatch_PendingBatchShowsProgress()
    {
        var result = XboxPartnerTableFormatter.FormatTestAccountBatch(
            """
            {"batch":{"id":42,"batchSize":3,"createdBy":5,"isComplete":false,"progress":"Created 1 of 3"},
             "accounts":[]}
            """);

        Assert.Equal(TableFormatter.EmptyNotice, result.Text);
        Assert.Equal(
            [
                "Batch 42 (-): pending, 0 accounts returned of batch size 3.",
                "Progress: Created 1 of 3",
            ],
            result.Notes);
    }

    [Fact]
    public void FormatTestAccountBatch_NeutralizesControlCharactersInNotes()
    {
        var result = XboxPartnerTableFormatter.FormatTestAccountBatch(
            """
            {"batch":{"id":42,"emailTemplate":"ABC\nDEF","batchSize":3,"createdBy":5,"isComplete":false,
              "progress":"a\u001b[31mb\u202Ec","sandboxAccess":["X\u2028Y"]},
             "accounts":[]}
            """);

        Assert.Equal(3, result.Notes.Count);
        Assert.All(result.Notes, note =>
        {
            Assert.DoesNotContain('\u001b', note);
            Assert.DoesNotContain('\n', note);
            Assert.DoesNotContain('\u202e', note);
            Assert.DoesNotContain('\u2028', note);
        });
    }

    [Fact]
    public void TestAccountViews_AreDerivedFromTheirFields()
    {
        Assert.Equal(
            XboxPartnerCommandSupport.TestAccountBatchFields.Select(field => field.Column),
            XboxPartnerCommandSupport.TestAccountBatchesView.Columns);
        Assert.Equal(
            XboxPartnerCommandSupport.TestAccountFields.Select(field => field.Column),
            XboxPartnerCommandSupport.TestAccountBatchView.Columns);
        Assert.DoesNotContain(
            XboxPartnerCommandSupport.TestAccountSandboxesField.Column,
            XboxPartnerCommandSupport.TestAccountBatchesView.Columns);
        Assert.Equal(
            XboxPartnerCommandSupport.TestAccountJobFields.Select(field => field.Column),
            XboxPartnerCommandSupport.TestAccountJobView.Columns);
        Assert.Equal(
            XboxPartnerCommandSupport.TestAccountBatchUpdateFields.Select(field => field.Column),
            XboxPartnerCommandSupport.TestAccountBatchUpdateView.Columns);
    }

    [Fact]
    public void FormatTestAccountJob_RendersOneRowWithProgressAndErrorNotes()
    {
        var job = new TestAccountJob(
            42, "job-1", "failed", 5, 3, 2, "test_account_job_failed", "Two\naccounts failed.", "3 of 5\u2028done");

        XboxPartnerTableFormatter.TestAccountTable table = XboxPartnerTableFormatter.FormatTestAccountJob(job);

        Assert.Contains("BATCH ID", table.Text, StringComparison.Ordinal);
        Assert.Contains("job-1", table.Text, StringComparison.Ordinal);
        Assert.Contains("failed", table.Text, StringComparison.Ordinal);
        Assert.Equal(2, table.Notes.Count);
        Assert.StartsWith("Progress: 3 of 5", table.Notes[0], StringComparison.Ordinal);
        Assert.StartsWith("Error: test_account_job_failed: Two", table.Notes[1], StringComparison.Ordinal);
        Assert.All(table.Notes, note =>
        {
            Assert.DoesNotContain('\n', note);
            Assert.DoesNotContain('\u2028', note);
        });
    }

    [Fact]
    public void FormatTestAccountJob_OmitsEmptyNotes()
    {
        var job = new TestAccountJob(42, "job-1", "succeeded", 5, 5, 0, null, " ", null);

        Assert.Empty(XboxPartnerTableFormatter.FormatTestAccountJob(job).Notes);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"accounts":[]}""")]
    [InlineData("""{"batch":{"id":1,"batchSize":1,"createdBy":1,"isComplete":true}}""")]
    [InlineData("""{"batch":{"id":1,"batchSize":1,"createdBy":1,"isComplete":true},"accounts":[null]}""")]
    public void FormatTestAccountBatch_RejectsMalformedResponses(string body)
    {
        Assert.Throws<InvalidDataException>(() => XboxPartnerTableFormatter.FormatTestAccountBatch(body));
    }

    private static string[] SplitColumns(string header) =>
        header.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
