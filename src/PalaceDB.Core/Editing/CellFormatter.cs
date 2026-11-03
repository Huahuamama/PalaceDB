using System.Globalization;
using System.Text;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Editing;

/// <summary>Visual state of a cell, used by the UI to highlight problems.</summary>
public enum CellState
{
    Ok,
    Empty,
    Missing,
    Duplicate,
    BadReference,
    NotFound,
    Error,
}

/// <summary>Display text plus validation state for a single cell.</summary>
public readonly record struct CellDisplay(string Text, CellState State, string? Detail = null)
{
    public static CellDisplay Ok(string text) => new(text, CellState.Ok);
    public static CellDisplay Empty() => new(string.Empty, CellState.Empty);
    public static CellDisplay Missing() => new("#NULL", CellState.Missing, "Required value is missing");
}

/// <summary>
/// Turns raw CDB values into the text the grid shows, reproducing the original
/// editor's markers (#NULL, #MISSING, #DUP, #REF, #NOTFOUND).
/// </summary>
public sealed class CellFormatter
{
    private readonly Database _database;
    private readonly DatabaseIndex _index;

    public CellFormatter(Database database, DatabaseIndex index)
    {
        _database = database;
        _index = index;
    }

    public CellDisplay Format(SheetData sheet, Column column, CdbObject row, CdbValue? value)
    {
        if (value is null or CdbValue.Null)
            return column.Optional ? CellDisplay.Empty() : CellDisplay.Missing();

        return column.Type switch
        {
            ColumnType.Id => FormatId(sheet, column, row, value),
            ColumnType.Text => FormatText(value),
            ColumnType.Bool => new CellDisplay(((CdbValue.Bool)value).Value ? "Y" : "N", CellState.Ok),
            ColumnType.Int => FormatNumber(column, value, integer: true),
            ColumnType.Float => FormatNumber(column, value, integer: false),
            ColumnType.Enum e => FormatEnum(e, column, value),
            ColumnType.Flags f => FormatFlags(f, value),
            ColumnType.Ref r => FormatRef(r, value),
            ColumnType.Image => FormatImage(value),
            ColumnType.File => FormatFile(value),
            ColumnType.Color c => FormatColor(c, value),
            ColumnType.List l => FormatList(sheet, column, value),
            ColumnType.Properties => FormatProperties(sheet, column, value),
            ColumnType.Polymorph => FormatPolymorph(sheet, column, value),
            ColumnType.Custom ct => FormatCustom(ct, value),
            ColumnType.Dynamic or ColumnType.Gradient or ColumnType.Curve or ColumnType.Formula => FormatRaw(value),
            ColumnType.Guid => new CellDisplay(RawText(value), CellState.Ok),
            _ => new CellDisplay(RawText(value), CellState.Ok),
        };
    }

    private CellDisplay FormatId(SheetData sheet, Column column, CdbObject row, CdbValue value)
    {
        var id = RawText(value);
        if (id.Length == 0)
            return new CellDisplay("#MISSING", CellState.Missing, "Identifier is empty");

        var index = _index.For(sheet);
        if (index is not null && index.IsDuplicate(id))
            return new CellDisplay($"#DUP({id})", CellState.Duplicate, "Duplicate identifier");
        return new CellDisplay(id, CellState.Ok);
    }

    private static CellDisplay FormatText(CdbValue value)
    {
        var text = RawText(value);
        return text.Length == 0 ? CellDisplay.Empty() : new CellDisplay(text, CellState.Ok);
    }

    private static CellDisplay FormatNumber(Column column, CdbValue value, bool integer)
    {
        if (value is not CdbValue.Number number)
            return new CellDisplay(RawText(value), CellState.Error, "Not a number");

        if (column.Display == DisplayType.Percent)
            return new CellDisplay((Math.Round(number.Value * 10000) / 100).ToString("0.##", CultureInfo.InvariantCulture) + "%", CellState.Ok);

        var text = integer
            ? ((long)number.Value).ToString(CultureInfo.InvariantCulture)
            : CdbNumber.Format(number.Value);
        return new CellDisplay(text, CellState.Ok);
    }

    private static CellDisplay FormatEnum(ColumnType.Enum type, Column column, CdbValue value)
    {
        if (column.EnumAsString)
            return new CellDisplay(RawText(value), CellState.Ok);

        if (value is CdbValue.Number n)
        {
            var i = (int)n.Value;
            if (i >= 0 && i < type.Values.Count)
                return new CellDisplay(type.Values[i], CellState.Ok);
        }
        return new CellDisplay("#INVALID", CellState.Error, "Enum index out of range");
    }

    private static CellDisplay FormatFlags(ColumnType.Flags type, CdbValue value)
    {
        if (value is not CdbValue.Number n)
            return new CellDisplay(RawText(value), CellState.Error, "Not a flags value");

        var mask = (int)n.Value;
        var sb = new StringBuilder();
        for (var i = 0; i < type.Values.Count; i++)
        {
            if ((mask & (1 << i)) == 0)
                continue;
            if (sb.Length > 0)
                sb.Append('|');
            sb.Append(type.Values[i]);
        }
        return sb.Length == 0
            ? new CellDisplay("\u2205", CellState.Empty)
            : new CellDisplay(sb.ToString(), CellState.Ok);
    }

    private CellDisplay FormatRef(ColumnType.Ref type, CdbValue value)
    {
        var id = RawText(value);
        if (id.Length == 0)
            return new CellDisplay("#MISSING", CellState.Missing, "Reference is empty");

        var target = _database.GetSheet(type.Sheet);
        if (target is null)
            return new CellDisplay($"#REF({id})", CellState.BadReference, $"Sheet '{type.Sheet}' not found");

        var index = _index.For(target);
        var row = index?.Get(id);
        if (row is null)
            return new CellDisplay($"#REF({id})", CellState.BadReference, "Referenced row not found");

        return new CellDisplay(index!.GetDisplay(row), CellState.Ok, id);
    }

    private static CellDisplay FormatImage(CdbValue value)
    {
        var path = RawText(value);
        return path.Length == 0
            ? new CellDisplay("#MISSING", CellState.Missing, "Image path is empty")
            : new CellDisplay(path, CellState.Ok);
    }

    private static CellDisplay FormatFile(CdbValue value)
    {
        var path = RawText(value);
        return path.Length == 0
            ? new CellDisplay("#MISSING", CellState.Missing, "File path is empty")
            : new CellDisplay(path, CellState.Ok);
    }

    private static CellDisplay FormatColor(ColumnType type, CdbValue value)
    {
        var rgb = value is CdbValue.Number n ? (int)n.Value : 0;
        return new CellDisplay("#" + (rgb & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture), CellState.Ok,
            rgb.ToString(CultureInfo.InvariantCulture));
    }

    private CellDisplay FormatList(SheetData sheet, Column column, CdbValue value)
    {
        if (value is not CdbValue.Arr arr)
            return new CellDisplay(RawText(value), CellState.Error, "Not a list");

        if (arr.Items.Count == 0)
            return CellDisplay.Empty();

        var sub = sheet.GetSub(column);
        var parts = new List<string>();
        foreach (var item in arr.Items.Take(8))
        {
            if (item is not CdbValue.Obj o || sub is null)
            {
                parts.Add(RawText(item));
                continue;
            }
            parts.Add(SummarizeObject(sub, o.Value));
        }
        if (arr.Items.Count > 8)
            parts.Add("...");
        return new CellDisplay(string.Join(", ", parts), CellState.Ok);
    }

    private CellDisplay FormatProperties(SheetData sheet, Column column, CdbValue value)
    {
        if (value is not CdbValue.Obj o)
            return new CellDisplay(RawText(value), CellState.Error, "Not an object");

        var sub = sheet.GetSub(column);
        if (sub is null)
            return new CellDisplay(RawText(value), CellState.Ok);

        var parts = new List<string>();
        foreach (var subColumn in sub.Columns)
        {
            var v = o.Value.Get(subColumn.Name);
            if (v is null && subColumn.Optional)
                continue;
            var display = Format(sub, subColumn, o.Value, v);
            parts.Add($"{subColumn.Name}: {display.Text}");
        }
        return parts.Count == 0
            ? CellDisplay.Empty()
            : new CellDisplay(string.Join("\n", parts), CellState.Ok);
    }

    private CellDisplay FormatPolymorph(SheetData sheet, Column column, CdbValue value)
    {
        if (value is not CdbValue.Obj o)
            return new CellDisplay(RawText(value), CellState.Error, "Not an object");

        var sub = sheet.GetSub(column);
        if (sub is null)
            return new CellDisplay(RawText(value), CellState.Ok);

        var parts = new List<string>();
        foreach (var subColumn in sub.Columns)
        {
            var v = o.Value.Get(subColumn.Name);
            if (v is null)
                continue;
            parts.Add($"{subColumn.Name}: {Format(sub, subColumn, o.Value, v).Text}");
        }
        return parts.Count == 0
            ? CellDisplay.Empty()
            : new CellDisplay(string.Join("\n", parts), CellState.Ok);
    }

    private CellDisplay FormatCustom(ColumnType.Custom type, CdbValue value)
    {
        if (value is not CdbValue.Arr arr || arr.Items.Count == 0)
            return new CellDisplay(RawText(value), CellState.Error, "Invalid custom value");

        var customType = _database.GetCustomType(type.TypeName2);
        if (customType is null)
            return new CellDisplay(RawText(value), CellState.Error, $"Unknown custom type '{type.TypeName2}'");

        if (arr.Items[0] is not CdbValue.Number caseIndex)
            return new CellDisplay(RawText(value), CellState.Error, "Invalid custom value");

        var i = (int)caseIndex.Value;
        if (i < 0 || i >= customType.Cases.Count)
            return new CellDisplay(RawText(value), CellState.Error, "Custom case out of range");

        var caseDef = customType.Cases[i];
        if (arr.Items.Count <= 1)
            return new CellDisplay(caseDef.Name, CellState.Ok);

        var args = arr.Items.Skip(1).Select(RawText);
        return new CellDisplay($"{caseDef.Name}({string.Join(",", args)})", CellState.Ok);
    }

    private static CellDisplay FormatRaw(CdbValue value)
    {
        var text = value switch
        {
            CdbValue.Str s => s.Value,
            CdbValue.Number n => n.ToString(),
            CdbValue.Bool b => b.Value ? "true" : "false",
            CdbValue.Arr or CdbValue.Obj => Compact(value),
            _ => string.Empty,
        };
        if (text.Length > 120)
            text = text[..117] + "...";
        return text.Length == 0 ? CellDisplay.Empty() : new CellDisplay(text, CellState.Ok);
    }

    private string SummarizeObject(SheetData sub, CdbObject obj)
    {
        var parts = new List<string>();
        foreach (var column in sub.Columns)
        {
            if (column.Type is ColumnType.List or ColumnType.Properties or ColumnType.Polymorph)
                continue;
            var v = obj.Get(column.Name);
            parts.Add(Format(sub, column, obj, v).Text);
            if (parts.Count >= 4)
                break;
        }
        return string.Join(" ", parts);
    }

    private static string RawText(CdbValue value) => value switch
    {
        CdbValue.Str s => s.Value,
        CdbValue.Number n => n.ToString(),
        CdbValue.Bool b => b.Value ? "true" : "false",
        CdbValue.Null => string.Empty,
        _ => Compact(value),
    };

    private static string Compact(CdbValue value) => value switch
    {
        CdbValue.Arr a => "[" + string.Join(",", a.Items.Select(Compact)) + "]",
        CdbValue.Obj o => "{" + string.Join(",", o.Value.Keys.Select(k => k + ":" + Compact(o.Value.Get(k)!))) + "}",
        CdbValue.Str s => "\"" + s.Value + "\"",
        _ => RawText(value),
    };
}
