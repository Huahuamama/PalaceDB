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

            case "search":
                SearchCells(payload);
                break;

            case "replaceOne":
                ReplaceOne(payload);
                break;

            case "replaceAll":
                ReplaceAllCells(payload);
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

            case "setSubFields":
                SetSubFields(payload);
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

            case "setColumnHidden":
                SetColumnHidden(payload);
                break;

            case "setGroupCollapsed":
                SetGroupCollapsed(payload);
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

            case "savePublicType":
                SavePublicType(payload);
                break;

            case "deletePublicType":
                DeletePublicType(payload);
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
