using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>Creates a new table with a single ID column.</summary>
public sealed class SheetInsertCommand : IEditorCommand
{
    private readonly Database _database;
    private readonly int _index;
    private readonly CdbObject _raw;

    public SheetInsertCommand(Database database, int index, string name)
    {
        _database = database;
        _index = Math.Clamp(index, 0, database.Sheets.Count);

        var columns = new CdbValue.Arr();
        var idColumn = new CdbObject();
        idColumn.Set("typeStr", new CdbValue.Str("0"));
        idColumn.Set("name", new CdbValue.Str("id"));
        columns.Items.Add(new CdbValue.Obj(idColumn));

        _raw = new CdbObject();
        _raw.Set("name", new CdbValue.Str(name));
        _raw.Set("columns", columns);
        _raw.Set("lines", new CdbValue.Arr());

        Name = name;
        Label = $"Add Table '{name}'";
    }

    public string Label { get; }

    public string Name { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public SheetData? Created { get; private set; }

    public void Execute() => Created = _database.InsertSheet(_index, _raw);

    public void Undo() => _database.RemoveSheetAt(_index);
}

/// <summary>
/// Removes a table. Hidden sub-tables that belong to it are removed as well and any
/// reference column that pointed at it becomes a plain text column, which is what the
/// original editor does so that no data is lost.
/// </summary>
/// <summary>
/// Creates the hidden sheet that backs a List or Properties column, with the columns it
/// declares. Separate from <see cref="SheetInsertCommand"/> so that turning an existing
/// column into a List can create its sub-table as part of the same undo step.
/// </summary>
public sealed class SubSheetInsertCommand : IEditorCommand
{
    private readonly Database _database;
    private readonly CdbObject _raw;
    private readonly int _index;

    public SubSheetInsertCommand(Database database, string name, IReadOnlyList<SubColumnSpec> columns)
    {
        _database = database;
        _index = database.Sheets.Count;

        var columnNodes = new CdbValue.Arr();
        foreach (var spec in columns)
        {
            var node = new CdbObject();
            node.Set("name", new CdbValue.Str(spec.Name));
            node.Set("typeStr", new CdbValue.Str(Serialization.ColumnTypeCodec.Encode(spec.Type)));
            columnNodes.Items.Add(new CdbValue.Obj(node));
        }

        var props = new CdbObject();
        props.Set("hide", new CdbValue.Bool(true));

        _raw = new CdbObject();
        _raw.Set("name", new CdbValue.Str(name));
        _raw.Set("columns", columnNodes);
        _raw.Set("lines", new CdbValue.Arr());
        _raw.Set("props", new CdbValue.Obj(props));

        Label = $"Add Sub-table '{name}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => _database.InsertSheet(_index, _raw);

    public void Undo() => _database.RemoveSheetAt(_index);
}

public sealed class SheetDeleteCommand : IEditorCommand
{
    private readonly Database _database;
    private readonly string _name;
    private readonly List<(int Index, CdbObject Raw)> _removed = new();
    private readonly List<(Column Column, string? TypeStr)> _converted = new();

    public SheetDeleteCommand(Database database, string name)
    {
        _database = database;
        _name = name;

        // Capture the table and every hidden sub-table that belongs to it.
        for (var i = database.Sheets.Count - 1; i >= 0; i--)
        {
            var sheet = database.Sheets[i];
            if (sheet.Name == name || sheet.Name.StartsWith(name + "@", StringComparison.Ordinal))
                _removed.Insert(0, (i, sheet.Raw));
        }

        foreach (var sheet in database.Sheets)
        {
            foreach (var column in sheet.Columns)
            {
                if (column.Type is ColumnType.Ref r && string.Equals(r.Sheet, name, StringComparison.Ordinal))
                    _converted.Add((column, column.TypeStr));
            }
        }

        Label = $"Delete Table '{name}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public int RemovedCount => _removed.Count;

    public void Execute()
    {
        foreach (var entry in _converted)
            entry.Column.Type = new ColumnType.Text();

        // Remove from the highest index down so the earlier indices stay valid.
        for (var i = _removed.Count - 1; i >= 0; i--)
            _database.RemoveSheetAt(_removed[i].Index);
    }

    public void Undo()
    {
        foreach (var (index, raw) in _removed)
            _database.InsertSheet(index, raw);

        foreach (var entry in _converted)
        {
            if (entry.TypeStr is null)
                entry.Column.Raw.Remove("typeStr");
            else
                entry.Column.Raw.Set("typeStr", new CdbValue.Str(entry.TypeStr));
            entry.Column.Type = Serialization.ColumnTypeCodec.Decode(entry.TypeStr ?? "1");
        }
    }
}

/// <summary>Renames a table, including the hidden sub-tables that carry its prefix.</summary>
public sealed class SheetRenameCommand : IEditorCommand
{
    private readonly Database _database;
    private readonly string _oldName;
    private readonly string _newName;

    public SheetRenameCommand(Database database, string oldName, string newName)
    {
        _database = database;
        _oldName = oldName;
        _newName = newName;
        Label = $"Rename Table '{oldName}'";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => Apply(_oldName, _newName);

    public void Undo() => Apply(_newName, _oldName);

    private void Apply(string from, string to)
    {
        foreach (var sheet in _database.Sheets)
        {
            if (sheet.Name == from)
                sheet.Name = to;
            else if (sheet.Name.StartsWith(from + "@", StringComparison.Ordinal))
                sheet.Name = to + sheet.Name[from.Length..];
        }

        // Reference columns follow the table they point at.
        foreach (var sheet in _database.Sheets)
        {
            foreach (var column in sheet.Columns)
            {
                if (column.Type is ColumnType.Ref r && string.Equals(r.Sheet, from, StringComparison.Ordinal))
                    column.Type = new ColumnType.Ref(to);
            }
        }
    }
}
