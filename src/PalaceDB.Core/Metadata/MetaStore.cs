using PalaceDB.Core.Model;
using PalaceDB.Core.Storage;

namespace PalaceDB.Core.Metadata;

/// <summary>
/// Owns the metadata side-car of one database file: loading with corruption
/// tolerance, dirty tracking and atomic persistence.
/// </summary>
public sealed class MetaStore
{
    public const string Suffix = ".meta.json";

    private string _path;

    public MetaDocument Document { get; }

    /// <summary>True when the on-disk metadata could not be used and was replaced.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>True when the file was written by a newer version and must not be overwritten.</summary>
    public bool IsReadOnly { get; private set; }

    public bool IsDirty { get; private set; }

    public string Path2 => _path;

    private MetaStore(string path, MetaDocument document)
    {
        _path = path;
        Document = document;
    }

    public static string PathFor(string cdbPath) => cdbPath + Suffix;

    /// <summary>
    /// Reads only the window geometry of a file, without loading the database or the rest
    /// of the side-car. Start-up uses it to place the window before it is shown, which is
    /// what keeps a remembered position from making the window jump after the first paint.
    /// Returns null when there is no side-car or it cannot be read.
    /// </summary>
    public static WindowState? ReadWindowState(string cdbPath)
    {
        try
        {
            var path = PathFor(cdbPath);
            if (!File.Exists(path))
                return null;
            return MetaDocument.Parse(AtomicFile.ReadText(path)).WindowState;
        }
        catch
        {
            // A damaged or half-written side-car must never stop the file from opening.
            return null;
        }
    }

    public static MetaStore Load(string cdbPath, Database database)
    {
        var path = PathFor(cdbPath);
        MetaDocument document;

        if (File.Exists(path))
        {
            try
            {
                document = MetaDocument.Parse(AtomicFile.ReadText(path));
                var store = new MetaStore(path, document);
                if (document.MetaVersion > MetaDocument.CurrentVersion)
                    store.IsReadOnly = true;
                document.SyncWith(database);

                // The tables are the authority for their own columns, so a definition left
                // behind by an older session (or by an edit made outside the application)
                // is brought back in line before anything is shown.
                store.RefreshTemplates(database);
                return store;
            }
            catch (Exception ex)
            {
                // Corrupted metadata must never block the document itself.
                var quarantine = path + ".bad";
                try
                {
                    if (File.Exists(quarantine))
                        File.Delete(quarantine);
                    File.Move(path, quarantine);
                }
                catch
                {
                    // ignore
                }

                document = new MetaDocument();
                var store = new MetaStore(path, document)
                {
                    LoadWarning = $"Metadata file was unreadable and has been reset ({ex.Message}).",
                };
                document.SyncWith(database);
                store.MarkDirty();
                return store;
            }
        }

        document = new MetaDocument();
        var fresh = new MetaStore(path, document);
        document.SyncWith(database);
        return fresh;
    }

    public void MarkDirty() => IsDirty = true;

    /// <summary>
    /// Re-aligns the side-car after a structural change (a column was added, renamed,
    /// moved or removed) so that widths and colours stay attached to their column.
    /// </summary>
    public void Sync(Database database)
    {
        Document.SyncWith(database);
        MarkDirty();
    }

    /// <summary>
    /// Re-targets the store at a different database file (used by "Save As").
    /// The old side-car is left untouched on disk.
    /// </summary>
    public void MoveTo(string cdbPath)
    {
        _path = PathFor(cdbPath);
        MarkDirty();
    }

    /// <summary>Writes the metadata file. No-op when clean or read-only.</summary>
    public bool Save(bool force = false)
    {
        if (IsReadOnly && !force)
            return false;
        if (!IsDirty && !force)
            return false;

        AtomicFile.WriteText(_path, Document.Serialize());
        IsDirty = false;
        return true;
    }

    // ---- window geometry -----------------------------------------------

    /// <summary>Geometry remembered for this file (F17).</summary>
    public WindowState WindowState => Document.WindowState;

    // ---- appearance -----------------------------------------------------

    /// <summary>Palette chosen for this file, or null when it was never set.</summary>
    public string? Scheme => Document.Appearance?.Scheme;

    /// <summary>
    /// Stores the palette of this file. Returns false when it did not change, so opening
    /// the settings window and closing it again leaves the side-car alone.
    /// </summary>
    public bool SetScheme(string? scheme)
    {
        var next = new AppearanceMeta { Scheme = string.IsNullOrWhiteSpace(scheme) ? null : scheme };
        if (next.SameAs(Document.Appearance))
            return false;
        Document.Appearance = next.Scheme is null ? null : next;
        MarkDirty();
        return true;
    }

    // ---- templates ------------------------------------------------------

    /// <summary>Template a table follows, or null when it stands alone.</summary>
    public string? GetTableTemplate(string sheetName) =>
        Document.Tables.TryGetValue(sheetName, out var table) ? table.Template : null;

    /// <summary>Links or unlinks a table. Returns false when nothing changed.</summary>
    public bool SetTableTemplate(string sheetName, string? templateId)
    {
        if (templateId is not null && !Document.Templates.ContainsKey(templateId))
            return false;

        var table = Document.GetOrCreateTable(sheetName);
        if (string.Equals(table.Template, templateId, StringComparison.Ordinal))
            return false;

        table.Template = templateId;
        MarkDirty();
        return true;
    }

    /// <summary>All templates of this file, in name order.</summary>
    public IEnumerable<KeyValuePair<string, TemplateMeta>> Templates =>
        Document.Templates.OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase);

    public TemplateMeta? GetTemplate(string? id) =>
        id is not null && Document.Templates.TryGetValue(id, out var meta) ? meta : null;

    public string? TemplateName(string? id) => GetTemplate(id)?.Name;

    /// <summary>Tables that follow a template, in document order.</summary>
    public IEnumerable<string> TemplateMembers(string templateId) =>
        Document.Tables
            .Where(kv => string.Equals(kv.Value.Template, templateId, StringComparison.Ordinal))
            .Select(kv => kv.Key);

    /// <summary>
    /// Adds a template definition and returns its id. The tables are linked separately so
    /// that the link can go through the undo history like any other edit.
    /// </summary>
    public string CreateTemplate(string name, IReadOnlyList<ColumnSpec> columns)
    {
        var id = NewTemplateId();
        Document.Templates[id] = TemplateMeta.FromSpecs(name, columns);
        MarkDirty();
        return id;
    }

    public bool RenameTemplate(string id, string name)
    {
        if (!Document.Templates.TryGetValue(id, out var meta))
            return false;
        if (string.Equals(meta.Name, name, StringComparison.Ordinal))
            return false;
        meta.Name = name;
        MarkDirty();
        return true;
    }

    /// <summary>Removes a template. The tables keep their columns and lose the link.</summary>
    public bool DeleteTemplate(string id)
    {
        if (!Document.Templates.Remove(id))
            return false;
        foreach (var table in Document.Tables.Values)
            if (string.Equals(table.Template, id, StringComparison.Ordinal))
                table.Template = null;
        MarkDirty();
        return true;
    }

    /// <summary>
    /// Rewrites the stored definition of a template from the columns a member table has
    /// right now. The definition is derived state: every edit of a member goes through
    /// this, and so does every undo, which keeps the snapshot equal to what the tables
    /// actually declare without storing schema changes in the undo history.
    /// </summary>
    public bool RefreshTemplate(string templateId, SheetData sheet)
    {
        if (!Document.Templates.TryGetValue(templateId, out var meta))
            return false;

        var columns = TemplateSpec.Snapshot(sheet);
        if (meta.SameAs(columns))
            return false;

        meta.Columns = TemplateMeta.FromSpecs(meta.Name, columns).Columns;
        MarkDirty();
        return true;
    }

    /// <summary>
    /// Refreshes every template of the document from its members. Called after the undo
    /// history changed, so a template always describes the tables it covers.
    /// <para>
    /// Only templates whose members all agree are refreshed. When they do not, the
    /// stored definition is the reference and the differing tables are reported as out of
    /// sync instead of silently rewriting the template from whichever table happens to
    /// come first.
    /// </para>
    /// </summary>
    public bool RefreshTemplates(Database database)
    {
        if (Document.Templates.Count == 0)
            return false;

        var changed = false;
        foreach (var id in Document.Templates.Keys.ToList())
        {
            var sheets = TemplateMembers(id)
                .Select(database.GetSheet)
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            if (sheets.Count == 0)
                continue;

            var reference = TemplateSpec.Snapshot(sheets[0]);
            if (sheets.Any(s => !TemplateSpec.SameColumns(TemplateSpec.Snapshot(s), reference)))
                continue;

            changed |= RefreshTemplate(id, sheets[0]);
        }
        return changed;
    }

    private string NewTemplateId()
    {
        while (true)
        {
            var id = "tpl_" + Guid.NewGuid().ToString("N")[..8];
            if (!Document.Templates.ContainsKey(id))
                return id;
        }
    }

    /// <summary>
    /// Stores the window geometry and reports whether anything changed, so a window that
    /// was never moved does not rewrite the side-car on every save.
    /// </summary>
    public bool SetWindowState(WindowState state)
    {
        if (Document.WindowState.SameAs(state))
            return false;
        Document.WindowState = state.Clone();
        MarkDirty();
        return true;
    }

    // ---- column widths -------------------------------------------------

    public double? GetColumnWidth(string sheetName, string columnName)
    {
        if (!Document.Tables.TryGetValue(sheetName, out var table))
            return null;
        var id = Document.FindColumnId(sheetName, columnName);
        return id is not null && table.Columns.TryGetValue(id, out var meta) ? meta.Width : null;
    }

    public void SetColumnWidth(string sheetName, string columnName, double width)
    {
        var table = Document.GetOrCreateTable(sheetName);
        var id = Document.FindColumnId(sheetName, columnName);
        if (id is null)
            return;
        table.Columns[id].Width = Math.Round(width, 1);
        MarkDirty();
    }

    // ---- hidden columns ------------------------------------------------

    public bool IsColumnHidden(string sheetName, string columnName)
    {
        var id = Document.FindColumnId(sheetName, columnName);
        return id is not null
            && Document.Tables.TryGetValue(sheetName, out var table)
            && table.Columns.TryGetValue(id, out var meta)
            && meta.Hidden == true;
    }

    public void SetColumnHidden(string sheetName, string columnName, bool hidden)
    {
        var id = Document.FindColumnId(sheetName, columnName);
        if (id is null || !Document.Tables.TryGetValue(sheetName, out var table))
            return;
        table.Columns[id].Hidden = hidden ? true : null;
        MarkDirty();
    }

    // ---- collapsed row groups ------------------------------------------

    /// <summary>Titles of the groups that are collapsed in one table.</summary>
    public IReadOnlyList<string> GetCollapsedGroups(string sheetName) =>
        Document.Tables.TryGetValue(sheetName, out var table)
            ? table.CollapsedGroups
            : Array.Empty<string>();

    public void SetGroupCollapsed(string sheetName, string title, bool collapsed)
    {
        if (string.IsNullOrEmpty(title))
            return;

        var table = Document.GetOrCreateTable(sheetName);
        var present = table.CollapsedGroups.Contains(title, StringComparer.Ordinal);
        if (collapsed == present)
            return;

        if (collapsed)
            table.CollapsedGroups.Add(title);
        else
            table.CollapsedGroups.RemoveAll(t => string.Equals(t, title, StringComparison.Ordinal));
        MarkDirty();
    }

    // ---- styles --------------------------------------------------------

    /// <summary>Table entry for a sheet, or null when the sheet is unknown.</summary>
    public TableMeta? GetTable(string sheetName) =>
        Document.Tables.TryGetValue(sheetName, out var table) ? table : null;

    public string? GetRowBackground(string sheetName, string rowKey) =>
        GetTable(sheetName) is { } table && table.RowStyles.TryGetValue(rowKey, out var style)
            ? style.Background
            : null;

    public string? GetColumnBackground(string sheetName, string columnName)
    {
        var table = GetTable(sheetName);
        if (table is null)
            return null;
        var id = Document.FindColumnId(sheetName, columnName);
        return id is not null && table.ColumnStyles.TryGetValue(id, out var style) ? style.Background : null;
    }

    public string? GetCellBackground(string sheetName, string rowKey, string columnName)
    {
        var table = GetTable(sheetName);
        if (table is null)
            return null;
        var id = Document.FindColumnId(sheetName, columnName);
        if (id is null || !table.CellStyles.TryGetValue(rowKey, out var cells))
            return null;
        return cells.TryGetValue(id, out var style) ? style.Background : null;
    }

    public void SetRowBackground(string sheetName, string rowKey, string? color)
    {
        var table = Document.GetOrCreateTable(sheetName);
        if (string.IsNullOrEmpty(color))
            table.RowStyles.Remove(rowKey);
        else
            table.RowStyles[rowKey] = new StyleEntry { Background = color };
        MarkDirty();
    }

    public void SetColumnBackground(string sheetName, string columnName, string? color)
    {
        var table = Document.GetOrCreateTable(sheetName);
        var id = Document.FindColumnId(sheetName, columnName);
        if (id is null)
            return;
        if (string.IsNullOrEmpty(color))
            table.ColumnStyles.Remove(id);
        else
            table.ColumnStyles[id] = new StyleEntry { Background = color };
        MarkDirty();
    }

    public void SetCellBackground(string sheetName, string rowKey, string columnName, string? color)
    {
        var table = Document.GetOrCreateTable(sheetName);
        var id = Document.FindColumnId(sheetName, columnName);
        if (id is null)
            return;

        if (!table.CellStyles.TryGetValue(rowKey, out var cells))
        {
            if (string.IsNullOrEmpty(color))
                return;
            cells = new Dictionary<string, StyleEntry>(StringComparer.Ordinal);
            table.CellStyles[rowKey] = cells;
        }

        if (string.IsNullOrEmpty(color))
            cells.Remove(id);
        else
            cells[id] = new StyleEntry { Background = color };

        if (cells.Count == 0)
            table.CellStyles.Remove(rowKey);
        MarkDirty();
    }

    /// <summary>Shifts row-keyed styles when a row is inserted or removed.</summary>
    public void ShiftRowKeys(string sheetName, int fromIndex, int delta, SheetData sheet)
    {
        var table = Document.GetOrCreateTable(sheetName);
        var idColumn = sheet.IdColumn;
        if (idColumn is not null)
            return; // id-keyed styles do not move with the row order

        table.RowStyles = Shift(table.RowStyles, fromIndex, delta);
        table.CellStyles = ShiftNested(table.CellStyles, fromIndex, delta);
        MarkDirty();
    }

    private static Dictionary<string, StyleEntry> Shift(Dictionary<string, StyleEntry> source, int from, int delta)
    {
        var result = new Dictionary<string, StyleEntry>(StringComparer.Ordinal);
        foreach (var kv in source)
        {
            if (!int.TryParse(kv.Key, out var index))
            {
                result[kv.Key] = kv.Value;
                continue;
            }
            if (index >= from)
                index += delta;
            if (index >= 0)
                result[index.ToString()] = kv.Value;
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, StyleEntry>> ShiftNested(
        Dictionary<string, Dictionary<string, StyleEntry>> source, int from, int delta)
    {
        var result = new Dictionary<string, Dictionary<string, StyleEntry>>(StringComparer.Ordinal);
        foreach (var kv in source)
        {
            if (!int.TryParse(kv.Key, out var index))
            {
                result[kv.Key] = kv.Value;
                continue;
            }
            if (index >= from)
                index += delta;
            if (index >= 0)
                result[index.ToString()] = kv.Value;
        }
        return result;
    }
}
