using System.Text.Json;
using System.Text.Json.Serialization;
using PalaceDB.Core.Storage;

namespace PalaceDB.Core.Settings;

/// <summary>One entry of the recent files list, already formatted for the menu.</summary>
public sealed record RecentFile(string Path, string Name, string Folder, bool Exists);

/// <summary>
/// Application-level preferences, as opposed to the per-file metadata side-car.
/// <para>
/// The list of recently opened databases belongs to the installation, not to any single
/// file, so it lives next to the log in the local application data folder. A corrupted
/// settings file must never prevent the application from starting: it is replaced by
/// defaults and the previous contents are ignored.
/// </para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>How many databases the File menu remembers.</summary>
    public const int MaxRecentFiles = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("recentFiles")]
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Set when the file on disk could not be parsed and defaults were used.</summary>
    [JsonIgnore]
    public string? LoadWarning { get; private set; }

    /// <summary>Default location of the settings file inside a data folder.</summary>
    public static string PathFor(string dataFolder) => System.IO.Path.Combine(dataFolder, "settings.json");

    /// <summary>
    /// Reads the settings. A missing file is normal (first run); an unreadable one is
    /// reported through <see cref="LoadWarning"/> and replaced by defaults.
    /// </summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
            return new AppSettings();

        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(AtomicFile.ReadText(path), Options);
            if (parsed is null)
                return new AppSettings { LoadWarning = "Settings file was empty and has been reset." };

            parsed.Normalize();
            return parsed;
        }
        catch (Exception ex)
        {
            return new AppSettings
            {
                LoadWarning = $"Settings file was unreadable and has been reset ({ex.Message}).",
            };
        }
    }

    /// <summary>Writes the settings atomically. Never throws.</summary>
    public bool Save(string path)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            AtomicFile.WriteText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch
        {
            // Preferences are not worth interrupting the user for.
            return false;
        }
    }

    /// <summary>
    /// Moves a path to the front of the list. Returns true when the stored order changed,
    /// so an unchanged list does not rewrite the file.
    /// </summary>
    public bool AddRecent(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return false;

        var existing = RecentFiles.FindIndex(p => Same(p, normalized));
        if (existing == 0 && RecentFiles.Count <= MaxRecentFiles)
            return false;

        if (existing >= 0)
            RecentFiles.RemoveAt(existing);

        RecentFiles.Insert(0, normalized);
        Trim();
        return true;
    }

    /// <summary>Drops one entry, used when a remembered file has disappeared.</summary>
    public bool RemoveRecent(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return false;
        var removed = RecentFiles.RemoveAll(p => Same(p, normalized));
        return removed > 0;
    }

    /// <summary>Removes entries whose file no longer exists. Returns how many went.</summary>
    public int PruneMissing()
    {
        var before = RecentFiles.Count;
        RecentFiles.RemoveAll(p =>
        {
            try
            {
                return !File.Exists(p);
            }
            catch
            {
                return true;
            }
        });
        return before - RecentFiles.Count;
    }

    public void ClearRecent()
    {
        RecentFiles.Clear();
    }

    /// <summary>
    /// The file a fresh start should reopen: the newest remembered database that is still
    /// there. Entries whose file has been deleted are skipped rather than reported, so a
    /// missing file never turns into a failed start-up.
    /// </summary>
    public string? MostRecentExisting()
    {
        foreach (var path in RecentFiles)
        {
            try
            {
                if (File.Exists(path))
                    return path;
            }
            catch
            {
                // An unusable path is simply skipped.
            }
        }

        return null;
    }

    /// <summary>Entries formatted for the File menu, newest first.</summary>
    public List<RecentFile> Describe()
    {
        var result = new List<RecentFile>(RecentFiles.Count);
        foreach (var path in RecentFiles)
        {
            var exists = false;
            try
            {
                exists = File.Exists(path);
            }
            catch
            {
                // An unusable path is simply shown as missing.
            }

            result.Add(new RecentFile(
                path,
                System.IO.Path.GetFileName(path),
                FolderOf(path),
                exists));
        }

        return result;
    }

    /// <summary>Folder shown next to the file name, shortened when it is very long.</summary>
    private static string FolderOf(string path)
    {
        string folder;
        try
        {
            folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }

        const int limit = 44;
        return folder.Length <= limit ? folder : "\u2026" + folder[^limit..];
    }

    private void Normalize()
    {
        RecentFiles ??= new List<string>();

        // Drop duplicates and unusable entries that a hand-edited file may contain.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>(RecentFiles.Count);
        foreach (var path in RecentFiles)
        {
            var normalized = Normalize(path);
            if (normalized is not null && seen.Add(normalized))
                cleaned.Add(normalized);
        }

        RecentFiles = cleaned;
        Trim();
    }

    private void Trim()
    {
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
