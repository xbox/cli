// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.CommandLine;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Checks that table columns start at the same terminal cell on every row, whatever script the
/// values are written in.
/// </summary>
/// <remarks>
/// Each expected table is written out by hand, padded by the cell widths that
/// <see cref="DisplayWidthTests"/> pins, so it doesn't depend on the renderer's own width
/// function. A renderer that padded by <see cref="string.Length"/> would fail these.
/// </remarks>
public class TableAlignmentTests
{
    /// <summary>
    /// A Japanese name is six code units and twelve cells, a combining accent is a code unit that
    /// draws nothing, and a ZWJ family is eleven code units and two cells. REGION still starts at
    /// the same cell on every row.
    /// </summary>
    [Fact]
    public void ColumnsLineUpByTerminalCells()
    {
        var table = TableFormatter.Render(
            [new TableColumn("BUILD NAME", "BuildName", 40), new TableColumn("REGION", "Region", 20)],
            [
                ["nod-arena", "EastUs"],
                ["日本語ビルド", "JapanEast"],
                ["Jose\u0301 build", "WestUs"],
                ["\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466 crew", "NorthEurope"],
            ]);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "BUILD NAME    REGION",
                "------------  -----------",
                "nod-arena     EastUs",
                "日本語ビルド  JapanEast",
                "Jose\u0301 build    WestUs",
                "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466 crew       NorthEurope"),
            table);
    }

    /// <summary>
    /// A wide value is cut to its column's cells, not its code units. The ellipsis leaves nine
    /// cells, four wide characters fill eight, and padding closes the one-cell gap.
    /// </summary>
    [Fact]
    public void WideValuesAreTruncatedAndPaddedByTerminalCells()
    {
        var table = TableFormatter.Render(
            [new TableColumn("NAME", "name", 12), new TableColumn("REGION", "region", 20)],
            [
                ["日本語ビルドテスト", "JapanEast"],
                ["aaaaaaaaaaaaaaaa", "WestUs"],
            ]);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "NAME          REGION",
                "------------  ---------",
                "日本語ビ...   JapanEast",
                "aaaaaaaaa...  WestUs"),
            table);
    }

    /// <summary>
    /// The truncation contract, stated directly: the budget is cells, the ellipsis costs three of
    /// them, and a cluster that will not fit in what is left is dropped rather than split.
    /// </summary>
    [Theory]
    // Nothing to truncate.
    [InlineData("build", 10, "build")]
    [InlineData("日本語", 6, "日本語")]
    // Narrow values spend one cell each, so nine characters and an ellipsis fill twelve.
    [InlineData("aaaaaaaaaaaaaaaa", 12, "aaaaaaaaa...")]
    // Wide values spend two, so only four fit in the nine cells the ellipsis leaves. The fifth is
    // dropped whole rather than split, which is what leaves the result a cell short.
    [InlineData("日本語ビルドテスト", 12, "日本語ビ...")]
    // Below the ellipsis' own width there is no room to signal the cut.
    [InlineData("abcdef", 3, "abc")]
    [InlineData("日本語", 3, "日")]
    [InlineData("日本語", 2, "日")]
    [InlineData("日本語", 1, "")]
    // A value one cell too wide still loses three to the ellipsis.
    [InlineData("abcdef", 5, "ab...")]
    [InlineData("abcdef", 4, "a...")]
    public void TruncateSpendsACellBudget(string value, int width, string expected) =>
        Assert.Equal(expected, TableFormatter.Truncate(value, width));

    /// <summary>
    /// A width of zero cannot reach a rendered column — every view's header is at least one cell
    /// wide — but the guard is what makes that true by construction rather than by argument.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TruncateToNoWidthYieldsNothing(int width) =>
        Assert.Equal(string.Empty, TableFormatter.Truncate("日本語", width));
}
