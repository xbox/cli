// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public sealed class XboxLiveLineDiffTests
{
    [Fact]
    public void ChangesFarApart_AreSeparateHunks()
    {
        string before = string.Join('\n', Enumerable.Range(1, 20).Select(line => $"line {line}")) + "\n";
        string after = before.Replace("line 2\n", "line two\n", StringComparison.Ordinal).Replace("line 19\n", "line nineteen\n", StringComparison.Ordinal);

        IReadOnlyList<string> lines = XboxLiveLineDiff.Compare("a", "b", before, after).Lines;

        Assert.Equal(
            [
                "--- a", "+++ b",
                "@@ -1,5 +1,5 @@", " line 1", "-line 2", "+line two", " line 3", " line 4", " line 5",
                "@@ -16,5 +16,5 @@", " line 16", " line 17", " line 18", "-line 19", "+line nineteen", " line 20",
            ],
            lines);
    }

    [Fact]
    public void ChangesSixLinesApart_ShareAHunk()
    {
        string before = string.Join('\n', Enumerable.Range(1, 12).Select(line => $"line {line}"));
        string after = before.Replace("line 2", "two", StringComparison.Ordinal).Replace("line 9", "nine", StringComparison.Ordinal);

        IReadOnlyList<string> lines = XboxLiveLineDiff.Compare("a", "b", before, after).Lines;

        Assert.Single(lines, line => line.StartsWith("@@", StringComparison.Ordinal));
        Assert.Equal("@@ -1,12 +1,12 @@", lines[2]);
    }

    [Fact]
    public void LineEndings_AreNotDifferences()
    {
        Assert.Equal(["--- a", "+++ b"], XboxLiveLineDiff.Compare("a", "b", "<A>\r\n<B />\r\n</A>\r\n", "<A>\n<B />\n</A>\n").Lines);
    }

    [Fact]
    public void AMissingFinalNewline_IsMarkedAsGitMarksIt()
    {
        Assert.Equal(
            ["--- a", "+++ b", "@@ -1,2 +1,2 @@", " <A>", "-</A>", XboxLiveLineDiff.NoNewlineAtEnd, "+</A>"],
            XboxLiveLineDiff.Compare("a", "b", "<A>\n</A>", "<A>\n</A>\n").Lines);
        Assert.Equal(
            ["--- a", "+++ b", "@@ -1 +1 @@", "-<A />", "+<B />", XboxLiveLineDiff.NoNewlineAtEnd],
            XboxLiveLineDiff.Compare("a", "b", "<A />\n", "<B />").Lines);
    }

    [Fact]
    public void AnEmptyDocument_HasNoLines()
    {
        Assert.Equal(["--- a", "+++ b", "@@ -0,0 +1 @@", "+<A />"], XboxLiveLineDiff.Compare("a", "b", string.Empty, "<A />\n").Lines);
    }

    [Fact]
    public void TooManyEdits_ShowTheChangedRegionRemovedAndAddedWhole()
    {
        int count = XboxLiveLineDiff.MaxEdits + 10;
        string before = "<Header />\n" + string.Join('\n', Enumerable.Range(0, count).Select(line => $"old {line}")) + "\n<Footer />\n";
        string after = "<Header />\n" + string.Join('\n', Enumerable.Range(0, count).Select(line => $"new {line}")) + "\n<Footer />\n";

        (IReadOnlyList<string> lines, bool minimal) = XboxLiveLineDiff.Compare("a", "b", before, after);

        Assert.False(minimal);
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"@@ -1,{count + 2} +1,{count + 2} @@"), lines[2]);
        Assert.True(XboxLiveLineDiff.Compare("a", "b", "x\n", "y\n").Minimal);
        Assert.Equal(count, lines.Count(line => line.StartsWith("-old", StringComparison.Ordinal)));
        Assert.Equal(count, lines.Count(line => line.StartsWith("+new", StringComparison.Ordinal)));
        List<string> hunk = [.. lines.Skip(3)];
        Assert.True(hunk.FindLastIndex(line => line.StartsWith('-')) < hunk.FindIndex(line => line.StartsWith('+')));
    }

    [Fact]
    public void Decode_UsesTheByteOrderMarkAndNeverTheXmlDeclaration()
    {
        const string Text = "<?xml version=\"1.0\" encoding=\"utf-16\"?><Name>Nod Arène</Name>";

        Assert.Equal(Text, XboxLiveDocumentText.Decode(Encoding.UTF8.GetBytes(Text)));
        Assert.Equal(Text, XboxLiveDocumentText.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Text)]));
        Assert.Equal(Text, XboxLiveDocumentText.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Text)]));
        Assert.Equal(Text, XboxLiveDocumentText.Decode([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Text)]));
        Assert.Equal(Text, XboxLiveDocumentText.Decode([0xFF, 0xFE, 0x00, 0x00, .. Encoding.UTF32.GetBytes(Text)]));
        Assert.Equal(Text, XboxLiveDocumentText.Decode([0x00, 0x00, 0xFE, 0xFF, .. new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes(Text)]));
    }

    [Fact]
    public void Hash_IsUppercaseHexSha1OfTheBytes() =>
        Assert.Equal("A9993E364706816ABA3E25717850C26C9CD0D89D", XboxLiveConfigurationComparison.Hash(Encoding.ASCII.GetBytes("abc")));
}
