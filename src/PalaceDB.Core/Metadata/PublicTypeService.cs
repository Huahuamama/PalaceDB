using PalaceDB.Core.Commands;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Metadata;

/// <summary>
/// Public enum / flags types: one named value list that several columns share.
/// <para>
/// The .cdb stays a plain CastleDB file — every bound column writes the same
/// <c>typeStr</c> — so nothing here has to be understood by another editor. The side-car
/// keeps the definition and the binding, which is what lets an edit reach every column
/// that follows it, and what lets the file be checked for drift when it is opened.
/// </para>
/// </summary>
public static class PublicTypeService
{
    /// <summary>The value list a public type stands for, never empty.</summary>
    public static IReadOnlyList<string> Normalized(PublicTypeMeta type) =>
        type.Values.Count == 0 ? new[] { string.Empty } : type.Values;

    public static ColumnType ToColumnType(PublicTypeMeta type) =>
        type.IsFlags
            ? new ColumnType.Flags(Normalized(type))
            : new ColumnType.Enum(Normalized(type));

    public static string Encode(PublicTypeMeta type) => ColumnTypeCodec.Encode(ToColumnType(type));

    /// <summary>Every column bound to this public type, across every table.</summary>
    public static List<(SheetData Sheet, Column Column)> BoundColumns(
        Database database, MetaDocument meta, string typeName)
    {
        var found = new List<(SheetData, Column)>();
        foreach (var (sheetName, columnName) in meta.ColumnsBoundTo(typeName))
        {
            var sheet = database.GetSheet(sheetName);
            var column = sheet?.GetColumn(columnName);
            if (sheet is not null && column is not null)
                found.Add((sheet, column));
        }
        return found;
    }

    /// <summary>
    /// Rewrites the <c>typeStr</c> of every bound column that disagrees with its public
    /// type, without touching the data. Returns how many columns were fixed; the caller
    /// decides whether to say so.
    /// </summary>
    public static int Repair(Database database, MetaDocument meta)
    {
        var fixedCount = 0;
        foreach (var (name, type) in meta.PublicTypes)
        {
            var encoded = Encode(type);
            foreach (var (_, column) in BoundColumns(database, meta, name))
            {
                if (string.Equals(ColumnTypeCodec.Encode(column.Type), encoded, StringComparison.Ordinal))
                    continue;
                column.Type = ToColumnType(type);
                column.FlushType();
                fixedCount++;
            }
        }
        return fixedCount;
    }

    /// <summary>
    /// True when the column's values already are the definition's, so an edit that only
    /// re-sends the same list is not reported as a change.
    /// </summary>
    public static bool Matches(Column column, PublicTypeMeta type)
    {
        var encoded = Encode(type);
        return string.Equals(ColumnTypeCodec.Encode(column.Type), encoded, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the one undoable step that gives a public type new values: the definition
    /// and every bound column's type and data move together.
    /// </summary>
    public static PublicTypeValuesCommand BuildUpdate(
        Database database,
        MetaDocument meta,
        string name,
        string kind,
        IReadOnlyList<string> values,
        string? renameFrom = null)
    {
        return new PublicTypeValuesCommand(
            database, meta, name, kind, values, renameFrom);
    }
}
