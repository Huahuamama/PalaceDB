using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The <c>typeStr</c> codec is the compatibility contract with the original runtime
/// library, so every id is pinned by a test.
/// </summary>
public class ColumnTypeCodecTests
{
    [Theory]
    [InlineData("0", 0, "Id")]
    [InlineData("1", 1, "Text")]
    [InlineData("2", 2, "Bool")]
    [InlineData("3", 3, "Int")]
    [InlineData("4", 4, "Float")]
    [InlineData("7", 7, "Image")]
    [InlineData("8", 8, "List")]
    [InlineData("11", 11, "Color")]
    [InlineData("13", 13, "File")]
    [InlineData("16", 16, "Dynamic")]
    [InlineData("17", 17, "Properties")]
    [InlineData("18", 18, "Gradient")]
    [InlineData("19", 19, "Curve")]
    [InlineData("20", 20, "Guid")]
    [InlineData("21", 21, "Polymorph")]
    [InlineData("22", 22, "Formula")]
    public void DecodesSimpleTypes(string typeStr, int expectedId, string expectedName)
    {
        var type = ColumnTypeCodec.Decode(typeStr);
        Assert.Equal(expectedId, type.TypeId);
        Assert.Equal(expectedName, type.TypeName);
        Assert.Equal(typeStr, ColumnTypeCodec.Encode(type));
    }

    [Fact]
    public void DecodesEnumValues()
    {
        var type = Assert.IsType<ColumnType.Enum>(ColumnTypeCodec.Decode("5:Red,Green,Blue"));
        Assert.Equal(new[] { "Red", "Green", "Blue" }, type.Values);
        Assert.Equal("5:Red,Green,Blue", ColumnTypeCodec.Encode(type));
    }

    [Fact]
    public void DecodesFlagsValues()
    {
        var type = Assert.IsType<ColumnType.Flags>(ColumnTypeCodec.Decode("10:A,B"));
        Assert.Equal(new[] { "A", "B" }, type.Values);
        Assert.Equal("10:A,B", ColumnTypeCodec.Encode(type));
    }

    [Fact]
    public void DecodesSheetReference()
    {
        var type = Assert.IsType<ColumnType.Ref>(ColumnTypeCodec.Decode("6:Items"));
        Assert.Equal("Items", type.Sheet);
        Assert.Equal("6:Items", ColumnTypeCodec.Encode(type));
    }

    [Fact]
    public void DecodesCustomTypeName()
    {
        var type = Assert.IsType<ColumnType.Custom>(ColumnTypeCodec.Decode("9:Vector2"));
        Assert.Equal("Vector2", type.TypeName2);
        Assert.Equal("9:Vector2", ColumnTypeCodec.Encode(type));
    }

    [Theory]
    [InlineData("12", "Layer")]
    [InlineData("14", "TilePos")]
    [InlineData("15", "TileLayer")]
    public void RejectsRemovedMapTypes(string typeStr, string name)
    {
        var ex = Assert.Throws<CdbFormatException>(() => ColumnTypeCodec.Decode(typeStr));
        Assert.Contains("not supported in this version", ex.Message);
        Assert.Contains(name, ex.Message);
        Assert.Contains("(type " + typeStr + ")", ex.Message);
    }

    [Fact]
    public void RejectsUnknownTypeId()
    {
        var ex = Assert.Throws<CdbFormatException>(() => ColumnTypeCodec.Decode("99"));
        Assert.Contains("Unknown column type id 99", ex.Message);
    }

    [Fact]
    public void RejectsEmptyTypeString()
    {
        Assert.Throws<CdbFormatException>(() => ColumnTypeCodec.Decode(""));
    }

    [Fact]
    public void RejectsNonNumericTypeString()
    {
        Assert.Throws<CdbFormatException>(() => ColumnTypeCodec.Decode("abc"));
    }

    [Fact]
    public void SingleEnumValueHasNoTrailingComma()
    {
        var type = ColumnTypeCodec.Decode("5:OnlyOne");
        Assert.Equal("5:OnlyOne", ColumnTypeCodec.Encode(type));
    }

    [Fact]
    public void EmptyEnumPayloadRoundTrips()
    {
        var type = ColumnTypeCodec.Decode("5:");
        Assert.Equal("5:", ColumnTypeCodec.Encode(type));
    }

    [Fact]
    public void AnUnnamedValueKeepsItsPlace()
    {
        // "5:,1,2" is an enum of three values whose first one has no label. Dropping the
        // blank would shift every stored index onto the next label.
        var type = Assert.IsType<ColumnType.Enum>(ColumnTypeCodec.Decode("5:,1,2"));
        Assert.Equal(new[] { "", "1", "2" }, type.Values);
        Assert.Equal("5:,1,2", ColumnTypeCodec.Encode(type));
    }

    [Fact]
    public void AnUnnamedValueInTheMiddleKeepsItsPlace()
    {
        var type = Assert.IsType<ColumnType.Flags>(ColumnTypeCodec.Decode("10:A,,C"));
        Assert.Equal(new[] { "A", "", "C" }, type.Values);
        Assert.Equal("10:A,,C", ColumnTypeCodec.Encode(type));
    }
}
