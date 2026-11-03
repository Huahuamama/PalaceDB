using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Commands;

/// <summary>
/// One semantic change to a column, addressed by name.
/// <para>
/// The same operation can be applied to any table that declares the column, which is
/// what lets a table template mirror an edit: the host builds the operation once and
/// every member table turns it into its own command. Names are used instead of indexes
/// because the tables in a group may not be in the same state, and an index would then
/// hit the wrong column.
/// </para>
/// </summary>
public abstract record ColumnOp(string Column);

public sealed record RenameColumnOp(string Column, string NewName) : ColumnOp(Column);

/// <summary>Changes the type of a column, and the fields of a list column with it.</summary>
public sealed record RetypeColumnOp(string Column, ColumnSpec Spec) : ColumnOp(Column);

/// <summary>Changes the optional parts only: required/optional, default, format, kind.</summary>
public sealed record OptionsColumnOp(string Column, ColumnSpec Spec) : ColumnOp(Column);

public sealed record DeleteColumnOp(string Column) : ColumnOp(Column);

public sealed record InsertColumnOp(string Column, int Index, ColumnSpec Spec) : ColumnOp(Column);

public sealed record MoveColumnOp(string Column, int ToIndex) : ColumnOp(Column);

/// <summary>
/// Turns a <see cref="ColumnOp"/> into the command that performs it on one sheet.
/// <para>
/// The rules that make a column edit safe (the last column cannot go, a table keeps a
/// single ID column, a list keeps its hidden sub-table in step) live here and nowhere
/// else, so a change applied to a table that follows a template behaves exactly like the
/// same change typed into that table.
/// </para>
/// </summary>
public static class ColumnOps
{
    /// <summary>
    /// Builds the command, or null when the operation does not apply to this sheet (the
    /// column is missing, the name is taken, the table would lose its last column).
    /// </summary>
    /// <param name="fillInsertedRows">
    /// When true a new column writes its default into every existing row. Mirrored edits
    /// pass false: the schema is shared, the data is not.
    /// </param>
    public static IEditorCommand? Build(
        SheetData sheet,
        ColumnOp op,
        Database database,
        bool fillInsertedRows = false)
    {
        return op switch
        {
            RenameColumnOp rename => BuildRename(sheet, rename, database),
            RetypeColumnOp retype => BuildRetype(sheet, retype, database),
            OptionsColumnOp options => BuildOptions(sheet, options),
            DeleteColumnOp delete => BuildDelete(sheet, delete, database),
            InsertColumnOp insert => BuildInsert(sheet, insert, database, fillInsertedRows),
            MoveColumnOp move => BuildMove(sheet, move),
            _ => null,
        };
    }

    private static IEditorCommand? BuildRename(SheetData sheet, RenameColumnOp op, Database database)
    {
        var column = sheet.GetColumn(op.Column);
        if (column is null)
            return null;
        if (string.Equals(column.Name, op.NewName, StringComparison.Ordinal))
            return null;
        if (sheet.GetColumn(op.NewName) is not null)
            return null;

        var index = sheet.Columns.IndexOf(column);
        var composite = new CompositeCommand($"Rename Column '{op.Column}'");
        composite.Add(new ColumnRenameCommand(sheet, index, op.NewName));

        // The sub-table of a list column is named after the column, so it travels with
        // the rename; a stale name would hide its fields.
        if (database.GetSheet($"{sheet.Name}@{op.Column}") is { } sub)
            composite.Add(new SubSheetRenameCommand(sub, $"{sheet.Name}@{op.NewName}"));

        return composite;
    }

    private static IEditorCommand? BuildRetype(SheetData sheet, RetypeColumnOp op, Database database)
    {
        var column = sheet.GetColumn(op.Column);
        if (column is null)
            return null;

        var type = op.Spec.Type;
        var index = sheet.Columns.IndexOf(column);
        var subName = $"{sheet.Name}@{op.Column}";
        var existingSub = database.GetSheet(subName);
        var composite = new CompositeCommand($"Change '{op.Column}' to {type.TypeName}");

        var retyping = !string.Equals(
            ColumnTypeCodec.Encode(column.Type), op.Spec.TypeStr, StringComparison.Ordinal);
        if (retyping)
            composite.Add(new ColumnTypeChangeCommand(sheet, index, type, database));

        var fields = ToSubColumns(op.Spec);
        if (type.IsContainer && existingSub is null && fields is { Count: > 0 })
            composite.Add(new SubSheetInsertCommand(database, subName, fields));

        if (existingSub is not null && fields is { Count: > 0 } && type is ColumnType.List or ColumnType.Properties)
        {
            var edit = new SubColumnsCommand(database, existingSub, sheet, column, fields);
            if (!edit.IsNoOp)
                composite.Add(edit);
        }

        var options = new ColumnOptionsCommand(column, op.Spec.Optional, op.Spec.DefaultValue, op.Spec.Display, op.Spec.Kind);
        if (!options.IsNoOp)
            composite.Add(options);

        return composite.Count == 0 ? null : composite;
    }

    private static IEditorCommand? BuildOptions(SheetData sheet, OptionsColumnOp op)
    {
        var column = sheet.GetColumn(op.Column);
        if (column is null)
            return null;

        var options = new ColumnOptionsCommand(column, op.Spec.Optional, op.Spec.DefaultValue, op.Spec.Display, op.Spec.Kind);
        return options.IsNoOp ? null : options;
    }

    private static IEditorCommand? BuildDelete(SheetData sheet, DeleteColumnOp op, Database database)
    {
        var column = sheet.GetColumn(op.Column);
        if (column is null)
            return null;

        // A table with no columns cannot be edited at all, so the last one stays.
        if (sheet.Columns.Count <= 1)
            return null;

        var index = sheet.Columns.IndexOf(column);
        var composite = new CompositeCommand($"Delete Column '{op.Column}'");
        composite.Add(new ColumnDeleteCommand(sheet, index));

        // Leaving the sub-table behind would make the next column of the same name
        // inherit fields nobody asked for.
        if (database.GetSheet($"{sheet.Name}@{op.Column}") is not null)
            composite.Add(new SheetDeleteCommand(database, $"{sheet.Name}@{op.Column}"));

        return composite;
    }

    private static IEditorCommand? BuildInsert(
        SheetData sheet,
        InsertColumnOp op,
        Database database,
        bool fillInsertedRows)
    {
        if (sheet.GetColumn(op.Column) is not null)
            return null;

        var type = op.Spec.Type;
        if (type is ColumnType.Id && sheet.IdColumn is not null)
            return null;

        var index = Math.Clamp(op.Index, 0, sheet.Columns.Count);
        var options = new ColumnOptions(op.Spec.Display, op.Spec.Kind, op.Spec.DefaultValue);

        return new ColumnInsertCommand(
            sheet,
            index,
            op.Column,
            type,
            database,
            op.Spec.Optional,
            options,
            ToSubColumns(op.Spec),
            fillInsertedRows);
    }

    private static IEditorCommand? BuildMove(SheetData sheet, MoveColumnOp op)
    {
        var column = sheet.GetColumn(op.Column);
        if (column is null)
            return null;

        var from = sheet.Columns.IndexOf(column);
        var to = Math.Clamp(op.ToIndex, 0, sheet.Columns.Count - 1);
        return from == to ? null : new ColumnMoveCommand(sheet, from, to);
    }

    /// <summary>Fields of a list column as the sub-table commands expect them.</summary>
    public static IReadOnlyList<SubColumnSpec>? ToSubColumns(ColumnSpec spec)
    {
        if (spec.Fields.Count == 0)
            return null;

        var fields = new List<SubColumnSpec>(spec.Fields.Count);
        foreach (var field in spec.Fields)
            fields.Add(new SubColumnSpec(
                field.Name, ColumnTypeCodec.Decode(field.TypeStr), field.Original));
        return fields;
    }
}
