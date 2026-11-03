using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Adds or removes a separator line in front of a row.
///
/// Separators are stored as the row index they appear before, in the <c>separators</c>
/// array of the sheet. Their optional titles live in <c>props.separatorTitles</c> and
/// must stay parallel to the sorted index list, so both are rewritten together.
/// </summary>
public sealed class SeparatorToggleCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _row;
    private readonly bool _adding;
    private readonly List<(int Index, string? Title)> _before;
    private readonly List<(int Index, string? Title)> _after;

    public SeparatorToggleCommand(SheetData sheet, int row)
    {
        if (row < 0 || row > sheet.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(row), "The row is outside the sheet.");

        _sheet = sheet;
        _row = row;
        _before = SeparatorTitles.Read(sheet);
        _adding = !_before.Any(p => p.Index == row);

        _after = _before.Where(p => p.Index != row).ToList();
        if (_adding)
            _after.Add((row, null));

        Label = _adding ? "Add Separator" : "Remove Separator";
    }

    public string Label { get; }

    public bool Added => _adding;

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => SeparatorTitles.Write(_sheet, _after);

    public void Undo() => SeparatorTitles.Write(_sheet, _before);
}

/// <summary>Renames the separator that sits in front of a row.</summary>
public sealed class SeparatorTitleCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _row;
    private readonly string? _title;
    private readonly List<(int Index, string? Title)> _before;
    private readonly List<(int Index, string? Title)> _after;

    public SeparatorTitleCommand(SheetData sheet, int row, string? title)
    {
        _sheet = sheet;
        _row = row;
        _title = string.IsNullOrWhiteSpace(title) ? null : title;
        _before = SeparatorTitles.Read(sheet);

        _after = _before
            .Select(p => p.Index == row ? (p.Index, _title) : p)
            .ToList();

        Label = "Name Separator";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => SeparatorTitles.Write(_sheet, _after);

    public void Undo() => SeparatorTitles.Write(_sheet, _before);
}

/// <summary>Reads and writes the index/title pairs of a sheet's separators.</summary>
internal static class SeparatorTitles
{
    public static List<(int Index, string? Title)> Read(SheetData sheet) =>
        sheet.GetSeparators().ToList();

    public static void Write(SheetData sheet, List<(int Index, string? Title)> pairs)
    {
        // The on-disk array is sorted, so the titles are sorted with it.
        var ordered = pairs.OrderBy(p => p.Index).ToList();

        sheet.Separators.Clear();
        foreach (var pair in ordered)
            sheet.Separators.Add(pair.Index);

        if (ordered.All(p => string.IsNullOrEmpty(p.Title)))
        {
            sheet.Props.SeparatorTitles = null;
            return;
        }

        // Untitled separators are stored as an explicit null, which is how PalaceDB 1.5
        // distinguishes "no title" from an empty one.
        var array = new CdbValue.Arr();
        foreach (var pair in ordered)
            array.Items.Add(pair.Title is null ? CdbValue.NullValue : new CdbValue.Str(pair.Title));

        sheet.Props.SeparatorTitles = array;
    }
}
