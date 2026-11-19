using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
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

/// <summary>Opening, saving, backups, external changes, merges and the recent file list.</summary>
public sealed partial class EditorHost
{

    // ---------------------------------------------------------------- recent files (F03)

    /// <summary>
    /// Opens an entry of the recent list. A remembered file that has since been deleted is
    /// dropped from the list instead of leaving a dead entry behind.
    /// <para>
    /// A recent file opens in a window of its own, so the database this window is showing
    /// stays exactly as it was. An empty window has nothing to protect and simply takes the
    /// file, and the file this window already shows needs no second window at all - two
    /// windows on one database would only make one of them read-only.
    /// </para>
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

        if (_document?.Path is { } current && SamePath(current, path))
        {
            SendToast($"'{Path.GetFileName(path)}' is already open in this window.", "info");
            return;
        }

        if (_document is null)
        {
            OpenDocument(path);
            return;
        }

        OpenInNewWindow(path);

        // The new window records the file itself once it is loaded; doing it here as well
        // keeps this window's menu in the right order straight away.
        RememberRecent(path);
    }

    /// <summary>
    /// Opens a database in a new window. The new window inherits the remembered geometry of
    /// its file, or a small offset from this window when it has none, so the two do not
    /// overlap exactly.
    /// </summary>
    private void OpenInNewWindow(string path)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            var window = new MainWindow();
            window.PlaceBeforeShow(MetaStore.ReadWindowState(path) ?? _window.CascadePlacement());
            window.OpenFileWhenReady(path);
            window.Show();
        });
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

            // A column edited outside the application can disagree with the public type it
            // follows; the definition wins, and the fix is an ordinary undoable step.
            RepairPublicTypes();

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
}
