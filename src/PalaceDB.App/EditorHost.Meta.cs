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

/// <summary>View state that lives in the side-car only: widths, hidden columns, colors, kinds.</summary>
public sealed partial class EditorHost
{

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
    /// Shows or hides columns. Hiding one is a way of looking at the table, not an edit,
    /// so it goes into the side-car and the document is re-sent with the new flag.
    /// </summary>
    private void SetColumnHidden(JsonElement payload)
    {
        // The state lives in the side-car, so a read-only document would take the change in
        // memory and lose it on the next open: refuse instead of pretending.
        if (_document is null || _document.IsReadOnly)
            return;

        var sheetName = GetString(payload, "sheet");
        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null)
            return;

        var hidden = GetBool(payload, "hidden");

        // "Show All Columns" hides nothing and shows everything: the flag is false and no
        // column is named, which would otherwise read as "hide nothing in particular".
        var all = GetBool(payload, "all");
        if (all)
        {
            foreach (var column in sheet.Columns)
                _document.Meta.SetColumnHidden(sheetName, column.Name, false);
        }
        else
        {
            var columnName = GetString(payload, "column");
            if (sheet.GetColumn(columnName) is null)
                return;

            // The last visible column stays: an empty table is not a view anyone asked for
            // and there would be no header left to bring the others back.
            var visible = sheet.Columns.Count(c => !_document.Meta.IsColumnHidden(sheetName, c.Name));
            if (hidden && visible <= 1 && !_document.Meta.IsColumnHidden(sheetName, columnName))
                return;

            _document.Meta.SetColumnHidden(sheetName, columnName, hidden);
        }

        _document.SaveMeta();
        SendDocument();
    }

    /// <summary>
    /// Collapses or expands one titled row group, named by its position among the titled
    /// headings. Stored per table in the side-car.
    /// </summary>
    private void SetGroupCollapsed(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheetName = GetString(payload, "sheet");
        if (_document.Database.GetSheet(sheetName) is null)
            return;

        _document.Meta.SetGroupCollapsed(sheetName, GetInt(payload, "index"), GetBool(payload, "collapsed"));
        _document.SaveMeta();
        SendDocument();
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
}
