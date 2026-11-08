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
        var columns = new List<Column>();
        var first = Math.Max(0, startColumn);
        var index = first;
        while (columns.Count < width && index < sheet.Columns.Count)
        {
            var candidate = sheet.Columns[index];
            var startedHere = index == first;
            index++;

            // The identifier column is skipped so a block pasted into the first data
            // column cannot overwrite keys — unless the paste starts on the identifier
            // itself, which is how identifiers are pasted in the first place.
            if (candidate.Type is ColumnType.Id && !startedHere)
                continue;

            columns.Add(candidate);
        }

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
    /// Overwrites cells starting at the cursor. The identifier column is never
    /// overwritten, so a paste can never create duplicate keys.
    /// </summary>
    public static MultiCellEditCommand? BuildOverwrite(
        SheetData sheet,
        int startRow,
        int startColumn,
        ClipboardPayload payload,
        string label = "Paste")
    {
        if (payload.Rows.Count == 0 || sheet.Lines.Count == 0)
            return null;

        var builder = new MultiCellEditCommand.Builder(label);

        for (var rowOffset = 0; rowOffset < payload.Rows.Count; rowOffset++)
        {
            var rowIndex = startRow + rowOffset;
            if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
                continue;

            var row = sheet.Lines[rowIndex];
            var values = payload.Rows[rowOffset];

            for (var columnOffset = 0; columnOffset < payload.Columns.Count; columnOffset++)
            {
                var columnName = payload.Columns[columnOffset];
                var column = sheet.GetColumn(columnName);
                // The identifier is written like any other value here: FromText already
                // decided which columns the block covers, and pasting a column of ids is
                // a normal thing to do.
                if (column is null)
                    continue;

                if (columnOffset >= values.Count)
                    continue;

                builder.Add(row, columnName, values[columnOffset]);
            }
        }

        return builder.Build();
    }

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
