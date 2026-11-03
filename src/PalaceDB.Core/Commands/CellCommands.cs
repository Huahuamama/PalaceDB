using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Changes one cell. The previous value is captured on first execution so that undo
/// can restore the exact previous state - including "the field was absent", which is
/// different from "the field was null" in a sparse CDB row.
/// </summary>
public sealed class CellEditCommand : IEditorCommand
{
    private readonly CdbObject _row;
    private readonly string _columnName;
    private readonly CdbValue? _after;

    private bool _captured;
    private CdbValue? _before;

    public CellEditCommand(CdbObject row, string columnName, CdbValue? after, string? label = null)
    {
        _row = row;
        _columnName = columnName;
        _after = after;
        Label = label ?? $"Edit '{columnName}'";
    }

    public string Label { get; }

    public PalaceDB.Core.Cursor.CursorAnchor? CursorBefore { get; set; }

    public PalaceDB.Core.Cursor.CursorAnchor? CursorAfter { get; set; }

    public CdbObject Row => _row;

    public string ColumnName => _columnName;

    public CdbValue? Value => _after;

    public void Execute()
    {
        if (!_captured)
        {
            _before = _row.Get(_columnName);
            _captured = true;
        }
        Apply(_after);
    }

    public void Undo() => Apply(_before);

    /// <summary>True when the command would not change anything.</summary>
    public bool IsNoOp => ValuesEqual(_before, _after) && _captured;

    private void Apply(CdbValue? value)
    {
        if (value is null)
            _row.Remove(_columnName);
        else
            _row.Set(_columnName, value);
    }

    private static bool ValuesEqual(CdbValue? a, CdbValue? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Equals(b);
    }
}

/// <summary>
/// A bulk cell change (paste, fill, clear) recorded as one undo step.
/// Use <see cref="Builder"/> to accumulate edits and skip unchanged cells.
/// </summary>
public sealed class MultiCellEditCommand : IEditorCommand
{
    private readonly List<CellEditCommand> _edits;

    private MultiCellEditCommand(string label, List<CellEditCommand> edits)
    {
        Label = label;
        _edits = edits;
    }

    public string Label { get; }

    public PalaceDB.Core.Cursor.CursorAnchor? CursorBefore { get; set; }

    public PalaceDB.Core.Cursor.CursorAnchor? CursorAfter { get; set; }

    public int Count => _edits.Count;

    public IReadOnlyList<CellEditCommand> Edits => _edits;

    public void Execute()
    {
        foreach (var edit in _edits)
            edit.Execute();
    }

    public void Undo()
    {
        for (var i = _edits.Count - 1; i >= 0; i--)
            _edits[i].Undo();
    }

    /// <summary>Collects cell edits before they are applied.</summary>
    public sealed class Builder
    {
        private readonly List<CellEditCommand> _edits = new();
        private readonly string _label;

        public Builder(string label) => _label = label;

        public int Count => _edits.Count;

        public void Add(CdbObject row, string columnName, CdbValue? value)
        {
            var current = row.Get(columnName);
            if (Equals(current, value))
                return;
            _edits.Add(new CellEditCommand(row, columnName, value, _label));
        }

        public MultiCellEditCommand? Build() =>
            _edits.Count == 0 ? null : new MultiCellEditCommand(_label, _edits);
    }
}
