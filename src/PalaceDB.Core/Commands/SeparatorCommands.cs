using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Adds or removes a heading in front of a row.
///
/// A heading owns the rows under it (see <see cref="SheetGroups"/>), so adding one splits
/// the group the row belongs to and removing one hands its rows back to the run above. The
/// row indices of the file are derived from that ownership when it is written, which is why
/// nothing here has to renumber anything.
/// </summary>
public sealed class SeparatorToggleCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _row;
    private readonly bool _adding;
    private readonly Snapshot _before;

    public SeparatorToggleCommand(SheetData sheet, int row)
    {
        if (row < 0 || row > sheet.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(row), "The row is outside the sheet.");

        _sheet = sheet;
        _row = row;
        _before = sheet.Groups.Capture();
        _adding = !sheet.Groups.HasHeadingAt(row);

        Label = _adding ? "Add Separator" : "Remove Separator";
    }

    public string Label { get; }

    public bool Added => _adding;

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => _sheet.Groups.Toggle(_row);

    public void Undo() => _sheet.Groups.Restore(_before);
}

/// <summary>Renames the heading that sits in front of a row.</summary>
public sealed class SeparatorTitleCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _row;
    private readonly string? _title;
    private readonly Snapshot _before;

    public SeparatorTitleCommand(SheetData sheet, int row, string? title)
    {
        _sheet = sheet;
        _row = row;
        _title = string.IsNullOrWhiteSpace(title) ? null : title;
        _before = sheet.Groups.Capture();

        Label = "Name Separator";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => _sheet.Groups.SetTitle(_row, _title);

    public void Undo() => _sheet.Groups.Restore(_before);
}
