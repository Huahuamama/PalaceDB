using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class SelectionRangeTests
{
    [Fact]
    public void SingleCellHasOneRowAndOneColumn()
    {
        var range = SelectionRange.Single(3, 4);
        Assert.Equal(3, range.Top);
        Assert.Equal(3, range.Bottom);
        Assert.Equal(4, range.Left);
        Assert.Equal(4, range.Right);
        Assert.Equal(1, range.RowCount);
        Assert.Equal(1, range.ColumnCount);
    }

    [Fact]
    public void RangeIsNormalisedRegardlessOfDragDirection()
    {
        var downRight = new SelectionRange(1, 1, 5, 3);
        var upLeft = new SelectionRange(5, 3, 1, 1);

        Assert.Equal(downRight.Top, upLeft.Top);
        Assert.Equal(downRight.Bottom, upLeft.Bottom);
        Assert.Equal(downRight.Left, upLeft.Left);
        Assert.Equal(downRight.Right, upLeft.Right);
        Assert.Equal(5, downRight.RowCount);
        Assert.Equal(3, downRight.ColumnCount);
    }

    [Fact]
    public void MixedDirectionIsNormalisedPerAxis()
    {
        var range = new SelectionRange(5, 1, 2, 4);
        Assert.Equal(2, range.Top);
        Assert.Equal(5, range.Bottom);
        Assert.Equal(1, range.Left);
        Assert.Equal(4, range.Right);
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(1, 1, true)]
    [InlineData(4, 3, true)]
    [InlineData(0, 2, false)]
    [InlineData(5, 2, false)]
    [InlineData(2, 0, false)]
    [InlineData(2, 4, false)]
    public void ContainsUsesTheNormalisedBounds(int row, int column, bool expected)
    {
        var range = new SelectionRange(1, 1, 4, 3);
        Assert.Equal(expected, range.Contains(row, column));
    }
}

public class CursorAnchorTests
{
    [Fact]
    public void AnchorKeepsBothIdentityAndPosition()
    {
        var anchor = new CursorAnchor("Items", "sword", 3, "cost", 2);

        Assert.Equal("Items", anchor.SheetName);
        Assert.Equal("sword", anchor.RowId);
        Assert.Equal(3, anchor.RowIndex);
        Assert.Equal("cost", anchor.ColumnId);
        Assert.Equal(2, anchor.ColumnIndex);
        Assert.Null(anchor.Selection);
    }

    [Fact]
    public void AnchorCarriesTheSelection()
    {
        var range = new SelectionRange(1, 1, 3, 2);
        var anchor = new CursorAnchor("Items", "sword", 1, "name", 1, range);

        Assert.Equal(range, anchor.Selection);
    }

    [Fact]
    public void DefaultPositionIsNone()
    {
        Assert.True(CursorPosition.None.IsNone);
        Assert.False(new CursorPosition("Items", 0, 0, null).IsNone);
    }
}

public class GuidGeneratorTests
{
    [Fact]
    public void EncodesZeroAsAllFirstCharacter()
    {
        Assert.Equal("####-####-###", GuidGenerator.Encode(0));
    }

    [Fact]
    public void EncodedValueHasTheExpectedShape()
    {
        var text = GuidGenerator.Encode(0x0123456789ABCDEF);
        Assert.Equal(13, text.Length);
        Assert.Equal('-', text[4]);
        Assert.Equal('-', text[9]);
    }

    [Fact]
    public void EncodedValueOnlyUsesTheAlphabet()
    {
        const string alphabet = "#&0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var text = GuidGenerator.Encode(0x7EDCBA9876543210);

        foreach (var c in text)
        {
            if (c == '-')
                continue;
            Assert.Contains(c, alphabet);
        }
    }

    [Fact]
    public void NewProducesAThirteenCharacterIdentifier()
    {
        var id = GuidGenerator.New();
        Assert.Equal(13, id.Length);
        Assert.Equal('-', id[4]);
        Assert.Equal('-', id[9]);
    }

    [Fact]
    public void NewProducesDistinctIdentifiers()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2000; i++)
            Assert.True(seen.Add(GuidGenerator.New()), "Generated a duplicate identifier.");
    }
}
