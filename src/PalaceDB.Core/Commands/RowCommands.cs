using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>Inserts rows at a given position.</summary>
public sealed class RowInsertCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _index;
    private readonly IReadOnlyList<CdbObject> _rows;
    private readonly bool _after;
    // The new rows join the group of the row they are added next to, which is a local
    // adjustment to the row counts; the heading indices of the file follow from them at
    // save time.
    private readonly Snapshot _beforeSeparators;

    public RowInsertCommand(
        SheetData sheet,
        int index,
        IReadOnlyList<CdbObject> rows,
        string? label = null,
        bool after = false)
    {
        _sheet = sheet;
        _index = index;
        _rows = rows;
        _after = after;
        _beforeSeparators = sheet.Groups.Capture();
        Label = label ?? (rows.Count == 1 ? "Insert Row" : $"Insert {rows.Count} Rows");
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public int Index => _index;

    public IReadOnlyList<CdbObject> Rows => _rows;

    public void Execute()
    {
        _sheet.Lines.InsertRange(_index, _rows);
        // `_after` names the row the insert was anchored to, and its group is the one above
        // the gap: the two descriptions are the same request.
        _sheet.Groups.InsertRows(_index, _rows.Count, joinAbove: _after);
    }

    public void Undo()
    {
        _sheet.Lines.RemoveRange(_index, _rows.Count);
        _sheet.Groups.Restore(_beforeSeparators);
    }
}

/// <summary>
/// Removes rows. The removed row objects are kept alive so that undo restores the
/// exact same instances (and therefore their identity for references and styles).
/// </summary>
public sealed class RowDeleteCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _index;
    private readonly List<CdbObject> _rows;
    private readonly Snapshot _beforeSeparators;

    public RowDeleteCommand(SheetData sheet, int index, int count, string? label = null)
    {
        if (index < 0 || count <= 0 || index + count > sheet.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(count), "The row range is outside the sheet.");

        _sheet = sheet;
        _index = index;
        _rows = sheet.Lines.GetRange(index, count);
        _beforeSeparators = sheet.Groups.Capture();
        Label = label ?? (count == 1 ? "Delete Row" : $"Delete {count} Rows");
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public int Index => _index;

    public IReadOnlyList<CdbObject> Rows => _rows;

    public void Execute()
    {
        _sheet.Lines.RemoveRange(_index, _rows.Count);
        _sheet.Groups.DeleteRows(_index, _rows.Count);
    }

    public void Undo()
    {
        _sheet.Lines.InsertRange(_index, _rows);
        _sheet.Groups.Restore(_beforeSeparators);
    }
}

/// <summary>Moves a single row to another position.</summary>
public sealed class RowMoveCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _from;
    private readonly int _to;
    private readonly bool _below;
    // The row changes group when it lands in another one; the headings stay put, because a
    // heading belongs to the rows it already owns and not to the row that happened to be
    // first under it.
    private readonly Snapshot _beforeSeparators;

    /// <summary>
    /// Moves the row at <paramref name="from"/> to the position <paramref name="to"/>.
    /// <paramref name="below"/> says which side of the heading that sits at the landing gap
    /// the row ends up on: under it, as the first row of its group, or above it with the
    /// rows that come before.
    /// </summary>
    public RowMoveCommand(SheetData sheet, int from, int to, bool below = true, string? label = null)
    {
        _sheet = sheet;
        _from = from;
        _to = to;
        _below = below;
        _beforeSeparators = sheet.Groups.Capture();
        Label = label ?? "Move Row";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute()
    {
        var row = _sheet.Lines[_from];
        _sheet.Lines.RemoveAt(_from);
        _sheet.Lines.Insert(_to, row);
        _sheet.Groups.MoveRow(_from, _to, _below);
    }

    public void Undo()
    {
        var row = _sheet.Lines[_to];
        _sheet.Lines.RemoveAt(_to);
        _sheet.Lines.Insert(_from, row);
        _sheet.Groups.Restore(_beforeSeparators);
    }
}
