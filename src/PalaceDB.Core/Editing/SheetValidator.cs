using PalaceDB.Core.Model;

namespace PalaceDB.Core.Editing;

/// <summary>A single validation problem, located by row and column.</summary>
/// <param name="Row">Zero based index inside <see cref="SheetData.Lines"/>.</param>
/// <param name="Column">Name of the column holding the problem.</param>
/// <param name="Detail">Short message shown to the author.</param>
public readonly record struct SheetIssue(int Row, string Column, string Detail);

/// <summary>
/// Finds the first validation problem of a whole table.
///
/// The grid only ever holds the rows it has loaded, so a status bar driven by the
/// view would miss a broken row until it is scrolled into sight. This walks the table
/// itself, but only the columns that can actually be wrong — the identifier and the
/// references — instead of formatting every cell, which keeps a table with a hundred
/// thousand rows answerable in milliseconds.
/// </summary>
public static class SheetValidator
{
    public static SheetIssue? FirstIssue(SheetData sheet, Database database, DatabaseIndex index)
    {
        var idColumn = sheet.IdColumn;
        var sheetIndex = idColumn is null ? null : index.For(sheet);
        var references = ResolveReferences(sheet, database, index);

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var row = sheet.Lines[i];

            if (idColumn is not null)
            {
                var id = row.GetString(idColumn.Name) ?? string.Empty;
                if (id.Length == 0)
                    return new SheetIssue(i, idColumn.Name, "Identifier is empty");
                if (sheetIndex is not null && sheetIndex.IsDuplicate(id))
                    return new SheetIssue(i, idColumn.Name, $"Duplicate identifier '{id}'");
            }

            foreach (var reference in references)
            {
                var problem = CheckReference(reference, row);
                if (problem is not null)
                    return new SheetIssue(i, reference.Column, problem);
            }
        }

        return null;
    }

    private static List<ReferenceCheck> ResolveReferences(SheetData sheet, Database database, DatabaseIndex index)
    {
        var checks = new List<ReferenceCheck>();
        foreach (var column in sheet.Columns)
        {
            if (column.Type is not ColumnType.Ref target)
                continue;

            var targetSheet = database.GetSheet(target.Sheet);
            checks.Add(new ReferenceCheck(
                column.Name,
                column.Optional,
                target.Sheet,
                targetSheet is null ? null : index.For(targetSheet),
                targetSheet is null));
        }
        return checks;
    }

    private static string? CheckReference(ReferenceCheck check, CdbObject row)
    {
        var value = row.Get(check.Column);
        if (value is null or CdbValue.Null)
            return check.Optional ? null : "Required value is missing";

        var id = value is CdbValue.Str s ? s.Value : value.ToString() ?? string.Empty;
        if (id.Length == 0)
            return "Reference is empty";

        if (check.MissingSheet)
            return $"Table '{check.TargetSheet}' not found";

        return check.Index?.Get(id) is null ? "Referenced row not found" : null;
    }

    private readonly record struct ReferenceCheck(
        string Column,
        bool Optional,
        string TargetSheet,
        SheetIndex? Index,
        bool MissingSheet);
}
