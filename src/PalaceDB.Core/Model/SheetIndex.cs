namespace PalaceDB.Core.Model;

/// <summary>
/// Lookup structure built on top of a sheet: rows by unique id, duplicate detection
/// and display names. Rebuilt on demand after structural changes.
/// </summary>
public sealed class SheetIndex
{
    private readonly Dictionary<string, CdbObject> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _duplicates = new(StringComparer.Ordinal);

    public SheetData Sheet { get; }

    /// <summary>Rows that have a non-empty unique id, sorted by display name.</summary>
    public List<CdbObject> Ordered { get; } = new();

    public SheetIndex(SheetData sheet)
    {
        Sheet = sheet;
        Rebuild();
    }

    public IReadOnlyDictionary<string, CdbObject> ById => _byId;

    public IReadOnlyCollection<string> Duplicates => _duplicates;

    public bool IsDuplicate(string id) => _duplicates.Contains(id);

    public CdbObject? Get(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        return _byId.TryGetValue(id, out var row) ? row : null;
    }

    public void Rebuild()
    {
        _byId.Clear();
        _duplicates.Clear();
        Ordered.Clear();

        var idColumn = Sheet.IdColumn;
        if (idColumn is null)
            return;

        foreach (var row in Sheet.Lines)
        {
            var id = row.GetString(idColumn.Name);
            if (string.IsNullOrEmpty(id))
                continue;

            if (!_byId.TryAdd(id, row))
                _duplicates.Add(id);
            Ordered.Add(row);
        }

        Ordered.Sort((a, b) => string.CompareOrdinal(GetDisplay(a), GetDisplay(b)));
    }

    /// <summary>Text shown when this row is referenced from another sheet.</summary>
    public string GetDisplay(CdbObject row)
    {
        var idColumn = Sheet.IdColumn;
        var id = idColumn is null ? null : row.GetString(idColumn.Name);
        var displayColumn = Sheet.Props.DisplayColumn;
        if (displayColumn is not null)
        {
            var display = row.GetString(displayColumn);
            if (!string.IsNullOrEmpty(display))
                return display;
        }
        return string.IsNullOrEmpty(id) ? string.Empty : id;
    }

    /// <summary>Icon path for a row, when the sheet declares a display icon column.</summary>
    public string? GetIcon(CdbObject row)
    {
        var iconColumn = Sheet.Props.DisplayIcon;
        return iconColumn is null ? null : row.GetString(iconColumn);
    }

    /// <summary>Index of a row inside <see cref="SheetData.Lines"/>, or -1.</summary>
    public int IndexOfRow(CdbObject row) => Sheet.Lines.IndexOf(row);
}

/// <summary>Indexes for every sheet of a document, rebuilt lazily when invalidated.</summary>
public sealed class DatabaseIndex
{
    private readonly Dictionary<string, SheetIndex> _indexes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    public Database Database { get; }

    public DatabaseIndex(Database database)
    {
        Database = database;
        Rebuild();
    }

    public SheetIndex? For(string sheetName)
    {
        if (_dirty.Remove(sheetName))
        {
            var sheet = Database.GetSheet(sheetName);
            if (sheet is not null)
                _indexes[sheetName] = new SheetIndex(sheet);
        }
        return _indexes.TryGetValue(sheetName, out var index) ? index : null;
    }

    public SheetIndex? For(SheetData sheet) => For(sheet.Name);

    public void Rebuild()
    {
        _indexes.Clear();
        _dirty.Clear();
        foreach (var sheet in Database.Sheets)
            _indexes[sheet.Name] = new SheetIndex(sheet);
    }

    /// <summary>Marks a sheet as stale; it will be rebuilt on next access.</summary>
    public void Invalidate(string sheetName) => _dirty.Add(sheetName);

    public void InvalidateAll()
    {
        foreach (var sheet in Database.Sheets)
            _dirty.Add(sheet.Name);
    }
}
