using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Model;

/// <summary>Display format of numeric columns.</summary>
public enum DisplayType
{
    Default = 0,
    Percent = 1,
}

/// <summary>Extra role a text column can play.</summary>
public enum ColumnKind
{
    None,
    Localizable,
    Script,
    Hidden,
    TypeKind,
}

/// <summary>A column (field) of a sheet.</summary>
public sealed class Column
{
    private readonly SheetData _sheet;
    private ColumnType? _type;
    private bool _typeDirty;

    public CdbObject Raw { get; }

    internal Column(SheetData sheet, CdbObject raw)
    {
        _sheet = sheet;
        Raw = raw;
    }

    public SheetData Sheet => _sheet;

    public string Name
    {
        get => Raw.GetString("name", string.Empty);
        set => Raw.Set("name", new CdbValue.Str(value));
    }

    /// <summary>Raw on-disk type string, when the column stores its own type.</summary>
    public string? TypeStr => Raw.GetString("typeStr");

    /// <summary>
    /// When set, this column shares the definition of another column
    /// (<c>Sheet@Column</c>) and does not carry its own <c>typeStr</c>.
    /// </summary>
    public string? StructRef
    {
        get => Raw.GetString("structRef");
        set
        {
            if (value is null)
                Raw.Remove("structRef");
            else
                Raw.Set("structRef", new CdbValue.Str(value));
            _type = null;
            _typeDirty = true;
        }
    }

    public ColumnType Type
    {
        get
        {
            if (_type is not null)
                return _type;

            if (StructRef is not null)
            {
                // Resolved later by SheetData.ResolveDeferred; fall back to Text so
                // that partially built documents never throw while loading.
                _type = new ColumnType.Text();
                return _type;
            }

            _type = ColumnTypeCodec.Decode(TypeStr ?? "1");
            return _type;
        }
        set
        {
            _type = value;
            _typeDirty = true;
        }
    }

    /// <summary>True when the column is optional and may be absent from a row.</summary>
    public bool Optional
    {
        get => Raw.GetBool("opt", false);
        set => SetBool("opt", value);
    }

    public DisplayType Display
    {
        get => Raw.GetInt("display") is { } v ? (DisplayType)v : DisplayType.Default;
        set
        {
            if (value == DisplayType.Default)
                Raw.Remove("display");
            else
                Raw.Set("display", new CdbValue.Number((int)value, null));
        }
    }

    public ColumnKind Kind
    {
        get => Raw.GetString("kind") switch
        {
            "localizable" => ColumnKind.Localizable,
            "script" => ColumnKind.Script,
            "hidden" => ColumnKind.Hidden,
            "typekind" => ColumnKind.TypeKind,
            _ => ColumnKind.None,
        };
        set
        {
            if (value == ColumnKind.None)
                Raw.Remove("kind");
            else
                Raw.Set("kind", new CdbValue.Str(value switch
                {
                    ColumnKind.Localizable => "localizable",
                    ColumnKind.Script => "script",
                    ColumnKind.Hidden => "hidden",
                    ColumnKind.TypeKind => "typekind",
                    _ => "hidden",
                }));
        }
    }

    public int? Scope
    {
        get => Raw.GetInt("scope");
        set
        {
            if (value is null) Raw.Remove("scope");
            else Raw.Set("scope", new CdbValue.Number(value.Value, null));
        }
    }

    public string? Documentation
    {
        get => Raw.GetString("documentation");
        set
        {
            if (value is null) Raw.Remove("documentation");
            else Raw.Set("documentation", new CdbValue.Str(value));
        }
    }

    public CdbValue? DefaultValue
    {
        get => Raw.Get("defaultValue");
        set
        {
            if (value is null || value is CdbValue.Null) Raw.Remove("defaultValue");
            else Raw.Set("defaultValue", value);
        }
    }

    public bool Shared
    {
        get => Raw.GetBool("shared", false);
        set => SetBool("shared", value);
    }

    /// <summary>Enum values are stored as strings instead of indices.</summary>
    public bool EnumAsString
    {
        get => Raw.GetBool("enumStr", false);
        set => SetBool("enumStr", value);
    }

    private void SetBool(string key, bool value)
    {
        if (value) Raw.Set(key, new CdbValue.Bool(true));
        else Raw.Remove(key);
    }

    /// <summary>Resolves <c>structRef</c> once every sheet exists.</summary>
    internal void ResolveDeferred()
    {
        var reference = StructRef;
        if (reference is null)
            return;

        var at = reference.LastIndexOf('@');
        if (at <= 0)
            return;

        var sheetName = reference[..at];
        var columnName = reference[(at + 1)..];
        var target = _sheet.Owner.GetSheet(sheetName)?.GetColumn(columnName);
        if (target is null)
            return;

        _type = target.Type;
        if (target.EnumAsString)
            Raw.Set("enumStr", new CdbValue.Bool(true));
    }

    /// <summary>Writes the type back to <c>typeStr</c> before saving.</summary>
    internal void FlushType()
    {
        if (StructRef is not null)
        {
            Raw.Remove("typeStr");
            return;
        }

        if (!_typeDirty && TypeStr is not null)
            return;

        Raw.Set("typeStr", new CdbValue.Str(ColumnTypeCodec.Encode(Type)));
    }

    public override string ToString() => $"{Name}:{Type.TypeName}";
}
