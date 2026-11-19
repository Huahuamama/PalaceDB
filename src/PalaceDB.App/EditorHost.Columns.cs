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
/// Column insertion, rename, retype, reorder and conversion.
/// <para>
/// Each handler reads as four steps: resolve the target, read what the dialog asked for,
/// build the operations, then run the shared tail. The tail is
/// <see cref="ExecuteColumnCommand"/> - the bookkeeping that follows a schema command is
/// order sensitive, and it lives in one place rather than in every handler.
/// </para>
/// </summary>
public sealed partial class EditorHost
{
    // ---------------------------------------------------------------- schema

    /// <summary>
    /// What a column edit wants the side-car to remember about the column. Passed as null
    /// by the edits that say nothing about it (move, delete), because writing it would
    /// clear a public type or an auto-increment flag they were never asked about.
    /// </summary>
    private readonly record struct ColumnBinding(string Column, string PublicType, bool AutoIncrement);

    /// <summary>
    /// Rewrites every value of a column in one step (requirement C07). The cursor is left
    /// where it is because a conversion never changes the shape of the table.
    /// </summary>
    private void ConvertColumn(JsonElement payload)
    {
        if (!TryResolveColumn(payload, out var sheet, out var column))
            return;

        var document = _document!;

        var name = GetString(payload, "conversion");
        if (!TryParseConversion(name, out var conversion))
        {
            SendError($"Unknown conversion '{name}'.");
            return;
        }

        ColumnConvertCommand command;
        try
        {
            command = new ColumnConvertCommand(document.Database, sheet, column, conversion);
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

        document.Commands.Execute(command);
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
        if (!TryResolveSheet(payload, out var sheet))
            return;

        if (!TryReadColumnName(payload, sheet, null, out var name))
            return;

        if (!TryReadColumnType(payload, sheet, null, out var type, out var publicTypeName, out var autoIncrement))
            return;

        // Requirements C08 / C09: the virtual index/group columns are taken by table props.
        if ((string.Equals(name, "index", StringComparison.OrdinalIgnoreCase) && sheet.Props.HasIndex)
            || (string.Equals(name, "group", StringComparison.OrdinalIgnoreCase) && sheet.Props.HasGroup))
        {
            SendError($"'{name}' is reserved by a table property.");
            return;
        }

        var index = Math.Clamp(GetInt(payload, "index"), 0, sheet.Columns.Count);
        if (!TryReadColumnSpec(payload, name, type, GetBool(payload, "optional"), out var spec, out var fields))
            return;

        var command = BuildColumnCommand(
            sheet,
            new ColumnOp[] { new InsertColumnOp(name, index, spec, autoIncrement) },
            $"Add Column '{name}'",
            fillInsertedRows: true);
        if (command is null)
            return;

        ExecuteColumnCommand(
            sheet, command, name, index,
            new ColumnBinding(name, publicTypeName, autoIncrement), fields,
            $"Added column '{name}'.");
    }

    /// <summary>
    /// Renames, retypes and re-options a column in one step, so the whole change is a
    /// single undo unit and every part is validated before anything is written: a rejected
    /// type cannot leave the column half-changed.
    /// </summary>
    private void EditColumn(JsonElement payload)
    {
        if (!TryResolveColumn(payload, out var sheet, out var column))
            return;

        if (!TryReadColumnName(payload, sheet, column, out var name))
            return;

        if (!TryReadColumnType(payload, sheet, column, out var type, out var publicTypeName, out var autoIncrement))
            return;

        if (!TryReadColumnSpec(payload, name, type, GetBool(payload, "optional"), out var spec, out var fields))
            return;

        var index = sheet.Columns.IndexOf(column);
        var renaming = !string.Equals(name, column.Name, StringComparison.Ordinal);
        var retyping = !string.Equals(
            ColumnTypeCodec.Encode(column.Type), ColumnTypeCodec.Encode(type), StringComparison.Ordinal);

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
        {
            // Nothing about the column itself changed. The side-car parts (public type,
            // auto-increment) may still have, and they are not part of that command. The
            // fields of a list are in the same boat: binding one to a public type changes
            // no schema, only the side-car.
            var rebound = RememberSubFieldPublicTypes(sheet, name, fields);
            if (!TryApplyColumnMetaOnly(sheet, name, publicTypeName, autoIncrement, index) && rebound)
            {
                CommitColumnEdit(sheet);
                RefreshAfterStructuralChange(sheet.Name, 0, index);
            }

            return;
        }

        ExecuteColumnCommand(
            sheet, command, name, index,
            new ColumnBinding(name, publicTypeName, autoIncrement), fields,
            $"Updated column '{name}'.");
    }

    private void MoveColumn(JsonElement payload)
    {
        if (!TryResolveColumn(payload, out var sheet, out var column))
            return;

        var from = sheet.Columns.IndexOf(column);
        var to = from + GetInt(payload, "delta");
        if (to < 0 || to >= sheet.Columns.Count || to == from)
            return;

        var command = BuildColumnCommand(
            sheet, new ColumnOp[] { new MoveColumnOp(column.Name, to) }, "Move Column");
        if (command is null)
            return;

        ExecuteColumnCommand(sheet, command, column.Name, to);
    }

    private void DeleteColumn(JsonElement payload)
    {
        if (!TryResolveColumn(payload, out var sheet, out var column))
            return;

        // The identifier column can be removed like any other. A table without one has
        // no key, so references to it stop resolving - that is the author's call. The
        // only refusal left is the last column, because a table with no columns cannot
        // be edited at all; the message says what to do about it.
        if (sheet.Columns.Count <= 1)
        {
            SendError(LastColumnMessage(sheet, column));
            return;
        }

        // Deleting a column other tables share deletes their values too, so it is
        // confirmed a second time with the count.
        if (!ConfirmSharedDelete(payload, sheet, column.Name))
            return;

        var index = sheet.Columns.IndexOf(column);
        var command = BuildColumnCommand(
            sheet, new ColumnOp[] { new DeleteColumnOp(column.Name) }, $"Delete Column '{column.Name}'");
        if (command is null)
        {
            SendError(LastColumnMessage(sheet, column));
            return;
        }

        ExecuteColumnCommand(
            sheet, command, column.Name, Math.Max(0, index - 1), message: $"Deleted column '{column.Name}'.");
    }

    private static string LastColumnMessage(SheetData sheet, Column column) =>
        $"'{sheet.Name}' would be left with no columns. Add another column first, then delete '{column.Name}'.";

    // ---------------------------------------------------------------- shared pipeline

    /// <summary>
    /// Resolves the table a schema message names. Every column and row edit starts here,
    /// so the read-only guard lives in one place instead of in every handler.
    /// </summary>
    private bool TryResolveSheet(JsonElement payload, out SheetData sheet)
    {
        sheet = null!;

        if (_document is null || _document.IsReadOnly)
            return false;

        var found = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (found is null)
            return false;

        sheet = found;
        return true;
    }

    /// <summary>Resolves the table and the column a schema message names.</summary>
    private bool TryResolveColumn(JsonElement payload, out SheetData sheet, out Column column)
    {
        column = null!;

        if (!TryResolveSheet(payload, out sheet))
            return false;

        var found = sheet.GetColumn(GetString(payload, "column"));
        if (found is null)
            return false;

        column = found;
        return true;
    }

    /// <summary>
    /// Reads and validates the name a dialog asked for. A rename may keep the name the
    /// column already has; anything else may not collide with an existing column.
    /// </summary>
    private bool TryReadColumnName(JsonElement payload, SheetData sheet, Column? current, out string name)
    {
        name = GetString(payload, "name").Trim();

        if (name.Length == 0)
        {
            SendError("A column needs a name.");
            return false;
        }

        if (!string.Equals(name, current?.Name, StringComparison.Ordinal) && sheet.GetColumn(name) is not null)
        {
            SendError($"The table already has a column named '{name}'.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the type a dialog asked for. A public type definition wins over the type
    /// string, so a copy stored in the .cdb can never drift from the other copies, and a
    /// table keeps a single ID column.
    /// </summary>
    private bool TryReadColumnType(
        JsonElement payload,
        SheetData sheet,
        Column? current,
        out ColumnType type,
        out string publicTypeName,
        out bool autoIncrement)
    {
        type = new ColumnType.Text();
        publicTypeName = string.Empty;
        autoIncrement = false;

        if (!TryDecodeType(GetString(payload, "type"), out var decoded))
            return false;

        type = decoded;

        // A column that follows a public type takes its values from the definition, so a
        // payload that disagrees can never make one copy drift from the others.
        publicTypeName = GetString(payload, "publicType").Trim();
        var publicType = _document!.Meta.Document.GetPublicType(publicTypeName);
        if (publicTypeName.Length > 0 && publicType is null)
        {
            SendError($"The public type '{publicTypeName}' no longer exists.");
            return false;
        }

        if (publicType is not null)
            type = PublicTypeService.ToColumnType(publicType);

        // A table keeps a single ID column, and changing a type is a way of making one.
        // `current` is null for a column that does not exist yet, which refuses any ID.
        if (type is ColumnType.Id && sheet.IdColumn is { } existingId && !ReferenceEquals(existingId, current))
        {
            SendError($"The table already has an ID column ('{existingId.Name}').");
            return false;
        }

        autoIncrement = GetBool(payload, "autoIncrement") && type is ColumnType.Int;
        return true;
    }

    /// <summary>
    /// Reads the optional settings and, for a structured column, its field list, and turns
    /// them into the definition the commands take.
    /// </summary>
    private bool TryReadColumnSpec(
        JsonElement payload,
        string name,
        ColumnType type,
        bool optional,
        out ColumnSpec spec,
        out List<(string Name, string? PublicType)> fields)
    {
        spec = null!;
        fields = new List<(string, string?)>();

        if (!TryReadColumnOptions(payload, type, optional, out var options))
            return false;

        if (!TryReadSubColumns(payload, type, name, out var subColumns, out var bindings))
            return false;

        fields = bindings;
        spec = BuildSpec(name, type, optional, options, subColumns);
        return true;
    }

    /// <summary>
    /// Runs a column command and performs the bookkeeping that has to follow it.
    ///
    /// The order is the whole point of this method (docs/07 9.1): the side-car has to know
    /// the new column set before a binding can be written, the template definition is
    /// rewritten from the table that was just edited, and only then is the page told.
    /// Doing these by hand in each handler is how a binding once disappeared silently.
    /// </summary>
    private void ExecuteColumnCommand(
        SheetData sheet,
        IEditorCommand command,
        string columnName,
        int cursorColumn,
        ColumnBinding? binding = null,
        IReadOnlyList<(string Name, string? PublicType)>? fields = null,
        string? message = null)
    {
        if (_document is null)
            return;

        _document.Commands.Execute(command);

        if (binding is { } bind)
        {
            RememberColumnMeta(sheet, bind.Column, bind.PublicType, bind.AutoIncrement);
            if (fields is not null)
                RememberSubFieldPublicTypes(sheet, bind.Column, fields);
        }

        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, cursorColumn);

        if (message is not null)
            SendToast(DescribeColumnEdit(message, sheet, columnName), "info");
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
        out IReadOnlyList<SubColumnSpec>? subColumns,
        out List<(string Name, string? PublicType)> bindings)
    {
        subColumns = null;
        bindings = new List<(string, string?)>();

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

                // A field can follow a public type just like a column of a real table. The
                // definition wins over the type string the dialog sent, so the copy in the
                // .cdb can never disagree with the others.
                var publicTypeName = GetString(item, "publicType").Trim();
                if (publicTypeName.Length > 0)
                {
                    var definition = _document!.Meta.Document.GetPublicType(publicTypeName);
                    if (definition is null)
                    {
                        SendError($"The public type '{publicTypeName}' no longer exists.");
                        return false;
                    }
                    subType = PublicTypeService.ToColumnType(definition);
                }

                // `id` is the name the field had when the dialog opened. It is what makes a
                // rename keep the values already stored under the old name.
                var original = GetString(item, "id").Trim();
                specs.Add(new SubColumnSpec(
                    name, subType, original.Length == 0 ? null : original));
                bindings.Add((name, publicTypeName.Length == 0 ? null : publicTypeName));
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
}
