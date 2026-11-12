using PalaceDB.Core.Cursor;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>
/// The optional parts of a column definition, as edited by the column dialog (C01):
/// <c>display</c>, <c>kind</c> and <c>defaultValue</c>. Kept separate from
/// <see cref="ColumnOptionsCommand"/> because a brand new column applies them through
/// <see cref="ColumnInsertCommand"/> so that the rows get the right starting values.
/// </summary>
public readonly record struct ColumnOptions(
    DisplayType Display = DisplayType.Default,
    ColumnKind Kind = ColumnKind.None,
    CdbValue? DefaultValue = null)
{
    public static readonly ColumnOptions None = new();
}

/// <summary>
/// One field of the hidden sub-table behind a List or Properties column.
/// <paramref name="Original"/> is the name the field had before the dialog was opened,
/// which is what lets a rename keep the values it already holds instead of looking like
/// a removal followed by an addition.
/// </summary>
public readonly record struct SubColumnSpec(string Name, ColumnType Type, string? Original = null);

/// <summary>
/// Inserts a column into a sheet. Every existing row receives the column default so
/// the table stays rectangular; optional columns are left out of the rows instead.
/// <para>
/// A column that arrives through a table template passes <c>fillExistingRows: false</c>:
/// it is added to the schema only, because writing a default into every row of every
/// table that shares the template would rewrite data nobody asked to change.
/// </para>
/// <para>
/// A List or Properties column is backed by a hidden sheet named <c>Table@Column</c>.
/// That sheet is created by the same command, so adding the column and adding its
/// sub-table are one undo step.
/// </para>
/// </summary>
public sealed class ColumnInsertCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly Database _database;
    private int _index;
    private readonly CdbObject _raw;
    private readonly bool _optional;
    private readonly bool _fillRows;
    private readonly bool _autoIncrement;
    private readonly Column _probe;
    private readonly CdbObject? _subRaw;
    private readonly string? _subName;
    private List<CdbValue?> _defaults = new();
    private bool _subInserted;

    public ColumnInsertCommand(
        SheetData sheet,
        int index,
        string name,
        ColumnType type,
        Database database,
        bool optional = false,
        ColumnOptions options = default,
        IReadOnlyList<SubColumnSpec>? subColumns = null,
        bool fillExistingRows = true,
        bool autoIncrement = false)
    {
        _sheet = sheet;
        _database = database;
        _index = Math.Clamp(index, 0, sheet.Columns.Count);
        _optional = optional;
        _fillRows = fillExistingRows;
        _autoIncrement = autoIncrement;

        _raw = new CdbObject();
        _raw.Set("name", new CdbValue.Str(name));
        _raw.Set("typeStr", new CdbValue.Str(Serialization.ColumnTypeCodec.Encode(type)));
        if (optional)
            _raw.Set("opt", new CdbValue.Bool(true));

        // The default is resolved against a throw-away column so that reference and
        // properties columns can look at the database while building it.
        _probe = new Column(sheet, _raw);

        // Applied before the defaults are computed, so a declared defaultValue is what
        // the existing rows receive.
        _probe.Display = options.Display;
        _probe.Kind = options.Kind;
        _probe.DefaultValue = options.DefaultValue;

        if (subColumns is { Count: > 0 })
        {
            _subName = $"{sheet.Name}@{name}";

            var columns = new CdbValue.Arr();
            foreach (var spec in subColumns)
            {
                var node = new CdbObject();
                node.Set("name", new CdbValue.Str(spec.Name));
                node.Set("typeStr", new CdbValue.Str(Serialization.ColumnTypeCodec.Encode(spec.Type)));
                columns.Items.Add(new CdbValue.Obj(node));
            }

            var props = new CdbObject();
            props.Set("hide", new CdbValue.Bool(true));

            _subRaw = new CdbObject();
            _subRaw.Set("name", new CdbValue.Str(_subName));
            _subRaw.Set("columns", columns);
            _subRaw.Set("lines", new CdbValue.Arr());
            _subRaw.Set("props", new CdbValue.Obj(props));
        }

        Name = name;
        Label = $"Add Column '{name}'";
    }

    /// <summary>Name of the hidden sub-table this column created, when it has one.</summary>
    public string? SubSheetName => _subName;

    public string Label { get; }

    public string Name { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public int Index => _index;

    public void Execute()
    {
        // Clamped here rather than in the constructor: several inserts can be built before
        // any of them runs (applying a template adds a whole set of columns), and the
        // count at construction time would be stale for every one of them.
        _index = Math.Clamp(_index, 0, _sheet.Columns.Count);

        // The sub-table has to exist before the defaults are built, because a Properties
        // column fills its default from the fields the sub-table declares.
        if (_subRaw is not null && !_subInserted)
        {
            _database.InsertSheet(_database.Sheets.Count, _subRaw);
            _subInserted = true;
        }

        // Resolved here rather than in the constructor so the sub-table is visible.
        var fallback = TypeDefaults.Create(_probe, _database, _sheet, ignoreOptional: true);
        // A Guid identifies one row, so a column that arrives with rows already in the
        // table must not hand them all the same generated value; an auto-increment column
        // numbers them 1..N in table order.
        var perRow = _probe.Type is ColumnType.Guid || _autoIncrement;
        // An id is the author's to choose, and a table that gets one back after the old id
        // column was deleted has no sensible value for its rows: writing an empty string
        // into every row would only fill the file with keys nobody typed. Leave them out;
        // the status bar already reports the rows that still need an identifier.
        var skipRows = _optional || !_fillRows || _probe.Type is ColumnType.Id;
        _defaults = new List<CdbValue?>(_sheet.Lines.Count);
        for (var i = 0; i < _sheet.Lines.Count; i++)
        {
            if (skipRows)
                _defaults.Add(null);
            else if (_autoIncrement)
                _defaults.Add(new CdbValue.Number(i + 1, null));
            else if (perRow)
                _defaults.Add(new CdbValue.Str(GuidGenerator.New()));
            else
                _defaults.Add(CdbValueCloner.CloneOrNull(fallback));
        }

        _sheet.InsertColumn(_index, _raw);

        for (var i = 0; i < _sheet.Lines.Count; i++)
        {
            if (_defaults[i] is { } value)
                _sheet.Lines[i].Set(Name, CdbValueCloner.Clone(value));
        }
    }

    public void Undo()
    {
        foreach (var row in _sheet.Lines)
            row.Remove(Name);

        _sheet.RemoveColumnAt(_index);

        if (_subRaw is not null && _subInserted)
        {
            for (var i = _database.Sheets.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_database.Sheets[i].Name, _subName, StringComparison.Ordinal))
                {
                    _database.RemoveSheetAt(i);
                    break;
                }
            }
            _subInserted = false;
        }
    }
}

/// <summary>Removes a column and the values it held in every row.</summary>
public sealed class ColumnDeleteCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _index;
    private readonly CdbObject _raw;
    private readonly string _name;
    private readonly List<(int Row, CdbValue Value)> _values = new();

    // A table can point at this column as its display or icon column. Deleting the
    // column would leave a dangling name behind, so the property is cleared with it
    // and restored on undo (C03).
    private readonly bool _wasDisplayColumn;
    private readonly bool _wasDisplayIcon;

    public ColumnDeleteCommand(SheetData sheet, int index)
    {
        if (index < 0 || index >= sheet.Columns.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "The column does not exist.");

        _sheet = sheet;
        _index = index;
        _raw = sheet.Columns[index].Raw;
        _name = sheet.Columns[index].Name;

        _wasDisplayColumn = string.Equals(sheet.Props.DisplayColumn, _name, StringComparison.Ordinal);
        _wasDisplayIcon = string.Equals(sheet.Props.DisplayIcon, _name, StringComparison.Ordinal);

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            if (sheet.Lines[i].Get(_name) is { } value)
                _values.Add((i, value));
        }

        Label = $"Delete Column '{_name}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute()
    {
        foreach (var row in _sheet.Lines)
            row.Remove(_name);

        _sheet.RemoveColumnAt(_index);

        if (_wasDisplayColumn)
            _sheet.Props.DisplayColumn = null;
        if (_wasDisplayIcon)
            _sheet.Props.DisplayIcon = null;
    }

    public void Undo()
    {
        _sheet.InsertColumn(_index, _raw);

        // Values are restored in row order so the key order inside each row stays
        // close to what it was before the column was removed.
        foreach (var (row, value) in _values)
        {
            if (row < _sheet.Lines.Count)
                _sheet.Lines[row].Set(_name, value);
        }

        if (_wasDisplayColumn)
            _sheet.Props.DisplayColumn = _name;
        if (_wasDisplayIcon)
            _sheet.Props.DisplayIcon = _name;
    }
}

/// <summary>
/// Changes the optional fields of a column definition in one undoable step (C01 / C02):
/// <c>opt</c>, <c>defaultValue</c>, <c>display</c> and <c>kind</c>.
/// <para>
/// They are grouped because the column dialog edits them together, and a user who fixes
/// two properties at once expects one Ctrl+Z to take both back.
/// </para>
/// </summary>
public sealed class ColumnOptionsCommand : IEditorCommand
{
    private readonly Column _column;
    private readonly bool _optionalBefore;
    private readonly bool _optionalAfter;
    private readonly CdbValue? _defaultBefore;
    private readonly CdbValue? _defaultAfter;
    private readonly DisplayType _displayBefore;
    private readonly DisplayType _displayAfter;
    private readonly ColumnKind _kindBefore;
    private readonly ColumnKind _kindAfter;

    public ColumnOptionsCommand(
        Column column,
        bool optional,
        CdbValue? defaultValue,
        DisplayType display,
        ColumnKind kind)
    {
        _column = column;
        _optionalBefore = column.Optional;
        _optionalAfter = optional;
        _defaultBefore = column.DefaultValue;
        _defaultAfter = defaultValue;
        _displayBefore = column.Display;
        _displayAfter = display;
        _kindBefore = column.Kind;
        _kindAfter = kind;

        Label = $"Options of '{column.Name}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public bool IsNoOp =>
        _optionalBefore == _optionalAfter &&
        _displayBefore == _displayAfter &&
        _kindBefore == _kindAfter &&
        SameValue(_defaultBefore, _defaultAfter);

    public void Execute()
    {
        _column.Optional = _optionalAfter;
        _column.DefaultValue = _defaultAfter;
        _column.Display = _displayAfter;
        _column.Kind = _kindAfter;
    }

    public void Undo()
    {
        _column.Optional = _optionalBefore;
        _column.DefaultValue = _defaultBefore;
        _column.Display = _displayBefore;
        _column.Kind = _kindBefore;
    }

    /// <summary>
    /// Structural comparison of default values. The record equality of
    /// <see cref="CdbValue.Number"/> also compares the source text, so <c>1</c> and
    /// <c>1.0</c> would look different even though they mean the same.
    /// </summary>
    private static bool SameValue(CdbValue? a, CdbValue? b) => (a, b) switch
    {
        (null, null) => true,
        (CdbValue.Null, CdbValue.Null) => true,
        (CdbValue.Bool x, CdbValue.Bool y) => x.Value == y.Value,
        (CdbValue.Number x, CdbValue.Number y) => x.Value.Equals(y.Value),
        (CdbValue.Str x, CdbValue.Str y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal),
        (CdbValue.Arr x, CdbValue.Arr y) => CdbWriter.WriteValue(x) == CdbWriter.WriteValue(y),
        (CdbValue.Obj x, CdbValue.Obj y) => CdbWriter.WriteValue(x) == CdbWriter.WriteValue(y),
        _ => false,
    };
}

/// <summary>Renames a column in the header and in every row.</summary>
public sealed class ColumnRenameCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly Column _column;
    private readonly string _oldName;
    private readonly string _newName;

    public ColumnRenameCommand(SheetData sheet, int index, string newName)
    {
        _sheet = sheet;
        _column = sheet.Columns[index];
        _oldName = _column.Name;
        _newName = newName;
        Label = $"Rename Column '{_oldName}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => Apply(_oldName, _newName);

    public void Undo() => Apply(_newName, _oldName);

    private void Apply(string from, string to)
    {
        _column.Name = to;

        foreach (var row in _sheet.Lines)
            row.Rename(from, to);
    }
}

/// <summary>
/// Puts the columns of a sheet into a given order, by name.
/// <para>
/// Reordering a single dragged column could be expressed as a move, but a table that
/// follows a template has to end up with exactly the template's order even when it was
/// missing a column; working from the whole name list makes that one step and one undo.
/// Names that are not in the list keep their relative position at the end.
/// </para>
/// </summary>
public sealed class ColumnReorderCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly List<string> _order;
    private readonly List<CdbObject> _before;
    private List<CdbObject> _after = new();

    public ColumnReorderCommand(SheetData sheet, IReadOnlyList<string> order)
    {
        _sheet = sheet;
        _order = order.ToList();
        _before = sheet.Columns.Select(c => c.Raw).ToList();

        // Compared against the columns as they are now. A command that is built before the
        // columns it has to order exist yet (applying a template) therefore reports "not a
        // no-op", which is the safe direction.
        IsNoOp = Compute().SequenceEqual(_before);
        Label = "Reorder Columns";
    }

    public string Label { get; }

    public bool IsNoOp { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute()
    {
        _after = Compute();
        Apply(_after);
    }

    public void Undo() => Apply(_before);

    /// <summary>
    /// The order the sheet should end up in, resolved against the columns it has right
    /// now. Names that are not in the list keep their relative position at the end.
    /// </summary>
    private List<CdbObject> Compute()
    {
        var remaining = _sheet.Columns.ToList();
        var result = new List<CdbObject>(remaining.Count);

        foreach (var name in _order)
        {
            var hit = remaining.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            if (hit is null)
                continue;
            result.Add(hit.Raw);
            remaining.Remove(hit);
        }

        foreach (var column in remaining)
            result.Add(column.Raw);

        return result;
    }

    private void Apply(List<CdbObject> order)
    {
        // The typed list and the raw array are rebuilt from the same object order so the
        // two stay parallel. Columns are few, so the linear lookup costs nothing.
        var columns = new List<Column>(order.Count);
        foreach (var raw in order)
            foreach (var column in _sheet.Columns)
                if (ReferenceEquals(column.Raw, raw))
                {
                    columns.Add(column);
                    break;
                }

        _sheet.ReplaceColumns(columns);
    }
}

/// <summary>Reorders a column inside the sheet.</summary>
public sealed class ColumnMoveCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly int _from;
    private readonly int _to;

    public ColumnMoveCommand(SheetData sheet, int from, int to)
    {
        _sheet = sheet;
        _from = from;
        _to = Math.Clamp(to, 0, sheet.Columns.Count - 1);
        Label = "Move Column";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => Move(_from, _to);

    public void Undo() => Move(_to, _from);

    private void Move(int from, int to)
    {
        if (from == to)
            return;

        var raw = _sheet.Columns[from].Raw;
        _sheet.RemoveColumnAt(from);
        _sheet.InsertColumn(to, raw);
    }
}

/// <summary>
/// Changes a column type. The type string is replaced and the values that cannot be
/// carried over are rewritten to the new type's default, which is what the original
/// editor does when a column is converted.
/// </summary>
public sealed class ColumnTypeChangeCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly Column _column;
    private readonly string _name;
    private readonly string? _oldTypeStr;
    private readonly string _newTypeStr;
    private readonly Database _database;
    private readonly List<CdbValue?> _before = new();
    private readonly List<CdbValue?> _after = new();

    public ColumnTypeChangeCommand(SheetData sheet, int index, ColumnType newType, Database database)
    {
        _sheet = sheet;
        _column = sheet.Columns[index];
        _name = _column.Name;
        _oldTypeStr = _column.TypeStr;
        _newTypeStr = Serialization.ColumnTypeCodec.Encode(newType);
        _database = database;

        foreach (var row in sheet.Lines)
            _before.Add(row.Get(_name));

        // Build the converted values once so undo and redo are exact.
        _column.Type = newType;
        var fallback = TypeDefaults.Create(_column, database, sheet, ignoreOptional: true);
        for (var i = 0; i < sheet.Lines.Count; i++)
            _after.Add(ValueConverter.Convert(_before[i], newType, fallback));
        _column.Type = Serialization.ColumnTypeCodec.Decode(_oldTypeStr ?? "1");

        Label = $"Change Type of '{_name}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute()
    {
        _column.Raw.Set("typeStr", new CdbValue.Str(_newTypeStr));
        _column.Type = Serialization.ColumnTypeCodec.Decode(_newTypeStr);
        Write(_after);
    }

    public void Undo()
    {
        if (_oldTypeStr is null)
            _column.Raw.Remove("typeStr");
        else
            _column.Raw.Set("typeStr", new CdbValue.Str(_oldTypeStr));

        _column.Type = Serialization.ColumnTypeCodec.Decode(_oldTypeStr ?? "1");
        Write(_before);
    }

    private void Write(List<CdbValue?> values)
    {
        for (var i = 0; i < _sheet.Lines.Count && i < values.Count; i++)
            _sheet.Lines[i].Set(_name, CdbValueCloner.CloneOrNull(values[i]));
    }
}

/// <summary>Value level conversions used when a column changes type.</summary>
public static class ValueConverter
{
    /// <summary>
    /// Converts a stored value to another type. Values that cannot be expressed in the
    /// new type fall back to <paramref name="fallback"/> instead of failing, so that a
    /// type change never destroys a whole column.
    /// </summary>
    public static CdbValue? Convert(CdbValue? value, ColumnType target, CdbValue? fallback)
    {
        if (value is null or CdbValue.Null)
            return null;

        switch (target)
        {
            case ColumnType.Text or ColumnType.Id or ColumnType.Image or ColumnType.File
                or ColumnType.Guid or ColumnType.Formula:
                return new CdbValue.Str(AsText(value));

            case ColumnType.Int or ColumnType.Float:
                return AsNumber(value) is { } number
                    ? new CdbValue.Number(target is ColumnType.Int ? Math.Round(number) : number, null)
                    : fallback;

            case ColumnType.Bool:
                return value switch
                {
                    CdbValue.Bool b => b,
                    CdbValue.Number n => new CdbValue.Bool(Math.Abs(n.Value) > double.Epsilon),
                    CdbValue.Str s => new CdbValue.Bool(s.Value.Length > 0 && s.Value != "false" && s.Value != "0"),
                    _ => fallback,
                };

            case ColumnType.Enum e:
                if (e.Values.Count == 0)
                    return fallback;
                if (value is CdbValue.Number n2)
                {
                    var index = (int)Math.Round(n2.Value);
                    return new CdbValue.Number(index >= 0 && index < e.Values.Count ? index : 0, null);
                }
                if (value is CdbValue.Str s2)
                {
                    var index = -1;
                    for (var i = 0; i < e.Values.Count; i++)
                    {
                        if (string.Equals(e.Values[i], s2.Value, StringComparison.Ordinal))
                        {
                            index = i;
                            break;
                        }
                    }
                    return new CdbValue.Number(index >= 0 ? index : 0, null);
                }
                return fallback;

            case ColumnType.Color:
                return value is CdbValue.Number or CdbValue.Str ? value : fallback;

            case ColumnType.List:
                return value is CdbValue.Arr ? value : new CdbValue.Arr();

            case ColumnType.Properties or ColumnType.Polymorph:
                return value is CdbValue.Obj ? value : fallback;

            default:
                return fallback;
        }
    }

    private static string AsText(CdbValue value) => value switch
    {
        CdbValue.Str s => s.Value,
        CdbValue.Number n => n.Raw ?? Model.CdbNumber.Format(n.Value),
        CdbValue.Bool b => b.Value ? "true" : "false",
        _ => value.ToString() ?? string.Empty,
    };

    private static double? AsNumber(CdbValue value) => value switch
    {
        CdbValue.Number n => n.Value,
        CdbValue.Bool b => b.Value ? 1 : 0,
        CdbValue.Str s => double.TryParse(s.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        _ => null,
    };
}
