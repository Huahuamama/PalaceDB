using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Model;

/// <summary>
/// Root of a CDB document. The model wraps the parsed JSON tree instead of
/// rebuilding it, so unknown fields, key order and explicit nulls survive a
/// load/save cycle untouched.
/// </summary>
public sealed class Database
{
    public const string KeySheets = "sheets";
    public const string KeyCustomTypes = "customTypes";
    public const string KeyCompress = "compress";

    public CdbObject Root { get; }

    public List<SheetData> Sheets { get; } = new();

    public List<CustomType> CustomTypes { get; } = new();

    public Database() : this(new CdbObject())
    {
        Root.Set(KeySheets, new CdbValue.Arr());
        Root.Set(KeyCustomTypes, new CdbValue.Arr());
        Root.Set(KeyCompress, new CdbValue.Bool(false));
    }

    public Database(CdbObject root)
    {
        Root = root;
        Load();
    }

    public bool Compress
    {
        get => Root.GetBool(KeyCompress, false);
        set => Root.Set(KeyCompress, new CdbValue.Bool(value));
    }

    private void Load()
    {
        Sheets.Clear();
        CustomTypes.Clear();

        if (Root.GetArray(KeySheets) is { } sheets)
            foreach (var item in sheets.Items)
                if (item is CdbValue.Obj o)
                    Sheets.Add(new SheetData(this, o.Value));

        if (Root.GetArray(KeyCustomTypes) is { } types)
            foreach (var item in types.Items)
                if (item is CdbValue.Obj o)
                {
                    var customType = new CustomType(o.Value);
                    customType.Load();
                    CustomTypes.Add(customType);
                }

        // structRef columns inherit their type from the referenced column, which can
        // only be resolved once every sheet has been read.
        foreach (var sheet in Sheets)
            sheet.ResolveDeferred();
    }

    /// <summary>
    /// Brings the raw tree in sync with the typed model before serialisation
    /// (column types, separators). Called automatically by <see cref="Json.CdbWriter"/>.
    /// </summary>
    public void PrepareForSave()
    {
        foreach (var sheet in Sheets)
            sheet.PrepareForSave();
    }

    public SheetData? GetSheet(string name) =>
        Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Adds a sheet to both the typed list and the raw "sheets" array. The two must stay
    /// parallel, so all structural edits go through this helper.
    /// </summary>
    internal SheetData InsertSheet(int index, CdbObject raw)
    {
        var array = Root.GetArray(KeySheets);
        if (array is null)
        {
            array = new CdbValue.Arr();
            Root.Set(KeySheets, array);
        }

        index = Math.Clamp(index, 0, Sheets.Count);
        array.Items.Insert(index, new CdbValue.Obj(raw));

        var sheet = new SheetData(this, raw);
        Sheets.Insert(index, sheet);
        return sheet;
    }

    /// <summary>Removes a sheet from both the typed list and the raw "sheets" array.</summary>
    internal void RemoveSheetAt(int index)
    {
        if (index < 0 || index >= Sheets.Count)
            return;

        Root.GetArray(KeySheets)?.Items.RemoveAt(index);
        Sheets.RemoveAt(index);
    }

    /// <summary>
    /// Moves a contiguous run of sheets to another position. Both the typed list and the
    /// raw array are reordered in place so that existing <see cref="SheetData"/> handles
    /// stay valid.
    /// </summary>
    /// <param name="from">Index of the first sheet of the run.</param>
    /// <param name="count">Number of sheets in the run.</param>
    /// <param name="to">Target index measured after the run has been taken out.</param>
    internal void MoveSheetRange(int from, int count, int to)
    {
        if (count <= 0 || from < 0 || from + count > Sheets.Count)
            return;

        to = Math.Clamp(to, 0, Sheets.Count - count);
        if (to == from)
            return;

        var sheets = Sheets.GetRange(from, count);
        Sheets.RemoveRange(from, count);
        Sheets.InsertRange(to, sheets);

        if (Root.GetArray(KeySheets) is not { } array || from + count > array.Items.Count)
            return;

        var raws = array.Items.GetRange(from, count);
        array.Items.RemoveRange(from, count);
        array.Items.InsertRange(to, raws);
    }

    /// <summary>Visible sheets only (hidden sheets are the storage of List/Properties columns).</summary>
    public IEnumerable<SheetData> VisibleSheets => Sheets.Where(s => !s.Props.Hide);

    public CustomType? GetCustomType(string name) =>
        CustomTypes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}

/// <summary>A sheet (table) inside the document.</summary>
public sealed class SheetData
{
    public const string KeyName = "name";
    public const string KeyColumns = "columns";
    public const string KeyLines = "lines";
    public const string KeySeparators = "separators";
    public const string KeyProps = "props";

    private readonly Database _db;
    // Separators as they were read, before the ids are resolved and the rows are counted.
    private readonly List<(int Index, string? Id, string? Title)> _rawSeparators = new();
    private readonly bool _propsDetached;

    public CdbObject Raw { get; }

    public SheetProps Props { get; }

    public List<Column> Columns { get; } = new();

    public List<CdbObject> Lines { get; } = new();

    /// <summary>
    /// The headings of the sheet and the rows each of them owns. This is the model; the
    /// index array of the .cdb is derived from it when the file is written.
    /// </summary>
    public SheetGroups Groups { get; }

    /// <summary>Zero-based row indices where a separator is drawn, in row order.</summary>
    public IReadOnlyList<int> Separators => Groups.Indexes();

    /// <summary>
    /// Heading positions paired with their optional titles, in row order. The two on-disk
    /// arrays are parallel, so they are always read and written together.
    /// </summary>
    public IReadOnlyList<(int Index, string? Title)> GetSeparators() => Groups.Starts();

    internal SheetData(Database db, CdbObject raw)
    {
        _db = db;
        Raw = raw;
        Groups = new SheetGroups(this);

        // A sheet without a "props" object must not gain one just by being opened,
        // otherwise the byte-stable round-trip guarantee would be broken. The object
        // is kept detached and only attached when a property is actually set.
        var propsObject = raw.GetObject(KeyProps);
        if (propsObject is null)
        {
            propsObject = new CdbObject();
            _propsDetached = true;
        }
        Props = new SheetProps(this, propsObject);

        if (raw.GetArray(KeyColumns) is { } cols)
            foreach (var item in cols.Items)
                if (item is CdbValue.Obj o)
                    Columns.Add(new Column(this, o.Value));

        if (raw.GetArray(KeyLines) is { } lines)
            foreach (var item in lines.Items)
                if (item is CdbValue.Obj o)
                    Lines.Add(o.Value);

        ReadSeparators(raw);
    }

    /// <summary>Attaches the detached props object when it carries any value.</summary>
    private void FlushProps()
    {
        if (_propsDetached && Props.Raw.Count > 0)
            Raw.Set(KeyProps, new CdbValue.Obj(Props.Raw));
    }

    public string Name
    {
        get => Raw.GetString(KeyName, string.Empty);
        set => Raw.Set(KeyName, new CdbValue.Str(value));
    }

    /// <summary>True for the hidden sheets backing List/Properties/Polymorph columns.</summary>
    public bool IsHidden => Props.Hide;

    public bool IsLevel => false; // map feature removed (R1)

    public Column? GetColumn(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Adds a column to both the typed list and the raw "columns" array. The two must
    /// stay parallel, so all structural edits go through this helper.
    /// </summary>
    internal Column InsertColumn(int index, CdbObject raw)
    {
        var array = Raw.GetArray(KeyColumns);
        if (array is null)
        {
            array = new CdbValue.Arr();
            Raw.Set(KeyColumns, array);
        }

        index = Math.Clamp(index, 0, Columns.Count);
        array.Items.Insert(index, new CdbValue.Obj(raw));

        var column = new Column(this, raw);
        Columns.Insert(index, column);
        return column;
    }

    /// <summary>Removes a column from both the typed list and the raw "columns" array.</summary>
    internal void RemoveColumnAt(int index)
    {
        if (index < 0 || index >= Columns.Count)
            return;

        Raw.GetArray(KeyColumns)?.Items.RemoveAt(index);
        Columns.RemoveAt(index);
    }

    /// <summary>
    /// Rewrites the column order from an existing list of columns. Used by the reorder
    /// command, which moves the same objects around instead of rebuilding them, so
    /// metadata, widths and the values already stored in the rows stay attached.
    /// </summary>
    internal void ReplaceColumns(IReadOnlyList<Column> columns)
    {
        Columns.Clear();
        Columns.AddRange(columns);

        var array = Raw.GetArray(KeyColumns);
        if (array is null)
        {
            array = new CdbValue.Arr();
            Raw.Set(KeyColumns, array);
        }

        array.Items.Clear();
        foreach (var column in columns)
            array.Items.Add(new CdbValue.Obj(column.Raw));
    }

    public Column? IdColumn => Columns.FirstOrDefault(c => c.Type is ColumnType.Id);

    public string? GetRowId(CdbObject row) => IdColumn is { } c ? row.GetString(c.Name) : null;

    /// <summary>Parent sheet when this is a hidden sub-sheet (<c>Parent@Column</c>).</summary>
    public (SheetData? Parent, string? Column)? GetParent()
    {
        if (!IsHidden)
            return null;
        var name = Name;
        var at = name.LastIndexOf('@');
        if (at <= 0)
            return null;
        var parent = _db.GetSheet(name[..at]);
        return parent is null ? null : (parent, name[(at + 1)..]);
    }

    /// <summary>Sheet backing a List/Properties/Polymorph column.</summary>
    public SheetData? GetSub(Column column)
    {
        var target = column.StructRef ?? $"{Name}@{column.Name}";
        return _db.GetSheet(target);
    }

    /// <summary>
    /// Collects the separators as they were written. The rows are not counted yet: a
    /// separator may be stored as an id, which can only be resolved once every sheet has
    /// been read (<see cref="ResolveDeferred"/>).
    /// </summary>
    private void ReadSeparators(CdbObject raw)
    {
        _rawSeparators.Clear();
        if (raw.GetArray(KeySeparators) is not { } arr)
            return;

        // The titles are parallel to the file, not to the entries that could be understood,
        // so the position of the item is what picks the title.
        var titles = Props.SeparatorTitles;
        for (var i = 0; i < arr.Items.Count; i++)
        {
            var title = titles is not null && i < titles.Items.Count
                ? (titles.Items[i] as CdbValue.Str)?.Value
                : null;

            switch (arr.Items[i])
            {
                case CdbValue.Number n:
                    _rawSeparators.Add(((int)n.Value, null, title));
                    break;
                case CdbValue.Obj o:
                    if (o.Value.GetInt("index") is { } index)
                        _rawSeparators.Add((index, null, title));
                    else if (o.Value.GetString("id") is { } id)
                        _rawSeparators.Add((-1, id, title));
                    break;
            }
        }
    }

    /// <summary>Resolves <c>{ "id": ... }</c> separators once rows are available.</summary>
    internal void ResolveDeferred()
    {
        foreach (var column in Columns)
            column.ResolveDeferred();

        var idCol = IdColumn;
        var pairs = new List<(int Index, string? Title)>(_rawSeparators.Count);

        foreach (var (index, id, title) in _rawSeparators)
        {
            if (index >= 0)
            {
                pairs.Add((index, title));
                continue;
            }

            // An id that names no row has lost its position: the heading goes to the top
            // rather than pointing at a row that is not there.
            var row = idCol is null || id is null
                ? -1
                : Lines.FindIndex(r => string.Equals(r.GetString(idCol.Name), id, StringComparison.Ordinal));
            pairs.Add((row < 0 ? 0 : row, title));
        }

        Groups.Load(pairs, Lines.Count);
        _rawSeparators.Clear();
    }

    /// <summary>
    /// Serialises separators back as a plain integer array (decision C5), which is
    /// what PalaceDB 1.5 writes and what both versions can read.
    /// </summary>
    internal void FlushSeparators()
    {
        var indexes = Groups.Indexes();

        // Never introduce the key on a sheet that does not use separators, so that
        // opening and saving an untouched file stays byte-identical.
        if (indexes.Count == 0 && !Raw.ContainsKey(KeySeparators))
            return;

        var arr = new CdbValue.Arr();
        foreach (var index in indexes)
            arr.Items.Add(new CdbValue.Number(index, null));
        Raw.Set(KeySeparators, arr);
    }

    /// <summary>
    /// Rewrites <c>props.separatorTitles</c> from the group list, so the two arrays of the
    /// file stay parallel. Only called when a heading is added, renamed or removed: an
    /// untouched file has to keep the array it was read with, byte for byte.
    /// </summary>
    internal void SyncSeparatorTitles()
    {
        if (Groups.Items.All(group => string.IsNullOrEmpty(group.Title)))
        {
            Props.SeparatorTitles = null;
            return;
        }

        // An untitled divider is stored as an explicit null, which is how PalaceDB 1.5
        // distinguishes "no title" from an empty one.
        var array = new CdbValue.Arr();
        foreach (var group in Groups.Items)
            array.Items.Add(group.Title is null ? CdbValue.NullValue : new CdbValue.Str(group.Title));

        Props.SeparatorTitles = array;
    }

    internal Database Owner => _db;

    /// <summary>Flushes typed model state back into the raw JSON tree.</summary>
    internal void PrepareForSave()
    {
        foreach (var column in Columns)
            column.FlushType();
        FlushLines();
        FlushSeparators();
        FlushProps();

        // index / group are derived from the row order and the titled separators, so
        // they are refreshed on every write instead of being edited by hand.
        if (Props.HasIndex)
            DerivedFields.ApplyIndex(this);
        if (Props.HasGroup)
            DerivedFields.ApplyGroup(this);
    }

    /// <summary>
    /// Rewrites the raw <c>lines</c> array from <see cref="Lines"/>. Row inserts, deletes
    /// and moves only touch the typed list, so without this step the file would keep the
    /// rows it was loaded with.
    /// </summary>
    private void FlushLines()
    {
        var array = Raw.GetArray(KeyLines);
        if (array is null)
        {
            if (Lines.Count == 0)
                return;
            array = new CdbValue.Arr();
            Raw.Set(KeyLines, array);
        }

        array.Items.Clear();
        foreach (var row in Lines)
            array.Items.Add(new CdbValue.Obj(row));
    }

    public override string ToString() => Name;
}

/// <summary>Typed view over the <c>props</c> object of a sheet.</summary>
public sealed class SheetProps
{
    private readonly SheetData _owner;

    public CdbObject Raw { get; }

    internal SheetProps(SheetData owner, CdbObject raw)
    {
        _owner = owner;
        Raw = raw;
    }

    public string? DisplayColumn
    {
        get => Raw.GetString("displayColumn");
        set => SetOrRemove("displayColumn", value is null ? null : new CdbValue.Str(value));
    }

    public string? DisplayIcon
    {
        get => Raw.GetString("displayIcon");
        set => SetOrRemove("displayIcon", value is null ? null : new CdbValue.Str(value));
    }

    public bool Hide
    {
        get => Raw.GetBool("hide", false);
        set => SetOrRemove("hide", value ? new CdbValue.Bool(true) : null);
    }

    public bool IsProps
    {
        get => Raw.GetBool("isProps", false);
        set => SetOrRemove("isProps", value ? new CdbValue.Bool(true) : null);
    }

    public bool HasIndex
    {
        get => Raw.GetBool("hasIndex", false);
        set => SetOrRemove("hasIndex", value ? new CdbValue.Bool(true) : null);
    }

    public bool HasGroup
    {
        get => Raw.GetBool("hasGroup", false);
        set => SetOrRemove("hasGroup", value ? new CdbValue.Bool(true) : null);
    }

    public string? DataFiles
    {
        get => Raw.GetString("dataFiles");
        set => SetOrRemove("dataFiles", value is null ? null : new CdbValue.Str(value));
    }

    /// <summary>
    /// Optional separator titles, parallel to <see cref="SheetData.Separators"/>. Writing the
    /// array also hands the titles to the headings, so a caller that edits the on-disk shape
    /// does not leave the model behind.
    /// </summary>
    public CdbValue.Arr? SeparatorTitles
    {
        get => Raw.GetArray("separatorTitles");
        set
        {
            SetOrRemove("separatorTitles", value);

            var titles = new List<string?>(value?.Items.Count ?? 0);
            if (value is not null)
                foreach (var item in value.Items)
                    titles.Add((item as CdbValue.Str)?.Value);

            _owner.Groups.ApplyTitles(titles);
        }
    }

    public string? GetSeparatorTitle(int separatorIndex)
    {
        var titles = SeparatorTitles;
        if (titles is null || separatorIndex < 0 || separatorIndex >= titles.Items.Count)
            return null;
        return (titles.Items[separatorIndex] as CdbValue.Str)?.Value;
    }

    private void SetOrRemove(string key, CdbValue? value)
    {
        if (value is null)
            Raw.Remove(key);
        else
            Raw.Set(key, value);
    }
}

/// <summary>A custom type declared with the "Edit Types" DSL.</summary>
public sealed class CustomType
{
    public CdbObject Raw { get; }

    public CustomType(CdbObject raw) => Raw = raw;

    public string Name
    {
        get => Raw.GetString("name", string.Empty);
        set => Raw.Set("name", new CdbValue.Str(value));
    }

    public List<CustomTypeCase> Cases { get; } = new();

    internal void Load()
    {
        Cases.Clear();
        if (Raw.GetArray("cases") is { } arr)
            foreach (var item in arr.Items)
                if (item is CdbValue.Obj o)
                    Cases.Add(new CustomTypeCase(o.Value));
    }
}

public sealed class CustomTypeCase
{
    public CdbObject Raw { get; }

    public CustomTypeCase(CdbObject raw) => Raw = raw;

    public string Name => Raw.GetString("name", string.Empty);
}
