using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Sets or clears <c>props.displayIcon</c> of a table: the column whose value is an image
/// path used as the icon of a row when another table references it (C06).
/// <para>
/// Like <see cref="DisplayColumnCommand"/> the property lives on the referenced table, not
/// on the reference column, which is how PalaceDB stores it. The icon and the display
/// column are independent: a row can show a sprite and a label at the same time.
/// </para>
/// </summary>
public sealed class DisplayIconCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly string? _before;
    private readonly string? _after;

    public DisplayIconCommand(SheetData sheet, string? columnName)
    {
        _sheet = sheet;
        _before = sheet.Props.DisplayIcon;
        _after = string.IsNullOrWhiteSpace(columnName) ? null : columnName;
        Label = _after is null ? "Clear Display Icon" : $"Display Icon '{_after}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    /// <summary>True when the command would not change anything.</summary>
    public bool IsNoOp => string.Equals(_before, _after, StringComparison.Ordinal);

    public void Execute() => _sheet.Props.DisplayIcon = _after;

    public void Undo() => _sheet.Props.DisplayIcon = _before;
}
