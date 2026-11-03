using PalaceDB.Core.Commands;
using PalaceDB.Core.Json;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Storage;

namespace PalaceDB.Core.Document;

/// <summary>Raised when a document cannot be opened.</summary>
public sealed class DocumentOpenException : Exception
{
    public DocumentOpenException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// One open database file: the data model, its metadata side-car, the undo history
/// and the dirty state. Each window owns exactly one document; several windows may
/// hold the same path (read-only sharing is handled by the window manager).
/// </summary>
public sealed class DatabaseDocument : IDisposable
{
    public const string BackupSuffix = ".autobackup";

    private readonly object _gate = new();
    private IEditorCommand? _savedCommand;

    /// <summary>
    /// Set when the loaded text came from a merge and has not been written yet (F16).
    /// The data is in memory only, so the document must look unsaved even though the
    /// undo history is still empty.
    /// </summary>
    private bool _pendingMerge;

    /// <summary>
    /// Set by <see cref="CreateUntitled"/>: the document holds a scratch path, so it must
    /// look unsaved and refuse a plain save until the user chose a real file.
    /// </summary>
    private bool _untitled;

    public const string UntitledName = "Untitled";

    /// <summary>True when the database was created in this session and has no file yet.</summary>
    public bool IsUntitled => _untitled;

    public string Path { get; private set; }

    public Database Database { get; }

    public MetaStore Meta { get; }

    public CommandStack Commands { get; }

    public DatabaseIndex Index { get; }

    /// <summary>True when the file on disk (or its metadata) must not be overwritten.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>Why the document is read-only, or null when it is editable.</summary>
    public string? ReadOnlyReason { get; private set; }

    /// <summary>Set when this document owns the process-wide write slot for its path.</summary>
    private string? _heldPath;

    /// <summary>Write time of the file as this window last read or wrote it.</summary>
    private DateTime _stamp;

    /// <summary>Human readable warning produced while loading the metadata file.</summary>
    public string? LoadWarning => Meta.LoadWarning;

    public event EventHandler? DirtyChanged;

    private DatabaseDocument(string path, Database database, MetaStore meta)
    {
        Path = path;
        Database = database;
        Meta = meta;
        Commands = new CommandStack();
        Index = new DatabaseIndex(database);
        Commands.Changed += OnCommandsChanged;
    }

    public static DatabaseDocument Open(string path)
    {
        if (!File.Exists(path))
            throw new DocumentOpenException($"The file '{path}' does not exist.");

        string text;
        try
        {
            text = AtomicFile.ReadText(path);
        }
        catch (Exception ex)
        {
            throw new DocumentOpenException($"The file '{path}' cannot be read: {ex.Message}", ex);
        }

        Database database;
        try
        {
            database = CdbReader.ParseDatabase(text);
        }
        catch (Exception ex)
        {
            throw new DocumentOpenException($"'{System.IO.Path.GetFileName(path)}' is not a valid PalaceDB file: {ex.Message}", ex);
        }

        var meta = MetaStore.Load(path, database);
        var document = new DatabaseDocument(path, database, meta);

        // A file may be visible in several windows, but only one of them may write.
        if (meta.IsReadOnly)
        {
            document.IsReadOnly = true;
            document.ReadOnlyReason = "the metadata file asks for read-only mode";
        }
        else if (IsFileReadOnly(path))
        {
            document.IsReadOnly = true;
            document.ReadOnlyReason = "the file is read-only on disk";
        }
        else if (!OpenFileRegistry.TryAcquire(path))
        {
            document.IsReadOnly = true;
            document.ReadOnlyReason = "it is already open in another window";
        }
        else
        {
            document._heldPath = path;
        }

        document._savedCommand = null;
        document.TouchStamp();
        return document;
    }

    /// <summary>
    /// Opens a database from text produced by a merge (F16). The result is flagged as
    /// unsaved so the user reviews it before the conflict files are removed; saving it
    /// through <see cref="Save"/> writes the merged content and clears the flag.
    /// </summary>
    public static DatabaseDocument OpenMerged(string path, string mergedText)
    {
        Database database;
        try
        {
            database = CdbReader.ParseDatabase(mergedText);
        }
        catch (Exception ex)
        {
            throw new DocumentOpenException(
                $"The merged content for '{System.IO.Path.GetFileName(path)}' is not a valid PalaceDB file: {ex.Message}", ex);
        }

        var meta = MetaStore.Load(path, database);
        var document = new DatabaseDocument(path, database, meta);

        if (meta.IsReadOnly)
        {
            document.IsReadOnly = true;
            document.ReadOnlyReason = "the metadata file asks for read-only mode";
        }
        else if (IsFileReadOnly(path))
        {
            document.IsReadOnly = true;
            document.ReadOnlyReason = "the file is read-only on disk";
        }
        else if (!OpenFileRegistry.TryAcquire(path))
        {
            document.IsReadOnly = true;
            document.ReadOnlyReason = "it is already open in another window";
        }
        else
        {
            document._heldPath = path;
        }

        document._savedCommand = null;
        document._pendingMerge = true;
        document.TouchStamp();
        document._lastDirty = document.IsDirty;
        return document;
    }

    public static DatabaseDocument CreateNew(string path)
    {
        var database = new Database();
        var meta = MetaStore.Load(path, database);
        var document = new DatabaseDocument(path, database, meta);

        if (OpenFileRegistry.TryAcquire(path))
        {
            document._heldPath = path;
        }
        else
        {
            document.IsReadOnly = true;
            document.ReadOnlyReason = "it is already open in another window";
        }

        return document;
    }

    /// <summary>
    /// Creates a database that has no file yet (File ▸ New). The path is a scratch name
    /// inside the application data folder, used only so the metadata has somewhere to
    /// live; the first save must go through <see cref="SaveAs"/> to pick the real file.
    /// </summary>
    public static DatabaseDocument CreateUntitled(string scratchPath)
    {
        var document = CreateNew(scratchPath);
        document._untitled = true;
        document._lastDirty = true;
        return document;
    }

    private static bool IsFileReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReadOnly);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the data differs from the last saved state. Presentation state
    /// (column widths, background colours) is stored in the side-car file and is
    /// written immediately, so it never makes the database itself look unsaved.
    /// </summary>
    public bool IsDirty
    {
        get
        {
            if (_untitled || _pendingMerge)
                return true;

            var top = Commands.UndoHistory.Count > 0 ? Commands.UndoHistory[^1] : null;
            return !ReferenceEquals(top, _savedCommand);
        }
    }

    private bool _lastDirty;

    /// <summary>
    /// True while the loaded content came from a merge and has not been written back
    /// (F16). The host uses it to remove the conflict leftovers after a successful save.
    /// </summary>
    public bool HasPendingMerge => _pendingMerge;

    private void OnCommandsChanged(object? sender, EventArgs e)
    {
        // Structural edits can add, remove or rename rows and columns.
        Index.InvalidateAll();

        // A template definition is derived state: it always describes the columns its
        // tables declare right now. Refreshing here means an undo puts the snapshot back
        // as well, without the schema change having to live in the undo history.
        Meta.RefreshTemplates(Database);

        RaiseDirtyIfChanged();
    }

    private void RaiseDirtyIfChanged()
    {
        var dirty = IsDirty;
        if (dirty == _lastDirty)
            return;
        _lastDirty = dirty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the editor after a metadata change so the dirty flag follows.</summary>
    public void NotifyMetaChanged()
    {
        Meta.MarkDirty();
        RaiseDirtyIfChanged();
    }

    /// <summary>
    /// Persists presentation state (widths, colours) immediately. The side-car file is
    /// independent from the database, so this never touches the .cdb file.
    /// </summary>
    public void SaveMeta()
    {
        lock (_gate)
        {
            if (IsReadOnly)
                return;
            Meta.Save();
            RaiseDirtyIfChanged();
        }
    }

    /// <summary>Writes the database and its metadata. Does nothing when read-only.</summary>
    public bool Save()
    {
        lock (_gate)
        {
            if (IsReadOnly)
                return false;

            if (_untitled)
                throw new DocumentOpenException(
                    "A new database has no file yet. Use Save As to choose where it is written.");

            var text = CdbWriter.Write(Database);
            AtomicFile.WriteText(Path, text);
            Meta.Save();

            _pendingMerge = false;
            _savedCommand = Commands.UndoHistory.Count > 0 ? Commands.UndoHistory[^1] : null;
            TouchStamp();
            RaiseDirtyIfChanged();
            return true;
        }
    }

    /// <summary>
    /// Writes the document to another location and switches this document to it.
    /// The metadata file follows the database file.
    /// </summary>
    public void SaveAs(string newPath)
    {
        lock (_gate)
        {
            // Writing over a file another window is editing would lose its changes.
            if (!string.Equals(OpenFileRegistry.Normalize(newPath), OpenFileRegistry.Normalize(Path),
                    StringComparison.OrdinalIgnoreCase)
                && OpenFileRegistry.IsHeld(newPath))
            {
                throw new DocumentOpenException(
                    $"'{System.IO.Path.GetFileName(newPath)}' is already open in another window. " +
                    "Close that window first.");
            }

            var text = CdbWriter.Write(Database);
            AtomicFile.WriteText(newPath, text);

            // The old path keeps no recovery point once the document has moved on.
            AtomicFile.TryDelete(BackupPath);

            var previous = _heldPath;
            Path = newPath;
            Meta.MoveTo(newPath);
            Meta.Save(force: true);

            if (previous is not null)
                OpenFileRegistry.Release(previous);

            _heldPath = OpenFileRegistry.TryAcquire(newPath) ? newPath : null;

            _pendingMerge = false;
            _untitled = false;
            _savedCommand = Commands.UndoHistory.Count > 0 ? Commands.UndoHistory[^1] : null;
            IsReadOnly = false;
            ReadOnlyReason = null;
            TouchStamp();
            RaiseDirtyIfChanged();
        }
    }

    /// <summary>Path of the single rolling backup file used for crash recovery.</summary>
    public string BackupPath => Path + BackupSuffix;

    /// <summary>
    /// Records the current write time of the file. Called after opening, saving and
    /// whenever an external change is deliberately ignored.
    /// </summary>
    public void TouchStamp()
    {
        try
        {
            _stamp = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;
        }
        catch
        {
            _stamp = DateTime.MinValue;
        }
    }

    /// <summary>
    /// True when another program wrote the file since this window last read or saved it
    /// (requirement F15). The check is a timestamp comparison, so it is cheap enough to
    /// run on a timer.
    /// </summary>
    public bool HasChangedOnDisk
    {
        get
        {
            try
            {
                // An untitled document has no file, so there is nothing to compare against.
                if (_untitled)
                    return false;

                if (!File.Exists(Path))
                    return true;
                return File.GetLastWriteTimeUtc(Path) != _stamp;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Writes the backup file when the document is dirty. Only one backup file is kept,
    /// as decided in requirement Q1.
    /// </summary>
    public bool WriteBackup()
    {
        // An untitled document has no file to recover into: the backup would sit at a
        // scratch path nothing ever opens. Its first save asks for a real location.
        if (_untitled || !IsDirty || IsReadOnly)
            return false;

        lock (_gate)
        {
            var text = CdbWriter.Write(Database);
            AtomicFile.WriteText(BackupPath, text);
            return true;
        }
    }

    public bool HasBackup => File.Exists(BackupPath);

    public void DeleteBackup() => AtomicFile.TryDelete(BackupPath);

    /// <summary>File name shown in the title bar, or "Untitled" for a new database.</summary>
    public string FileLabel =>
        _untitled ? UntitledName : System.IO.Path.GetFileName(Path);

    /// <summary>Title shown in the window header and the tab.</summary>
    public string Title => FileLabel + (IsDirty ? " *" : string.Empty);

    /// <summary>Display name without the extension.</summary>
    public string DisplayName =>
        _untitled ? UntitledName : System.IO.Path.GetFileNameWithoutExtension(Path);

    public void Dispose()
    {
        Commands.Changed -= OnCommandsChanged;

        if (_heldPath is { } held)
        {
            _heldPath = null;
            OpenFileRegistry.Release(held);
        }
    }
}
