namespace PalaceDB.Core.Model;

/// <summary>
/// The <c>index</c> and <c>group</c> fields are derived: they are not columns, they are
/// recomputed from the row order and from the titled separators every time the file is
/// written. PalaceDB 1.5 does the same in its save step, and this class reproduces that
/// behaviour so the produced files stay interchangeable.
/// </summary>
public static class DerivedFields
{
    public const string IndexKey = "index";
    public const string GroupKey = "group";

    /// <summary>Writes <c>index</c> on every row, starting at 0.</summary>
    public static void ApplyIndex(SheetData sheet)
    {
        for (var i = 0; i < sheet.Lines.Count; i++)
            sheet.Lines[i].Set(IndexKey, new CdbValue.Number(i, null));
    }

    /// <summary>
    /// Writes <c>group</c> on every row. The group number starts at 0 and is incremented
    /// by every separator that carries a title. A titled separator in front of the first
    /// row opens the first group instead of starting a new one.
    /// <para>
    /// Like PalaceDB 1.5, nothing is written when the sheet has no separator titles at
    /// all: without titles there is no group structure to describe.
    /// </para>
    /// </summary>
    public static void ApplyGroup(SheetData sheet)
    {
        var titles = sheet.Props.SeparatorTitles;
        if (titles is null)
            return;

        var separators = sheet.Separators.OrderBy(i => i).ToList();
        var slot = 0;
        var group = 0;

        if (separators.Count > 0 && separators[0] == 0 && HasTitle(titles, 0))
            slot++;

        for (var row = 0; row < sheet.Lines.Count; row++)
        {
            if (slot < separators.Count && separators[slot] == row)
            {
                if (HasTitle(titles, slot))
                    group++;
                slot++;
            }

            sheet.Lines[row].Set(GroupKey, new CdbValue.Number(group, null));
        }
    }

    public static void RemoveIndex(SheetData sheet) => Remove(sheet, IndexKey);

    public static void RemoveGroup(SheetData sheet) => Remove(sheet, GroupKey);

    private static void Remove(SheetData sheet, string key)
    {
        foreach (var row in sheet.Lines)
            row.Remove(key);
    }

    /// <summary>
    /// True when the title slot holds text. An empty string is treated as "no title",
    /// which is what the separator editor writes when the name is cleared.
    /// </summary>
    private static bool HasTitle(CdbValue.Arr titles, int slot) =>
        slot >= 0 && slot < titles.Items.Count
        && titles.Items[slot] is CdbValue.Str { Value.Length: > 0 };
}
