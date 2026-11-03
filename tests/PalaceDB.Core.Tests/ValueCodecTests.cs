using PalaceDB.Core.Editing;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;
using Xunit;

namespace PalaceDB.Core.Tests;

public class ValueCodecTests
{
    private static Column Col(string sheet, string column) => Fixtures.Column(Fixtures.Sample(), sheet, column);

    [Fact]
    public void IntegerAcceptsNegativeValues()
    {
        var value = ValueCodec.ParseText(Col("Items", "cost"), "-42");
        Assert.Equal(-42d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1e3")]
    public void IntegerRejectsNonIntegralInput(string text)
    {
        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(Col("Items", "cost"), text));
    }

    [Fact]
    public void FloatAcceptsDecimals()
    {
        var value = ValueCodec.ParseText(Col("Items", "weight"), "3.25");
        Assert.Equal(3.25d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Fact]
    public void FloatRejectsText()
    {
        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(Col("Items", "weight"), "heavy"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void BoolParsesCommonSpellings(string text, bool expected)
    {
        var value = ValueCodec.ParseText(Col("Items", "enabled"), text);
        Assert.Equal(expected, Assert.IsType<CdbValue.Bool>(value).Value);
    }

    [Fact]
    public void BoolRejectsAnythingElse()
    {
        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(Col("Items", "enabled"), "yes"));
    }

    [Theory]
    [InlineData("sword")]
    [InlineData("_private")]
    [InlineData("a1")]
    public void IdentifierAcceptsValidNames(string text)
    {
        var value = ValueCodec.ParseText(Col("Items", "id"), text);
        Assert.Equal(text, Assert.IsType<CdbValue.Str>(value).Value);
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("")]
    public void IdentifierRejectsInvalidNames(string text)
    {
        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(Col("Items", "id"), text));
    }

    [Fact]
    public void EmptyReferenceIsAllowedForNonOptionalColumns()
    {
        var column = new Column(Fixtures.Items(Fixtures.Sample()), new CdbObject());
        column.Name = "r";
        column.Type = new ColumnType.Ref("Items");

        var value = ValueCodec.ParseText(column, string.Empty);
        Assert.Equal(string.Empty, Assert.IsType<CdbValue.Str>(value).Value);
    }

    [Fact]
    public void EmptyOptionalValueParsesToNull()
    {
        Assert.Null(ValueCodec.ParseText(Col("Items", "note"), string.Empty));
    }

    [Fact]
    public void EnumAcceptsAnIndex()
    {
        var value = ValueCodec.ParseText(Col("Items", "rarity"), "2");
        Assert.Equal(2d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Fact]
    public void EnumAcceptsAName()
    {
        var value = ValueCodec.ParseText(Col("Items", "rarity"), "Rare");
        Assert.Equal(1d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Fact]
    public void EnumRejectsAnOutOfRangeIndex()
    {
        var ex = Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(Col("Items", "rarity"), "9"));
        Assert.Contains("between 0 and 2", ex.Message);
    }

    [Fact]
    public void EnumRejectsAnUnknownName()
    {
        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(Col("Items", "rarity"), "Legendary"));
    }

    [Fact]
    public void FlagsAcceptPipedNames()
    {
        var value = ValueCodec.ParseText(Col("Items", "tags"), "A|C");
        Assert.Equal(5d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Fact]
    public void FlagsAcceptCommaSeparatedNames()
    {
        var value = ValueCodec.ParseText(Col("Items", "tags"), "B, C");
        Assert.Equal(6d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Fact]
    public void FlagsAcceptANumericMask()
    {
        var value = ValueCodec.ParseText(Col("Items", "tags"), "7");
        Assert.Equal(7d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Fact]
    public void FlagsRejectUnknownNames()
    {
        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(Col("Items", "tags"), "A|Z"));
    }

    [Fact]
    public void AnUnnamedEnumValueIsReachedByEmptyText()
    {
        // "5:,A,B" — the first value has no label, so empty text is that value and not
        // "nothing was typed".
        var type = ColumnTypeCodec.Decode("5:,A,B");
        var value = ValueCodec.ParseDefaultValue(type, optional: false, text: "");
        Assert.Equal(0d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Fact]
    public void AnUnnamedFlagIsTheEmptyToken()
    {
        var type = Assert.IsType<ColumnType.Flags>(ColumnTypeCodec.Decode("10:A,,C"));

        // The empty token is the unnamed flag, so "A,,C" is every one of the three.
        Assert.Equal(0b111, ValueCodec.ParseFlagNames(type, "A,,C"));
        // On its own it is the middle flag, not an empty selection.
        Assert.Equal(0b010, ValueCodec.ParseFlagNames(type, ""));
    }

    [Fact]
    public void EmptyFlagTokensAreIgnoredWhenNoFlagIsUnnamed()
    {
        var type = Assert.IsType<ColumnType.Flags>(ColumnTypeCodec.Decode("10:A,B"));
        Assert.Equal(0b11, ValueCodec.ParseFlagNames(type, "A,,B"));
        Assert.Equal(0, ValueCodec.ParseFlagNames(type, ""));
    }

    [Theory]
    [InlineData("#ff0000", 16711680)]
    [InlineData("ff0000", 16711680)]
    [InlineData("0xFF0000", 16711680)]
    [InlineData("#000000", 0)]
    public void ColorParsesHexNotation(string text, int expected)
    {
        Assert.Equal(expected, ValueCodec.ParseColor(text));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("")]
    public void ColorRejectsInvalidNotation(string text)
    {
        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseColor(text));
    }

    [Fact]
    public void EmptyListParsesToAnEmptyArray()
    {
        var column = new Column(Fixtures.Items(Fixtures.Sample()), new CdbObject());
        column.Name = "l";
        column.Type = new ColumnType.List();

        var value = Assert.IsType<CdbValue.Arr>(ValueCodec.ParseText(column, string.Empty));
        Assert.Empty(value.Items);
    }

    [Fact]
    public void JsonParserAcceptsUnquotedKeys()
    {
        var value = Assert.IsType<CdbValue.Obj>(ValueCodec.ParseJson("{ a: 1, b: [true, null] }"));
        Assert.Equal(1d, ((CdbValue.Number)value.Value.Get("a")!).Value);
        Assert.Equal(2, ((CdbValue.Arr)value.Value.Get("b")!).Items.Count);
    }

    [Fact]
    public void JsonParserReportsSyntaxErrors()
    {
        var ex = Assert.Throws<CdbValidationException>(() => ValueCodec.ParseJson("{ broken"));
        Assert.Contains("Invalid JSON", ex.Message);
    }

    [Fact]
    public void PropertiesRequireAnObject()
    {
        var column = new Column(Fixtures.Items(Fixtures.Sample()), new CdbObject());
        column.Name = "p";
        column.Type = new ColumnType.Properties();

        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(column, "[1,2]"));
        Assert.IsType<CdbValue.Obj>(ValueCodec.ParseText(column, "{}"));
    }
}
