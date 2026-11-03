namespace PalaceDB.Core.Model;

/// <summary>
/// Column data types. Mirrors the original Haxe <c>enum ColumnType</c> so that
/// pattern matching reads almost like the original code.
/// The numeric ids are the on-disk <c>typeStr</c> prefixes and must never change.
/// Map-related types (12 Layer, 14 TilePos, 15 TileLayer) are intentionally absent.
/// </summary>
public abstract record ColumnType
{
    /// <summary>On-disk type id (the part before ':' in <c>typeStr</c>).</summary>
    public abstract int TypeId { get; }

    /// <summary>Name used by the UI and by error messages.</summary>
    public abstract string TypeName { get; }

    public sealed record Id : ColumnType
    {
        public override int TypeId => 0;
        public override string TypeName => "Id";
    }

    public sealed record Text : ColumnType
    {
        public override int TypeId => 1;
        public override string TypeName => "Text";
    }

    public sealed record Bool : ColumnType
    {
        public override int TypeId => 2;
        public override string TypeName => "Bool";
    }

    public sealed record Int : ColumnType
    {
        public override int TypeId => 3;
        public override string TypeName => "Int";
    }

    public sealed record Float : ColumnType
    {
        public override int TypeId => 4;
        public override string TypeName => "Float";
    }

    public sealed record Enum(IReadOnlyList<string> Values) : ColumnType
    {
        public override int TypeId => 5;
        public override string TypeName => "Enum";
    }

    public sealed record Ref(string Sheet) : ColumnType
    {
        public override int TypeId => 6;
        public override string TypeName => "Ref";
    }

    /// <summary>Image stored as a path string relative to the CDB file (requirement R9).</summary>
    public sealed record Image : ColumnType
    {
        public override int TypeId => 7;
        public override string TypeName => "Image";
    }

    public sealed record List : ColumnType
    {
        public override int TypeId => 8;
        public override string TypeName => "List";
    }

    public sealed record Custom(string TypeName2) : ColumnType
    {
        public override int TypeId => 9;
        public override string TypeName => "Custom";
    }

    public sealed record Flags(IReadOnlyList<string> Values) : ColumnType
    {
        public override int TypeId => 10;
        public override string TypeName => "Flags";
    }

    public sealed record Color : ColumnType
    {
        public override int TypeId => 11;
        public override string TypeName => "Color";
    }

    public sealed record File : ColumnType
    {
        public override int TypeId => 13;
        public override string TypeName => "File";
    }

    public sealed record Dynamic : ColumnType
    {
        public override int TypeId => 16;
        public override string TypeName => "Dynamic";
    }

    public sealed record Properties : ColumnType
    {
        public override int TypeId => 17;
        public override string TypeName => "Properties";
    }

    public sealed record Gradient : ColumnType
    {
        public override int TypeId => 18;
        public override string TypeName => "Gradient";
    }

    public sealed record Curve : ColumnType
    {
        public override int TypeId => 19;
        public override string TypeName => "Curve";
    }

    public sealed record Guid : ColumnType
    {
        public override int TypeId => 20;
        public override string TypeName => "Guid";
    }

    public sealed record Polymorph : ColumnType
    {
        public override int TypeId => 21;
        public override string TypeName => "Polymorph";
    }

    public sealed record Formula : ColumnType
    {
        public override int TypeId => 22;
        public override string TypeName => "Formula";
    }

    public bool IsContainer => this is List or Properties or Polymorph;

    public override string ToString() => TypeName;
}
