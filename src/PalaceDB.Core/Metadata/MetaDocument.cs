using System.Text.Json;
using System.Text.Json.Serialization;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Metadata;

/// <summary>Visual style of a row, a column or a single cell.</summary>
public sealed class StyleEntry
{
    [JsonPropertyName("bg")]
    public string? Background { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Background);
}

public sealed class ColumnMeta
{
    /// <summary>Last known column name; used to re-attach metadata after external renames.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public double? Width { get; set; }
}

public sealed class TableMeta
{
    [JsonPropertyName("tableId")]
    public string TableId { get; set; } = string.Empty;

    /// <summary>Id of the template this table follows, or null when it stands alone.</summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("columns")]
    public Dictionary<string, ColumnMeta> Columns { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("rowStyles")]
    public Dictionary<string, StyleEntry> RowStyles { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("columnStyles")]
    public Dictionary<string, StyleEntry> ColumnStyles { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("cellStyles")]
    public Dictionary<string, Dictionary<string, StyleEntry>> CellStyles { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WindowState
{
    [JsonPropertyName("width")] public double Width { get; set; } = WindowPlacement.DefaultWidth;
    [JsonPropertyName("height")] public double Height { get; set; } = WindowPlacement.DefaultHeight;
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }

    /// <summary>
    /// True when a position was ever stored. A freshly created side-car has a size but no
    /// position, which must not move the window away from the default placement.
    /// </summary>
    [JsonIgnore]
    public bool HasPosition => X is not null && Y is not null;

    public WindowState Clone() => new()
    {
        Width = Width,
        Height = Height,
        X = X,
        Y = Y,
        Maximized = Maximized,
    };

    /// <summary>Value equality, so an unchanged window does not dirty the side-car.</summary>
    public bool SameAs(WindowState other) =>
        Width.Equals(other.Width) &&
        Height.Equals(other.Height) &&
        X.Equals(other.X) &&
        Y.Equals(other.Y) &&
        Maximized == other.Maximized;
}

/// <summary>
/// How this particular file is drawn. It is per file on purpose: two databases open in
/// the same window can then be told apart at a glance, and the choice travels with the
/// file (it is presentation state, so it belongs in the side-car, not in the .cdb).
/// </summary>
public sealed class AppearanceMeta
{
    /// <summary>Palette id, matching the <c>data-scheme</c> values in themes.css.</summary>
    [JsonPropertyName("scheme")]
    public string? Scheme { get; set; }

    public bool SameAs(AppearanceMeta? other) =>
        string.Equals(Scheme ?? string.Empty, other?.Scheme ?? string.Empty, StringComparison.Ordinal);
}

/// <summary>
/// Side-car metadata document (<c>X.cdb.meta.json</c>).
/// Holds everything that is presentation state and must never leak into the .cdb file.
/// </summary>
public sealed class MetaDocument
{
    /// <summary>
    /// Bumped to 2 when table templates were added. A side-car of a newer version is
    /// treated as read-only by an older build, which is what keeps it from silently
    /// dropping the <c>templates</c> block it does not understand.
    /// </summary>
    public const int CurrentVersion = 2;

    [JsonPropertyName("metaVersion")]
    public int MetaVersion { get; set; } = CurrentVersion;

    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("createdWith")]
    public string CreatedWith { get; set; } = "PalaceDB.CSharp/1.0";

    [JsonPropertyName("tables")]
    public Dictionary<string, TableMeta> Tables { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Table templates of this file, keyed by an id that survives renames.</summary>
    [JsonPropertyName("templates")]
    public Dictionary<string, TemplateMeta> Templates { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("windowState")]
    public WindowState WindowState { get; set; } = new();

    /// <summary>Null until a palette was chosen for this file; the default is used then.</summary>
    [JsonPropertyName("appearance")]
    public AppearanceMeta? Appearance { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static MetaDocument Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<MetaDocument>(json, Options);
        if (doc is null)
            throw new InvalidDataException("Metadata document is empty.");
        return doc;
    }

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public TableMeta GetOrCreateTable(string sheetName)
    {
        if (Tables.TryGetValue(sheetName, out var table))
            return table;
        table = new TableMeta { TableId = Guid.NewGuid().ToString("N") };
        Tables[sheetName] = table;
        return table;
    }

    /// <summary>
    /// Re-attaches metadata to the current document: creates ids for new sheets and
    /// columns, follows renames by name, and falls back to positional matching when a
    /// column was renamed outside the application.
    /// </summary>
    public void SyncWith(Database database)
    {
        foreach (var sheet in database.Sheets)
        {
            var table = GetOrCreateTable(sheet.Name);

            // Pass 1 - match by name. This is the normal path and also covers
            // renames performed through the application (which update the name here).
            var matched = new HashSet<string>(StringComparer.Ordinal);
            var unmatchedColumns = new List<Column>();

            foreach (var column in sheet.Columns)
            {
                var hit = table.Columns.FirstOrDefault(kv =>
                    !matched.Contains(kv.Key) &&
                    string.Equals(kv.Value.Name, column.Name, StringComparison.Ordinal));

                if (hit.Key is not null)
                    matched.Add(hit.Key);
                else
                    unmatchedColumns.Add(column);
            }

            var orphanIds = table.Columns.Keys.Where(id => !matched.Contains(id)).ToList();

            // Pass 2 - positional fallback. Columns renamed outside the application keep
            // no name match, so they are paired with the leftover entries in document order.
            // Only applied when the counts agree, which means "pure rename, no add/remove".
            // Otherwise a single orphan/single column pair is still accepted (one rename).
            var canPairPositionally =
                unmatchedColumns.Count > 0 &&
                orphanIds.Count > 0 &&
                (unmatchedColumns.Count == orphanIds.Count || (unmatchedColumns.Count == 1 && orphanIds.Count == 1));

            if (canPairPositionally)
            {
                for (var i = 0; i < unmatchedColumns.Count; i++)
                {
                    table.Columns[orphanIds[i]].Name = unmatchedColumns[i].Name;
                    matched.Add(orphanIds[i]);
                }
            }

            // Pass 3 - create ids for genuinely new columns.
            foreach (var column in unmatchedColumns)
            {
                if (table.Columns.Values.Any(v => string.Equals(v.Name, column.Name, StringComparison.Ordinal)))
                    continue;
                var id = Guid.NewGuid().ToString("N");
                table.Columns[id] = new ColumnMeta { Name = column.Name };
                matched.Add(id);
            }

            // Pass 4 - drop entries whose column is gone, so widths and styles do not
            // silently migrate to a column added later.
            foreach (var id in table.Columns.Keys.Where(id => !matched.Contains(id)).ToList())
            {
                if (sheet.GetColumn(table.Columns[id].Name) is null)
                {
                    table.Columns.Remove(id);
                    table.ColumnStyles.Remove(id);
                    foreach (var cells in table.CellStyles.Values)
                        cells.Remove(id);
                }
            }
        }

        // A link to a template that no longer exists would leave the table following
        // nothing at all, so it is dropped instead of dangling.
        foreach (var table in Tables.Values)
            if (table.Template is { } templateId && !Templates.ContainsKey(templateId))
                table.Template = null;
    }

    /// <summary>Stable metadata id of a column, or null when unknown.</summary>
    public string? FindColumnId(string sheetName, string columnName)
    {
        if (!Tables.TryGetValue(sheetName, out var table))
            return null;
        foreach (var kv in table.Columns)
            if (string.Equals(kv.Value.Name, columnName, StringComparison.Ordinal))
                return kv.Key;
        return null;
    }

    public void RenameColumn(string sheetName, string oldName, string newName)
    {
        if (!Tables.TryGetValue(sheetName, out var table))
            return;
        foreach (var kv in table.Columns)
            if (string.Equals(kv.Value.Name, oldName, StringComparison.Ordinal))
                kv.Value.Name = newName;
    }

    public void RemoveColumn(string sheetName, string columnName)
    {
        if (!Tables.TryGetValue(sheetName, out var table))
            return;
        var id = FindColumnId(sheetName, columnName);
        if (id is null)
            return;
        table.Columns.Remove(id);
        table.ColumnStyles.Remove(id);
        foreach (var cells in table.CellStyles.Values)
            cells.Remove(id);
    }

    public void RenameTable(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return;
        if (!Tables.TryGetValue(oldName, out var table))
            return;
        Tables.Remove(oldName);
        Tables[newName] = table;
    }
}
