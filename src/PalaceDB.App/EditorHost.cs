using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using PalaceDB.Core.Commands;
using PalaceDB.Core.Cursor;
using PalaceDB.Core.Document;
using PalaceDB.Core.Editing;
using PalaceDB.Core.Export;
using PalaceDB.Core.Json;
using PalaceDB.Core.Media;
using PalaceDB.Core.Merge;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;
using PalaceDB.Core.Settings;
using PalaceDB.Core.Storage;
using PalaceDB.Core.Templates;
using Microsoft.Web.WebView2.Core;

namespace PalaceDB.App;

/// <summary>
/// Bridges the HTML interface and the document model.
/// <para>
/// Every message is a JSON object <c>{ "type": "...", "payload": { ... } }</c>.
/// Requests come from the page, notifications are pushed back through
/// <see cref="CoreWebView2.PostWebMessageAsJson"/>.
/// </para>
/// </summary>
public sealed partial class EditorHost
{
    private readonly MainWindow _window;
    private readonly CoreWebView2 _web;

    private DatabaseDocument? _document;
    private string? _currentSheet;
    private CellFormatter? _formatter;

    /// <summary>Reads the image files referenced by cells so the grid can show thumbnails.</summary>
    private ImageResolver? _images;

    /// <summary>
    /// Path of the document whose content came from a merge and still has conflict
    /// leftovers next to it (F16). Cleared once the merged content has been saved.
    /// </summary>
    private string? _pendingMergePath;

    /// <summary>Application-level preferences: the recent files list (F03).</summary>
    private AppSettings _settings;

    private static readonly string SettingsPath = AppSettings.PathFor(App.DataFolder);

    /// <summary>Writes the rolling backup while a document has unsaved changes (C1).</summary>
    private readonly System.Windows.Threading.DispatcherTimer _backupTimer;

    /// <summary>Polls the file timestamp to notice writes from other programs (F15).</summary>
    private readonly System.Windows.Threading.DispatcherTimer _watchTimer;

    private bool _backupAnnounced;
    private bool _checkingExternalChange;

    /// <summary>
    /// Automatic test hook: answers the recovery and external-change prompts with the
    /// safe default instead of showing a modal dialog.
    /// </summary>
    private static readonly bool Automation = Environment.GetEnvironmentVariable("PALACEDB_AUTOMATION") == "1";

    public EditorHost(MainWindow window, CoreWebView2 web)
    {
        _window = window;
        _web = web;

        _settings = AppSettings.Load(SettingsPath);
        if (_settings.LoadWarning is { } warning)
            App.Log(warning);

        _backupTimer = new System.Windows.Threading.DispatcherTimer { Interval = BackupInterval };
        _backupTimer.Tick += (_, _) => WriteAutomaticBackup();
        _backupTimer.Start();

        _watchTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _watchTimer.Tick += (_, _) => CheckExternalChange();
        _watchTimer.Start();

        // A window that opened this file read-only because another one was editing it has
        // to hear about it when that window goes away, or it stays read-only for good.
        OpenFileRegistry.Released += OnWriteSlotReleased;
    }

    /// <summary>
    /// Lets go of everything that outlives the window: the write slot, the timers and the
    /// static registry event. Without this a closed window kept its file held for the
    /// rest of the process, so any other window showing that file could never edit it.
    /// </summary>
    public void Detach()
    {
        OpenFileRegistry.Released -= OnWriteSlotReleased;
        _backupTimer.Stop();
        _watchTimer.Stop();
        _document?.Dispose();
        _document = null;
    }

    /// <summary>
    /// How often the rolling backup is written. Five minutes by default; the environment
    /// variable only exists so the end-to-end tests do not have to wait that long.
    /// </summary>
    private static TimeSpan BackupInterval
    {
        get
        {
            var text = Environment.GetEnvironmentVariable("PALACEDB_BACKUP_SECONDS");
            return int.TryParse(text, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromMinutes(5);
        }
    }

    public DatabaseDocument? Document => _document;

    // ---------------------------------------------------------------- incoming

    public void HandleMessage(string json)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
                return;

            var type = typeElement.GetString() ?? string.Empty;
            var payload = root.TryGetProperty("payload", out var p) ? p : default;

            try
            {
                Dispatch(type, payload);
            }
            catch (Exception ex)
            {
                SendError(ex.Message);
            }
        }
    }

    private void Dispatch(string type, JsonElement payload)
    {
        switch (type)
        {
            case "ready":
                SendAppInfo();
                if (_document is not null)
                    SendDocument();
                break;

            case "openFile":
                PromptOpenFile();
                break;

            case "newDatabase":
                NewDocument();
                break;

            case "simulateDrop":
                // Test hook: a real drag cannot be driven over DevTools.
                if (Automation)
                    _window.SimulateDrop(DroppedFiles(payload));
                break;

            case "openPath":
                OpenDocument(GetString(payload, "path"));
                break;

            case "openRecent":
                OpenRecent(GetString(payload, "path"));
                break;

            case "clearRecent":
                ClearRecent();
                break;

            case "about":
                SendAppInfo();
                break;

            case "save":
                SaveDocument();
                break;

            case "saveAs":
                PromptSaveAs(AutomationTarget(payload));
                break;

            case "closeDocument":
                CloseDocument();
                break;

            case "selectSheet":
                SelectSheet(GetString(payload, "sheet"));
                break;

            case "requestRows":
                SendRows(GetString(payload, "sheet"), GetInt(payload, "start"), GetInt(payload, "count"));
                break;

            case "setCell":
                SetCell(payload);
                break;

            case "refOptions":
                SendRefOptions(payload);
                break;

            case "browseFile":
                BrowseFile(payload);
                break;

            case "clearCells":
                ClearCells(payload);
                break;

            case "fillCells":
                FillCells(payload);
                break;

            case "copyCells":
                CopyCells(payload, cut: false);
                break;

            case "cutCells":
                CopyCells(payload, cut: true);
                break;

            case "copyText":
                CopyText(payload);
                break;

            case "pasteCells":
                PasteCells(payload);
                break;

            case "insertRow":
                InsertRow(payload);
                break;

            case "deleteRows":
                DeleteRows(payload);
                break;

            case "duplicateRows":
                DuplicateRows(payload);
                break;

            case "moveRow":
                MoveRow(payload);
                break;

            case "addColumn":
                AddColumn(payload);
                break;

            case "editColumn":
                EditColumn(payload);
                break;

            case "renameColumn":
                RenameColumn(payload);
                break;

            case "changeColumnType":
                ChangeColumnType(payload);
                break;

            case "moveColumn":
                MoveColumn(payload);
                break;

            case "deleteColumn":
                DeleteColumn(payload);
                break;

            case "convertColumn":
                ConvertColumn(payload);
                break;

            case "openSubValue":
                OpenSubValue(payload);
                break;

            case "setSubValue":
                SetSubValue(payload);
                break;

            case "addSheet":
                AddSheet(payload);
                break;

            case "renameSheet":
                RenameSheet(payload);
                break;

            case "deleteSheet":
                DeleteSheet(payload);
                break;

            case "moveSheet":
                MoveSheet(payload);
                break;

            case "toggleSheetIndex":
                ToggleSheetFeature(payload, SheetFeature.Index);
                break;

            case "toggleSheetGroup":
                ToggleSheetFeature(payload, SheetFeature.Group);
                break;

            case "toggleSeparator":
                ToggleSeparator(payload);
                break;

            case "setSeparatorTitle":
                SetSeparatorTitle(payload);
                break;

            case "setDisplayColumn":
                SetDisplayColumn(payload);
                break;

            case "setDisplayIcon":
                SetDisplayIcon(payload);
                break;

            case "setColumnKind":
                SetColumnKind(payload);
                break;

            case "setColumnOptions":
                SetColumnOptions(payload);
                break;

            case "exportLocalized":
                ExportLocalized(payload);
                break;

            case "resolveImages":
                ResolveImages(payload);
                break;

            case "setColumnWidth":
                SetColumnWidth(payload);
                break;

            case "setStyle":
                SetStyle(payload);
                break;

            case "setFileScheme":
                SetFileScheme(payload);
                break;

            case "applyTemplate":
                ApplyTemplate(payload);
                break;

            case "extendTemplate":
                ExtendTemplate(payload);
                break;

            case "detachTemplate":
                DetachTemplate(payload);
                break;

            case "saveTemplate":
                SaveTemplate(payload);
                break;

            case "renameTemplate":
                RenameTemplate(payload);
                break;

            case "deleteTemplate":
                DeleteTemplate(payload);
                break;

            case "undo":
                UndoOrRedo(undo: true);
                break;

            case "redo":
                UndoOrRedo(undo: false);
                break;

            case "newWindow":
                App.Current.Dispatcher.Invoke(() =>
                {
                    var window = new MainWindow();
                    window.Show();
                });
                break;

            case "exit":
                _window.Close();
                break;

            case "setTitle":
                _window.UpdateTitle(GetString(payload, "title"), GetBool(payload, "dirty"));
                break;

            default:
                SendError($"Unknown command '{type}'.");
                break;
        }
    }

    // ---------------------------------------------------------------- recent files (F03)

    /// <summary>
    /// Opens an entry of the recent list. A remembered file that has since been deleted is
    /// dropped from the list instead of leaving a dead entry behind.
    /// </summary>
    private void OpenRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            ReloadSettings();
            if (_settings.RemoveRecent(path))
                PersistSettings();
            SendRecentFiles();
            SendError($"'{Path.GetFileName(path)}' no longer exists and was removed from the list.");
            return;
        }

        OpenDocument(path);
    }

    private void ClearRecent()
    {
        ReloadSettings();
        if (_settings.RecentFiles.Count == 0)
        {
            SendRecentFiles();
            return;
        }

        _settings.ClearRecent();
        PersistSettings();
        SendRecentFiles();
        SendToast("Recent files cleared.", "info");
    }

    /// <summary>Moves the file to the front of the recent list and tells the menu.</summary>
    private void RememberRecent(string path)
    {
        // Another window may have opened a file since this one started, and every write
        // replaces the whole file, so the list is re-read before it is changed.
        ReloadSettings();
        if (!_settings.AddRecent(path))
        {
            SendRecentFiles();
            return;
        }

        PersistSettings();
        SendRecentFiles();
    }

    /// <summary>Re-reads the preferences so several windows do not overwrite each other.</summary>
    private void ReloadSettings()
    {
        var fresh = AppSettings.Load(SettingsPath);
        if (fresh.LoadWarning is null)
            _settings = fresh;
    }

    private void PersistSettings()
    {
        if (!_settings.Save(SettingsPath))
            App.Log("Could not write the settings file: " + SettingsPath);
    }

    private void SendRecentFiles()
    {
        var files = new JsonArray();
        foreach (var entry in _settings.Describe())
        {
            files.Add(new JsonObject
            {
                ["path"] = entry.Path,
                ["name"] = entry.Name,
                ["folder"] = entry.Folder,
                ["exists"] = entry.Exists,
            });
        }

        Send(new JsonObject
        {
            ["type"] = "recentFiles",
            ["payload"] = new JsonObject { ["files"] = files },
        });
    }

    // ---------------------------------------------------------------- commands

    public void PromptOpenFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open database",
            Filter = "PalaceDB files (*.cdb)|*.cdb|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(_window) == true)
            OpenDocument(dialog.FileName);
    }

    public void OpenDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_document is not null && !ConfirmClose())
            return;

        // A backup newer than the file means the previous session died with unsaved
        // work; offer it before the file is parsed (F19).
        if (!OfferRecovery(path))
            return;

        try
        {
            DatabaseDocument document;
            // The previous document has to release its write slot first, otherwise
            // reopening the file this window already holds comes back read-only. The
            // release is held back until the new document has asked for the slot again,
            // so another window cannot slip in and take it in between.
            using (OpenFileRegistry.DeferNotifications())
            {
                _document?.Dispose();
                _document = null;
                _pendingMergePath = null;

                document = OpenWithMerge(path);
                AdoptDocument(document);
            }

            RememberRecent(document.Path);
            SendDocument();

            if (document.IsReadOnly && document.ReadOnlyReason is { } reason)
                SendToast($"Opened read-only: {reason}.", "warning");

            if (document.LoadWarning is { } warning)
                SendToast(warning, "warning");
        }
        catch (DocumentOpenException ex)
        {
            SendError(ex.Message);
            SendDocument();
        }
    }

    /// <summary>
    /// File ▸ New: an empty database that has no file yet. The metadata still needs a
    /// home, so the document borrows a scratch path in the application data folder; the
    /// first save asks for a real location instead of writing there.
    /// </summary>
    public void NewDocument()
    {
        if (_document is not null && !ConfirmClose())
            return;

        var scratch = Path.Combine(
            App.DataFolder, "new", Guid.NewGuid().ToString("N") + ".cdb");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
        }
        catch (Exception ex)
        {
            SendError($"Could not create a new database: {ex.Message}");
            return;
        }

        AdoptDocument(DatabaseDocument.CreateUntitled(scratch));
        _pendingMergePath = null;
        SendDocument();
        SendToast("New database. Add a table, then use Save As to choose where it is written.", "info");
    }

    /// <summary>
    /// Wires a freshly opened or created document into this window. The caller has already
    /// released the previous document, because the write slot has to be free before the
    /// new file is opened.
    /// </summary>
    private void AdoptDocument(DatabaseDocument document)
    {
        _document?.Dispose();
        _document = document;
        _formatter = new CellFormatter(document.Database, document.Index);
        _images = new ImageResolver(Path.GetDirectoryName(document.Path) ?? string.Empty);
        _backupAnnounced = false;

        // Remembered window geometry is per file, and only the first file of a window
        // is allowed to move it (F17).
        _window.ApplyPlacement(document.Meta.WindowState);
        _currentSheet = document.Database.VisibleSheets.FirstOrDefault()?.Name;
    }

    /// <summary>
    /// Opens a file, first merging any conflict leftovers git put next to it (F16).
    /// <para>
    /// A clean merge is opened as an ordinary unsaved document, so the user sees the
    /// combined result before it reaches the disk. A merge that could not run at all
    /// falls back to the plain open, which reports the parse error in the usual way.
    /// </para>
    /// </summary>
    private DatabaseDocument OpenWithMerge(string path)
    {
        if (!MergeResolver.HasPendingMerge(path))
            return DatabaseDocument.Open(path);

        var outcome = MergeResolver.Prepare(path);

        switch (outcome.State)
        {
            case MergeFileState.Merged:
            case MergeFileState.Conflicted:
            {
                var document = DatabaseDocument.OpenMerged(path, outcome.MergedText!);
                _pendingMergePath = path;

                SendMergeReport(outcome);

                if (outcome.State == MergeFileState.Conflicted)
                {
                    SendToast(
                        $"Merged with {outcome.Conflicts.Count} conflict(s); the local values were kept. Review before saving.",
                        "warning");
                }
                else
                {
                    SendToast("Merged the conflicting versions. Review before saving.", "info");
                }

                return document;
            }

            case MergeFileState.Failed:
                SendToast(
                    $"Could not merge the conflicting versions: {outcome.Error}. Opening the file as it is.",
                    "warning");
                return DatabaseDocument.Open(path);

            default:
                return DatabaseDocument.Open(path);
        }
    }

    private void SendMergeReport(MergeOutcome outcome)
    {
        var conflicts = new JsonArray();
        foreach (var conflict in outcome.Conflicts)
        {
            conflicts.Add(new JsonObject
            {
                ["path"] = conflict.Path,
                ["base"] = conflict.Base,
                ["mine"] = conflict.Mine,
                ["theirs"] = conflict.Theirs,
                ["kept"] = conflict.Kept == MergeSide.Mine ? "mine" : "theirs",
            });
        }

        var sideFiles = new JsonArray();
        foreach (var file in outcome.SideFiles)
            sideFiles.Add(Path.GetFileName(file));

        Send(new JsonObject
        {
            ["type"] = "mergeReport",
            ["payload"] = new JsonObject
            {
                ["state"] = outcome.State.ToString(),
                ["conflicts"] = conflicts,
                ["sideFiles"] = sideFiles,
            },
        });
    }

    // ---------------------------------------------------------------- backup and external changes
    /// <summary>Writes the rolling backup when the document has unsaved changes (C1).</summary>
    private void WriteAutomaticBackup()
    {
        if (_document is null || _document.IsReadOnly || !_document.IsDirty)
            return;

        try
        {
            if (!_document.WriteBackup() || _backupAnnounced)
                return;

            _backupAnnounced = true;
            SendToast("Automatic backup written for the unsaved changes.", "info");
        }
        catch (Exception ex)
        {
            App.Log("Automatic backup failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Notices that another program wrote the file (F15). Without unsaved changes the
    /// window simply reloads; with them the user decides what to keep.
    /// </summary>
    private void CheckExternalChange()
    {
        if (_document is null || _checkingExternalChange)
            return;

        // A deleted file is not a conflict: the next save recreates it.
        if (!File.Exists(_document.Path))
            return;

        if (!_document.HasChangedOnDisk)
            return;

        _checkingExternalChange = true;
        try
        {
            if (!_document.IsDirty)
            {
                ReloadFromDisk("Reloaded because the file was changed on disk.");
                return;
            }

            if (Automation)
            {
                ReloadFromDisk("Reloaded because the file was changed on disk.");
                return;
            }

            var result = MessageBox.Show(
                _window,
                $"'{Path.GetFileName(_document.Path)}' was changed by another program, and this window has unsaved changes.\n\n" +
                "Reloading discards the changes made here. Keep editing instead?",
                "PalaceDB",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
                ReloadFromDisk("Reloaded because the file was changed on disk.");
            else
                _document.TouchStamp(); // keep the local version and stop asking
        }
        catch (Exception ex)
        {
            App.Log("External change check failed: " + ex.Message);
        }
        finally
        {
            _checkingExternalChange = false;
        }
    }

    /// <summary>Reopens the current path, throwing away the in-memory state.</summary>
    private void ReloadFromDisk(string message)
    {
        if (_document is null)
            return;

        var path = _document.Path;
        var sheet = _currentSheet;

        _document.Dispose();
        _document = null;
        _formatter = null;

        try
        {
            _document = DatabaseDocument.Open(path);
            _formatter = new CellFormatter(_document.Database, _document.Index);
            _backupAnnounced = false;
            _currentSheet = _document.Database.GetSheet(sheet ?? string.Empty)?.Name
                            ?? _document.Database.VisibleSheets.FirstOrDefault()?.Name;

            SendDocument();
            SendToast(message, "info");
        }
        catch (DocumentOpenException ex)
        {
            SendError(ex.Message);
            SendDocument(); // the document is gone, so the window falls back to the welcome page
        }
    }

    /// <summary>
    /// Asks what to do with a backup that is newer than the file (F19). Returns false
    /// only when the user cancels the open.
    /// </summary>
    private bool OfferRecovery(string path)
    {
        switch (BackupRecovery.Inspect(path))
        {
            case BackupState.None:
                return true;

            case BackupState.Stale:
                BackupRecovery.Discard(path);
                return true;
        }

        if (!Automation)
        {
            var result = MessageBox.Show(
                _window,
                $"A newer automatic backup of '{Path.GetFileName(path)}' was found.\n\n" +
                "It holds changes that were never saved because the previous session ended unexpectedly.\n\n" +
                "Yes: recover the backup into the file\n" +
                "No: discard the backup and open the saved file\n" +
                "Cancel: do not open the file",
                "PalaceDB",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
                return false;

            if (result == MessageBoxResult.No)
            {
                BackupRecovery.Discard(path);
                return true;
            }
        }

        try
        {
            BackupRecovery.Recover(path);
        }
        catch (Exception ex)
        {
            // A damaged backup must never stop the saved file from opening.
            App.Log("Backup recovery failed: " + ex);
            SendError($"The backup could not be recovered ({ex.Message}). The saved file was opened instead.");
        }

        return true;
    }

    public void SaveDocument()
    {
        if (_document is null)
            return;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only and cannot be saved.");
            return;
        }

        // A new database has no file yet, so saving means choosing one first.
        if (_document.IsUntitled)
        {
            PromptSaveAs();
            return;
        }

        try
        {
            // Recorded before the save so one write of the side-car covers both.
            CapturePlacement();
            var merged = _document.HasPendingMerge;
            _document.Save();
            _document.DeleteBackup();

            // The merge is only finished once the result is on disk (F16).
            if (merged)
                FinishMerge();

            SendDocument();
            SendToast("Saved.", "info");
        }
        catch (Exception ex)
        {
            SendError($"Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the conflict leftovers after a merged document has been written (F16).
    /// A failure here must not turn a successful save into an error.
    /// </summary>
    private void FinishMerge()
    {
        var path = _pendingMergePath ?? _document?.Path;
        _pendingMergePath = null;

        if (path is null)
            return;

        try
        {
            var removed = MergeResolver.Cleanup(path);
            if (removed > 0)
                SendToast($"Removed {removed} conflict file(s).", "info");
        }
        catch (Exception ex)
        {
            App.Log("Merge cleanup failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Stores the current window geometry in the side-car (F17). Called on save and on
    /// close so a crash costs at most the movement since the last write.
    /// </summary>
    public void SaveWindowPlacement()
    {
        if (CapturePlacement())
            _document?.SaveMeta();
    }

    /// <summary>
    /// Copies the window geometry into the metadata. Returns true when the side-car
    /// actually changed, so an untouched window never dirties the file.
    /// </summary>
    private bool CapturePlacement()
    {
        if (_document is null || _document.IsReadOnly)
            return false;

        return _window.TryCapturePlacement(out var state) && _document.Meta.SetWindowState(state);
    }

    /// <summary>Asks for a destination file. Returns null when the user cancelled.</summary>
    private string? AskSaveTarget()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save database as",
            Filter = "PalaceDB files (*.cdb)|*.cdb|All files (*.*)|*.*",
            DefaultExt = ".cdb",
            FileName = _document is { IsUntitled: true }
                ? DatabaseDocument.UntitledName + ".cdb"
                : Path.GetFileName(_document?.Path ?? string.Empty),
        };

        return dialog.ShowDialog(_window) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Asks for a destination and writes the document there. A new database always comes
    /// through here, because it has no file to save over yet.
    /// </summary>
    public void PromptSaveAs(string? target = null)
    {
        if (_document is null)
            return;

        target ??= AskSaveTarget();
        if (target is null)
            return;

        try
        {
            CapturePlacement();
            _document.SaveAs(target);

            // The conflict leftovers belong to the original file, which was not written.
            _pendingMergePath = null;

            RememberRecent(target);
            SendDocument();
            SendToast("Saved as " + Path.GetFileName(target), "info");
        }
        catch (Exception ex)
        {
            SendError($"Save failed: {ex.Message}");
        }
    }

    public void CloseDocument()
    {
        if (!ConfirmClose())
            return;

        SaveWindowPlacement();
        _document?.Dispose();
        _document = null;
        _formatter = null;
        _images = null;
        _currentSheet = null;
        _pendingMergePath = null;
        SendDocument();
    }

    /// <summary>
    /// Another window let go of a file's write slot. A window that is read-only only
    /// because of that other window can take the slot over, which is what the user
    /// expects after closing the window that had the file open for editing.
    /// </summary>
    private void OnWriteSlotReleased(string path)
    {
        // The release happens on the closing window's thread; everything below touches
        // this window's document and its page.
        if (_window.Dispatcher.CheckAccess())
            TakeOverWriteSlot(path);
        else
            _window.Dispatcher.BeginInvoke(() => TakeOverWriteSlot(path));
    }

    private void TakeOverWriteSlot(string path)
    {
        if (_document is null)
            return;

        if (!string.Equals(
                OpenFileRegistry.Normalize(path),
                OpenFileRegistry.Normalize(_document.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_document.TryTakeOverWriteSlot())
            return;

        SendDocument();
        SendToast("The window that was editing this file is gone, so it can be edited here now.", "info");
    }

    /// <summary>Asks the user about unsaved changes. Returns false to cancel the close.</summary>
    public bool ConfirmClose()
    {
        if (_document is null)
            return true;

        if (!_document.IsDirty)
        {
            // Nothing to recover, so a leftover backup is only noise.
            _document.DeleteBackup();
            return true;
        }

        var result = MessageBox.Show(
            _window,
            $"'{_document.FileLabel}' has unsaved changes.\n\nSave before closing?",
            "PalaceDB",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Yes:
                return SaveForClose();

            case MessageBoxResult.No:
                // The user explicitly discarded the changes, so the crash backup goes too.
                _document.DeleteBackup();
                return true;

            default:
                return false;
        }
    }

    private bool SaveForClose()
    {
        if (_document is null)
            return true;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only and cannot be saved.");
            return false;
        }

        // A new database has to be given a file before it can be written on the way out.
        if (_document.IsUntitled)
        {
            var target = AskSaveTarget();
            if (target is null)
                return false;

            try
            {
                CapturePlacement();
                _document.SaveAs(target);
                RememberRecent(target);
                return true;
            }
            catch (Exception ex)
            {
                SendError($"Save failed: {ex.Message}");
                return false;
            }
        }

        try
        {
            var merged = _document.HasPendingMerge;
            _document.Save();
            _document.DeleteBackup();

            // Saving on the way out finishes the merge just like Ctrl+S does (F16).
            if (merged)
                FinishMerge();

            return true;
        }
        catch (Exception ex)
        {
            SendError($"Save failed: {ex.Message}");
            return false;
        }
    }

    private void SelectSheet(string sheet)
    {
        if (_document is null)
            return;

        if (_document.Database.GetSheet(sheet) is null)
            return;

        _currentSheet = sheet;
        Send(new JsonObject
        {
            ["type"] = "sheetSelected",
            ["payload"] = new JsonObject { ["sheet"] = sheet },
        });
    }

    // ---------------------------------------------------------------- editing

    /// <summary>Parses and applies a typed cell value, recording it as one undo step.</summary>
    private void SetCell(JsonElement payload)
    {
        if (_document is null || _formatter is null)
            return;

        var sheetName = GetString(payload, "sheet");
        var rowIndex = GetInt(payload, "row");
        var columnName = GetString(payload, "column");
        var text = GetString(payload, "text");

        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null || rowIndex < 0 || rowIndex >= sheet.Lines.Count)
            return;

        var column = sheet.GetColumn(columnName);
        if (column is null)
            return;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only.");
            SendRowUpdated(sheet, rowIndex);
            return;
        }

        var row = sheet.Lines[rowIndex];

        CdbValue? value;
        try
        {
            value = ValueCodec.ParseText(column, text);
        }
        catch (CdbValidationException ex)
        {
            SendCellRejected(sheetName, rowIndex, columnName, ex.Message);
            return;
        }

        if (ValuesEqual(row.Get(columnName), value))
        {
            SendRowUpdated(sheet, rowIndex);
            return;
        }

        var command = new CellEditCommand(row, columnName, value)
        {
            CursorBefore = CursorResolver.CreateAnchor(sheet, rowIndex, sheet.Columns.IndexOf(column)),
            CursorAfter = CursorResolver.CreateAnchor(sheet, rowIndex, sheet.Columns.IndexOf(column)),
        };

        _document.Commands.Execute(command);
        SendRowUpdated(sheet, rowIndex);
        SendDirty();
    }

    /// <summary>Clears every cell in the given rectangle (bulk clear / Delete key).</summary>
    /// <summary>
    /// Fills the selected rectangle with one value (requirement R5: Ctrl+Enter in the
    /// cell editor). The whole fill is a single undo step.
    /// </summary>
    private void FillCells(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheetName = GetString(payload, "sheet");
        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null || _document.IsReadOnly)
            return;

        if (!payload.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
            return;
        if (!payload.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Array)
            return;

        var rowIndices = rowsElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetInt32()).Where(i => i >= 0 && i < sheet.Lines.Count).Distinct().ToList();
        var columnNames = columnsElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? string.Empty).ToList();

        if (rowIndices.Count == 0 || columnNames.Count == 0)
            return;

        var text = GetString(payload, "text");
        var builder = new MultiCellEditCommand.Builder("Fill selection");

        try
        {
            foreach (var columnName in columnNames)
            {
                var column = sheet.GetColumn(columnName);
                if (column is null || column.Type is ColumnType.Id)
                    continue;

                var value = ValueCodec.ParseText(column, text);
                foreach (var rowIndex in rowIndices)
                    builder.Add(sheet.Lines[rowIndex], columnName, value);
            }
        }
        catch (CdbValidationException ex)
        {
            SendError(ex.Message);
            return;
        }

        var command = builder.Build();
        if (command is null)
            return;

        _document.Commands.Execute(command);
        SendRows(sheetName, rowIndices.Min(), rowIndices.Max() - rowIndices.Min() + 1);
        SendDirty();
    }

    private void ClearCells(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheetName = GetString(payload, "sheet");
        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null || _document.IsReadOnly)
            return;

        if (!payload.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
            return;
        if (!payload.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Array)
            return;

        var rowIndices = rowsElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetInt32()).Where(i => i >= 0 && i < sheet.Lines.Count).Distinct().ToList();
        var columnNames = columnsElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? string.Empty).ToList();

        if (rowIndices.Count == 0 || columnNames.Count == 0)
            return;

        var builder = new MultiCellEditCommand.Builder("Clear cells");
        foreach (var rowIndex in rowIndices)
        {
            var row = sheet.Lines[rowIndex];
            foreach (var columnName in columnNames)
            {
                var column = sheet.GetColumn(columnName);
                if (column is null)
                    continue;
                builder.Add(row, columnName, TypeDefaults.Create(column, _document.Database, sheet));
            }
        }

        var command = builder.Build();
        if (command is null)
            return;

        _document.Commands.Execute(command);
        SendRows(sheetName, rowIndices.Min(), rowIndices.Max() - rowIndices.Min() + 1);
        SendDirty();
    }

    /// <summary>Returns the rows of the referenced sheet so the editor can offer them.</summary>
    private void SendRefOptions(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        var column = sheet?.GetColumn(GetString(payload, "column"));
        if (column?.Type is not ColumnType.Ref reference)
            return;

        var target = _document.Database.GetSheet(reference.Sheet);
        var options = new JsonArray();

        if (target is not null)
        {
            var index = _document.Index.For(target);
            var idColumn = target.IdColumn;
            var iconColumn = target.Props.DisplayIcon;

            foreach (var row in target.Lines)
            {
                var id = idColumn is null ? string.Empty : row.GetString(idColumn.Name) ?? string.Empty;
                options.Add(new JsonObject
                {
                    ["id"] = id,
                    ["display"] = index?.GetDisplay(row) ?? id,
                    // Only sent when the table declares an icon column, so the dropdown
                    // does not ask for image data it will never use.
                    ["icon"] = iconColumn is null ? null : row.GetString(iconColumn),
                });
            }
        }

        Send(new JsonObject
        {
            ["type"] = "refOptions",
            ["payload"] = new JsonObject
            {
                ["sheet"] = reference.Sheet,
                ["iconColumn"] = target?.Props.DisplayIcon,
                ["options"] = options,
            },
        });
    }

    /// <summary>Native file picker for Image and File columns.</summary>
    private void BrowseFile(JsonElement payload)
    {
        if (_document is null)
            return;

        var kind = GetString(payload, "kind");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = kind == "image" ? "Select an image" : "Select a file",
            Filter = kind == "image"
                ? "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files (*.*)|*.*"
                : "All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(_window) != true)
            return;

        // Store the path relative to the database so game engines can resolve it.
        var relative = MakeRelative(_document.Path, dialog.FileName);
        Send(new JsonObject
        {
            ["type"] = "browseResult",
            ["payload"] = new JsonObject { ["path"] = relative },
        });
    }

    private static string MakeRelative(string baseFile, string target)
    {
        try
        {
            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(baseFile));
            if (string.IsNullOrEmpty(baseDirectory))
                return target;

            var relative = Path.GetRelativePath(baseDirectory, target);
            return relative.Replace('\\', '/');
        }
        catch
        {
            return target;
        }
    }

    /// <summary>
    /// Puts plain text on the system clipboard.
    /// <para>
    /// Used for text selected outside the table — an error message, a conflict report —
    /// so copying it does not have to go through the engine's own copy accelerator.
    /// </para>
    /// </summary>
    private void CopyText(JsonElement payload)
    {
        var text = GetString(payload, "text");
        if (text.Length == 0)
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            SendError("The clipboard is not available: " + ex.Message);
        }
    }

    /// <summary>Copies (or cuts) the selected rectangle to the system clipboard.</summary>
    private void CopyCells(JsonElement payload, bool cut)
    {
        if (_document is null)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var rows = ReadIntArray(payload, "rows");
        var columns = ReadStringArray(payload, "columns");
        if (rows.Count == 0 || columns.Count == 0)
            return;

        // Copy exactly what was selected. Dropping the identifier column here made
        // copying an id (or a whole row) silently produce an empty payload, so pasting it
        // back did nothing at all.
        var text = ClipboardService.Serialize(sheet, rows, columns, includeIdColumn: true);
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            SendError("The clipboard is not available: " + ex.Message);
            return;
        }

        if (cut && !_document.IsReadOnly)
        {
            var builder = new MultiCellEditCommand.Builder("Cut cells");
            foreach (var rowIndex in rows)
            {
                if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
                    continue;
                var row = sheet.Lines[rowIndex];
                foreach (var columnName in columns)
                {
                    var column = sheet.GetColumn(columnName);
                    if (column is null || column.Type is ColumnType.Id)
                        continue;
                    builder.Add(row, columnName, TypeDefaults.Create(column, _document.Database, sheet));
                }
            }

            var command = builder.Build();
            if (command is not null)
            {
                _document.Commands.Execute(command);
                SendRows(sheet.Name, rows.Min(), rows.Max() - rows.Min() + 1);
                SendDirty();
            }
        }

        Send(new JsonObject
        {
            ["type"] = "clipboardWritten",
            ["payload"] = new JsonObject
            {
                ["cut"] = cut,
                ["rows"] = rows.Count,
                ["columns"] = columns.Count,
            },
        });
    }

    /// <summary>Pastes the clipboard at the cursor, either overwriting or inserting rows.</summary>
    private void PasteCells(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only.");
            return;
        }

        string? text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception ex)
        {
            SendError("The clipboard is not available: " + ex.Message);
            return;
        }

        var clipboard = ClipboardService.Deserialize(text);
        var mode = GetString(payload, "mode");
        var row = GetInt(payload, "row");
        var column = GetInt(payload, "column");

        if (clipboard is null || clipboard.Rows.Count == 0)
        {
            // Not our format: treat it as tab separated text from a spreadsheet (R5).
            var grid = TsvParser.Parse(text);
            if (grid.Count == 0)
            {
                SendToast("The clipboard does not contain cells.", "warning");
                return;
            }

            clipboard = PasteService.FromText(sheet, column, grid, out var problem);
            if (clipboard is null)
            {
                SendError("Cannot paste: " + problem);
                return;
            }
        }

        IEditorCommand? command = mode == "insert"
            ? PasteService.BuildInsert(sheet, row, clipboard, _document.Database)
            : PasteService.BuildOverwrite(sheet, row, column, clipboard);

        if (command is null)
            return;

        _document.Commands.Execute(command);
        RefreshAfterStructuralChange(sheet.Name, row);
        SendToast(mode == "insert" ? "Rows inserted." : "Pasted.", "info");
    }

    private void InsertRow(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var index = Math.Clamp(GetInt(payload, "row"), 0, sheet.Lines.Count);
        var row = CreateEmptyRow(sheet, index);

        var command = new RowInsertCommand(sheet, index, new[] { row });
        _document.Commands.Execute(command);
        RefreshAfterStructuralChange(sheet.Name, index);
    }

    private void DeleteRows(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var rows = ReadIntArray(payload, "rows");
        if (rows.Count == 0)
            return;

        var start = rows.Min();
        var count = rows.Max() - start + 1;

        // Contiguous selection is the common case; delete it in one undo step.
        var command = new RowDeleteCommand(sheet, start, count);
        _document.Commands.Execute(command);

        // Requirement R3: land on the next row, or the previous one at the end.
        var target = CursorRules.RowAfterDelete(start, count, sheet.Lines.Count);
        RefreshAfterStructuralChange(sheet.Name, Math.Max(target, 0));
    }

    private void DuplicateRows(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var rows = ReadIntArray(payload, "rows");
        if (rows.Count == 0)
            return;

        var idColumn = sheet.IdColumn;
        var existing = new HashSet<string>(
            sheet.Lines.Select(r => idColumn is null ? string.Empty : r.GetString(idColumn.Name) ?? string.Empty)
                .Where(id => id.Length > 0),
            StringComparer.Ordinal);

        var copies = new List<CdbObject>();
        foreach (var index in rows.OrderBy(i => i))
        {
            if (index < 0 || index >= sheet.Lines.Count)
                continue;

            var copy = sheet.Lines[index].Clone();
            if (idColumn is not null)
            {
                var id = copy.GetString(idColumn.Name) ?? string.Empty;
                copy.Set(idColumn.Name, new CdbValue.Str(
                    PasteService.MakeUnique(id.Length == 0 ? "copy" : id, existing)));
            }
            copies.Add(copy);
        }

        if (copies.Count == 0)
            return;

        var insertAt = rows.Max() + 1;
        _document.Commands.Execute(new RowInsertCommand(sheet, insertAt, copies));
        RefreshAfterStructuralChange(sheet.Name, insertAt);
    }

    private void MoveRow(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var from = GetInt(payload, "from");
        var to = GetInt(payload, "to");
        if (from < 0 || from >= sheet.Lines.Count || to < 0 || to >= sheet.Lines.Count || from == to)
            return;

        _document.Commands.Execute(new RowMoveCommand(sheet, from, to));
        RefreshAfterStructuralChange(sheet.Name, to);
    }

    /// <summary>Builds a row pre-filled with the column defaults.</summary>
    private CdbObject CreateEmptyRow(SheetData sheet, int index)
    {
        var row = new CdbObject();
        var idColumn = sheet.IdColumn;

        foreach (var column in sheet.Columns)
        {
            if (column.Type is ColumnType.Id)
                continue;
            var value = TypeDefaults.Create(column, _document!.Database, sheet);
            if (value is not null)
                row.Set(column.Name, value);
        }

        // The identifier is the author's to choose: a generated "row12" only ever gets in
        // the way, and a missing id is reported in the status bar instead.
        if (idColumn is not null)
            row.Set(idColumn.Name, new CdbValue.Str(string.Empty));

        return row;
    }

    /// <summary>Refreshes the document description and the visible rows after a structural edit.</summary>
    private void RefreshAfterStructuralChange(string sheetName, int cursorRow)
    {
        _currentSheet = sheetName;
        SendDocument();
        Send(new JsonObject
        {
            ["type"] = "invalidate",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheetName,
                ["cursor"] = new JsonObject
                {
                    ["sheet"] = sheetName,
                    ["row"] = cursorRow,
                    ["column"] = 0,
                },
            },
        });
        SendDirty();
    }

    /// <summary>Same as the two argument overload but keeps the cursor on a column.</summary>
    private void RefreshAfterStructuralChange(string sheetName, int cursorRow, int cursorColumn)
    {
        _currentSheet = sheetName;
        SendDocument();
        Send(new JsonObject
        {
            ["type"] = "invalidate",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheetName,
                ["cursor"] = new JsonObject
                {
                    ["sheet"] = sheetName,
                    ["row"] = cursorRow,
                    ["column"] = cursorColumn,
                },
            },
        });
        SendDirty();
    }

    /// <summary>
    /// Refreshes the document description and the visible rows while leaving the cursor
    /// untouched. Used by edits that do not change the shape of the table.
    /// </summary>
    private void RefreshKeepingCursor(string sheetName)
    {
        _currentSheet = sheetName;
        SendDocument();
        Send(new JsonObject
        {
            ["type"] = "invalidate",
            ["payload"] = new JsonObject { ["sheet"] = sheetName },
        });
        SendDirty();
    }

    // ---------------------------------------------------------------- schema

    /// <summary>
    /// Rewrites every value of a column in one step (requirement C07). The cursor is left
    /// where it is because a conversion never changes the shape of the table.
    /// </summary>
    private void ConvertColumn(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var column = sheet.GetColumn(GetString(payload, "column"));
        if (column is null)
            return;

        var name = GetString(payload, "conversion");
        if (!TryParseConversion(name, out var conversion))
        {
            SendError($"Unknown conversion '{name}'.");
            return;
        }

        ColumnConvertCommand command;
        try
        {
            command = new ColumnConvertCommand(_document.Database, sheet, column, conversion);
        }
        catch (InvalidOperationException ex)
        {
            SendError(ex.Message);
            return;
        }

        if (command.ChangedValues == 0 && command.ChangedReferences == 0)
        {
            SendToast($"'{column.Name}' already matches \"{conversion.Label()}\".", "info");
            return;
        }

        _document.Commands.Execute(command);
        RefreshKeepingCursor(sheet.Name);

        var message = column.Type is ColumnType.Enum or ColumnType.Flags
            ? $"Renamed {command.ChangedValues} value(s) of '{column.Name}'."
            : $"Converted {command.ChangedValues} value(s) in '{column.Name}'.";

        if (command.ChangedReferences > 0)
            message += $" Retargeted {command.ChangedReferences} reference(s).";

        SendToast(message, "info");
    }

    private static bool TryParseConversion(string name, out ColumnConversion conversion)
    {
        switch (name)
        {
            case "lowercase": conversion = ColumnConversion.Lowercase; return true;
            case "uppercase": conversion = ColumnConversion.Uppercase; return true;
            case "upperIdent": conversion = ColumnConversion.UpperIdent; return true;
            case "lowerIdent": conversion = ColumnConversion.LowerIdent; return true;
            case "mul10": conversion = ColumnConversion.Multiply10; return true;
            case "div10": conversion = ColumnConversion.Divide10; return true;
            case "plus1": conversion = ColumnConversion.Plus1; return true;
            case "minus1": conversion = ColumnConversion.Minus1; return true;
            default: conversion = default; return false;
        }
    }

    private void AddColumn(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var name = GetString(payload, "name").Trim();
        if (name.Length == 0)
        {
            SendError("A column needs a name.");
            return;
        }

        if (sheet.GetColumn(name) is not null)
        {
            SendError($"The table already has a column named '{name}'.");
            return;
        }

        if (!TryDecodeType(GetString(payload, "type"), out var type))
            return;

        // Requirements C08 / C09: one ID column per table, and no clash with the
        // virtual index/group columns.
        if (type is ColumnType.Id && sheet.IdColumn is not null)
        {
            SendError($"The table already has an ID column ('{sheet.IdColumn.Name}').");
            return;
        }

        if ((string.Equals(name, "index", StringComparison.OrdinalIgnoreCase) && sheet.Props.HasIndex)
            || (string.Equals(name, "group", StringComparison.OrdinalIgnoreCase) && sheet.Props.HasGroup))
        {
            SendError($"'{name}' is reserved by a table property.");
            return;
        }

        var index = Math.Clamp(GetInt(payload, "index"), 0, sheet.Columns.Count);
        var optional = GetBool(payload, "optional");
        if (!TryReadColumnOptions(payload, type, optional, out var options))
            return;

        if (!TryReadSubColumns(payload, type, name, out var subColumns))
            return;

        var spec = BuildSpec(name, type, optional, options, subColumns);
        var command = BuildColumnCommand(
            sheet, new[] { (ColumnOp)new InsertColumnOp(name, index, spec) }, $"Add Column '{name}'", fillInsertedRows: true);
        if (command is null)
            return;

        _document.Commands.Execute(command);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, index);
        SendToast(DescribeColumnEdit($"Added column '{name}'.", sheet, name), "info");
    }

    /// <summary>
    /// Renames, retypes and re-options a column in one step.
    ///
    /// The three edits used to be three dialogs and three commands; doing them together
    /// keeps the whole change a single undo unit, and lets every part be validated before
    /// anything is written, so a rejected type cannot leave the column half-changed.
    /// </summary>
    private void EditColumn(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var column = sheet.GetColumn(GetString(payload, "column"));
        if (column is null)
            return;

        var name = GetString(payload, "name").Trim();
        if (name.Length == 0)
        {
            SendError("A column needs a name.");
            return;
        }

        var renaming = !string.Equals(name, column.Name, StringComparison.Ordinal);
        if (renaming && sheet.GetColumn(name) is not null)
        {
            SendError($"The table already has a column named '{name}'.");
            return;
        }

        if (!TryDecodeType(GetString(payload, "type"), out var type))
            return;

        var optional = GetBool(payload, "optional");
        if (!TryReadColumnOptions(payload, type, optional, out var options))
            return;

        if (!TryReadSubColumns(payload, type, name, out var subColumns))
            return;

        var index = sheet.Columns.IndexOf(column);
        var retyping = !string.Equals(
            ColumnTypeCodec.Encode(column.Type), ColumnTypeCodec.Encode(type), StringComparison.Ordinal);
        var spec = BuildSpec(name, type, optional, options, subColumns);

        // Rewriting the values of a column that other tables share loses data nobody
        // clicked on, so it is confirmed a second time.
        if (retyping && !ConfirmSharedLoss(payload, sheet, spec, "editColumn"))
            return;

        // One operation covers all three parts of the dialog: the rename, the type change
        // (with the fields of a list column) and the optional settings.
        var ops = new List<ColumnOp>();
        if (renaming)
            ops.Add(new RenameColumnOp(column.Name, name));
        ops.Add(new RetypeColumnOp(name, spec));

        var command = BuildColumnCommand(sheet, ops, $"Edit Column '{column.Name}'");
        if (command is null)
            return;

        _document.Commands.Execute(command);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, index);
        SendToast(DescribeColumnEdit($"Updated column '{name}'.", sheet, name), "info");
    }

    private void RenameColumn(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var column = sheet.GetColumn(GetString(payload, "column"));
        if (column is null)
            return;

        var name = GetString(payload, "name").Trim();
        if (name.Length == 0)
        {
            SendError("A column needs a name.");
            return;
        }

        if (string.Equals(name, column.Name, StringComparison.Ordinal))
            return;

        if (sheet.GetColumn(name) is not null)
        {
            SendError($"The table already has a column named '{name}'.");
            return;
        }

        var index = sheet.Columns.IndexOf(column);
        var command = BuildColumnCommand(
            sheet, new[] { new RenameColumnOp(column.Name, name) }, $"Rename Column '{column.Name}'");
        if (command is null)
            return;

        _document.Commands.Execute(command);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, index);
        SendToast(DescribeColumnEdit($"Renamed the column to '{name}'.", sheet, name), "info");
    }

    private void ChangeColumnType(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var column = sheet.GetColumn(GetString(payload, "column"));
        if (column is null)
            return;

        if (!TryDecodeType(GetString(payload, "type"), out var type))
            return;

        var index = sheet.Columns.IndexOf(column);

        if (!TryReadSubColumns(payload, type, column.Name, out var subColumns))
            return;

        var spec = BuildSpec(
            column.Name,
            type,
            column.Optional,
            new ColumnOptions(column.Display, column.Kind, column.DefaultValue),
            subColumns);

        if (!ConfirmSharedLoss(payload, sheet, spec, "changeColumnType"))
            return;

        // Converting an existing column to List or Properties needs the hidden sub-table
        // that goes with it; ColumnOps creates it as part of the same undo step.
        var command = BuildColumnCommand(
            sheet,
            new[] { new RetypeColumnOp(column.Name, spec) },
            $"Change '{column.Name}' to {type.TypeName}");
        if (command is null)
            return;

        _document.Commands.Execute(command);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, index);
        SendToast(DescribeColumnEdit($"'{column.Name}' is now a {type.TypeName} column.", sheet, column.Name), "info");
    }

    private void MoveColumn(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var column = sheet.GetColumn(GetString(payload, "column"));
        if (column is null)
            return;

        var from = sheet.Columns.IndexOf(column);
        var to = from + GetInt(payload, "delta");
        if (to < 0 || to >= sheet.Columns.Count || to == from)
            return;

        var command = BuildColumnCommand(sheet, new[] { new MoveColumnOp(column.Name, to) }, "Move Column");
        if (command is null)
            return;

        _document.Commands.Execute(command);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, to);
    }

    private void DeleteColumn(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var column = sheet.GetColumn(GetString(payload, "column"));
        if (column is null)
            return;

        // The identifier column can be removed like any other. A table without one has
        // no key, so references to it stop resolving — that is the author's call. The
        // only refusal left is the last column, because a table with no columns cannot
        // be edited at all; the message says what to do about it.
        if (sheet.Columns.Count <= 1)
        {
            SendError($"'{sheet.Name}' would be left with no columns. Add another column first, then delete '{column.Name}'.");
            return;
        }

        // Deleting a column other tables share deletes their values too, so it is
        // confirmed a second time with the count.
        if (!ConfirmSharedDelete(payload, sheet, column.Name))
            return;

        var index = sheet.Columns.IndexOf(column);
        var command = BuildColumnCommand(
            sheet, new[] { new DeleteColumnOp(column.Name) }, $"Delete Column '{column.Name}'");
        if (command is null)
        {
            SendError($"'{sheet.Name}' would be left with no columns. Add another column first, then delete '{column.Name}'.");
            return;
        }

        _document.Commands.Execute(command);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, Math.Max(0, index - 1));
        SendToast(DescribeColumnEdit($"Deleted column '{column.Name}'.", sheet, column.Name), "info");
    }

    // ---------------------------------------------------------------- structured values

    /// <summary>
    /// Sends the content of a List or Properties cell so the interface can edit it as a
    /// small table. Only the scalar columns of the sub-sheet are exposed; nested columns
    /// are shown read-only, which keeps the editor predictable.
    /// </summary>
    private void OpenSubValue(JsonElement payload)
    {
        if (_document is null || _formatter is null)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var rowIndex = GetInt(payload, "row");
        if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
            return;

        var column = sheet.GetColumn(GetString(payload, "column"));
        if (column is null)
            return;

        if (column.Type is not (ColumnType.List or ColumnType.Properties))
        {
            SendError("Only list and properties columns have a structured editor.");
            return;
        }

        var sub = sheet.GetSub(column);
        if (sub is null)
        {
            SendError($"The column '{column.Name}' has no sub-table to edit.");
            return;
        }

        var columns = new JsonArray();
        foreach (var subColumn in sub.Columns)
        {
            if (!IsStructured(subColumn.Type))
                continue;

            var node = new JsonObject
            {
                ["name"] = subColumn.Name,
                ["type"] = subColumn.Type.TypeName,
                ["typeStr"] = subColumn.TypeStr ?? ColumnTypeCodec.Encode(subColumn.Type),
                ["optional"] = subColumn.Optional,
            };

            switch (subColumn.Type)
            {
                case ColumnType.Enum e:
                    node["values"] = new JsonArray(e.Values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                    node["enumStr"] = subColumn.EnumAsString;
                    break;

                case ColumnType.Flags f:
                    node["values"] = new JsonArray(f.Values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                    break;

                // A reference inside a list is a picker, not a text field: the panel needs
                // the rows of the target table to offer them.
                case ColumnType.Ref r:
                {
                    var options = new JsonArray();
                    var target = _document!.Database.GetSheet(r.Sheet);
                    var index = target is null ? null : _document.Index.For(target);
                    if (target is not null && index is not null)
                    {
                        foreach (var targetRow in target.Lines)
                        {
                            var id = targetRow.GetString(target.IdColumn?.Name ?? string.Empty) ?? string.Empty;
                            if (id.Length == 0)
                                continue;
                            options.Add(new JsonObject
                            {
                                ["id"] = id,
                                ["display"] = index.GetDisplay(targetRow),
                            });
                        }
                    }
                    node["options"] = options;
                    break;
                }
            }

            columns.Add(node);
        }

        var value = sheet.Lines[rowIndex].Get(column.Name);
        var rows = new JsonArray();

        if (column.Type is ColumnType.List)
        {
            if (value is CdbValue.Arr list)
            {
                foreach (var item in list.Items)
                {
                    if (item is CdbValue.Obj obj)
                        rows.Add(BuildSubRow(sub, obj.Value, columns));
                }
            }
        }
        else if (value is CdbValue.Obj props)
        {
            rows.Add(BuildSubRow(sub, props.Value, columns));
        }

        Send(new JsonObject
        {
            ["type"] = "subValue",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheet.Name,
                ["row"] = rowIndex,
                ["column"] = column.Name,
                ["kind"] = column.Type is ColumnType.List ? "list" : "props",
                ["columns"] = columns,
                ["rows"] = rows,
            },
        });
    }

    private JsonArray BuildSubRow(SheetData sub, CdbObject row, JsonArray columns)
    {
        var cells = new JsonArray();
        foreach (var node in columns)
        {
            var name = node?["name"]?.GetValue<string>() ?? string.Empty;
            var subColumn = sub.GetColumn(name);
            var value = subColumn is null ? null : row.Get(name);

            // `t` is what the grid would show, `v` is the value the editor works with:
            // an index for an enum, true/false for a flag, the id for a reference.
            cells.Add(new JsonObject
            {
                ["t"] = subColumn is null ? string.Empty : FormatSubCell(sub, subColumn, row, value),
                ["v"] = RawSubValue(value),
            });
        }
        return cells;
    }

    private string FormatSubCell(SheetData sub, Column column, CdbObject row, CdbValue? value)
    {
        if (value is null)
            return string.Empty;

        return _formatter!.Format(sub, column, row, value).Text;
    }

    private static string RawSubValue(CdbValue? value) => value switch
    {
        null or CdbValue.Null => string.Empty,
        CdbValue.Str s => s.Value,
        CdbValue.Bool b => b.Value ? "true" : "false",
        CdbValue.Number n => CdbNumber.Format(n.Value),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool IsStructured(ColumnType type) =>
        type is ColumnType.Text or ColumnType.Id or ColumnType.Int or ColumnType.Float
            or ColumnType.Bool or ColumnType.Enum or ColumnType.Flags or ColumnType.Color
            or ColumnType.Image or ColumnType.File or ColumnType.Guid or ColumnType.Ref;

    /// <summary>Writes the edited rows back into a List or Properties cell as one undo step.</summary>
    private void SetSubValue(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var rowIndex = GetInt(payload, "row");
        if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
            return;

        var column = sheet.GetColumn(GetString(payload, "column"));
        if (column is null)
            return;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only.");
            return;
        }

        var sub = sheet.GetSub(column);
        if (sub is null)
            return;

        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("rows", out var rowsElement) ||
            rowsElement.ValueKind != JsonValueKind.Array)
            return;

        // The panel writes back while the author is still typing, so one half-filled row
        // must not reject the whole edit: an empty field is simply left out, and a value
        // that cannot be parsed keeps its previous one and is reported once at the end.
        var built = new List<CdbValue>();
        string? problem = null;

        foreach (var rowElement in rowsElement.EnumerateArray())
        {
            var obj = new CdbObject();
            var index = 0;
            foreach (var cellElement in rowElement.EnumerateArray())
            {
                var subColumn = index < sub.Columns.Count ? sub.Columns[index] : null;
                index++;
                if (subColumn is null || !IsStructured(subColumn.Type))
                    continue;

                var text = cellElement.ValueKind == JsonValueKind.String
                    ? cellElement.GetString() ?? string.Empty
                    : cellElement.ToString();

                // An empty numeric, enum or reference field is "not filled in yet".
                if (text.Length == 0 && subColumn.Type is not (
                        ColumnType.Text or ColumnType.Image or ColumnType.File or
                        ColumnType.Guid or ColumnType.Formula))
                    continue;

                try
                {
                    var parsed = ValueCodec.ParseText(subColumn, text);
                    if (parsed is not null)
                        obj.Set(subColumn.Name, parsed);
                }
                catch (CdbValidationException ex)
                {
                    problem ??= $"{subColumn.Name}: {ex.Message}";
                }
            }
            built.Add(new CdbValue.Obj(obj));
        }

        if (problem is not null)
            SendToast($"Some values were kept as they were — {problem}", "warning");

        CdbValue after = column.Type is ColumnType.List
            ? new CdbValue.Arr(built)
            : built.Count > 0 ? built[0] : new CdbValue.Obj(new CdbObject());

        var row = sheet.Lines[rowIndex];
        var command = new CellEditCommand(row, column.Name, after, $"Edit '{column.Name}'")
        {
            CursorBefore = CursorResolver.CreateAnchor(sheet, rowIndex, sheet.Columns.IndexOf(column)),
            CursorAfter = CursorResolver.CreateAnchor(sheet, rowIndex, sheet.Columns.IndexOf(column)),
        };

        _document.Commands.Execute(command);
        SendRowUpdated(sheet, rowIndex);
        SendDirty();
    }

    // ---------------------------------------------------------------- tables

    /// <summary>Table names follow the same identifier rule as column names.</summary>
    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
            return false;

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private void AddSheet(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var name = GetString(payload, "name").Trim();
        if (!IsValidName(name))
        {
            SendError("A table name must start with a letter or '_' and contain only letters, digits and '_'.");
            return;
        }

        if (_document.Database.GetSheet(name) is not null)
        {
            SendError($"A table named '{name}' already exists.");
            return;
        }

        var index = _document.Database.Sheets.Count;

        // A table created from a template starts with the template's columns and follows
        // it from then on; both happen in one undo step.
        if (GetString(payload, "template").Length > 0)
        {
            AddSheetFromTemplate(payload, name);
            return;
        }

        var command = new SheetInsertCommand(_document.Database, index, name);
        _document.Commands.Execute(command);

        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshAfterStructuralChange(name, 0, 0);
        SendToast($"Added table '{name}'.", "info");
    }

    private void RenameSheet(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var oldName = GetString(payload, "sheet");
        var sheet = _document.Database.GetSheet(oldName);
        if (sheet is null)
            return;

        var name = GetString(payload, "name").Trim();
        if (!IsValidName(name))
        {
            SendError("A table name must start with a letter or '_' and contain only letters, digits and '_'.");
            return;
        }

        if (string.Equals(name, oldName, StringComparison.Ordinal))
            return;

        if (_document.Database.GetSheet(name) is not null)
        {
            SendError($"A table named '{name}' already exists.");
            return;
        }

        _document.Commands.Execute(new SheetRenameCommand(_document.Database, oldName, name));
        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshAfterStructuralChange(name, 0, 0);
        SendToast($"Renamed the table to '{name}'.", "info");
    }

    private void DeleteSheet(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var name = GetString(payload, "sheet");
        if (_document.Database.GetSheet(name) is null)
            return;

        if (_document.Database.VisibleSheets.Count() <= 1)
        {
            SendError("A database must keep at least one table.");
            return;
        }

        _document.Commands.Execute(new SheetDeleteCommand(_document.Database, name));
        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();

        var next = _document.Database.VisibleSheets.FirstOrDefault()?.Name ?? string.Empty;
        RefreshAfterStructuralChange(next, 0, 0);
        SendToast($"Deleted table '{name}'.", "info");
    }

    // ---------------------------------------------------------------- sheet order and derived fields

    /// <summary>Moves a table one position up or down, sub-sheets included (S04).</summary>
    private void MoveSheet(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var direction = GetInt(payload, "direction") < 0 ? -1 : 1;

        try
        {
            _document.Commands.Execute(new SheetMoveCommand(_document.Database, sheet, direction));
        }
        catch (InvalidOperationException ex)
        {
            SendToast(ex.Message, "info");
            return;
        }

        RefreshKeepingCursor(sheet.Name);
    }

    /// <summary>Turns the generated <c>index</c> or <c>group</c> field on or off (S05 / S06).</summary>
    private void ToggleSheetFeature(JsonElement payload, SheetFeature feature)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var enabled = GetBool(payload, "enabled");

        SheetFeatureCommand command;
        try
        {
            command = new SheetFeatureCommand(sheet, feature, enabled);
        }
        catch (InvalidOperationException ex)
        {
            SendError(ex.Message);
            return;
        }

        var key = feature == SheetFeature.Index ? DerivedFields.IndexKey : DerivedFields.GroupKey;

        if (command.IsNoOp)
            return;

        _document.Commands.Execute(command);
        RefreshKeepingCursor(sheet.Name);

        if (!enabled)
            SendToast($"Removed the {key} field from '{sheet.Name}'.", "info");
        else if (feature == SheetFeature.Group && sheet.Props.SeparatorTitles is null)
            SendToast(
                $"'{sheet.Name}' has no separator titles yet, so the group field is only written once a separator is named.",
                "info");
        else
            SendToast($"'{sheet.Name}' now writes the {key} field on save.", "info");
    }

    // ---------------------------------------------------------------- display column

    /// <summary>
    /// Chooses the column used to label rows of the referenced table. The property lives
    /// on the target table, not on the reference column, which is how PalaceDB stores it.
    /// </summary>
    private void SetDisplayColumn(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var columnName = GetString(payload, "column");
        if (columnName.Length > 0 && sheet.GetColumn(columnName) is null)
        {
            SendError($"The table '{sheet.Name}' has no column named '{columnName}'.");
            return;
        }

        var command = new DisplayColumnCommand(sheet, columnName);
        _document.Commands.Execute(command);
        RefreshAfterStructuralChange(_currentSheet ?? sheet.Name, 0, 0);
        SendToast(
            columnName.Length > 0
                ? $"'{sheet.Name}' now displays '{columnName}'."
                : $"'{sheet.Name}' now displays its identifier.",
            "info");
    }

    /// <summary>
    /// Chooses the column holding the icon of a row, shown next to the label in reference
    /// dropdowns (C06). Like the display column it belongs to the referenced table.
    /// </summary>
    private void SetDisplayIcon(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var columnName = GetString(payload, "column");
        if (columnName.Length > 0 && sheet.GetColumn(columnName) is null)
        {
            SendError($"The table '{sheet.Name}' has no column named '{columnName}'.");
            return;
        }

        var command = new DisplayIconCommand(sheet, columnName);
        if (command.IsNoOp)
            return;

        _document.Commands.Execute(command);
        RefreshAfterStructuralChange(_currentSheet ?? sheet.Name, 0, 0);
        SendToast(
            columnName.Length > 0
                ? $"'{sheet.Name}' now uses '{columnName}' as its icon."
                : $"'{sheet.Name}' no longer shows an icon.",
            "info");
    }

    /// <summary>
    /// Marks a text column as localizable, which is what the localization export collects
    /// (F08). The property lives in the column definition, so it round-trips through the
    /// .cdb file like every other column setting.
    /// </summary>
    private void SetColumnKind(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        var column = sheet?.GetColumn(GetString(payload, "column"));
        if (sheet is null || column is null)
            return;

        if (column.Type is not (ColumnType.Text or ColumnType.Id))
        {
            SendError($"Only text columns can be localizable ('{column.Name}' is {column.Type.TypeName}).");
            return;
        }

        var localizable = GetBool(payload, "localizable");
        var spec = BuildSpec(
            column.Name,
            column.Type,
            column.Optional,
            new ColumnOptions(column.Display, localizable ? ColumnKind.Localizable : ColumnKind.None, column.DefaultValue),
            null);

        var command = BuildColumnCommand(
            sheet, new[] { new OptionsColumnOp(column.Name, spec) }, "Localizable");
        if (command is null)
            return;

        var index = sheet.Columns.IndexOf(column);
        _document.Commands.Execute(command);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, index);
        SendToast(
            localizable
                ? $"'{column.Name}' is now collected by the localization export."
                : $"'{column.Name}' is no longer exported.",
            "info");
    }

    /// <summary>
    /// Changes the optional fields of a column in one undoable step (C02): required or
    /// optional, default value, display format and the localizable flag.
    /// <para>
    /// The flag only says whether a value may be missing; switching it never deletes or
    /// invents values, which is why no rows are touched here.
    /// </para>
    /// </summary>
    private void SetColumnOptions(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        var column = sheet?.GetColumn(GetString(payload, "column"));
        if (sheet is null || column is null)
            return;

        var optional = GetBool(payload, "optional");
        if (!TryReadColumnOptions(payload, column.Type, optional, out var options))
            return;

        var spec = BuildSpec(column.Name, column.Type, optional, options, null);
        var command = BuildColumnCommand(
            sheet, new[] { new OptionsColumnOp(column.Name, spec) }, $"Options of '{column.Name}'");
        if (command is null)
            return;

        var index = sheet.Columns.IndexOf(column);
        _document.Commands.Execute(command);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, index);
        SendToast(DescribeColumnEdit($"Updated the options of '{column.Name}'.", sheet, column.Name), "info");
    }

    /// <summary>
    /// Reads the optional column fields shared by "add column" and "column options".
    /// Values that do not fit the column type are dropped rather than reported, so a
    /// dialog that keeps a stale field around cannot fail the whole edit.
    /// </summary>
    private bool TryReadColumnOptions(
        JsonElement payload,
        ColumnType type,
        bool optional,
        out ColumnOptions options)
    {
        options = ColumnOptions.None;

        var display = GetString(payload, "display") == "percent" &&
                      type is ColumnType.Int or ColumnType.Float
            ? DisplayType.Percent
            : DisplayType.Default;

        var kind = GetBool(payload, "localizable") &&
                   type is ColumnType.Text or ColumnType.Id
            ? ColumnKind.Localizable
            : ColumnKind.None;

        CdbValue? defaultValue = null;
        var text = GetString(payload, "default");
        if (text.Length > 0)
        {
            try
            {
                // Structured columns are written as JSON in the dialog, everything else
                // is parsed with the same rules as a cell.
                defaultValue = IsJsonColumn(type)
                    ? ValueCodec.ParseJson(text)
                    : ValueCodec.ParseDefaultValue(type, optional, text);
            }
            catch (CdbValidationException ex)
            {
                SendError($"Default value: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                SendError($"Default value: {ex.Message}");
                return false;
            }
        }

        options = new ColumnOptions(display, kind, defaultValue);
        return true;
    }

    /// <summary>Column types whose values are JSON documents rather than scalars.</summary>
    private static bool IsJsonColumn(ColumnType type) =>
        type is ColumnType.List or ColumnType.Properties or ColumnType.Polymorph
            or ColumnType.Dynamic or ColumnType.Gradient or ColumnType.Curve;

    /// <summary>The default value of a column as editable text, or null when it has none.</summary>
    private static string? DefaultTextOf(CdbValue? value) => value switch
    {
        null or CdbValue.Null => null,
        CdbValue.Arr or CdbValue.Obj => CdbWriter.WriteValue(value),
        _ => value.ToString(),
    };

    // ---------------------------------------------------------------- localization export (F08)
    /// <summary>
    /// Writes the localizable text of the document to an XML file. The original PalaceDB
    /// prefixes the file with a byte order mark so older translation tools detect UTF-8.
    /// </summary>
    private void ExportLocalized(JsonElement payload)
    {
        if (_document is null)
            return;

        if (!LocalizationExporter.HasLocalizableText(_document.Database))
        {
            SendError(
                "This database has no localizable columns. " +
                "Right-click a text column header and choose 'Localizable' first.");
            return;
        }

        var target = AutomationTarget(payload);
        if (target is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export localized texts",
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                DefaultExt = ".xml",
                FileName = Path.GetFileNameWithoutExtension(_document.Path) + ".xml",
            };

            if (dialog.ShowDialog(_window) != true)
                return;

            target = dialog.FileName;
        }

        try
        {
            var export = LocalizationExporter.Export(_document.Database);

            // AtomicFile writes UTF-8 without a BOM, which is right for .cdb but not for
            // the export; prefixing the character produces the BOM bytes.
            AtomicFile.WriteText(target, LocalizationExporter.Bom + export.Xml);

            SendToast(
                $"Exported {export.Count} text{(export.Count == 1 ? string.Empty : "s")} to " +
                Path.GetFileName(target) + ".",
                "info");
        }
        catch (Exception ex)
        {
            SendError($"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A destination supplied by an automated test. File dialogs block the DevTools
    /// driver, so the tests pass the path in the message instead; real users never can.
    /// </summary>
    private static string? AutomationTarget(JsonElement payload)
    {
        if (!Automation || payload.ValueKind != JsonValueKind.Object)
            return null;

        return payload.TryGetProperty("path", out var element) &&
               element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    /// <summary>
    /// The file list of a simulated drop. Only used by the automated tests, which cannot
    /// produce a real drag over the DevTools protocol.
    /// </summary>
    private static string[] DroppedFiles(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("files", out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    // ---------------------------------------------------------------- images
    /// <summary>
    /// Turns stored image paths into data URLs so the grid can draw thumbnails and
    /// reference dropdowns can draw icons. Paths are resolved against the folder of the
    /// database, which is what PalaceDB's relative paths assume.
    /// </summary>
    private void ResolveImages(JsonElement payload)
    {
        var resolver = _images;
        var entries = new JsonArray();

        if (resolver is not null &&
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("paths", out var paths) &&
            paths.ValueKind == JsonValueKind.Array)
        {
            var requested = new List<string>();
            foreach (var element in paths.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                    requested.Add(element.GetString() ?? string.Empty);
            }

            foreach (var result in resolver.ResolveMany(requested))
            {
                entries.Add(new JsonObject
                {
                    ["path"] = result.Path,
                    ["url"] = result.DataUrl,
                    ["error"] = result.IsImage ? null : ImageResolver.Describe(result.Failure),
                });
            }
        }

        Send(new JsonObject
        {
            ["type"] = "images",
            ["payload"] = new JsonObject { ["entries"] = entries },
        });
    }

    // ---------------------------------------------------------------- separators

    private void ToggleSeparator(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var row = GetInt(payload, "row");
        if (row < 0 || row > sheet.Lines.Count)
            return;

        var command = new SeparatorToggleCommand(sheet, row);
        _document.Commands.Execute(command);
        RefreshAfterStructuralChange(sheet.Name, Math.Min(row, Math.Max(0, sheet.Lines.Count - 1)), 0);
        SendToast(command.Added ? "Separator added." : "Separator removed.", "info");
    }

    private void SetSeparatorTitle(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var row = GetInt(payload, "row");
        if (row < 0 || row > sheet.Lines.Count)
            return;

        var title = GetString(payload, "title");
        _document.Commands.Execute(new SeparatorTitleCommand(sheet, row, title));
        RefreshAfterStructuralChange(sheet.Name, Math.Min(row, Math.Max(0, sheet.Lines.Count - 1)), 0);
    }

    /// <summary>Decodes a type string sent by the interface, reporting failures to the user.</summary>
    private bool TryDecodeType(string typeStr, out ColumnType type)
    {
        try
        {
            type = ColumnTypeCodec.Decode(string.IsNullOrWhiteSpace(typeStr) ? "1" : typeStr);
            return true;
        }
        catch (CdbFormatException ex)
        {
            SendError(ex.Message);
            type = new ColumnType.Text();
            return false;
        }
    }

    /// <summary>
    /// Reads the field list of a List / Properties column. Only those types need one, so
    /// every other request passes straight through.
    /// </summary>
    private bool TryReadSubColumns(
        JsonElement payload,
        ColumnType type,
        string columnName,
        out IReadOnlyList<SubColumnSpec>? subColumns)
    {
        subColumns = null;

        if (type is not (ColumnType.List or ColumnType.Properties or ColumnType.Polymorph))
            return true;

        var specs = new List<SubColumnSpec>();
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("sub", out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var name = GetString(item, "name").Trim();
                if (name.Length == 0)
                    continue;

                if (specs.Any(spec => string.Equals(spec.Name, name, StringComparison.Ordinal)))
                {
                    SendError($"'{name}' is used twice in the field list.");
                    return false;
                }

                if (!TryDecodeType(GetString(item, "type"), out var subType))
                    return false;

                // `id` is the name the field had when the dialog opened. It is what makes a
                // rename keep the values already stored under the old name.
                var original = GetString(item, "id").Trim();
                specs.Add(new SubColumnSpec(
                    name, subType, original.Length == 0 ? null : original));
            }
        }

        if (specs.Count == 0)
        {
            SendError($"'{columnName}' needs at least one field.");
            return false;
        }

        subColumns = specs;
        return true;
    }

    private static List<int> ReadIntArray(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Array)
            return new List<int>();

        return element.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetInt32())
            .ToList();
    }

    private static List<string> ReadStringArray(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return element.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? string.Empty)
            .ToList();
    }

    /// <summary>Stores a column width in the side-car file and refreshes the grid.</summary>
    /// <summary>
    /// Stores the palette of the open file in its side-car. The palette is presentation
    /// state of one document, so it travels with the file instead of living in the
    /// global settings, which is what lets two databases be told apart at a glance.
    /// </summary>
    private void SetFileScheme(JsonElement payload)
    {
        if (_document is null)
            return;

        var scheme = GetString(payload, "scheme");
        var changed = _document.Meta.SetScheme(scheme);
        if (changed)
            _document.SaveMeta();

        Send(new JsonObject
        {
            ["type"] = "fileScheme",
            ["payload"] = new JsonObject
            {
                ["scheme"] = _document.Meta.Scheme,
                ["changed"] = changed,
            },
        });
    }

    private void SetColumnWidth(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheetName = GetString(payload, "sheet");
        var columnName = GetString(payload, "column");
        var width = GetDouble(payload, "width");
        if (width <= 0 || _document.Database.GetSheet(sheetName)?.GetColumn(columnName) is null)
            return;

        _document.Meta.SetColumnWidth(sheetName, columnName, width);
        _document.SaveMeta();

        Send(new JsonObject
        {
            ["type"] = "columnWidth",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheetName,
                ["column"] = columnName,
                ["width"] = Math.Round(width, 1),
            },
        });
    }

    /// <summary>
    /// Applies a background colour to the selection. The scope decides whether the
    /// colour belongs to cells, whole rows or whole columns; an empty colour clears it.
    /// </summary>
    private void SetStyle(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheetName = GetString(payload, "sheet");
        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null)
            return;

        var kind = GetString(payload, "kind");
        var color = GetString(payload, "color");
        var rows = ReadIntArray(payload, "rows");
        var columns = ReadStringArray(payload, "columns");
        var normalized = string.IsNullOrEmpty(color) ? null : color;

        switch (kind)
        {
            case "cell":
                foreach (var rowIndex in rows)
                {
                    if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
                        continue;
                    var key = RowKey(sheet, rowIndex);
                    foreach (var columnName in columns)
                        _document.Meta.SetCellBackground(sheetName, key, columnName, normalized);
                }
                break;

            case "row":
                foreach (var rowIndex in rows)
                {
                    if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
                        continue;
                    _document.Meta.SetRowBackground(sheetName, RowKey(sheet, rowIndex), normalized);
                }
                break;

            case "column":
                foreach (var columnName in columns)
                    _document.Meta.SetColumnBackground(sheetName, columnName, normalized);
                break;

            default:
                return;
        }

        _document.SaveMeta();
        Send(new JsonObject
        {
            ["type"] = "invalidate",
            ["payload"] = new JsonObject { ["sheet"] = sheetName },
        });
    }

    /// <summary>Stable key for row-keyed styles: the identifier when available, otherwise the index.</summary>
    private static string RowKey(SheetData sheet, int index)
    {
        var idColumn = sheet.IdColumn;
        if (idColumn is null)
            return index.ToString();

        var id = sheet.Lines[index].GetString(idColumn.Name);
        return string.IsNullOrEmpty(id) ? index.ToString() : id;
    }

    private static double GetDouble(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private void UndoOrRedo(bool undo)
    {
        if (_document is null)
            return;

        var command = undo ? _document.Commands.Undo() : _document.Commands.Redo();
        if (command is null)
            return;

        // A template definition follows the tables, so undoing a column edit has to write
        // the side-car again as well.
        _document.SaveMeta();

        // Structure may have changed, so the whole document description is refreshed.
        SendDocument();
        Send(new JsonObject
        {
            ["type"] = "invalidate",
            ["payload"] = new JsonObject
            {
                ["sheet"] = _currentSheet ?? string.Empty,
                ["cursor"] = CursorToJson(undo ? command.CursorBefore : command.CursorAfter),
                // The cursor moves back to the change, but the page must not scroll to it:
                // undo and redo have to leave the view exactly where it is.
                ["keepView"] = true,
            },
        });
        SendDirty();
    }

    private static JsonObject? CursorToJson(CursorAnchor? anchor)
    {
        if (anchor is null)
            return null;

        return new JsonObject
        {
            ["sheet"] = anchor.SheetName,
            ["row"] = anchor.RowIndex,
            ["column"] = anchor.ColumnIndex,
            ["columnId"] = anchor.ColumnId,
            ["rowId"] = anchor.RowId,
        };
    }

    private void SendDirty()
    {
        Send(new JsonObject
        {
            ["type"] = "state",
            ["payload"] = new JsonObject
            {
                ["dirty"] = _document?.IsDirty ?? false,
                ["canUndo"] = _document?.Commands.CanUndo ?? false,
                ["canRedo"] = _document?.Commands.CanRedo ?? false,
                ["undoLabel"] = _document?.Commands.UndoLabel,
                ["redoLabel"] = _document?.Commands.RedoLabel,
            },
        });
    }

    private void SendCellRejected(string sheet, int row, string column, string message)
    {
        Send(new JsonObject
        {
            ["type"] = "cellRejected",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheet,
                ["row"] = row,
                ["column"] = column,
                ["message"] = message,
            },
        });
    }

    private void SendRowUpdated(SheetData sheet, int rowIndex)
    {
        if (_document is null || _formatter is null)
            return;
        if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
            return;

        var row = sheet.Lines[rowIndex];
        var cells = new JsonArray();
        foreach (var column in sheet.Columns)
        {
            var display = _formatter.Format(sheet, column, row, row.Get(column.Name));
            cells.Add(new JsonObject
            {
                ["t"] = display.Text,
                ["s"] = StateName(display.State),
                ["d"] = display.Detail,
            });
        }

        var idColumn = sheet.IdColumn;

        Send(new JsonObject
        {
            ["type"] = "rowUpdated",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheet.Name,
                ["row"] = new JsonObject
                {
                    ["i"] = rowIndex,
                    ["key"] = idColumn is null ? rowIndex.ToString() : row.GetString(idColumn.Name) ?? string.Empty,
                    ["cells"] = cells,
                },
            },
        });

        // An edited cell can introduce or clear a duplicate id, so the status line is
        // refreshed with every row update.
        SendFirstIssue();
    }

    private static bool ValuesEqual(CdbValue? a, CdbValue? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Equals(b);
    }

    // ---------------------------------------------------------------- outgoing

    /// <summary>
    /// Describes the build for the About dialog (F09) and tells the page that opening files
    /// is supported. The runtime details are read here rather than hard coded so the dialog
    /// always shows what is actually running.
    /// </summary>
    private void SendAppInfo()
    {
        Send(new JsonObject
        {
            ["type"] = "appInfo",
            ["payload"] = new JsonObject
            {
                ["version"] = Version(),
                ["canOpen"] = true,
                ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["webView"] = SafeBrowserVersion(),
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["dataFolder"] = App.DataFolder,
                ["format"] = "PalaceDB .cdb (JSON, UTF-8, tab indented)",
            },
        });

        SendRecentFiles();
    }

    private static string Version()
    {
        var assembly = typeof(EditorHost).Assembly.GetName().Version;
        return assembly is null
            ? "1.0"
            : $"{assembly.Major}.{assembly.Minor}.{Math.Max(0, assembly.Build)}";
    }

    /// <summary>The WebView2 runtime version, or a placeholder when it cannot be read.</summary>
    private string SafeBrowserVersion()
    {
        try
        {
            return _web.Environment.BrowserVersionString;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Pushes the full document description: sheets, columns and counts.</summary>
    public void SendDocument()
    {
        if (_document is null)
        {
            Send(new JsonObject { ["type"] = "documentClosed" });
            _window.UpdateTitle(string.Empty, false);
            return;
        }

        var sheets = new JsonArray();
        foreach (var sheet in _document.Database.VisibleSheets)
        {
            var columns = new JsonArray();
            foreach (var column in sheet.Columns)
            {
                var info = new JsonObject
                {
                    ["name"] = column.Name,
                    ["type"] = column.Type.TypeName,
                    ["typeStr"] = column.TypeStr ?? ColumnTypeCodec.Encode(column.Type),
                    ["optional"] = column.Optional,
                    ["localizable"] = column.Kind == ColumnKind.Localizable,
                    ["display"] = column.Display == DisplayType.Percent ? "percent" : "default",
                    ["defaultValue"] = DefaultTextOf(column.DefaultValue),
                    ["width"] = _document.Meta.GetColumnWidth(sheet.Name, column.Name),
                    ["bg"] = _document.Meta.GetColumnBackground(sheet.Name, column.Name),
                };

                switch (column.Type)
                {
                    case ColumnType.Enum e:
                        info["values"] = new JsonArray(e.Values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                        info["enumStr"] = column.EnumAsString;
                        break;
                    case ColumnType.Flags f:
                        info["values"] = new JsonArray(f.Values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                        break;
                    case ColumnType.Ref r:
                        info["refSheet"] = r.Sheet;
                        break;
                    case ColumnType.Custom c:
                        info["customType"] = c.TypeName2;
                        break;
                    case ColumnType.List or ColumnType.Properties or ColumnType.Polymorph:
                        info["subSheet"] = sheet.GetSub(column)?.Name;
                        break;
                }

                columns.Add(info);
            }

            var sheetObject = new JsonObject
            {
                ["name"] = sheet.Name,
                ["rowCount"] = sheet.Lines.Count,
                ["hasIndex"] = sheet.Props.HasIndex,
                ["hasGroup"] = sheet.Props.HasGroup,
                ["displayColumn"] = sheet.Props.DisplayColumn,
                ["displayIcon"] = sheet.Props.DisplayIcon,
                ["separators"] = new JsonArray(
                    sheet.GetSeparators().OrderBy(p => p.Index)
                        .Select(p => (JsonNode?)JsonValue.Create(p.Index)).ToArray()),
                ["separatorTitles"] = new JsonArray(
                    sheet.GetSeparators().OrderBy(p => p.Index)
                        .Select(p => (JsonNode?)JsonValue.Create(p.Title ?? string.Empty)).ToArray()),
                ["columns"] = columns,
            };

            // The table list shows a dot for a table that follows a template and says in
            // its tooltip which one, how big it is and whether this table still matches.
            if (TemplateService.TemplateOf(_document.Meta, sheet) is { } template)
            {
                var audit = TemplateService.Audit(sheet, template);
                sheetObject["template"] = template.Id;
                sheetObject["templateName"] = template.Name;
                sheetObject["templateColumns"] = template.Columns.Count;
                sheetObject["templateMembers"] = TemplateService.Members(_document.Database, _document.Meta, template.Id).Count;
                sheetObject["templateInSync"] = audit.IsInSync;
            }

            sheets.Add(sheetObject);
        }

        // Everything the "new table" and "table settings" dialogs need to offer a choice.
        var templates = new JsonArray();
        foreach (var entry in _document.Meta.Templates)
        {
            var members = TemplateService.Members(_document.Database, _document.Meta, entry.Key).Count;
            templates.Add(new JsonObject
            {
                ["id"] = entry.Key,
                ["name"] = entry.Value.Name,
                ["columns"] = entry.Value.Columns.Count,
                ["members"] = members,
            });
        }

        // Hidden sub-tables are not part of the table list, but the column dialog needs
        // to know the fields of an existing List or Properties column.
        var subSheets = new JsonArray();
        foreach (var hidden in _document.Database.Sheets)
        {
            if (!hidden.Props.Hide)
                continue;

            var fields = new JsonArray();
            foreach (var field in hidden.Columns)
            {
                fields.Add(new JsonObject
                {
                    ["name"] = field.Name,
                    ["type"] = field.Type.TypeName,
                    ["typeStr"] = field.TypeStr ?? ColumnTypeCodec.Encode(field.Type),
                });
            }

            subSheets.Add(new JsonObject { ["name"] = hidden.Name, ["columns"] = fields });
        }

        Send(new JsonObject
        {
            ["type"] = "document",
            ["payload"] = new JsonObject
            {
                ["path"] = _document.Path,
                ["name"] = _document.FileLabel,
                ["readOnly"] = _document.IsReadOnly,
                ["readOnlyReason"] = _document.ReadOnlyReason,
                ["dirty"] = _document.IsDirty,
                ["canUndo"] = _document.Commands.CanUndo,
                ["canRedo"] = _document.Commands.CanRedo,
                ["undoLabel"] = _document.Commands.UndoLabel,
                ["redoLabel"] = _document.Commands.RedoLabel,
                ["currentSheet"] = _currentSheet,
                ["scheme"] = _document.Meta.Scheme,
                ["sheets"] = sheets,
                ["subSheets"] = subSheets,
                ["templates"] = templates,
            },
        });

        _window.UpdateTitle(_document.FileLabel, _document.IsDirty);
        SendFirstIssue();
    }

    /// <summary>
    /// Reports the first validation problem of the current table so the status bar can
    /// name the table and the row. The grid only holds the rows it has loaded, so this
    /// is answered by the document rather than by the view.
    /// </summary>
    private void SendFirstIssue()
    {
        if (_document is null)
            return;

        var sheet = _currentSheet is null ? null : _document.Database.GetSheet(_currentSheet);
        var issue = sheet is null ? null : SheetValidator.FirstIssue(sheet, _document.Database, _document.Index);

        Send(new JsonObject
        {
            ["type"] = "firstIssue",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheet?.Name,
                ["row"] = issue?.Row,
                ["column"] = issue?.Column,
                ["detail"] = issue?.Detail,
            },
        });
    }

    /// <summary>Sends a window of rows, already formatted for display.</summary>
    public void SendRows(string sheetName, int start, int count)
    {
        if (_document is null || _formatter is null)
            return;

        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null)
        {
            SendError($"Unknown table '{sheetName}'.");
            return;
        }

        start = Math.Max(0, start);
        count = Math.Clamp(count, 0, 2000);

        var end = Math.Min(sheet.Lines.Count, start + count);
        var rows = new JsonArray();
        var idColumn = sheet.IdColumn;

        // Presentation state is resolved once per request, not once per cell.
        var table = _document.Meta.GetTable(sheet.Name);
        var columnIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in sheet.Columns)
        {
            var id = _document.Meta.Document.FindColumnId(sheet.Name, column.Name);
            if (id is not null)
                columnIds[column.Name] = id;
        }

        for (var i = start; i < end; i++)
        {
            var row = sheet.Lines[i];
            var rowKey = RowKey(sheet, i);
            var cells = new JsonArray();

            string? rowBackground = null;
            if (table is not null && table.RowStyles.TryGetValue(rowKey, out var rowStyle))
                rowBackground = rowStyle.Background;

            Dictionary<string, StyleEntry>? cellStyles = null;
            table?.CellStyles.TryGetValue(rowKey, out cellStyles);

            foreach (var column in sheet.Columns)
            {
                var value = row.Get(column.Name);
                var display = _formatter.Format(sheet, column, row, value);

                var cell = new JsonObject
                {
                    ["t"] = display.Text,
                    ["s"] = StateName(display.State),
                    ["d"] = display.Detail,
                };

                if (cellStyles is not null &&
                    columnIds.TryGetValue(column.Name, out var columnId) &&
                    cellStyles.TryGetValue(columnId, out var cellStyle) &&
                    cellStyle.Background is { } background)
                {
                    cell["bg"] = background;
                }

                cells.Add(cell);
            }

            var rowNode = new JsonObject
            {
                ["i"] = i,
                ["key"] = idColumn is null ? rowKey : row.GetString(idColumn.Name) ?? string.Empty,
                ["cells"] = cells,
            };

            if (rowBackground is not null)
                rowNode["bg"] = rowBackground;

            rows.Add(rowNode);
        }

        Send(new JsonObject
        {
            ["type"] = "rows",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheetName,
                ["start"] = start,
                ["total"] = sheet.Lines.Count,
                ["rows"] = rows,
            },
        });
    }

    private static string StateName(CellState state) => state switch
    {
        CellState.Ok => "ok",
        CellState.Empty => "empty",
        CellState.Missing => "missing",
        CellState.Duplicate => "duplicate",
        CellState.BadReference => "badref",
        CellState.NotFound => "notfound",
        _ => "error",
    };

    public void SendToast(string message, string level)
    {
        Send(new JsonObject
        {
            ["type"] = "toast",
            ["payload"] = new JsonObject { ["message"] = message, ["level"] = level },
        });
    }

    public void SendError(string message)
    {
        Send(new JsonObject
        {
            ["type"] = "error",
            ["payload"] = new JsonObject { ["message"] = message },
        });
    }

    private void Send(JsonNode node)
    {
        try
        {
            _web.PostWebMessageAsJson(node.ToJsonString());
        }
        catch (Exception)
        {
            // The page may already be gone while the window is closing.
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string GetString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static bool GetBool(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();
}
