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

/// <summary>Nested List and Properties values, edited through the sub-table.</summary>
public sealed partial class EditorHost
{

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
                ["publicType"] = _document.Meta.Document.GetColumnPublicType(sub.Name, subColumn.Name),
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

    /// <summary>
    /// Rewrites the field list of the sub-table behind a List or Properties column.
    /// <para>
    /// The header menu of the panel renames, retypes, reorders, inserts and removes one
    /// field at a time, but all of those are the same operation from the document's point
    /// of view - a new field list - so the host only needs this one message. It is also the
    /// only way to give a sub-field an enum or a flag its own values, which the panel's
    /// "Edit Column..." needs.
    /// </para>
    /// </summary>
    private void SetSubFields(JsonElement payload)
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
            !payload.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Array)
            return;

        var specs = new List<SubColumnSpec>();
        var bindings = new List<(string Name, string? PublicType)>();
        foreach (var item in fields.EnumerateArray())
        {
            var name = GetString(item, "name").Trim();
            if (name.Length == 0)
                continue;

            if (specs.Any(spec => string.Equals(spec.Name, name, StringComparison.Ordinal)))
            {
                SendError($"'{name}' is used twice in the field list.");
                return;
            }

            if (!TryDecodeType(GetString(item, "type"), out var type))
                return;

            // A field can follow a public enum / flags type, exactly like a column of a
            // real table: the definition decides the values, so the field's own type string
            // is overwritten with the shared one before the sub-table is rewritten.
            var publicTypeName = GetString(item, "publicType").Trim();
            if (publicTypeName.Length > 0)
            {
                var definition = _document.Meta.Document.GetPublicType(publicTypeName);
                if (definition is null)
                {
                    SendError($"The public type '{publicTypeName}' no longer exists.");
                    return;
                }
                type = PublicTypeService.ToColumnType(definition);
            }

            specs.Add(new SubColumnSpec(name, type));

            // Only a payload that says something about the binding may change it: a caller
            // that does not know about public types (a reorder, a delete) leaves it alone.
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("publicType", out _))
                bindings.Add((name, publicTypeName.Length == 0 ? null : publicTypeName));
        }

        if (specs.Count == 0)
        {
            SendError("A list needs at least one field.");
            return;
        }

        var command = new SubColumnsCommand(_document.Database, sub, sheet, column, specs);
        if (command.IsNoOp)
        {
            // The field list itself did not change, but the binding may have.
            if (RememberSubFieldPublicTypes(sub, bindings))
                CommitColumnEdit(sheet);
            SendDocument();
            return;
        }

        _document.Commands.Execute(command);
        RememberSubFieldPublicTypes(sub, bindings);
        CommitColumnEdit(sheet);

        // The field list lives in the document: the column dialog reads the fields of a
        // list from the hidden sub-tables, so a stale document would let that dialog apply
        // the old list and quietly drop the field that was just added. Sending the document
        // also refreshes every open panel against the new header.
        SendRowUpdated(sheet, rowIndex);
        SendDocument();
        SendDirty();
    }

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
}
