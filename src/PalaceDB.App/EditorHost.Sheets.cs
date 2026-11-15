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

/// <summary>Tables, sheet order, derived index/group fields and separators.</summary>
public sealed partial class EditorHost
{

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
}
