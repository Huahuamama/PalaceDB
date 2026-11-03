using PalaceDB.Core.Model;

namespace PalaceDB.Core.Cursor;

/// <summary>
/// Turns a <see cref="CursorAnchor"/> back into concrete indices.
/// <para>
/// This is what fixes the original editor's cursor bug: after undo or a deletion the
/// selection is recomputed from the anchor, and when the anchor no longer exists the
/// position is clamped to the nearest valid cell instead of silently falling back to
/// row 0 / column 0.
/// </para>
/// </summary>
public static class CursorResolver
{
    /// <summary>Row and column index used when a sheet has no rows or no columns.</summary>
    public const int NoIndex = -1;

    /// <summary>Builds an anchor from a live position, capturing row id and column name.</summary>
    public static CursorAnchor CreateAnchor(SheetData sheet, int row, int column, SelectionRange? selection = null)
    {
        string? rowId = null;
        if (row >= 0 && row < sheet.Lines.Count)
        {
            var idColumn = sheet.IdColumn;
            if (idColumn is not null)
                rowId = sheet.Lines[row].GetString(idColumn.Name);
        }

        string? columnId = null;
        if (column >= 0 && column < sheet.Columns.Count)
            columnId = sheet.Columns[column].Name;

        return new CursorAnchor(sheet.Name, rowId, row, columnId, column, selection);
    }

    public static CursorPosition Resolve(CursorAnchor? anchor, Database database)
    {
        if (anchor is null)
            return CursorPosition.None;

        var sheet = database.GetSheet(anchor.SheetName);
        if (sheet is null)
            return CursorPosition.None;

        // A sheet without rows has no valid cell at all.
        if (sheet.Lines.Count == 0)
            return new CursorPosition(sheet.Name, NoIndex, NoIndex, null);

        var row = ResolveRow(sheet, anchor);
        var column = ResolveColumn(sheet, anchor);
        var selection = ClampSelection(anchor.Selection, sheet);

        return new CursorPosition(sheet.Name, row, column, selection);
    }

    /// <summary>
    /// Row resolution order: stable identifier, then the recorded index, then the
    /// closest valid index. Never returns 0 as a silent fallback.
    /// </summary>
    public static int ResolveRow(SheetData sheet, CursorAnchor anchor)
    {
        if (sheet.Lines.Count == 0)
            return NoIndex;

        if (!string.IsNullOrEmpty(anchor.RowId))
        {
            var idColumn = sheet.IdColumn;
            if (idColumn is not null)
            {
                for (var i = 0; i < sheet.Lines.Count; i++)
                    if (string.Equals(sheet.Lines[i].GetString(idColumn.Name), anchor.RowId, StringComparison.Ordinal))
                        return i;
            }
        }

        return Clamp(anchor.RowIndex, sheet.Lines.Count);
    }

    /// <summary>Column resolution order: stable name, then the recorded index, then the closest valid index.</summary>
    public static int ResolveColumn(SheetData sheet, CursorAnchor anchor)
    {
        if (sheet.Columns.Count == 0)
            return NoIndex;

        if (!string.IsNullOrEmpty(anchor.ColumnId))
        {
            for (var i = 0; i < sheet.Columns.Count; i++)
                if (string.Equals(sheet.Columns[i].Name, anchor.ColumnId, StringComparison.Ordinal))
                    return i;
        }

        return Clamp(anchor.ColumnIndex, sheet.Columns.Count);
    }

    private static SelectionRange? ClampSelection(SelectionRange? selection, SheetData sheet)
    {
        if (selection is not { } range)
            return null;
        if (sheet.Lines.Count == 0 || sheet.Columns.Count == 0)
            return null;

        return new SelectionRange(
            Clamp(range.AnchorRow, sheet.Lines.Count),
            Clamp(range.AnchorColumn, sheet.Columns.Count),
            Clamp(range.FocusRow, sheet.Lines.Count),
            Clamp(range.FocusColumn, sheet.Columns.Count));
    }

    private static int Clamp(int index, int count) => index < 0 ? 0 : Math.Min(index, count - 1);
}

/// <summary>
/// Rules that decide where the cursor goes after a structural change.
/// These encode the requirement "delete moves to the next row, or the previous row
/// when the last row was deleted".
/// </summary>
public static class CursorRules
{
    /// <summary>
    /// Row to select after removing <paramref name="count"/> rows starting at
    /// <paramref name="index"/>, when <paramref name="remaining"/> rows are left.
    /// </summary>
    public static int RowAfterDelete(int index, int count, int remaining)
    {
        if (remaining <= 0)
            return CursorResolver.NoIndex;
        return Math.Min(Math.Max(index, 0), remaining - 1);
    }

    /// <summary>Column to select after removing <paramref name="count"/> columns starting at <paramref name="index"/>.</summary>
    public static int ColumnAfterDelete(int index, int count, int remaining)
    {
        if (remaining <= 0)
            return CursorResolver.NoIndex;
        return Math.Min(Math.Max(index, 0), remaining - 1);
    }

    /// <summary>Row to select after inserting rows at <paramref name="index"/>: the first inserted row.</summary>
    public static int RowAfterInsert(int index, int count, int total) =>
        total <= 0 ? CursorResolver.NoIndex : Math.Min(Math.Max(index, 0), total - 1);
}
