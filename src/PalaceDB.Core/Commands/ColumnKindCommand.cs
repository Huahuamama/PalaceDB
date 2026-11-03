using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Changes the <c>kind</c> of a column (C06 / F08). Only <see cref="ColumnKind.Localizable"/>
/// is exposed in the interface today: it marks text that the localization export collects.
/// </summary>
public sealed class ColumnKindCommand : IEditorCommand
{
    private readonly Column _column;
    private readonly ColumnKind _before;
    private readonly ColumnKind _after;

    public ColumnKindCommand(Column column, ColumnKind kind)
    {
        _column = column;
        _before = column.Kind;
        _after = kind;
        Label = kind == ColumnKind.Localizable
            ? $"Mark '{column.Name}' as Localizable"
            : $"Clear Localizable on '{column.Name}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public bool IsNoOp => _before == _after;

    public void Execute() => _column.Kind = _after;

    public void Undo() => _column.Kind = _before;
}
