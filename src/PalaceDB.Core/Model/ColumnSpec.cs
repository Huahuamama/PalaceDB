using PalaceDB.Core.Json;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Model;

/// <summary>
/// One field of the hidden sub-table behind a List or Properties column.
/// <para>
/// <paramref name="Original"/> is the name the field had before a dialog renamed it, which
/// is what lets the values follow the rename instead of looking like a removal followed by
/// an addition. A template never stores it: its fields are definitions, not edits.
/// </para>
/// </summary>
public readonly record struct SubFieldSpec(string Name, string TypeStr, string? Original = null);

/// <summary>
/// A column definition detached from any sheet: exactly the settings that a table
/// template keeps identical across the tables that share it.
/// <para>
/// The type is stored as the canonical <c>typeStr</c> rather than as a
/// <see cref="ColumnType"/> because that is what the file holds: two columns are only
/// interchangeable when their encoded form agrees, including the enum values and the
/// referenced table.
/// </para>
/// </summary>
public sealed class ColumnSpec
{
    public string Name { get; set; } = string.Empty;

    public string TypeStr { get; set; } = "1";

    public bool Optional { get; set; }

    public DisplayType Display { get; set; }

    public ColumnKind Kind { get; set; }

    public CdbValue? DefaultValue { get; set; }

    public bool EnumAsString { get; set; }

    public int? Scope { get; set; }

    public string? Documentation { get; set; }

    public bool Shared { get; set; }

    /// <summary>Fields of the sub-table, for List / Properties / Polymorph columns.</summary>
    public List<SubFieldSpec> Fields { get; set; } = new();

    public ColumnType Type => ColumnTypeCodec.Decode(TypeStr);

    /// <summary>
    /// The type in its canonical encoding, which is what comparisons use. A file may spell
    /// the same type in more than one way ("5" and "5:" for an enum without values), and two
    /// columns that describe the same type must compare equal no matter how they were
    /// written. An unreadable type falls back to the raw string, so it still compares.
    /// </summary>
    public string CanonicalTypeStr => Canonicalize(TypeStr);

    public static string Canonicalize(string typeStr)
    {
        try
        {
            return ColumnTypeCodec.Encode(ColumnTypeCodec.Decode(typeStr));
        }
        catch (CdbFormatException)
        {
            return typeStr;
        }
    }

    public bool IsContainer => Type.IsContainer;

    /// <summary>Reads the definition of an existing column, sub-table fields included.</summary>
    public static ColumnSpec From(Column column, SheetData sheet)
    {
        var spec = new ColumnSpec
        {
            Name = column.Name,
            // Always the canonical form, so a template never inherits a hand written
            // variant of the same type from whichever table it was created from.
            TypeStr = ColumnTypeCodec.Encode(column.Type),
            Optional = column.Optional,
            Display = column.Display,
            Kind = column.Kind,
            DefaultValue = column.DefaultValue,
            EnumAsString = column.EnumAsString,
            Scope = column.Scope,
            Documentation = column.Documentation,
            Shared = column.Shared,
        };

        if (column.Type.IsContainer && sheet.GetSub(column) is { } sub)
            foreach (var field in sub.Columns)
                spec.Fields.Add(new SubFieldSpec(field.Name, ColumnTypeCodec.Encode(field.Type)));

        return spec;
    }

    public ColumnSpec Clone()
    {
        var copy = new ColumnSpec
        {
            Name = Name,
            TypeStr = TypeStr,
            Optional = Optional,
            Display = Display,
            Kind = Kind,
            DefaultValue = CdbValueCloner.CloneOrNull(DefaultValue),
            EnumAsString = EnumAsString,
            Scope = Scope,
            Documentation = Documentation,
            Shared = Shared,
        };
        copy.Fields.AddRange(Fields);
        return copy;
    }

    /// <summary>Builds the raw column node, ready to be inserted into a sheet.</summary>
    public CdbObject ToRaw()
    {
        var raw = new CdbObject();
        raw.Set("name", new CdbValue.Str(Name));
        raw.Set("typeStr", new CdbValue.Str(TypeStr));

        if (Optional)
            raw.Set("opt", new CdbValue.Bool(true));
        if (Display != DisplayType.Default)
            raw.Set("display", new CdbValue.Number((int)Display, null));
        if (Kind != ColumnKind.None)
            raw.Set("kind", new CdbValue.Str(KindName(Kind)));
        if (DefaultValue is { } declared && declared is not CdbValue.Null)
            raw.Set("defaultValue", CdbValueCloner.Clone(declared));
        if (EnumAsString)
            raw.Set("enumStr", new CdbValue.Bool(true));
        if (Scope is { } scope)
            raw.Set("scope", new CdbValue.Number(scope, null));
        if (!string.IsNullOrEmpty(Documentation))
            raw.Set("documentation", new CdbValue.Str(Documentation));
        if (Shared)
            raw.Set("shared", new CdbValue.Bool(true));

        return raw;
    }

    public static string KindName(ColumnKind kind) => kind switch
    {
        ColumnKind.Localizable => "localizable",
        ColumnKind.Script => "script",
        ColumnKind.Hidden => "hidden",
        ColumnKind.TypeKind => "typekind",
        _ => string.Empty,
    };

    public static ColumnKind ParseKind(string? name) => name switch
    {
        "localizable" => ColumnKind.Localizable,
        "script" => ColumnKind.Script,
        "hidden" => ColumnKind.Hidden,
        "typekind" => ColumnKind.TypeKind,
        _ => ColumnKind.None,
    };

    /// <summary>True when both columns declare the same type, fields included.</summary>
    public bool SameType(ColumnSpec other)
    {
        // Compared in canonical form: the same type written two ways is the same type.
        if (!string.Equals(CanonicalTypeStr, other.CanonicalTypeStr, StringComparison.Ordinal))
            return false;
        if (Fields.Count != other.Fields.Count)
            return false;
        for (var i = 0; i < Fields.Count; i++)
        {
            if (!string.Equals(Fields[i].Name, other.Fields[i].Name, StringComparison.Ordinal) ||
                !string.Equals(Canonicalize(Fields[i].TypeStr), Canonicalize(other.Fields[i].TypeStr),
                    StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>True when every setting matches, so the two columns are interchangeable.</summary>
    public bool SameDefinition(ColumnSpec other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        Optional == other.Optional &&
        Display == other.Display &&
        Kind == other.Kind &&
        EnumAsString == other.EnumAsString &&
        Scope == other.Scope &&
        Shared == other.Shared &&
        string.Equals(Documentation ?? string.Empty, other.Documentation ?? string.Empty, StringComparison.Ordinal) &&
        SameDefault(DefaultValue, other.DefaultValue) &&
        SameType(other);

    private static bool SameDefault(CdbValue? a, CdbValue? b) => (a, b) switch
    {
        (null, null) => true,
        (CdbValue.Null, CdbValue.Null) => true,
        (CdbValue.Null, null) => true,
        (null, CdbValue.Null) => true,
        (CdbValue.Bool x, CdbValue.Bool y) => x.Value == y.Value,
        (CdbValue.Number x, CdbValue.Number y) => x.Value.Equals(y.Value),
        (CdbValue.Str x, CdbValue.Str y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal),
        (CdbValue.Arr x, CdbValue.Arr y) => CdbWriter.WriteValue(x) == CdbWriter.WriteValue(y),
        (CdbValue.Obj x, CdbValue.Obj y) => CdbWriter.WriteValue(x) == CdbWriter.WriteValue(y),
        _ => false,
    };

    public override string ToString() => $"{Name}:{TypeStr}";
}

/// <summary>A named, ordered list of column definitions shared by several tables.</summary>
public sealed class TemplateSpec
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<ColumnSpec> Columns { get; set; } = new();

    public ColumnSpec? Find(string columnName) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.Ordinal));

    public bool Has(string columnName) => Find(columnName) is not null;

    /// <summary>Reads the current definition of a table as a template body.</summary>
    public static List<ColumnSpec> Snapshot(SheetData sheet)
    {
        var columns = new List<ColumnSpec>(sheet.Columns.Count);
        foreach (var column in sheet.Columns)
            columns.Add(ColumnSpec.From(column, sheet));
        return columns;
    }

    public static bool SameColumns(IReadOnlyList<ColumnSpec> a, IReadOnlyList<ColumnSpec> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (!a[i].SameDefinition(b[i]))
                return false;
        return true;
    }
}
