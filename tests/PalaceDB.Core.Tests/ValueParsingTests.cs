using PalaceDB.Core.Editing;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Text typed into a cell, turned into a value. The cases here are the ones where the
/// obvious reading of a string is the wrong one.
/// </summary>
public class ValueParsingTests
{
    private static Column Column(ColumnType type) =>
        new(Fixtures.Items(Fixtures.Sample()), Raw(type));

    private static CdbObject Raw(ColumnType type)
    {
        var raw = new CdbObject();
        raw.Set("name", new CdbValue.Str("value"));
        raw.Set("typeStr", new CdbValue.Str(PalaceDB.Core.Serialization.ColumnTypeCodec.Encode(type)));
        return raw;
    }

    [Fact]
    public void AnEnumReadsALabelBeforeAnIndex()
    {
        // "1,2,3" is the everyday case: the label and the index disagree.
        var column = Column(new ColumnType.Enum(new[] { "1", "2", "3" }));

        var value = ValueCodec.ParseText(column, "2");

        Assert.Equal(1d, ((CdbValue.Number)value!).Value);
    }

    [Fact]
    public void AnEnumStillAcceptsAnIndexThatIsNotALabel()
    {
        var column = Column(new ColumnType.Enum(new[] { "common", "rare", "epic" }));

        var value = ValueCodec.ParseText(column, "2");

        Assert.Equal(2d, ((CdbValue.Number)value!).Value);
    }

    [Fact]
    public void AnEnumRejectsTextThatIsNeitherLabelNorIndex()
    {
        var column = Column(new ColumnType.Enum(new[] { "common", "rare" }));

        Assert.Throws<CdbValidationException>(() => ValueCodec.ParseText(column, "legendary"));
    }

    [Fact]
    public void FlagsReadNamesBeforeAMask()
    {
        // A flag called "3" must win over the mask 3, which would be the first two flags.
        var column = Column(new ColumnType.Flags(new[] { "meat", "3", "dairy" }));

        var value = ValueCodec.ParseText(column, "3");

        Assert.Equal(2d, ((CdbValue.Number)value!).Value);
    }

    [Fact]
    public void FlagsAcceptAListOfNames()
    {
        var column = Column(new ColumnType.Flags(new[] { "meat", "veg", "dairy" }));

        var value = ValueCodec.ParseText(column, "meat,dairy");

        Assert.Equal(5d, ((CdbValue.Number)value!).Value);
    }

    [Fact]
    public void FlagsStillAcceptAPlainMask()
    {
        var column = Column(new ColumnType.Flags(new[] { "meat", "veg", "dairy" }));

        var value = ValueCodec.ParseText(column, "6");

        Assert.Equal(6d, ((CdbValue.Number)value!).Value);
    }

    [Fact]
    public void AnEmptyFlagListIsAnEmptyMask()
    {
        var column = Column(new ColumnType.Flags(new[] { "meat", "veg" }));

        var value = ValueCodec.ParseText(column, string.Empty);

        Assert.Equal(0d, ((CdbValue.Number)value!).Value);
    }
}
