using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Metadata;

/// <summary>One field of a list column, as stored in a template.</summary>
public sealed class TemplateFieldMeta
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("typeStr")]
    public string TypeStr { get; set; } = "1";
}

/// <summary>
/// One column of a template, in the side-car. The shape mirrors the keys a column has
/// inside the .cdb, minus the ones that only make sense per table, so that reading a
/// template and reading a table's columns describe the same thing.
/// </summary>
public sealed class TemplateColumnMeta
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("typeStr")]
    public string TypeStr { get; set; } = "1";

    [JsonPropertyName("opt")]
    public bool? Optional { get; set; }

    [JsonPropertyName("display")]
    public int? Display { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("defaultValue")]
    public JsonNode? DefaultValue { get; set; }

    [JsonPropertyName("enumStr")]
    public bool? EnumAsString { get; set; }

    [JsonPropertyName("scope")]
    public int? Scope { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }

    [JsonPropertyName("shared")]
    public bool? Shared { get; set; }

    [JsonPropertyName("fields")]
    public List<TemplateFieldMeta>? Fields { get; set; }

    public static TemplateColumnMeta From(ColumnSpec spec)
    {
        var meta = new TemplateColumnMeta
        {
            Name = spec.Name,
            TypeStr = spec.TypeStr,
            Optional = spec.Optional ? true : null,
            Display = spec.Display == DisplayType.Default ? null : (int)spec.Display,
            Kind = spec.Kind == ColumnKind.None ? null : ColumnSpec.KindName(spec.Kind),
            EnumAsString = spec.EnumAsString ? true : null,
            Scope = spec.Scope,
            Documentation = string.IsNullOrEmpty(spec.Documentation) ? null : spec.Documentation,
            Shared = spec.Shared ? true : null,
        };

        if (spec.DefaultValue is { } value && value is not CdbValue.Null)
        {
            try
            {
                meta.DefaultValue = JsonNode.Parse(CdbWriter.WriteValue(value));
            }
            catch
            {
                // A default that cannot be expressed as JSON is dropped rather than
                // blocking the whole template.
            }
        }

        if (spec.Fields.Count > 0)
            meta.Fields = spec.Fields
                .Select(f => new TemplateFieldMeta { Name = f.Name, TypeStr = f.TypeStr })
                .ToList();

        return meta;
    }

    public ColumnSpec ToSpec()
    {
        var spec = new ColumnSpec
        {
            Name = Name,
            TypeStr = string.IsNullOrEmpty(TypeStr) ? "1" : TypeStr,
            Optional = Optional == true,
            Display = Display is { } display && Enum.IsDefined(typeof(DisplayType), display)
                ? (DisplayType)display
                : DisplayType.Default,
            Kind = ColumnSpec.ParseKind(Kind),
            EnumAsString = EnumAsString == true,
            Scope = Scope,
            Documentation = Documentation,
            Shared = Shared == true,
        };

        if (DefaultValue is { } node)
        {
            try
            {
                spec.DefaultValue = CdbReader.ParseValue(node.ToJsonString());
            }
            catch
            {
                // Ignore a default that no longer parses; the column keeps its type.
            }
        }

        if (Fields is { Count: > 0 })
            foreach (var field in Fields)
                spec.Fields.Add(new SubFieldSpec(field.Name, string.IsNullOrEmpty(field.TypeStr) ? "1" : field.TypeStr));

        return spec;
    }
}

/// <summary>A named set of column definitions that several tables keep in step.</summary>
public sealed class TemplateMeta
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    public List<TemplateColumnMeta> Columns { get; set; } = new();

    public List<ColumnSpec> ToSpecs()
    {
        var specs = new List<ColumnSpec>(Columns.Count);
        foreach (var column in Columns)
            specs.Add(column.ToSpec());
        return specs;
    }

    public static TemplateMeta FromSpecs(string name, IReadOnlyList<ColumnSpec> specs)
    {
        var meta = new TemplateMeta { Name = name };
        foreach (var spec in specs)
            meta.Columns.Add(TemplateColumnMeta.From(spec));
        return meta;
    }

    /// <summary>True when the stored definition still equals the given columns.</summary>
    public bool SameAs(IReadOnlyList<ColumnSpec> specs) =>
        Model.TemplateSpec.SameColumns(ToSpecs(), specs);
}
