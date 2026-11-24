// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using XboxDevCLI.CommandLine;

namespace XboxDevCLI.Output;

/// <summary>
/// Renders an API response as a plain text table, driven by the <see cref="TableView"/> a command
/// declares in its metadata: many records as rows, or a single record as a one-row table, or a row
/// per item of its expanded list.
/// </summary>
/// <remarks>
/// Deliberately not a generic "flatten any JSON" formatter. A command opts in by naming its
/// source and its columns, so nested data is never silently dropped to invent a column. Output is
/// plain and uncolored: it is a human convenience, and anything parsed by a script or agent should
/// ask for JSON.
/// <para>
/// Every width here — the column sizes, the padding, and the truncation budget — is counted in
/// terminal cells by <see cref="DisplayWidth"/>, not in UTF-16 code units, so a value written in
/// CJK, or carrying a combining mark or an emoji, lines up with the rest of its column. The
/// seller tables render through <see cref="Render"/> for the same reason: two renderers with two
/// copies of the arithmetic drift apart one fix at a time.
/// </para>
/// </remarks>
internal static class TableFormatter
{
    private const string NullCell = "-";

    /// <summary>What a truncated cell ends with, and how many cells that costs.</summary>
    private const string Ellipsis = "...";

    private const int EllipsisWidth = 3;

    /// <summary>
    /// What every table renderer prints instead of bare headers when there is nothing to show.
    /// </summary>
    /// <remarks>
    /// Shared rather than duplicated because the CLI has a second renderer
    /// (<c>XboxPartnerTableFormatter</c>, which reads gateway responses rather than PlayFab ones).
    /// The two having different empty states is an inconsistency no user can predict, and it is
    /// only reachable through a code path nobody exercises until a collection happens to come back
    /// empty — so the notice lives in one place.
    /// </remarks>
    internal const string EmptyNotice = "No results.";

    /// <summary>
    /// Renders <paramref name="payload"/> using <paramref name="view"/>.
    /// </summary>
    /// <param name="payload">
    /// The response payload. For PlayFab responses this is the inner <c>data</c> object, so the
    /// envelope does not have to be described by every view.
    /// </param>
    /// <param name="view">The declared collection and columns.</param>
    /// <returns>The rendered table, or a short notice when the collection is empty.</returns>
    /// <exception cref="InvalidOperationException">
    /// The response doesn't have the shape the view declares, which <see cref="JsonElement"/>
    /// reports when it's read. A missing collection fails rather than reporting no results, which
    /// would say "nothing to show" for a field the service renamed. An explicitly null collection
    /// or record is treated as empty, because null is the service saying it has no value.
    /// </exception>
    internal static string Format(JsonElement payload, TableView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        ValidateColumns(view);

        if (view.IsRecord)
        {
            return FormatRecord(payload, view);
        }

        // A merged view reads each of its collections by the same rule, labelling each one's rows.
        var rows = view.Sources.Count > 0
            ? [.. view.Sources.SelectMany(source => ReadCollection(payload, source.Collection, view, source.Label))]
            : ReadCollection(payload, view.Collection, view, label: null);

        return rows.Count == 0
            ? EmptyNotice
            : Render(view.Columns, rows);
    }

    /// <summary>Renders a collection, including a gateway response that is a bare JSON array.</summary>
    internal static string FormatCollection(JsonElement collection, TableView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        ValidateColumns(view);

        var rows = ReadRows(collection, view.Collection, view, label: null);
        return rows.Count == 0
            ? EmptyNotice
            : Render(view.Columns, rows);
    }

    /// <summary>
    /// Reads the rows of the collection named <paramref name="name"/> in
    /// <paramref name="payload"/>.
    /// </summary>
    private static List<string[]> ReadCollection(JsonElement payload, string name, TableView view, string? label)
    {
        // A missing collection is left as a default element, which JsonElement refuses to read.
        _ = payload.TryGetProperty(name, out var collection);

        // An explicitly null collection is the service saying "no value", which is the same thing
        // to a reader as an empty one. Failing here would make a legitimately empty response exit
        // non-zero while the JSON path prints an empty result happily.
        return collection.ValueKind == JsonValueKind.Null
            ? []
            : ReadRows(collection, name, view, label);
    }

    /// <summary>
    /// Reads one row per record of <paramref name="collection"/>, or per item of each record's
    /// expanded list when the view declares one.
    /// </summary>
    private static List<string[]> ReadRows(JsonElement collection, string name, TableView view, string? label)
    {
        // Some responses key their records by id or name rather than returning an array — file
        // metadata is keyed by file name, for example. Those are still a list of records to a
        // reader, so the values are enumerated in the order the service returned them.
        var records = collection.ValueKind == JsonValueKind.Array
            ? collection.EnumerateArray().ToArray()
            : collection.EnumerateObject().Select(property => property.Value).ToArray();

        var rows = new List<string[]>();
        foreach (var record in records)
        {
            foreach (var item in ExpandedItems(record, view.Expand))
            {
                rows.Add([.. view.Columns.Select(column =>
                    column.IsSourceLabel ? label ?? NullCell
                    : column.OnlyIn is { } onlyIn && !onlyIn.Contains(name) ? NullCell
                    : ReadCell(record, column, view.Expand, item))]);
            }
        }

        return rows;
    }

    /// <summary>
    /// The items of a record's expanded list, one per row. A view that expands nothing, or a
    /// record whose list is absent, null, or empty, gets one undefined item, so the record keeps
    /// its row.
    /// </summary>
    private static IEnumerable<JsonElement> ExpandedItems(JsonElement record, string? expand)
    {
        if (expand is null
            || !record.TryGetProperty(expand, out var items)
            || items.ValueKind == JsonValueKind.Null)
        {
            return [default];
        }

        var itemRecords = items.EnumerateArray().ToArray();
        return itemRecords.Length > 0 ? itemRecords : [default];
    }

    private static void ValidateColumns(TableView view)
    {
        // An element-itself column consumes the whole element, so it cannot share a view.
        if (view.Columns.Count > 1 && view.Columns.Any(column => column.IsElementItself))
        {
            throw new InvalidOperationException(
                $"The table view for '{view.Collection}' declares a column that reads the array " +
                "element itself alongside other columns. A scalar collection has one value per " +
                "element, so it must declare exactly one column.");
        }
    }

    /// <summary>
    /// Renders a single record as a one-row table, or as a row per item of its expanded list, with
    /// the same headers and separator a collection gets.
    /// </summary>
    /// <remarks>
    /// One layout for everything: a reader should not have to work out which shape a command
    /// returns before they can read its output, and a one-row table still pastes into a report or
    /// pipes to <c>awk</c> the way the list commands do.
    /// <para>
    /// The cost is width, since a row must fit across a terminal. That is answered by declaring
    /// fewer columns rather than by turning the table on its side — a view is an editorial choice
    /// about the few fields worth seeing, and anyone who needs all of them wants JSON.
    /// </para>
    /// </remarks>
    private static string FormatRecord(JsonElement payload, TableView view)
    {
        var record = payload;

        if (!string.IsNullOrEmpty(view.Collection))
        {
            // A missing record is left as a default element, which JsonElement refuses to read.
            _ = payload.TryGetProperty(view.Collection, out record);

            if (record.ValueKind == JsonValueKind.Null)
            {
                return EmptyNotice;
            }
        }

        var rows = ExpandedItems(record, view.Expand)
            .Select(item => view.Columns.Select(column => ReadCell(record, column, view.Expand, item)).ToArray())
            .ToArray();
        return Render(view.Columns, rows);
    }

    /// <summary>
    /// Reads a cell value, walking a dotted path so a column can surface a nested field
    /// explicitly rather than having the renderer guess at nesting. A path through the view's
    /// expanded list reads the row's item rather than the whole list, unless the column sets
    /// <see cref="TableColumn.ReadsWholeList"/>.
    /// </summary>
    private static string ReadCell(
        JsonElement record,
        TableColumn column,
        string? expand = null,
        JsonElement item = default)
    {
        // A scalar collection: the element is the value. Guaranteed single-column by Format.
        var current = record;

        if (!column.IsElementItself)
        {
            var segments = column.Property.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (expand is not null && !column.ReadsWholeList && segments.Length > 0 && segments[0] == expand)
            {
                // A record with no items gets an undefined one, whose fields read as missing.
                if (item.ValueKind == JsonValueKind.Undefined)
                {
                    return NullCell;
                }

                current = item;
                segments = segments[1..];
            }

            // A field that's missing, or under a nested value that's null or not an object, reads
            // as missing. Only the record itself must be an object, which TryGetProperty enforces.
            for (var index = 0; index < segments.Length; index++)
            {
                if ((index > 0 && current.ValueKind != JsonValueKind.Object) ||
                    !current.TryGetProperty(segments[index], out var next))
                {
                    return NullCell;
                }

                current = next;
            }
        }

        // Mask only a value the response actually carried, so the table never claims a secret
        // exists where there was none. Decided on the JSON value kind rather than the rendered
        // text: comparing against the "-" placeholder would leak a secret whose value happens to
        // be the string "-".
        //
        // Applied after resolving the value rather than inside the branch above, so it covers a
        // scalar collection too. Redaction that silently does nothing for one column shape is the
        // same defect this design exists to remove: a declaration the renderer ignores.
        if (column.Redact && current.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return TableColumn.RedactedCell;
        }

        return column.Transform is null ? Stringify(current) : column.Transform(current);
    }

    private static string Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? NullCell,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => NullCell,
        // A column pointing at an object or array is a declaration bug, but rendering the raw
        // JSON is more diagnosable than an empty cell.
        _ => value.GetRawText(),
    };

    /// <summary>
    /// Lays out already-extracted rows under <paramref name="columns"/>.
    /// </summary>
    /// <remarks>
    /// Shared with <c>XboxPartnerTableFormatter</c>, which reads gateway responses rather than
    /// PlayFab ones but owes its readers the same table. Only the extraction differs, so only the
    /// extraction is written twice.
    /// </remarks>
    internal static string Render(IReadOnlyList<TableColumn> columns, IReadOnlyList<string[]> rows)
    {
        var normalized = rows
            .Select(row => row.Select(NormalizeCell).ToArray())
            .ToArray();

        // Measured in cells and capped as it goes: a column can never be wider than its declared
        // maximum, so a cell far wider than that does not have to be walked to the end to be
        // known too wide.
        var widths = columns
            .Select((column, index) => Math.Max(
                DisplayWidth.MeasureCapped(column.Header, column.MaximumWidth),
                normalized
                    .Select(row => DisplayWidth.MeasureCapped(row[index], column.MaximumWidth))
                    .DefaultIfEmpty(0)
                    .Max()))
            .ToArray();

        var builder = new StringBuilder();
        AppendRow(builder, columns.Select(column => column.Header).ToArray(), widths);
        AppendRow(builder, widths.Select(width => new string('-', width)).ToArray(), widths);
        foreach (var row in normalized)
        {
            AppendRow(builder, row, widths);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendRow(
        StringBuilder builder,
        IReadOnlyList<string> cells,
        IReadOnlyList<int> widths)
    {
        for (var index = 0; index < cells.Count; index++)
        {
            if (index > 0)
            {
                builder.Append("  ");
            }

            var cell = Truncate(cells[index], widths[index]);
            builder.Append(cell);

            // Padded by cells rather than with PadRight, which counts code units: a CJK value
            // needs half as many spaces after it as its length suggests, and a value carrying a
            // combining mark needs one more. The last column is left unpadded so rows do not end
            // in trailing whitespace.
            if (index == cells.Count - 1)
            {
                continue;
            }

            var padding = widths[index] - DisplayWidth.MeasureCapped(cell, widths[index]);
            if (padding > 0)
            {
                builder.Append(' ', padding);
            }
        }

        builder.AppendLine();
    }

    /// <summary>
    /// Replaces layout-breaking characters one-for-one so a value containing a newline, Unicode
    /// line separator, bidi override, or ANSI escape cannot break or visually reorder the table.
    /// </summary>
    internal static string NormalizeCell(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(IsUnsafeCellCharacter(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    internal static bool IsUnsafeCellCharacter(char character) =>
        char.IsControl(character)
        || char.GetUnicodeCategory(character) is
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        || IsBidiFormattingControl(character);

    private static bool IsBidiFormattingControl(char character) =>
        character is '\u061c' or '\u200e' or '\u200f'
        || character is >= '\u202a' and <= '\u202e'
        || character is >= '\u2066' and <= '\u2069';

    /// <summary>
    /// Truncates to <paramref name="width"/> terminal cells, ending with an ellipsis when the
    /// value is wider and the column has room for one.
    /// </summary>
    /// <remarks>
    /// Cuts on a grapheme cluster boundary rather than a UTF-16 code unit, so a value containing
    /// an emoji, a surrogate pair, or a combining mark cannot be split mid-character. Slicing by
    /// index emits a lone surrogate, which the console renders as a replacement character and
    /// which makes valid data look corrupt.
    /// <para>
    /// The budget is in cells because the column width is, and the two have to agree: measuring
    /// the column in cells while cutting by code unit would let a CJK value survive truncation at
    /// twice its column's width and push every later column right — the same misalignment, moved.
    /// The ellipsis costs three of those cells, and a cluster that will not fit in what remains is
    /// dropped whole, so a result can land one cell short of the width. Padding closes that gap.
    /// </para>
    /// </remarks>
    internal static string Truncate(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        // Measuring to width + 1 is enough to know the value is too wide, without walking a long
        // value to its end for an answer settled in its first few characters.
        if (DisplayWidth.MeasureCapped(value, width + 1) <= width)
        {
            return value;
        }

        // At or below the ellipsis' own width there is no room to signal the cut and still show
        // anything, so the value is cut short rather than replaced entirely by dots.
        var budget = width <= EllipsisWidth ? width : width - EllipsisWidth;
        var suffix = width <= EllipsisWidth ? string.Empty : Ellipsis;

        var kept = 0;
        var taken = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(remaining);
            var cells = DisplayWidth.MeasureTextElement(remaining[..length]);
            if (taken + cells > budget)
            {
                break;
            }

            taken += cells;
            kept += length;
            remaining = remaining[length..];
        }

        return string.Concat(value.AsSpan(0, kept), suffix);
    }
}
