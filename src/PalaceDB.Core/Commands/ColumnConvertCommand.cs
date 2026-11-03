using System.Globalization;
using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Commands;

/// <summary>Bulk value conversions offered by the "Convert" submenu of a column header (C07).</summary>
public enum ColumnConversion
{
    Lowercase,
    Uppercase,
    UpperIdent,
    LowerIdent,
    Multiply10,
    Divide10,
    Plus1,
    Minus1,
}

/// <summary>The value level rules behind <see cref="ColumnConversion"/>.</summary>
public static class ColumnConversions
{
    /// <summary>Conversions that rewrite the text of a value (or an enum value list).</summary>
    public static bool IsTextual(this ColumnConversion conversion) =>
        conversion is ColumnConversion.Lowercase or ColumnConversion.Uppercase
            or ColumnConversion.UpperIdent or ColumnConversion.LowerIdent;

    /// <summary>Conversions that rewrite the number of a value.</summary>
    public static bool IsNumeric(this ColumnConversion conversion) =>
        conversion is ColumnConversion.Multiply10 or ColumnConversion.Divide10
            or ColumnConversion.Plus1 or ColumnConversion.Minus1;

    /// <summary>Menu label, matching the original editor.</summary>
    public static string Label(this ColumnConversion conversion) => conversion switch
    {
        ColumnConversion.Lowercase => "lowercase",
        ColumnConversion.Uppercase => "UPPERCASE",
        ColumnConversion.UpperIdent => "UpperIdent",
        ColumnConversion.LowerIdent => "lowerIdent",
        ColumnConversion.Multiply10 => "* 10",
        ColumnConversion.Divide10 => "/ 10",
        ColumnConversion.Plus1 => "+ 1",
        ColumnConversion.Minus1 => "- 1",
        _ => conversion.ToString(),
    };

    public static string ApplyToText(this ColumnConversion conversion, string value) => conversion switch
    {
        ColumnConversion.Lowercase => value.ToLowerInvariant(),
        ColumnConversion.Uppercase => value.ToUpperInvariant(),
        ColumnConversion.UpperIdent => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
        ColumnConversion.LowerIdent => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..],
        _ => value,
    };

    public static double ApplyToNumber(this ColumnConversion conversion, double value) => conversion switch
    {
        ColumnConversion.Multiply10 => value * 10,
        ColumnConversion.Divide10 => value / 10,
        ColumnConversion.Plus1 => value + 1,
        ColumnConversion.Minus1 => value - 1,
        _ => value,
    };
}

/// <summary>
/// Rewrites every value of a column in one undoable step.
/// <para>
/// Text-like columns get their cell text transformed; Int and Float columns get their
/// number scaled or shifted; Enum and Flags columns get their value list renamed in the
/// column definition (their cells store an index, so no row is touched).
/// </para>
/// <para>
/// Converting an ID column also retargets every reference that points at this sheet, so
/// references stay valid. Conversions that would produce duplicate IDs are refused
/// before anything is changed.
/// </para>
/// </summary>
public sealed class ColumnConvertCommand : IEditorCommand
{
    private readonly Column _column;
    private readonly string _columnName;
    private readonly ColumnConversion _conversion;

    /// <summary>Row level rewrites of the converted column itself.</summary>
    private readonly List<(int Row, CdbValue? Before, CdbValue? After)> _cells = new();

    /// <summary>Row level rewrites of reference columns pointing at the converted sheet.</summary>
    private readonly List<(SheetData Sheet, string Column, int Row, CdbValue? Before, CdbValue? After)> _refs = new();

    private readonly string? _typeStrBefore;
    private readonly string? _typeStrAfter;
    private readonly bool _changesType;

    public ColumnConvertCommand(
        Database database,
        SheetData sheet,
        Column column,
        ColumnConversion conversion)
    {
        _column = column;
        _columnName = column.Name;
        _conversion = conversion;

        var type = column.Type;

        if (type is ColumnType.Enum or ColumnType.Flags)
        {
            // Enum and Flags keep their values in the column definition, so the type
            // string is the only thing that changes.
            if (column.StructRef is not null)
                throw new InvalidOperationException(
                    "This column shares its type with another column, so its values cannot be renamed here.");

            var values = type switch
            {
                ColumnType.Enum e => e.Values,
                ColumnType.Flags f => f.Values,
                _ => Array.Empty<string>(),
            };

            var renamed = values.Select(v => conversion.ApplyToText(v)).ToList();
            _typeStrBefore = column.TypeStr;
            _typeStrAfter = ColumnTypeCodec.Encode(type switch
            {
                ColumnType.Enum => new ColumnType.Enum(renamed),
                _ => new ColumnType.Flags(renamed),
            });
            _changesType = true;

            ChangedValues = values.Where((v, i) => !string.Equals(v, renamed[i], StringComparison.Ordinal)).Count();
            Label = $"Convert Values of '{_columnName}' ({conversion.Label()})";
            return;
        }

        if (!conversion.IsTextual() && !conversion.IsNumeric())
        {
            Label = $"Convert Values of '{_columnName}'";
            return;
        }

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var before = sheet.Lines[i].Get(_columnName);
            var after = Rewrite(before, type);
            if (after is null)
                continue;

            // Unchanged cells are left out so that undo only touches what really moved.
            if (Equals(before, after))
                continue;

            _cells.Add((i, before, after));
        }

        ChangedValues = _cells.Count;

        if (type is ColumnType.Id && _cells.Count > 0)
        {
            var map = BuildIdMap();
            EnsureUniqueIds(sheet, map);
            CollectRefUpdates(database, sheet, map);
        }

        Label = $"Convert Values of '{_columnName}' ({conversion.Label()})";
    }

    public string Label { get; }

    /// <summary>Number of values the command rewrites (cells, value names or references).</summary>
    public int ChangedValues { get; private set; }

    /// <summary>Number of reference cells that are retargeted by an ID conversion.</summary>
    public int ChangedReferences => _refs.Count;

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute()
    {
        ApplyType(_typeStrAfter);

        foreach (var (row, _, after) in _cells)
            _column.Sheet.Lines[row].Set(_columnName, CdbValueCloner.CloneOrNull(after));

        foreach (var (sheet, column, row, _, after) in _refs)
            sheet.Lines[row].Set(column, CdbValueCloner.CloneOrNull(after));
    }

    public void Undo()
    {
        foreach (var (sheet, column, row, before, _) in _refs)
            sheet.Lines[row].Set(column, CdbValueCloner.CloneOrNull(before));

        foreach (var (row, before, _) in _cells)
            _column.Sheet.Lines[row].Set(_columnName, CdbValueCloner.CloneOrNull(before));

        ApplyType(_typeStrBefore);
    }

    // ---------------------------------------------------------------- helpers

    private void ApplyType(string? typeStr)
    {
        if (!_changesType)
            return;

        if (typeStr is null)
            _column.Raw.Remove("typeStr");
        else
            _column.Raw.Set("typeStr", new CdbValue.Str(typeStr));

        // Re-decoding keeps the cached type and the on-disk string in step.
        _column.Type = ColumnTypeCodec.Decode(typeStr ?? "1");
    }

    /// <summary>Computes the new value of one cell, or null when it cannot be converted.</summary>
    private CdbValue? Rewrite(CdbValue? value, ColumnType type)
    {
        if (value is null or CdbValue.Null)
            return null;

        if (_conversion.IsTextual())
        {
            // Only text-like cells carry a meaningful string; empty cells are skipped the
            // same way the original editor skips them.
            var text = value switch
            {
                CdbValue.Str s => s.Value,
                _ => null,
            };

            if (string.IsNullOrEmpty(text))
                return null;

            var converted = _conversion.ApplyToText(text);
            return string.Equals(converted, text, StringComparison.Ordinal)
                ? null
                : new CdbValue.Str(converted);
        }

        if (type is not (ColumnType.Int or ColumnType.Float))
            return null;

        if (value is not CdbValue.Number number)
            return null;

        var result = _conversion.ApplyToNumber(number.Value);

        // Int columns truncate toward zero, matching Std.int in the original editor.
        if (type is ColumnType.Int)
            result = Math.Truncate(result);

        if (double.IsNaN(result) || double.IsInfinity(result))
            return null;

        return new CdbValue.Number(result, null);
    }

    private Dictionary<string, string> BuildIdMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, before, after) in _cells)
        {
            if (before is not CdbValue.Str from || after is not CdbValue.Str to)
                continue;
            if (from.Value.Length == 0)
                continue;
            map[from.Value] = to.Value;
        }

        return map;
    }

    /// <summary>
    /// Refuses conversions that would give two rows the same ID, because references to
    /// them would become ambiguous.
    /// </summary>
    private static void EnsureUniqueIds(SheetData sheet, Dictionary<string, string> map)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var id = sheet.GetRowId(sheet.Lines[i]);
            if (string.IsNullOrEmpty(id))
                continue;

            if (map.TryGetValue(id, out var converted))
                id = converted;

            if (seen.TryGetValue(id, out var other))
                throw new InvalidOperationException(
                    $"The conversion would give row {other + 1} and row {i + 1} the same ID '{id}'. " +
                    "Rename one of them first.");

            seen[id] = i;
        }
    }

    private void CollectRefUpdates(Database database, SheetData source, Dictionary<string, string> map)
    {
        if (map.Count == 0)
            return;

        foreach (var sheet in database.Sheets)
        {
            foreach (var column in sheet.Columns)
            {
                if (column.Type is not ColumnType.Ref reference)
                    continue;
                if (!string.Equals(reference.Sheet, source.Name, StringComparison.Ordinal))
                    continue;

                for (var i = 0; i < sheet.Lines.Count; i++)
                {
                    if (sheet.Lines[i].Get(column.Name) is not CdbValue.Str value)
                        continue;
                    if (!map.TryGetValue(value.Value, out var updated))
                        continue;
                    if (string.Equals(updated, value.Value, StringComparison.Ordinal))
                        continue;

                    _refs.Add((sheet, column.Name, i, value, new CdbValue.Str(updated)));
                }
            }
        }
    }
}
