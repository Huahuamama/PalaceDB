using PalaceDB.Core.Cursor;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Gives a public enum / flags type a new value list and pushes it through every column
/// that is bound to it, as one undoable step.
/// <para>
/// The columns keep their own <c>typeStr</c> — that is what makes the .cdb a plain
/// CastleDB file — so the command rewrites all of them at once, and it also rewrites the
/// stored values so that a renamed, inserted or removed entry does not silently point at
/// a different label afterwards. Undo restores the definition, the bindings and the data.
/// </para>
/// </summary>
public sealed class PublicTypeValuesCommand : IEditorCommand
{
    private readonly MetaDocument _meta;
    private readonly string _name;
    private readonly string _fromName;
    private readonly PublicTypeMeta? _before;
    private readonly PublicTypeMeta _after;
    private readonly List<ColumnState> _columns = new();

    private sealed class ColumnState
    {
        public required Column Column { get; init; }
        public required string? BeforeTypeStr { get; init; }
        public required ColumnType BeforeType { get; init; }
        public required List<CdbValue?> Before { get; init; }
        public required List<CdbValue?> After { get; init; }

        /// <summary>
        /// The records a field of a list lives in, or null for a column of a real table.
        /// A field is a column of the hidden sub-table, which has no rows of its own: its
        /// values sit inside the records of the parent column.
        /// </summary>
        public List<RecordSlot>? Slots { get; init; }
    }

    /// <summary>One value of a sub-table field: the record it is in and the name it has.</summary>
    private sealed record RecordSlot(CdbObject Row, string Name);

    public PublicTypeValuesCommand(
        Database database,
        MetaDocument meta,
        string name,
        string kind,
        IReadOnlyList<string> values,
        string? renameFrom = null)
    {
        _meta = meta;
        _name = name;
        _fromName = string.IsNullOrEmpty(renameFrom) ? name : renameFrom;

        _after = new PublicTypeMeta
        {
            Kind = string.Equals(kind, "flags", StringComparison.OrdinalIgnoreCase) ? "flags" : "enum",
            Values = values.Count == 0 ? new List<string> { string.Empty } : new List<string>(values),
        };

        if (meta.PublicTypes.TryGetValue(_fromName, out var existing))
            _before = existing.Clone();

        var oldValues = _before is null
            ? Array.Empty<string>()
            : PublicTypeService.Normalized(_before);
        var oldIsFlags = _before?.IsFlags == true;

        // A rename moves the bindings; a plain update keeps them where they are.
        var bound = PublicTypeService.BoundColumns(database, meta, _fromName);

        foreach (var (sheet, column) in bound)
        {
            // A field of a List / Properties column has no rows of its own: its values are
            // the matching key of every record of the column that owns the sub-table.
            var slots = sheet.Props.Hide ? SlotsOf(database, sheet, column) : null;

            var before = new List<CdbValue?>(slots?.Count ?? sheet.Lines.Count);
            if (slots is null)
            {
                foreach (var row in sheet.Lines)
                    before.Add(row.Get(column.Name));
            }
            else
            {
                foreach (var slot in slots)
                    before.Add(slot.Row.Get(slot.Name));
            }

            var after = new List<CdbValue?>(before.Count);
            foreach (var value in before)
                after.Add(Remap(value, oldValues, oldIsFlags, _after.Values, _after.IsFlags,
                    column.EnumAsString));

            _columns.Add(new ColumnState
            {
                Column = column,
                BeforeTypeStr = column.TypeStr,
                BeforeType = column.Type,
                Before = before,
                After = after,
                Slots = slots,
            });
        }

        Label = _before is null ? $"Create Public Type '{name}'" : $"Update Public Type '{name}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    /// <summary>True when nothing but the definition itself would change.</summary>
    public bool IsDefinitionOnly => _columns.Count == 0 && _before is not null &&
                                    SameValues(_before, _after);

    public void Execute()
    {
        ApplyMeta(_name, _after);
        if (!string.Equals(_fromName, _name, StringComparison.Ordinal))
        {
            _meta.PublicTypes.Remove(_fromName);
            foreach (var (sheetName, columnName) in _meta.ColumnsBoundTo(_fromName))
                _meta.SetColumnPublicType(sheetName, columnName, _name);
        }

        foreach (var state in _columns)
        {
            state.Column.Type = PublicTypeService.ToColumnType(_after);
            state.Column.FlushType();
            Write(state.Column, state.After, state.Slots);
        }
    }

    public void Undo()
    {
        foreach (var state in _columns)
        {
            if (state.BeforeTypeStr is null)
                state.Column.Raw.Remove("typeStr");
            else
                state.Column.Raw.Set("typeStr", new CdbValue.Str(state.BeforeTypeStr));
            state.Column.Type = state.BeforeType;
            Write(state.Column, state.Before, state.Slots);
        }

        if (_before is null)
        {
            _meta.PublicTypes.Remove(_name);
            return;
        }

        ApplyMeta(_fromName, _before);
        if (!string.Equals(_fromName, _name, StringComparison.Ordinal))
        {
            _meta.PublicTypes.Remove(_name);
            foreach (var (sheetName, columnName) in _meta.ColumnsBoundTo(_name))
                _meta.SetColumnPublicType(sheetName, columnName, _fromName);
        }
    }

    private void ApplyMeta(string name, PublicTypeMeta type)
    {
        _meta.PublicTypes[name] = new PublicTypeMeta
        {
            Kind = type.Kind,
            Values = new List<string>(type.Values),
        };
    }

    private static void Write(Column column, List<CdbValue?> values, List<RecordSlot>? slots)
    {
        if (slots is not null)
        {
            for (var i = 0; i < slots.Count && i < values.Count; i++)
                slots[i].Row.Set(slots[i].Name, CdbValueCloner.CloneOrNull(values[i]));
            return;
        }

        var sheet = column.Sheet;
        for (var i = 0; i < sheet.Lines.Count && i < values.Count; i++)
            sheet.Lines[i].Set(column.Name, CdbValueCloner.CloneOrNull(values[i]));
    }

    /// <summary>
    /// Every record a sub-table field appears in, in document order: the parent column is
    /// the List / Properties column whose hidden sub-table this is.
    /// </summary>
    private static List<RecordSlot> SlotsOf(Database database, SheetData sub, Column field)
    {
        var slots = new List<RecordSlot>();
        foreach (var parent in database.Sheets)
        {
            if (parent.Props.Hide)
                continue;

            foreach (var column in parent.Columns)
            {
                if (column.Type is not (ColumnType.List or ColumnType.Properties))
                    continue;

                if (!ReferenceEquals(parent.GetSub(column), sub))
                    continue;

                foreach (var row in parent.Lines)
                {
                    switch (row.Get(column.Name))
                    {
                        case CdbValue.Arr list:
                            foreach (var item in list.Items)
                                if (item is CdbValue.Obj obj)
                                    slots.Add(new RecordSlot(obj.Value, field.Name));
                            break;

                        case CdbValue.Obj props:
                            slots.Add(new RecordSlot(props.Value, field.Name));
                            break;
                    }
                }

                return slots;
            }
        }

        return slots;
    }

    private static bool SameValues(PublicTypeMeta a, PublicTypeMeta b)
    {
        if (!string.Equals(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase) ||
            a.Values.Count != b.Values.Count)
            return false;
        for (var i = 0; i < a.Values.Count; i++)
            if (!string.Equals(a.Values[i], b.Values[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// Points a stored value at the same label in the new list. A label that survived is
    /// followed by name (so reordering and deletions are safe). When the list kept its
    /// length a label that vanished was renamed in place, so its position is kept;
    /// otherwise it was deleted, and the value falls back to the first entry instead of
    /// silently becoming whatever now sits at that index.
    /// Turning an enum into a flags column (or back) turns the index into a bit and the
    /// other way round.
    /// </summary>
    private static CdbValue? Remap(
        CdbValue? value,
        IReadOnlyList<string> oldValues,
        bool oldIsFlags,
        IReadOnlyList<string> newValues,
        bool newIsFlags,
        bool enumAsString)
    {
        var renamedInPlace = oldValues.Count == newValues.Count;

        switch (value)
        {
            case null or CdbValue.Null:
                return value;

            case CdbValue.Number number when oldIsFlags != newIsFlags:
            {
                var raw = (long)Math.Round(number.Value);
                var label = oldIsFlags
                    ? LabelOfBit(oldValues, raw)
                    : raw >= 0 && raw < oldValues.Count ? oldValues[(int)raw] : null;
                if (label is null)
                    return new CdbValue.Number(0, null);

                var index = IndexOf(newValues, label);
                if (index < 0 && !oldIsFlags && raw < newValues.Count)
                    index = (int)raw;
                if (index < 0)
                    index = 0;
                return new CdbValue.Number(newIsFlags ? 1L << index : index, null);
            }

            case CdbValue.Number number when newIsFlags:
            {
                var mask = (long)number.Value;
                var rebuilt = 0L;
                for (var bit = 0; bit < oldValues.Count && bit < 32; bit++)
                {
                    if ((mask & (1L << bit)) == 0)
                        continue;
                    var target = IndexOf(newValues, oldValues[bit]);
                    if (target < 0 && renamedInPlace && bit < newValues.Count)
                        target = bit;
                    if (target >= 0)
                        rebuilt |= 1L << target;
                }
                return new CdbValue.Number(rebuilt, null);
            }

            case CdbValue.Str text when !newIsFlags:
            {
                if (IndexOf(newValues, text.Value) >= 0)
                    return text;
                var oldIndex = IndexOf(oldValues, text.Value);
                if (renamedInPlace && oldIndex >= 0 && oldIndex < newValues.Count)
                    return new CdbValue.Str(newValues[oldIndex]);
                return new CdbValue.Str(newValues.Count > 0 ? newValues[0] : string.Empty);
            }

            case CdbValue.Number number when !newIsFlags:
            {
                var oldIndex = (int)Math.Round(number.Value);
                var label = oldIndex >= 0 && oldIndex < oldValues.Count ? oldValues[oldIndex] : null;
                var target = label is null ? -1 : IndexOf(newValues, label);
                if (target < 0 && renamedInPlace && oldIndex >= 0 && oldIndex < newValues.Count)
                    target = oldIndex;
                if (target < 0)
                    target = 0;
                return new CdbValue.Number(target, null);
            }

            default:
                return value;
        }
    }

    private static string? LabelOfBit(IReadOnlyList<string> values, long mask)
    {
        for (var bit = 0; bit < values.Count && bit < 32; bit++)
            if ((mask & (1L << bit)) != 0)
                return values[bit];
        return null;
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
