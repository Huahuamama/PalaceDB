using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Rewrites the field list of the hidden sub-table behind a List or Properties column.
/// <para>
/// Adding, removing, renaming, retyping and reordering a field is one undo step, and the
/// values already stored in the cells follow the change: a renamed field keeps its
/// values, a removed one drops them, and a new one starts from its type default. Without
/// that, editing the fields of a list would only change the header of the panel while the
/// data underneath stayed in the old shape.
/// </para>
/// </summary>
public sealed class SubColumnsCommand : IEditorCommand
{
    private readonly Database _database;
    private readonly SheetData _sub;
    private readonly SheetData _parent;
    private readonly Column _column;
    private readonly IReadOnlyList<SubColumnSpec> _specs;

    // What the sub-table looked like before the edit: the raw definitions (so undo can
    // put the exact same objects back) and the type each field had (so a value is only
    // converted when its type really changed).
    private readonly List<CdbObject> _columnsBefore = new();
    private readonly List<string> _typesBefore = new();
    private readonly List<string> _namesBefore = new();

    private readonly List<CdbValue?> _valuesBefore = new();
    private List<CdbValue?>? _valuesAfter;

    public SubColumnsCommand(
        Database database,
        SheetData sub,
        SheetData parent,
        Column column,
        IReadOnlyList<SubColumnSpec> specs)
    {
        _database = database;
        _sub = sub;
        _parent = parent;
        _column = column;
        _specs = specs;

        foreach (var field in sub.Columns)
        {
            _columnsBefore.Add(field.Raw);
            _typesBefore.Add(ColumnTypeCodec.Encode(field.Type));
            _namesBefore.Add(field.Name);
        }

        foreach (var row in parent.Lines)
            _valuesBefore.Add(row.Get(column.Name));

        IsNoOp = Matches();
        Label = $"Edit the fields of '{column.Name}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    /// <summary>True when the request describes the fields the sub-table already has.</summary>
    public bool IsNoOp { get; }

    public void Execute()
    {
        ApplyColumns(BuildColumns());
        ApplyValues(_valuesAfter ??= BuildValues());
    }

    public void Undo()
    {
        ApplyColumns(_columnsBefore);
        ApplyValues(_valuesBefore);
    }

    private bool Matches()
    {
        if (_sub.Columns.Count != _specs.Count)
            return false;

        for (var i = 0; i < _specs.Count; i++)
        {
            var spec = _specs[i];
            var field = _sub.Columns[i];
            if (!string.Equals(spec.Name, field.Name, StringComparison.Ordinal))
                return false;
            if (!string.Equals(ColumnTypeCodec.Encode(spec.Type), _typesBefore[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Builds the new list of definitions. A field that kept its identity reuses its raw
    /// object, so anything the dialog does not show (an enum's values, a display format,
    /// a shared definition) survives the edit. A field that changed is written to a copy:
    /// the originals stay untouched, which is what makes undo exact.
    /// </summary>
    private List<CdbObject> BuildColumns()
    {
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _namesBefore.Count; i++)
            byName[_namesBefore[i]] = i;

        var used = new HashSet<int>();
        var result = new List<CdbObject>(_specs.Count);

        foreach (var spec in _specs)
        {
            var key = spec.Original ?? spec.Name;
            var code = ColumnTypeCodec.Encode(spec.Type);

            if (byName.TryGetValue(key, out var index) && used.Add(index))
            {
                var before = _columnsBefore[index];
                var renaming = !string.Equals(spec.Name, _namesBefore[index], StringComparison.Ordinal);
                var retyping = !string.Equals(code, _typesBefore[index], StringComparison.Ordinal);

                if (!renaming && !retyping)
                {
                    result.Add(before);
                    continue;
                }

                var raw = before.Clone();
                raw.Set("name", new CdbValue.Str(spec.Name));
                if (retyping)
                {
                    // An explicit type replaces a shared definition: the two can no longer
                    // describe the same column.
                    raw.Remove("structRef");
                    raw.Set("typeStr", new CdbValue.Str(code));
                }
                result.Add(raw);
                continue;
            }

            var added = new CdbObject();
            added.Set("name", new CdbValue.Str(spec.Name));
            added.Set("typeStr", new CdbValue.Str(code));
            result.Add(added);
        }

        return result;
    }

    private void ApplyColumns(List<CdbObject> raws)
    {
        _sub.Raw.GetArray("columns")?.Items.Clear();
        _sub.Columns.Clear();

        for (var i = 0; i < raws.Count; i++)
            _sub.InsertColumn(i, raws[i]);
    }

    private void ApplyValues(List<CdbValue?> values)
    {
        for (var i = 0; i < _parent.Lines.Count && i < values.Count; i++)
        {
            if (values[i] is null)
                _parent.Lines[i].Remove(_column.Name);
            else
                _parent.Lines[i].Set(_column.Name, CdbValueCloner.Clone(values[i]!));
        }
    }

    private List<CdbValue?> BuildValues()
    {
        var result = new List<CdbValue?>(_valuesBefore.Count);
        foreach (var value in _valuesBefore)
            result.Add(Migrate(value));
        return result;
    }

    private CdbValue? Migrate(CdbValue? value) => value switch
    {
        CdbValue.Arr list => new CdbValue.Arr(list.Items
            .Select(item => item is CdbValue.Obj obj
                ? (CdbValue)new CdbValue.Obj(MigrateObject(obj.Value))
                : item)
            .ToList()),
        CdbValue.Obj props => new CdbValue.Obj(MigrateObject(props.Value)),
        _ => value,
    };

    /// <summary>Rebuilds one record in the new field order, keeping what still applies.</summary>
    private CdbObject MigrateObject(CdbObject source)
    {
        var target = new CdbObject();
        var count = Math.Min(_specs.Count, _sub.Columns.Count);

        for (var i = 0; i < count; i++)
        {
            var spec = _specs[i];
            var field = _sub.Columns[i];
            var oldName = spec.Original ?? spec.Name;

            var existing = source.Get(oldName);
            if (existing is null && !string.Equals(oldName, spec.Name, StringComparison.Ordinal))
                existing = source.Get(spec.Name);

            CdbValue? value;
            if (existing is null or CdbValue.Null)
            {
                value = TypeDefaults.Create(field, _database, _sub);
            }
            else if (KeptType(oldName, field))
            {
                value = existing;
            }
            else
            {
                // A field whose type changed keeps what can be expressed in the new type
                // and falls back to its default otherwise.
                value = ValueConverter.Convert(existing, field.Type, TypeDefaults.Create(field, _database, _sub));
            }

            if (value is not null)
                target.Set(field.Name, CdbValueCloner.Clone(value));
        }

        return target;
    }

    private bool KeptType(string oldName, Column field)
    {
        var index = _namesBefore.IndexOf(oldName);
        return index >= 0 &&
            string.Equals(_typesBefore[index], ColumnTypeCodec.Encode(field.Type), StringComparison.Ordinal);
    }
}

/// <summary>
/// Renames a hidden sub-table. The name carries the column it belongs to
/// (<c>Table@Column</c>), so renaming that column has to move the sub-table with it —
/// otherwise the fields would silently disappear from the list they describe.
/// </summary>
public sealed class SubSheetRenameCommand : IEditorCommand
{
    private readonly SheetData _sub;
    private readonly string _oldName;
    private readonly string _newName;

    public SubSheetRenameCommand(SheetData sub, string newName)
    {
        _sub = sub;
        _oldName = sub.Name;
        _newName = newName;
        Label = $"Rename Sub-table '{_oldName}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => _sub.Name = _newName;

    public void Undo() => _sub.Name = _oldName;
}
