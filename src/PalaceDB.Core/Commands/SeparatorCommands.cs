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

    /// <summary>
    /// Separator indices after rows are inserted at <paramref name="index"/>.
    /// A separator sitting exactly on the insertion point stays where it is, so the new
    /// rows land under that heading; everything below it moves down with its row.
    /// </summary>
    public static List<(int Index, string? Title)> ForInsert(
        IReadOnlyList<(int Index, string? Title)> before,
        int index,
        int count)
    {
        return before
            .Select(pair => (pair.Index > index ? pair.Index + count : pair.Index, pair.Title))
            .ToList();
    }

    /// <summary>
    /// Separator indices after a range of rows is deleted. A separator inside the range
    /// goes with the row it marked; one on the first deleted row stays (it now heads the
    /// row that moved up), and everything after the range moves up. If a heading and the
    /// next heading end up on the same row, the first one wins, so the list stays sorted
    /// and free of duplicates.
    /// </summary>
    public static List<(int Index, string? Title)> ForDelete(
        IReadOnlyList<(int Index, string? Title)> before,
        int index,
        int count,
        int newRowCount)
    {
        var result = new List<(int Index, string? Title)>(before.Count);

        foreach (var pair in before)
        {
            if (pair.Index > index && pair.Index < index + count)
                continue;

            // A heading on the first deleted row survives only when another row takes its
            // place; deleting the tail must not leave a divider after the last row.
            if (pair.Index == index && index >= newRowCount)
                continue;

            var next = pair.Index >= index + count ? pair.Index - count : pair.Index;
            if (result.Any(entry => entry.Index == next))
                continue;

            result.Add((next, pair.Title));
        }

        return result;
    }

    /// <summary>
    /// Separator indices after a single row is dragged from <paramref name="from"/> to
    /// <paramref name="to"/>. A divider heads the row it sits in front of, so it travels
    /// with that row; every other divider keeps heading its own row, which means it shifts
    /// by one when the dragged row crosses over it.
    /// </summary>
    public static List<(int Index, string? Title)> ForMove(
        IReadOnlyList<(int Index, string? Title)> before,
        int from,
        int to)
    {
        if (from == to)
            return before.ToList();

        var result = new List<(int Index, string? Title)>(before.Count);

        foreach (var pair in before)
        {
            var index = pair.Index;

            if (index == from)
            {
                // The divider on the dragged row follows it.
                index = to;
            }
            else if (from < to && index > from && index <= to)
            {
                // Dragged down: the rows it passed move up one, their dividers with them.
                index--;
            }
            else if (to < from && index >= to && index < from)
            {
                // Dragged up: the rows it passed move down one.
                index++;
            }

            result.Add((index, pair.Title));
        }

        return result;
    }

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
