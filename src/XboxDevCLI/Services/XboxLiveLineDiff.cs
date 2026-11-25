// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace XboxDevCLI.Services;

/// <summary>Turns a configuration document's bytes into text for a line diff.</summary>
internal static class XboxLiveDocumentText
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Decodes by byte order mark, and as UTF-8 without one. The XML declaration's encoding is
    /// never used: some published documents are UTF-8 bytes that declare <c>utf-16</c>.
    /// </summary>
    public static string Decode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ReadOnlySpan<byte> bytes = content;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Utf8.GetString(bytes[3..]);
        }

        // UTF-32's little-endian mark starts with UTF-16's, so it's checked first.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return Encoding.UTF32.GetString(bytes[4..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(bytes[4..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        return Utf8.GetString(bytes);
    }
}

/// <summary>A unified line diff, as <c>diff -u</c> and <c>git diff</c> write it.</summary>
internal static class XboxLiveLineDiff
{
    private const int Context = 3;

    /// <summary>
    /// Above this many inserted and deleted lines (a changed line is one of each), the changed
    /// region is shown as removed and added whole, which bounds the time and memory a diff takes.
    /// </summary>
    internal const int MaxEdits = 2000;

    /// <summary>The line that follows a last line with no line break, as <c>git diff</c> writes it.</summary>
    internal const string NoNewlineAtEnd = "\\ No newline at end of file";

    /// <summary>Marks, inside this class, a last line with no line break. A split line never holds one.</summary>
    private const char NoNewline = '\n';

    private enum Operation
    {
        Equal,
        Delete,
        Insert,
    }

    /// <summary>
    /// The diff of <paramref name="oldText"/> and <paramref name="newText"/>, with <c>---</c> and
    /// <c>+++</c> headers, and whether it's minimal: one with more than <see cref="MaxEdits"/>
    /// inserted and deleted lines shows the changed region removed and added whole. A null side
    /// doesn't have the document.
    /// </summary>
    public static (IReadOnlyList<string> Lines, bool Minimal) Compare(string oldHeader, string newHeader, string? oldText, string? newText)
    {
        string[] before = SplitLines(oldText);
        string[] after = SplitLines(newText);
        var lines = new List<string>
        {
            "--- " + (oldText is null ? "/dev/null" : oldHeader),
            "+++ " + (newText is null ? "/dev/null" : newHeader),
        };
        List<(Operation Operation, string Text)> edits = Diff(before, after, out bool minimal);
        int[] changes = [.. Enumerable.Range(0, edits.Count).Where(index => edits[index].Operation != Operation.Equal)];
        int next = 0;
        while (next < changes.Length)
        {
            int start = Math.Max(0, changes[next] - Context);
            int end = changes[next] + 1;
            while (next + 1 < changes.Length && changes[next + 1] - end <= 2 * Context)
            {
                next++;
                end = changes[next] + 1;
            }

            end = Math.Min(edits.Count, end + Context);
            next++;

            int oldLine = edits.Take(start).Count(edit => edit.Operation != Operation.Insert);
            int newLine = edits.Take(start).Count(edit => edit.Operation != Operation.Delete);
            var hunk = edits.Skip(start).Take(end - start).ToArray();
            int oldCount = hunk.Count(edit => edit.Operation != Operation.Insert);
            int newCount = hunk.Count(edit => edit.Operation != Operation.Delete);
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"@@ -{Range(oldLine, oldCount)} +{Range(newLine, newCount)} @@"));
            foreach ((Operation operation, string text) in hunk)
            {
                string prefix = operation switch
                {
                    Operation.Delete => "-",
                    Operation.Insert => "+",
                    _ => " ",
                };
                if (text.EndsWith(NoNewline))
                {
                    lines.Add(prefix + text[..^1]);
                    lines.Add(NoNewlineAtEnd);
                }
                else
                {
                    lines.Add(prefix + text);
                }
            }
        }

        return (lines, minimal);
    }

    private static string Range(int before, int count) => count switch
    {
        0 => string.Create(CultureInfo.InvariantCulture, $"{before},0"),
        1 => string.Create(CultureInfo.InvariantCulture, $"{before + 1}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{before + 1},{count}"),
    };

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        if (text.EndsWith('\n') || text.EndsWith('\r'))
        {
            return lines[..^1];
        }

        lines[^1] += NoNewline;
        return lines;
    }

    /// <summary>The edits that turn <paramref name="before"/> into <paramref name="after"/>, in order.</summary>
    private static List<(Operation, string)> Diff(string[] before, string[] after, out bool minimal)
    {
        int prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix &&
               before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        var edits = new List<(Operation, string)>();
        edits.AddRange(before.Take(prefix).Select(line => (Operation.Equal, line)));
        string[] oldCore = before[prefix..(before.Length - suffix)];
        string[] newCore = after[prefix..(after.Length - suffix)];
        List<(Operation, string)>? shortest = Myers(oldCore, newCore);
        minimal = shortest is not null;
        edits.AddRange(shortest ?? Replace(oldCore, newCore));
        edits.AddRange(before.Skip(before.Length - suffix).Select(line => (Operation.Equal, line)));
        return edits;
    }

    private static IEnumerable<(Operation, string)> Replace(string[] before, string[] after) =>
        before.Select(line => (Operation.Delete, line)).Concat(after.Select(line => (Operation.Insert, line)));

    /// <summary>
    /// Myers' shortest edit script, or null when it needs more than <see cref="MaxEdits"/> edits.
    /// </summary>
    private static List<(Operation, string)>? Myers(string[] before, string[] after)
    {
        int n = before.Length;
        int m = after.Length;
        int max = n + m;
        if (max == 0)
        {
            return [];
        }

        int offset = max + 1;
        int[] v = new int[(2 * max) + 3];
        var trace = new List<int[]>();
        int finalD = -1;
        for (int d = 0; d <= Math.Min(max, MaxEdits) && finalD < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && before[x] == after[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    finalD = d;
                    break;
                }
            }

            trace.Add(v[(offset - d)..(offset + d + 1)]);
        }

        if (finalD < 0)
        {
            return null;
        }

        var edits = new List<(Operation, string)>();
        int px = n;
        int py = m;
        for (int d = finalD; d > 0; d--)
        {
            int[] previous = trace[d - 1];
            int k = px - py;
            int previousK = k == -d || (k != d && previous[k - 1 + (d - 1)] < previous[k + 1 + (d - 1)]) ? k + 1 : k - 1;
            int previousX = previous[previousK + (d - 1)];
            int previousY = previousX - previousK;
            while (px > previousX && py > previousY)
            {
                edits.Add((Operation.Equal, before[--px]));
                py--;
            }

            if (px == previousX)
            {
                edits.Add((Operation.Insert, after[--py]));
            }
            else
            {
                edits.Add((Operation.Delete, before[--px]));
            }
        }

        while (px > 0 && py > 0)
        {
            edits.Add((Operation.Equal, before[--px]));
            py--;
        }

        edits.Reverse();
        return edits;
    }
}
