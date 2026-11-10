using PalaceDB.Core.Commands;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Editing;

public enum PasteMode
{
    /// <summary>Writes into existing rows, keeping their identifiers.</summary>
    Overwrite,

    /// <summary>Creates new rows above the cursor.</summary>
    Insert,
}

/// <summary>Builds undoable paste commands from a clipboard payload.</summary>
public static class PasteService
{
    /// <summary>
    /// Turns tab separated text into a payload for <see cref="BuildOverwrite"/> or
    /// <see cref="BuildInsert"/> (R5). The block is placed at <paramref name="startColumn"/>
    /// and filled left to right, skipping identifier columns so a paste can never create
    /// duplicate keys.
    /// <para>
    /// Every cell is parsed with the rules of the column it lands in. One unparsable cell
    /// rejects the whole block with a message naming the cell, because writing half a
    /// block is worse than writing none.
    /// </para>
    /// </summary>
    public static ClipboardPayload? FromText(
        SheetData sheet,
        int startColumn,
        IReadOnlyList<IReadOnlyList<string>> grid,
        out string? error)
    {
        error = null;

        if (grid.Count == 0 || grid[0].Count == 0)
        {
            error = "The clipboard does not contain any cells.";
            return null;
        }

        var width = 0;
        foreach (var row in grid)
            width = Math.Max(width, row.Count);

        // Target columns in order, skipping the identifier column: the block flows into
        // the next usable column instead of stopping at the identifier.
        var columns = TargetColumns(sheet, startColumn, width);

        if (columns.Count == 0)
        {
            error = "There is no column to paste into at this position.";
            return null;
        }

        if (columns.Count < width)
        {
            error = $"The block is {width} columns wide but only {columns.Count} column(s) are available here.";
            return null;
        }

        var payload = new ClipboardPayload { Sheet = sheet.Name };
        foreach (var column in columns)
            payload.Columns.Add(column.Name);

        for (var rowIndex = 0; rowIndex < grid.Count; rowIndex++)
        {
            var cells = grid[rowIndex];
            var values = new List<CdbValue>(columns.Count);

            for (var offset = 0; offset < columns.Count; offset++)
            {
                if (offset >= cells.Count)
                    break;

                var column = columns[offset];
                try
                {
                    // A null means "this optional column has no value here", which the
                    // paste commands translate into removing the key.
                    values.Add(ValueCodec.ParseText(column, cells[offset])!);
                }
                catch (Exception ex)
                {
                    error = $"Row {rowIndex + 1}, column '{column.Name}': {ex.Message}";
                    return null;
                }
            }

            payload.Rows.Add(values);
        }

        return payload;
    }

    /// <summary>
    /// Overwrites cells starting at the cursor. The block is laid out by position: its
    /// first cell goes into the cursor cell and the rest flows right and down, exactly
    /// like a block of tab separated text. Matching by column name instead would make a
    /// copied column land back in the column it came from.
    /// </summary>
    public static MultiCellEditCommand? BuildOverwrite(
        SheetData sheet,
        int startRow,
        int startColumn,
        ClipboardPayload payload,
        string label = "Paste") =>
        BuildOverwrite(sheet, startRow, startColumn, payload, out _, label);

    /// <summary>
    /// Same as the four argument overload, reporting how many cells could not be written
    /// (no column left, or the value does not fit the target column's type).
    /// </summary>
    public static MultiCellEditCommand? BuildOverwrite(
        SheetData sheet,
        int startRow,
        int startColumn,
        ClipboardPayload payload,
        out int skipped,
        string label = "Paste")
    {
        skipped = 0;
        if (payload.Rows.Count == 0 || sheet.Lines.Count == 0)
            return null;

        var targets = TargetColumns(sheet, startColumn, payload.Columns.Count);
        if (targets.Count == 0)
            return null;

        // The payload remembers which table it came from. When that is this table the
        // source columns are available too, which lets a value be converted through the
        // text its own editor would show (an enum through its label, for instance).
        var sources = payload.Sheet == sheet.Name
            ? payload.Columns.Select(name => sheet.GetColumn(name)).ToList()
            : new List<Column?>();

        var builder = new MultiCellEditCommand.Builder(label);

        for (var rowOffset = 0; rowOffset < payload.Rows.Count; rowOffset++)
        {
            var rowIndex = startRow + rowOffset;
            if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
            {
                skipped += payload.Columns.Count;
                continue;
            }

            var row = sheet.Lines[rowIndex];
            var values = payload.Rows[rowOffset];

            for (var columnOffset = 0; columnOffset < targets.Count; columnOffset++)
            {
                if (columnOffset >= values.Count)
                {
                    skipped++;
                    continue;
                }

                var target = targets[columnOffset];
                var source = columnOffset < sources.Count ? sources[columnOffset] : null;

                if (!TryConvert(values[columnOffset], target, source, out var value))
                {
                    skipped++;
                    continue;
                }

                builder.Add(row, target.Name, value);
            }

            // Cells of the block that had no column to go to: it is wider than the table.
            if (payload.Columns.Count > targets.Count)
                skipped += payload.Columns.Count - targets.Count;
        }

        return builder.Build();
    }

    /// <summary>
    /// The columns a block lands in, starting at <paramref name="startColumn"/>. The
    /// identifier column is skipped so a block pasted into the first data column cannot
    /// overwrite keys — unless the paste starts on the identifier itself.
    /// </summary>
    private static List<Column> TargetColumns(SheetData sheet, int startColumn, int count)
    {
        var columns = new List<Column>(count);
        var first = Math.Max(0, startColumn);
        var index = first;

        while (columns.Count < count && index < sheet.Columns.Count)
        {
            var candidate = sheet.Columns[index];
            var startedHere = index == first;
            index++;

            if (candidate.Type is ColumnType.Id && !startedHere)
                continue;

            columns.Add(candidate);
        }

        return columns;
    }

    /// <summary>
    /// Adapts a copied value to the column it lands in. A value that already has the
    /// right shape is written untouched (numbers keep their raw text); anything else is
    /// re-parsed from the text its own column would show, so a number can go into a text
    /// column and vice versa. Returns false when the value cannot be written at all.
    /// </summary>
    private static bool TryConvert(CdbValue? value, Column target, Column? source, out CdbValue? result)
    {
        result = null;

        // An empty cell stays empty: the key is removed, which is what "blank" means.
        if (value is null or CdbValue.Null)
            return true;

        if (Fits(target, value))
        {
            result = value;
            return true;
        }

        var text = source is null ? PlainText(value) : SearchEngine.TextOf(source, value);
        try
        {
            result = ValueCodec.ParseText(target, text);
            return true;
        }
        catch (CdbValidationException)
        {
            return false;
        }
    }

    /// <summary>True when the value already is what the column stores.</summary>
    private static bool Fits(Column column, CdbValue value) => column.Type switch
    {
        ColumnType.Text or ColumnType.Image or ColumnType.File or ColumnType.Formula
            or ColumnType.Id or ColumnType.Ref or ColumnType.Guid => value is CdbValue.Str,
        ColumnType.Enum => column.EnumAsString ? value is CdbValue.Str : value is CdbValue.Number,
        ColumnType.Int or ColumnType.Float or ColumnType.Color or ColumnType.Flags
            => value is CdbValue.Number,
        ColumnType.Bool => value is CdbValue.Bool,
        // Structured columns (List, Properties, Dynamic, ...) are copied verbatim.
        _ => true,
    };

    private static string PlainText(CdbValue value) => value switch
    {
        CdbValue.Str s => s.Value,
        CdbValue.Number n => n.ToString(),
        CdbValue.Bool b => b.Value ? "true" : "false",
        _ => string.Empty,
    };

    /// <summary>
    /// Creates new rows from the payload. Identifiers that already exist receive a
    /// numeric suffix, which keeps the table valid without discarding pasted data.
    /// </summary>
    public static RowInsertCommand? BuildInsert(
        SheetData sheet,
        int startRow,
        ClipboardPayload payload,
        Database database,
        string label = "Insert pasted rows")
    {
        if (payload.Rows.Count == 0)
            return null;

        var idColumn = sheet.IdColumn;
        var existing = new HashSet<string>(
            sheet.Lines.Select(r => idColumn is null ? string.Empty : r.GetString(idColumn.Name) ?? string.Empty)
                .Where(id => id.Length > 0),
            StringComparer.Ordinal);

        var newRows = new List<CdbObject>(payload.Rows.Count);

        for (var rowOffset = 0; rowOffset < payload.Rows.Count; rowOffset++)
        {
            var row = new CdbObject();

            // Start from the column defaults so required fields are never missing.
            foreach (var column in sheet.Columns)
            {
                var value = TypeDefaults.Create(column, database, sheet);
                if (value is not null)
                    row.Set(column.Name, value);
            }

            var values = payload.Rows[rowOffset];
            for (var columnOffset = 0; columnOffset < payload.Columns.Count; columnOffset++)
            {
                if (columnOffset >= values.Count)
                    continue;

                var columnName = payload.Columns[columnOffset];
                var column = sheet.GetColumn(columnName);
                if (column is null)
                    continue;
                if (column.Type is ColumnType.Id)
                    continue;

                row.Set(columnName, values[columnOffset]);
            }

            if (idColumn is not null)
            {
                var proposed = string.Empty;
                var idOffset = payload.Columns.FindIndex(c => string.Equals(c, idColumn.Name, StringComparison.Ordinal));
                if (idOffset >= 0 && idOffset < values.Count && values[idOffset] is CdbValue.Str s)
                    proposed = s.Value;

                if (proposed.Length == 0)
                    proposed = $"row{rowOffset + 1}";

                row.Set(idColumn.Name, new CdbValue.Str(MakeUnique(proposed, existing)));
            }

            newRows.Add(row);
        }

        var index = Math.Clamp(startRow, 0, sheet.Lines.Count);
        return new RowInsertCommand(sheet, index, newRows, label);
    }

    /// <summary>
    /// Appends "_2", "_3", ... until the identifier is free. A numeric id keeps its form:
    /// "123_2" is not a valid id any more, so the suffix is appended as digits instead
    /// ("1232", "1233", ...), and leading zeros survive ("007" -> "0072").
    /// </summary>
    public static string MakeUnique(string proposed, ISet<string> existing)
    {
        if (existing.Add(proposed))
            return proposed;

        var numeric = ValueCodec.IsNumericId(proposed);
        for (var suffix = 2; suffix < 10000; suffix++)
        {
            var candidate = numeric ? $"{proposed}{suffix}" : $"{proposed}_{suffix}";
            if (existing.Add(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Could not generate a unique identifier.");
    }
}
