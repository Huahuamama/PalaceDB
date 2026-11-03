using PalaceDB.Core.Cursor;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Creates a table from a list of column definitions (a table template), together with
/// the hidden sub-tables the list columns need.
/// <para>
/// The whole thing is one undo step: a table that arrives with five columns and two
/// sub-tables has to disappear in one go, or Ctrl+Z would leave half a table behind.
/// </para>
/// </summary>
public sealed class CreateTableCommand : IEditorCommand
{
    private readonly Database _database;
    private readonly int _index;
    private readonly CdbObject _raw;
    private readonly List<CdbObject> _subRaws = new();

    public CreateTableCommand(
        Database database,
        int index,
        string name,
        IReadOnlyList<ColumnSpec> columns)
    {
        _database = database;
        _index = Math.Clamp(index, 0, database.Sheets.Count);

        var columnNodes = new CdbValue.Arr();
        foreach (var spec in columns)
            columnNodes.Items.Add(new CdbValue.Obj(spec.ToRaw()));

        _raw = new CdbObject();
        _raw.Set("name", new CdbValue.Str(name));
        _raw.Set("columns", columnNodes);
        _raw.Set("lines", new CdbValue.Arr());

        foreach (var spec in columns)
        {
            if (!spec.IsContainer || spec.Fields.Count == 0)
                continue;

            var fields = new CdbValue.Arr();
            foreach (var field in spec.Fields)
            {
                var node = new CdbObject();
                node.Set("name", new CdbValue.Str(field.Name));
                node.Set("typeStr", new CdbValue.Str(field.TypeStr));
                fields.Items.Add(new CdbValue.Obj(node));
            }

            var props = new CdbObject();
            props.Set("hide", new CdbValue.Bool(true));

            var sub = new CdbObject();
            sub.Set("name", new CdbValue.Str($"{name}@{spec.Name}"));
            sub.Set("columns", fields);
            sub.Set("lines", new CdbValue.Arr());
            sub.Set("props", new CdbValue.Obj(props));
            _subRaws.Add(sub);
        }

        Name = name;
        Label = $"Add Table '{name}'";
    }

    public string Label { get; }

    public string Name { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public SheetData? Created { get; private set; }

    public void Execute()
    {
        Created = _database.InsertSheet(_index, _raw);
        foreach (var sub in _subRaws)
            _database.InsertSheet(_database.Sheets.Count, sub);
    }

    public void Undo()
    {
        // The sub-tables were appended at the end, so they are removed first.
        for (var i = _database.Sheets.Count - 1; i >= 0; i--)
            if (_subRaws.Any(raw => ReferenceEquals(_database.Sheets[i].Raw, raw)))
                _database.RemoveSheetAt(i);

        _database.RemoveSheetAt(_index);
        Created = null;
    }
}

/// <summary>
/// Records that a table follows a template (or stops following one).
/// <para>
/// The link lives in the side-car, which is not part of the undo history, so it is
/// carried by a command of its own: undoing "apply template" has to unlink the table as
/// well, otherwise the columns would come back to a table that still believes it is
/// managed by a template it no longer matches.
/// </para>
/// </summary>
public sealed class TemplateLinkCommand : IEditorCommand
{
    private readonly MetaStore _meta;
    private readonly string _sheetName;
    private readonly string? _before;
    private readonly string? _after;

    public TemplateLinkCommand(MetaStore meta, string sheetName, string? templateId, string? label = null)
    {
        _meta = meta;
        _sheetName = sheetName;
        _before = meta.GetTableTemplate(sheetName);
        _after = templateId;

        var name = meta.TemplateName(templateId) ?? "template";
        Label = label ?? (templateId is null
            ? "Stop Following the Template"
            : $"Follow Template '{name}'");
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public bool IsNoOp => string.Equals(_before, _after, StringComparison.Ordinal);

    public void Execute() => _meta.SetTableTemplate(_sheetName, _after);

    public void Undo() => _meta.SetTableTemplate(_sheetName, _before);
}
