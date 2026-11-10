using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>Inserts rows at a given position.</summary>
public sealed class RowInsertCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _index;
    private readonly IReadOnlyList<CdbObject> _rows;
    // Separators are row indices, so inserting rows above them has to move them too;
    // otherwise a divider keeps its old number and lands in front of the wrong row.
    private readonly List<(int Index, string? Title)> _beforeSeparators;
    private readonly List<(int Index, string? Title)> _afterSeparators;

    public RowInsertCommand(SheetData sheet, int index, IReadOnlyList<CdbObject> rows, string? label = null)
    {
        _sheet = sheet;
        _index = index;
        _rows = rows;
        _beforeSeparators = SeparatorTitles.Read(sheet);
        _afterSeparators = SeparatorTitles.ForInsert(_beforeSeparators, index, rows.Count);
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
        SeparatorTitles.Write(_sheet, _afterSeparators);
    }

    public void Undo()
    {
        _sheet.Lines.RemoveRange(_index, _rows.Count);
        SeparatorTitles.Write(_sheet, _beforeSeparators);
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
    private readonly List<(int Index, string? Title)> _beforeSeparators;
    private readonly List<(int Index, string? Title)> _afterSeparators;

    public RowDeleteCommand(SheetData sheet, int index, int count, string? label = null)
    {
        if (index < 0 || count <= 0 || index + count > sheet.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(count), "The row range is outside the sheet.");

        _sheet = sheet;
        _index = index;
        _rows = sheet.Lines.GetRange(index, count);
        _beforeSeparators = SeparatorTitles.Read(sheet);
        _afterSeparators = SeparatorTitles.ForDelete(
            _beforeSeparators, index, count, sheet.Lines.Count - count);
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
        SeparatorTitles.Write(_sheet, _afterSeparators);
    }

    public void Undo()
    {
        _sheet.Lines.InsertRange(_index, _rows);
        SeparatorTitles.Write(_sheet, _beforeSeparators);
    }
}

/// <summary>Moves a single row to another position.</summary>
public sealed class RowMoveCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _from;
    private readonly int _to;
    // A divider heads the row below it, so dragging that row has to carry the divider
    // along instead of leaving it on the row number the row used to have.
    private readonly List<(int Index, string? Title)> _beforeSeparators;
    private readonly List<(int Index, string? Title)> _afterSeparators;

    public RowMoveCommand(SheetData sheet, int from, int to, string? label = null)
    {
        _sheet = sheet;
        _from = from;
        _to = to;
        _beforeSeparators = SeparatorTitles.Read(sheet);
        _afterSeparators = SeparatorTitles.ForMove(_beforeSeparators, from, to);
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
        SeparatorTitles.Write(_sheet, _afterSeparators);
    }

    public void Undo()
    {
        var row = _sheet.Lines[_to];
        _sheet.Lines.RemoveAt(_to);
        _sheet.Lines.Insert(_from, row);
        SeparatorTitles.Write(_sheet, _beforeSeparators);
    }
}
