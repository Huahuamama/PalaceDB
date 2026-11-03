using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Sets or clears <c>props.displayColumn</c> of a table: the column used to label rows
/// when another table references them. Reference dropdowns show this value instead of
/// the raw identifier.
/// </summary>
public sealed class DisplayColumnCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly string? _before;
    private readonly string? _after;

    public DisplayColumnCommand(SheetData sheet, string? columnName)
    {
        _sheet = sheet;
        _before = sheet.Props.DisplayColumn;
        _after = string.IsNullOrWhiteSpace(columnName) ? null : columnName;
        Label = _after is null ? "Clear Display Column" : $"Display Column '{_after}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => _sheet.Props.DisplayColumn = _after;

    public void Undo() => _sheet.Props.DisplayColumn = _before;
}
