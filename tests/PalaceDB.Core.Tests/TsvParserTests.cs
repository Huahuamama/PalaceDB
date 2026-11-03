using PalaceDB.Core.Editing;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Tab separated text arrives from every spreadsheet, and each of them quotes fields a
/// little differently. These cases pin the format the paste path relies on (R5).
/// </summary>
public class TsvParserTests
{
    [Fact]
    public void ASingleCellIsOneRowOfOneField()
    {
        var rows = TsvParser.Parse("hello");

        var row = Assert.Single(rows);
        Assert.Equal(new[] { "hello" }, row);
    }

    [Fact]
    public void TabsSeparateFieldsAndNewlinesSeparateRows()
    {
        var rows = TsvParser.Parse("a\tb\nc\td");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b" }, rows[0]);
        Assert.Equal(new[] { "c", "d" }, rows[1]);
    }

    [Fact]
    public void WindowsLineEndingsAreUnderstood()
    {
        var rows = TsvParser.Parse("a\tb\r\nc\td\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "c", "d" }, rows[1]);
    }

    [Fact]
    public void ATrailingNewlineAddsNoEmptyRow()
    {
        Assert.Equal(2, TsvParser.Parse("a\nb\n").Count);
        Assert.Equal(2, TsvParser.Parse("a\nb").Count);
    }

    [Fact]
    public void AnEmptyFieldIsKept()
    {
        var rows = TsvParser.Parse("a\t\tb");

        Assert.Equal(new[] { "a", "", "b" }, rows[0]);
    }

    [Fact]
    public void AnEmptyInputHasNoRows()
    {
        Assert.Empty(TsvParser.Parse(""));
        Assert.Empty(TsvParser.Parse(null));
    }

    [Fact]
    public void AQuotedFieldMayContainTabs()
    {
        var rows = TsvParser.Parse("\"a\tb\"\tc");

        Assert.Equal(new[] { "a\tb", "c" }, rows[0]);
    }

    [Fact]
    public void AQuotedFieldMayContainLineBreaks()
    {
        var rows = TsvParser.Parse("\"line one\nline two\"\tx");

        var row = Assert.Single(rows);
        Assert.Equal("line one\nline two", row[0]);
        Assert.Equal("x", row[1]);
    }

    [Fact]
    public void ADoubledQuoteInsideAQuotedFieldIsOneQuote()
    {
        var rows = TsvParser.Parse("\"say \"\"hi\"\"\"\tx");

        Assert.Equal("say \"hi\"", rows[0][0]);
    }

    [Fact]
    public void AQuoteInTheMiddleOfAFieldIsLiteral()
    {
        var rows = TsvParser.Parse("a\"b\tc");

        Assert.Equal(new[] { "a\"b", "c" }, rows[0]);
    }

    [Fact]
    public void AByteOrderMarkIsDropped()
    {
        var rows = TsvParser.Parse("\uFEFFa\tb");

        Assert.Equal(new[] { "a", "b" }, rows[0]);
    }

    [Fact]
    public void AnEmptyQuotedFieldIsAnEmptyCell()
    {
        var rows = TsvParser.Parse("\"\"\tb");

        Assert.Equal(new[] { "", "b" }, rows[0]);
    }

    [Fact]
    public void LooksLikeTableNeedsATabOrALineBreak()
    {
        Assert.False(TsvParser.LooksLikeTable("plain"));
        Assert.True(TsvParser.LooksLikeTable("a\tb"));
        Assert.True(TsvParser.LooksLikeTable("a\nb"));
        Assert.False(TsvParser.LooksLikeTable(""));
    }
}
