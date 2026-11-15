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

/// <summary>Column insertion, rename, retype, reorder and conversion.</summary>
public sealed partial class EditorHost
{

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

        // A column that follows a public type takes its values from the definition, so a
        // payload that disagrees can never make one copy drift from the others.
        var publicTypeName = GetString(payload, "publicType").Trim();
        var publicType = _document.Meta.Document.GetPublicType(publicTypeName);
        if (publicTypeName.Length > 0 && publicType is null)
        {
            SendError($"The public type '{publicTypeName}' no longer exists.");
            return;
        }
        if (publicType is not null)
            type = PublicTypeService.ToColumnType(publicType);

        var autoIncrement = GetBool(payload, "autoIncrement") && type is ColumnType.Int;

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

        if (!TryReadSubColumns(payload, type, name, out var subColumns, out var subBindings))
            return;

        var spec = BuildSpec(name, type, optional, options, subColumns);
        var command = BuildColumnCommand(
            sheet,
            new[] { (ColumnOp)new InsertColumnOp(name, index, spec, autoIncrement) },
            $"Add Column '{name}'",
            fillInsertedRows: true);
        if (command is null)
            return;

        _document.Commands.Execute(command);
        RememberColumnMeta(sheet, name, publicTypeName, autoIncrement);
        RememberSubFieldPublicTypes(sheet, name, subBindings);
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

        // A column bound to a public type always takes the definition's values, so the
        // dialog can only choose which type it follows, not a private copy of the list.
        var publicTypeName = GetString(payload, "publicType").Trim();
        var publicType = _document.Meta.Document.GetPublicType(publicTypeName);
        if (publicTypeName.Length > 0 && publicType is null)
        {
            SendError($"The public type '{publicTypeName}' no longer exists.");
            return;
        }
        if (publicType is not null)
            type = PublicTypeService.ToColumnType(publicType);

        // A table keeps a single ID column, and changing a type is a way of making one.
        if (type is ColumnType.Id && sheet.IdColumn is { } existingId && !ReferenceEquals(existingId, column))
        {
            SendError($"The table already has an ID column ('{existingId.Name}').");
            return;
        }

        var autoIncrement = GetBool(payload, "autoIncrement") && type is ColumnType.Int;

        var optional = GetBool(payload, "optional");
        if (!TryReadColumnOptions(payload, type, optional, out var options))
            return;

        if (!TryReadSubColumns(payload, type, name, out var subColumns, out var subBindings))
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
        {
            // Nothing about the column itself changed. The side-car parts (public type,
            // auto-increment) may still have, and they are not part of that command.
            // The fields of a list are in the same boat: binding one to a public type
            // changes no schema, only the side-car.
            var rebound = RememberSubFieldPublicTypes(sheet, name, subBindings);
            if (!TryApplyColumnMetaOnly(sheet, name, publicTypeName, autoIncrement, index) && rebound)
            {
                CommitColumnEdit(sheet);
                RefreshAfterStructuralChange(sheet.Name, 0, index);
            }
            return;
        }

        _document.Commands.Execute(command);
        RememberColumnMeta(sheet, name, publicTypeName, autoIncrement);
        RememberSubFieldPublicTypes(sheet, name, subBindings);
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

        var publicTypeName = GetString(payload, "publicType").Trim();
        var publicType = _document.Meta.Document.GetPublicType(publicTypeName);
        if (publicTypeName.Length > 0 && publicType is null)
        {
            SendError($"The public type '{publicTypeName}' no longer exists.");
            return;
        }
        if (publicType is not null)
            type = PublicTypeService.ToColumnType(publicType);

        // A table keeps a single ID column, and changing a type is a way of making one.
        if (type is ColumnType.Id && sheet.IdColumn is { } existingId && !ReferenceEquals(existingId, column))
        {
            SendError($"The table already has an ID column ('{existingId.Name}').");
            return;
        }

        var autoIncrement = GetBool(payload, "autoIncrement") && type is ColumnType.Int;

        var index = sheet.Columns.IndexOf(column);

        if (!TryReadSubColumns(payload, type, column.Name, out var subColumns, out var subBindings))
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
        {
            TryApplyColumnMetaOnly(sheet, column.Name, publicTypeName, autoIncrement, index);
            return;
        }

        _document.Commands.Execute(command);
        RememberColumnMeta(sheet, column.Name, publicTypeName, autoIncrement);
        RememberSubFieldPublicTypes(sheet, column.Name, subBindings);
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
