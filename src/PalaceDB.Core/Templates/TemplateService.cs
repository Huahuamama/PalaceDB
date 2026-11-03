using System.Globalization;
using PalaceDB.Core.Commands;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Templates;

/// <summary>Why a table cannot take a template.</summary>
public enum TemplateConflictKind
{
    /// <summary>The table has a column the template does not declare.</summary>
    ExtraColumn,

    /// <summary>Same name, different type.</summary>
    TypeMismatch,

    /// <summary>Same name and type, different fields of a list column.</summary>
    FieldMismatch,

    /// <summary>Both the table and the template would end up with an ID column.</summary>
    IdColumn,

    /// <summary>The name is taken by a generated column (<c>index</c> / <c>group</c>).</summary>
    ReservedName,
}

public sealed record TemplateConflict(string Column, TemplateConflictKind Kind, string Detail);

/// <summary>
/// The result of comparing a table with a template.
/// <para>
/// Missing columns and a different order are allowed: applying the template fills the
/// gaps and puts the columns in the template's order, and neither of those touches the
/// data. Anything else is a conflict, because resolving it would mean deleting a column
/// or rewriting values.
/// </para>
/// </summary>
public sealed class TemplateAudit
{
    /// <summary>Template columns the table does not have yet; they will be added.</summary>
    public List<string> Missing { get; } = new();

    /// <summary>Columns that exist but sit in another position; they will be reordered.</summary>
    public List<string> Reordered { get; } = new();

    public List<TemplateConflict> Conflicts { get; } = new();

    public bool CanApply => Conflicts.Count == 0;

    public bool IsInSync => Missing.Count == 0 && Reordered.Count == 0 && Conflicts.Count == 0;
}

/// <summary>How much data one column operation would cost in one table.</summary>
public sealed record TemplateImpactTable(string Table, int Rows, int Values);

/// <summary>
/// What an operation would do to every table that shares a column. Built before the
/// operation runs, so the second confirmation can name the tables and count the values.
/// </summary>
public sealed class TemplateImpact
{
    public string Column { get; init; } = string.Empty;

    /// <summary>"delete" or "retype".</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>True when more than one table is involved.</summary>
    public bool Shared { get; init; }

    public List<TemplateImpactTable> Tables { get; } = new();

    public int Total => Tables.Sum(t => t.Values);

    public int Rows => Tables.Sum(t => t.Rows);

    /// <summary>True when at least one value would be thrown away.</summary>
    public bool Lossy => Total > 0;
}

/// <summary>
/// Everything the editor needs to know about table templates: who is in a group, what
/// a table would have to change to join one, and what a change would cost the others.
/// </summary>
public static class TemplateService
{
    /// <summary>The template a table follows, or null.</summary>
    public static TemplateSpec? TemplateOf(MetaStore meta, SheetData sheet)
    {
        var id = meta.GetTableTemplate(sheet.Name);
        var stored = meta.GetTemplate(id);
        if (stored is null)
            return null;

        return new TemplateSpec { Id = id!, Name = stored.Name, Columns = stored.ToSpecs() };
    }

    /// <summary>Tables that follow a template, in document order.</summary>
    public static List<SheetData> Members(Database database, MetaStore meta, string templateId)
    {
        var names = new HashSet<string>(meta.TemplateMembers(templateId), StringComparer.Ordinal);
        var members = new List<SheetData>();
        foreach (var sheet in database.Sheets)
            if (!sheet.Props.Hide && names.Contains(sheet.Name))
                members.Add(sheet);
        return members;
    }

    /// <summary>Every table of the document that follows the template of this one.</summary>
    public static List<SheetData> Siblings(Database database, MetaStore meta, SheetData sheet)
    {
        var id = meta.GetTableTemplate(sheet.Name);
        if (id is null)
            return new List<SheetData>();

        return Members(database, meta, id)
            .Where(s => !ReferenceEquals(s, sheet))
            .ToList();
    }

    // ---------------------------------------------------------------- audit

    /// <summary>Compares a table with a template without changing anything.</summary>
    public static TemplateAudit Audit(SheetData sheet, TemplateSpec template)
    {
        var audit = new TemplateAudit();
        var known = new HashSet<string>(template.Columns.Select(c => c.Name), StringComparer.Ordinal);

        foreach (var column in sheet.Columns)
            if (!known.Contains(column.Name))
                audit.Conflicts.Add(new TemplateConflict(
                    column.Name,
                    TemplateConflictKind.ExtraColumn,
                    $"'{column.Name}' is not part of the template."));

        var idColumn = sheet.IdColumn;

        foreach (var spec in template.Columns)
        {
            var column = sheet.GetColumn(spec.Name);

            if (spec.Type is ColumnType.Id && idColumn is not null &&
                !string.Equals(idColumn.Name, spec.Name, StringComparison.Ordinal))
            {
                audit.Conflicts.Add(new TemplateConflict(
                    spec.Name,
                    TemplateConflictKind.IdColumn,
                    $"The table already has an ID column ('{idColumn.Name}'), and a table can only have one."));
            }

            if ((string.Equals(spec.Name, "index", StringComparison.OrdinalIgnoreCase) && sheet.Props.HasIndex) ||
                (string.Equals(spec.Name, "group", StringComparison.OrdinalIgnoreCase) && sheet.Props.HasGroup))
            {
                audit.Conflicts.Add(new TemplateConflict(
                    spec.Name,
                    TemplateConflictKind.ReservedName,
                    $"'{spec.Name}' is generated by a table property."));
            }

            if (column is null)
            {
                audit.Missing.Add(spec.Name);
                continue;
            }

            var current = ColumnSpec.From(column, sheet);
            // Canonical on both sides: a type written "5" and the same type written "5:"
            // are the same type, and refusing to apply a template over the difference was
            // reported as "enum here, enum there", which reads like nonsense.
            if (!string.Equals(current.CanonicalTypeStr, spec.CanonicalTypeStr, StringComparison.Ordinal))
            {
                audit.Conflicts.Add(new TemplateConflict(
                    spec.Name,
                    TemplateConflictKind.TypeMismatch,
                    DescribeTypeDifference(spec.Name, current, spec)));
            }
            else if (!current.SameType(spec))
            {
                audit.Conflicts.Add(new TemplateConflict(
                    spec.Name,
                    TemplateConflictKind.FieldMismatch,
                    $"'{spec.Name}' declares different fields than the template."));
            }
        }

        // Order: look at where the shared columns sit now and compare that with the
        // order the template asks for.
        var positions = new List<int>();
        foreach (var column in sheet.Columns)
        {
            var at = template.Columns.FindIndex(c => string.Equals(c.Name, column.Name, StringComparison.Ordinal));
            if (at >= 0)
                positions.Add(at);
        }

        if (!positions.SequenceEqual(positions.OrderBy(p => p)))
            foreach (var position in positions)
                audit.Reordered.Add(template.Columns[position].Name);

        return audit;
    }

    /// <summary>
    /// Says what actually differs between two column types.
    /// <para>
    /// "'rarity' is Enum in this table but Enum in the template" is technically true and
    /// tells the reader nothing: when both sides are the same kind of type, the difference
    /// lives in the payload, so the payload is what gets reported.
    /// </para>
    /// </summary>
    private static string DescribeTypeDifference(string name, ColumnSpec current, ColumnSpec template)
    {
        var mine = current.Type;
        var theirs = template.Type;

        if ((mine is ColumnType.Enum or ColumnType.Flags) && (theirs is ColumnType.Enum or ColumnType.Flags))
        {
            var left = DescribeValues(mine);
            var right = DescribeValues(theirs);
            return string.Equals(mine.TypeName, theirs.TypeName, StringComparison.Ordinal)
                ? $"'{name}' lists {left} in this table but {right} in the template."
                : $"'{name}' is {mine.TypeName} with {left} in this table but {theirs.TypeName} with {right} in the template.";
        }

        if (mine is ColumnType.Ref mineRef && theirs is ColumnType.Ref templateRef)
        {
            return $"'{name}' points at the table '{mineRef.Sheet}' in this table but at '{templateRef.Sheet}' in the template.";
        }

        if (mine is ColumnType.Custom mineCustom && theirs is ColumnType.Custom templateCustom)
        {
            return $"'{name}' uses the custom type '{mineCustom.TypeName2}' in this table but '{templateCustom.TypeName2}' in the template.";
        }

        return $"'{name}' is {mine.TypeName} in this table but {theirs.TypeName} in the template.";
    }

    /// <summary>The enum / flags values of a type, quoted so a stray space is visible.</summary>
    private static string DescribeValues(ColumnType type)
    {
        var values = type switch
        {
            ColumnType.Enum e => e.Values,
            ColumnType.Flags f => f.Values,
            _ => Array.Empty<string>(),
        };

        return values.Count == 0
            ? "no values"
            : "(" + string.Join(", ", values.Select(value => $"'{value}'")) + ")";
    }

    // ---------------------------------------------------------------- mirroring

    /// <summary>
    /// Builds the command for a column edit and replays it on every other table that
    /// follows the same template, as one undo step.
    /// <para>
    /// The operations are addressed by column name, so the same list can be applied to a
    /// table that is not in the same state as the one that was edited. A table where an
    /// operation does not apply (it does not have the column, the name is taken) is left
    /// alone instead of failing the whole edit.
    /// </para>
    /// </summary>
    public static IEditorCommand? BuildMirrored(
        Database database,
        MetaStore meta,
        SheetData sheet,
        IReadOnlyList<ColumnOp> ops,
        string label,
        bool fillInsertedRows = false)
    {
        if (ops.Count == 0)
            return null;

        var source = BuildOps(sheet, ops, database, fillInsertedRows);
        if (source is null)
            return null;

        var siblings = Siblings(database, meta, sheet);
        if (siblings.Count == 0)
            return source;

        var composite = new CompositeCommand(label);
        composite.Add(source);

        foreach (var sibling in siblings)
        {
            // The schema is shared, the data is not: a column that appears in another
            // table because of this edit starts empty there.
            if (BuildOps(sibling, ops, database, fillInsertedRows: false) is { } mirror)
                composite.Add(mirror);
        }

        return composite;
    }

    private static IEditorCommand? BuildOps(
        SheetData sheet,
        IReadOnlyList<ColumnOp> ops,
        Database database,
        bool fillInsertedRows)
    {
        var built = new List<IEditorCommand>();
        foreach (var op in ops)
            if (ColumnOps.Build(sheet, op, database, fillInsertedRows) is { } command)
                built.Add(command);

        if (built.Count == 0)
            return null;
        if (built.Count == 1)
            return built[0];

        var composite = new CompositeCommand(built[0].Label);
        foreach (var command in built)
            composite.Add(command);
        return composite;
    }

    // ---------------------------------------------------------------- apply

    /// <summary>
    /// Builds the commands that make a table match its template: the missing columns are
    /// inserted (schema only — no values are written into existing rows) and the order is
    /// aligned. Returns null when there is nothing to do.
    /// </summary>
    public static IEditorCommand? BuildApply(SheetData sheet, TemplateSpec template, Database database)
    {
        var composite = new CompositeCommand($"Apply Template '{template.Name}'");
        var inserted = false;

        for (var i = 0; i < template.Columns.Count; i++)
        {
            var spec = template.Columns[i];
            if (sheet.GetColumn(spec.Name) is not null)
                continue;

            composite.Add(new ColumnInsertCommand(
                sheet,
                i,
                spec.Name,
                spec.Type,
                database,
                spec.Optional,
                new ColumnOptions(spec.Display, spec.Kind, spec.DefaultValue),
                ColumnOps.ToSubColumns(spec),
                fillExistingRows: false));
            inserted = true;
        }

        // The order is resolved when the command runs, so it sees the columns that were
        // just added. It is kept when the table was missing columns even if the current
        // order already looks right, because the new columns land where they fit.
        var reorder = new ColumnReorderCommand(sheet, template.Columns.Select(c => c.Name).ToList());
        if (inserted || !reorder.IsNoOp)
            composite.Add(reorder);

        return composite.Count == 0 ? null : composite;
    }

    /// <summary>
    /// Creates a table from a template, sub-tables included, as one undo step.
    /// </summary>
    public static CreateTableCommand CreateTable(
        Database database,
        int index,
        string name,
        IReadOnlyList<ColumnSpec> columns) =>
        new(database, index, name, columns);

    // ---------------------------------------------------------------- impact

    /// <summary>How many values a delete would remove from every table that shares the column.</summary>
    public static TemplateImpact MeasureDelete(Database database, MetaStore meta, SheetData sheet, string columnName)
    {
        var templateId = meta.GetTableTemplate(sheet.Name);
        var members = templateId is null
            ? new List<SheetData> { sheet }
            : Members(database, meta, templateId);
        if (members.Count == 0)
            members.Add(sheet);

        var impact = new TemplateImpact
        {
            Column = columnName,
            Operation = "delete",
            Shared = members.Count > 1,
        };

        foreach (var member in members)
        {
            var column = member.GetColumn(columnName);
            if (column is null)
                continue;

            var values = 0;
            foreach (var row in member.Lines)
                if (!IsEmpty(row.Get(columnName)))
                    values++;

            impact.Tables.Add(new TemplateImpactTable(member.Name, member.Lines.Count, values));
        }

        return impact;
    }

    /// <summary>
    /// How many values would not survive a type change, in every table that shares the
    /// column. Counted by actually converting, so the number is not a guess.
    /// </summary>
    public static TemplateImpact MeasureRetype(Database database, MetaStore meta, SheetData sheet, ColumnSpec target)
    {
        var templateId = meta.GetTableTemplate(sheet.Name);
        var members = templateId is null
            ? new List<SheetData> { sheet }
            : Members(database, meta, templateId);
        if (members.Count == 0)
            members.Add(sheet);

        var impact = new TemplateImpact
        {
            Column = target.Name,
            Operation = "retype",
            Shared = members.Count > 1,
        };

        var type = target.Type;

        foreach (var member in members)
        {
            var column = member.GetColumn(target.Name);
            if (column is null)
                continue;

            var probe = new Column(member, target.ToRaw());
            var fallback = TypeDefaults.Create(probe, database, member, ignoreOptional: true);

            var lost = 0;
            foreach (var row in member.Lines)
            {
                var value = row.Get(target.Name);
                if (value is null || IsEmpty(value))
                    continue;

                // A text value that names an enum entry converts without losing anything,
                // even though the stored form changes from text to an index.
                if (type is ColumnType.Enum e && value is CdbValue.Str s &&
                    e.Values.Any(v => string.Equals(v, s.Value, StringComparison.Ordinal)))
                    continue;

                var converted = ValueConverter.Convert(value, type, fallback);
                if (Loses(value, converted))
                    lost++;
            }

            impact.Tables.Add(new TemplateImpactTable(member.Name, member.Lines.Count, lost));
        }

        return impact;
    }

    private static bool IsEmpty(CdbValue? value) => value switch
    {
        null => true,
        CdbValue.Null => true,
        CdbValue.Str s => s.Value.Length == 0,
        _ => false,
    };

    /// <summary>True when the converted value no longer carries what the original said.</summary>
    private static bool Loses(CdbValue original, CdbValue? converted)
    {
        if (converted is null or CdbValue.Null)
            return true;

        switch (converted)
        {
            case CdbValue.Number number:
                var before = AsNumber(original);
                return before is null || !before.Value.Equals(number.Value);

            case CdbValue.Str text:
                return !string.Equals(AsText(original), text.Value, StringComparison.Ordinal);

            case CdbValue.Bool flag:
                return AsFlag(original) is not { } value || value != flag.Value;

            case CdbValue.Arr:
                return original is not CdbValue.Arr;

            case CdbValue.Obj:
                return original is not CdbValue.Obj;

            default:
                return true;
        }
    }

    private static double? AsNumber(CdbValue value) => value switch
    {
        CdbValue.Number n => n.Value,
        CdbValue.Bool b => b.Value ? 1 : 0,
        CdbValue.Str s => double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null,
        _ => null,
    };

    private static bool? AsFlag(CdbValue value) => value switch
    {
        CdbValue.Bool b => b.Value,
        CdbValue.Number n => Math.Abs(n.Value) > double.Epsilon,
        CdbValue.Str s => s.Value.Length > 0 && s.Value != "false" && s.Value != "0",
        _ => null,
    };

    private static string AsText(CdbValue value) => value switch
    {
        CdbValue.Str s => s.Value,
        CdbValue.Number n => n.Raw ?? CdbNumber.Format(n.Value),
        CdbValue.Bool b => b.Value ? "true" : "false",
        _ => string.Empty,
    };

    /// <summary>Reads the type string of a column without touching the model.</summary>
    public static string TypeStrOf(Column column) => ColumnTypeCodec.Encode(column.Type);
}
