using System.Globalization;
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

/// <summary>Building and sending the document, rows and notifications to the page.</summary>
public sealed partial class EditorHost
{

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

    /// <summary>The default value of a column as editable text, or null when it has none.</summary>
    private static string? DefaultTextOf(CdbValue? value) => value switch
    {
        null or CdbValue.Null => null,
        CdbValue.Arr or CdbValue.Obj => CdbWriter.WriteValue(value),
        _ => value.ToString(),
    };

    /// <summary>Stable key for row-keyed styles: the identifier when available, otherwise the index.</summary>
    private static string RowKey(SheetData sheet, int index)
    {
        var idColumn = sheet.IdColumn;
        if (idColumn is null)
            return index.ToString();

        var id = sheet.Lines[index].GetString(idColumn.Name);
        return string.IsNullOrEmpty(id) ? index.ToString() : id;
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

        // Same presentation state as the bulk load: a refresh must not drop the row's
        // background colour (or its per-cell fills) just because one cell changed.
        var table = _document.Meta.GetTable(sheet.Name);
        var columnIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in sheet.Columns)
        {
            var id = _document.Meta.Document.FindColumnId(sheet.Name, column.Name);
            if (id is not null)
                columnIds[column.Name] = id;
        }

        Send(new JsonObject
        {
            ["type"] = "rowUpdated",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheet.Name,
                ["row"] = BuildRowNode(sheet, rowIndex, table, columnIds),
            },
        });

        // An edited cell can introduce or clear a duplicate id, so the status line is
        // refreshed with every row update.
        SendFirstIssue();
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
                    ["publicType"] = _document.Meta.Document.GetColumnPublicType(sheet.Name, column.Name),
                    ["autoIncrement"] = _document.Meta.Document.IsAutoIncrement(sheet.Name, column.Name),
                    ["hidden"] = _document.Meta.IsColumnHidden(sheet.Name, column.Name),
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

            // Folded groups live in the side-car, keyed by the position of the heading among
            // the titled ones: a row number would move as soon as a row is inserted above
            // it, and the title is not unique.
            var collapsedGroups = new HashSet<string>(
                _document.Meta.GetCollapsedGroups(sheet.Name), StringComparer.Ordinal);
            var groups = sheet.Groups.TitledGroups();

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
                // The page folds groups and keeps rows inside them, so it is told which rows
                // belong to which heading instead of working it out from the divider rows.
                ["groups"] = new JsonArray(
                    groups.Select((group, position) => (JsonNode?)new JsonObject
                    {
                        ["title"] = group.Title,
                        ["start"] = group.Start,
                        ["count"] = group.Count,
                        ["collapsed"] = collapsedGroups.Contains(
                            position.ToString(CultureInfo.InvariantCulture)),
                    }).ToArray()),
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
                    ["publicType"] = _document.Meta.Document.GetColumnPublicType(hidden.Name, field.Name),
                });
            }

            subSheets.Add(new JsonObject { ["name"] = hidden.Name, ["columns"] = fields });
        }

        // The sidebar lists the public enum / flags types of this file, and the column
        // dialog needs the value lists to offer them as a type choice.
        var publicTypes = new JsonArray();
        foreach (var (name, definition) in _document.Meta.Document.PublicTypes)
        {
            var users = new JsonArray();
            foreach (var (table, column) in _document.Meta.Document.ColumnsBoundTo(name))
                users.Add(new JsonObject { ["sheet"] = table, ["column"] = column });

            publicTypes.Add(new JsonObject
            {
                ["name"] = name,
                ["kind"] = definition.IsFlags ? "flags" : "enum",
                ["values"] = new JsonArray(definition.Values
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["columns"] = users,
            });
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
                ["publicTypes"] = publicTypes,
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
            rows.Add(BuildRowNode(sheet, i, table, columnIds));

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

    /// <summary>
    /// Builds one display row, including the row and cell background colours stored in the
    /// side-car. Both the initial window load and the single-row refresh after an edit go
    /// through here: when the edit path built its own row it silently dropped <c>bg</c>, so
    /// a coloured row lost its fill the moment a cell was edited and only got it back after
    /// reloading the sheet.
    /// </summary>
    private JsonObject BuildRowNode(
        SheetData sheet,
        int index,
        TableMeta? table,
        Dictionary<string, string> columnIds)
    {
        var row = sheet.Lines[index];
        var rowKey = RowKey(sheet, index);
        var cells = new JsonArray();

        string? rowBackground = null;
        if (table is not null && table.RowStyles.TryGetValue(rowKey, out var rowStyle))
            rowBackground = rowStyle.Background;

        Dictionary<string, StyleEntry>? cellStyles = null;
        table?.CellStyles.TryGetValue(rowKey, out cellStyles);

        foreach (var column in sheet.Columns)
        {
            var value = row.Get(column.Name);
            var display = _formatter!.Format(sheet, column, row, value);

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

        var idColumn = sheet.IdColumn;
        var node = new JsonObject
        {
            ["i"] = index,
            ["key"] = idColumn is null ? rowKey : row.GetString(idColumn.Name) ?? string.Empty,
            ["cells"] = cells,
        };

        if (rowBackground is not null)
            node["bg"] = rowBackground;

        return node;
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
}
