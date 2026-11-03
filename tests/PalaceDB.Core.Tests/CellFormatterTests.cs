using PalaceDB.Core.Editing;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class CellFormatterTests
{
    private static (Database Db, SheetData Sheet, CellFormatter Formatter) Setup()
    {
        var db = Fixtures.Sample();
        var index = new DatabaseIndex(db);
        return (db, Fixtures.Items(db), new CellFormatter(db, index));
    }

    private static CdbValue? Value(SheetData sheet, CdbObject row, string column) => row.Get(column);

    [Fact]
    public void MissingRequiredValueShowsTheNullMarker()
    {
        var (db, sheet, formatter) = Setup();
        var row = sheet.Lines[0];
        var column = sheet.GetColumn("name")!;

        var display = formatter.Format(sheet, column, row, CdbValue.NullValue);
        Assert.Equal("#NULL", display.Text);
        Assert.Equal(CellState.Missing, display.State);
    }

    [Fact]
    public void MissingOptionalValueShowsAnEmptyCell()
    {
        var (db, sheet, formatter) = Setup();
        var row = sheet.Lines[0];
        var column = sheet.GetColumn("note")!;

        var display = formatter.Format(sheet, column, row, null);
        Assert.Equal(string.Empty, display.Text);
        Assert.Equal(CellState.Empty, display.State);
    }

    [Fact]
    public void DuplicateIdentifierIsFlagged()
    {
        var (db, sheet, formatter) = Setup();
        var row = sheet.Lines[0];
        var column = sheet.GetColumn("id")!;

        var display = formatter.Format(sheet, column, row, Value(sheet, row, "id"));
        Assert.Equal("#DUP(sword)", display.Text);
        Assert.Equal(CellState.Duplicate, display.State);
    }

    [Fact]
    public void UniqueIdentifierIsShownAsIs()
    {
        var (db, sheet, formatter) = Setup();
        var row = sheet.Lines[1];
        var column = sheet.GetColumn("id")!;

        var display = formatter.Format(sheet, column, row, Value(sheet, row, "id"));
        Assert.Equal("shield", display.Text);
        Assert.Equal(CellState.Ok, display.State);
    }

    [Fact]
    public void BoolRendersAsYOrN()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("enabled")!;

        Assert.Equal("Y", formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(true)).Text);
        Assert.Equal("N", formatter.Format(sheet, column, sheet.Lines[1], CdbValue.Of(false)).Text);
    }

    [Fact]
    public void EnumRendersTheValueName()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("rarity")!;

        Assert.Equal("Rare", formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(1)).Text);
    }

    [Fact]
    public void OutOfRangeEnumIsFlagged()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("rarity")!;

        var display = formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(9));
        Assert.Equal("#INVALID", display.Text);
        Assert.Equal(CellState.Error, display.State);
    }

    [Fact]
    public void FlagsRenderAsPipedNames()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("tags")!;

        Assert.Equal("A|C", formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(5)).Text);
    }

    [Fact]
    public void EmptyFlagsRenderAsAnEmptySetSymbol()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("tags")!;

        var display = formatter.Format(sheet, column, sheet.Lines[1], CdbValue.Of(0));
        Assert.Equal(CellState.Empty, display.State);
    }

    [Fact]
    public void ColorRendersAsHex()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("color")!;

        Assert.Equal("#ff0000", formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(16711680)).Text);
    }

    [Fact]
    public void ReferenceShowsTheDisplayNameOfTheTargetRow()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("parent")!;

        var display = formatter.Format(sheet, column, sheet.Lines[1], CdbValue.Of("sword"));
        Assert.Equal("Sword", display.Text);
        Assert.Equal(CellState.Ok, display.State);
        Assert.Equal("sword", display.Detail);
    }

    [Fact]
    public void BrokenReferenceIsFlagged()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("parent")!;

        var display = formatter.Format(sheet, column, sheet.Lines[2], CdbValue.Of("ghost"));
        Assert.Equal("#REF(ghost)", display.Text);
        Assert.Equal(CellState.BadReference, display.State);
    }

    [Fact]
    public void ReferenceToAnUnknownSheetIsFlagged()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        var column = new Column(sheet, new CdbObject());
        column.Name = "r";
        column.Type = new ColumnType.Ref("NoSuchSheet");

        var formatter = new CellFormatter(db, new DatabaseIndex(db));
        var display = formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of("x"));

        Assert.Equal("#REF(x)", display.Text);
        Assert.Contains("NoSuchSheet", display.Detail);
    }

    [Fact]
    public void EmptyReferenceIsFlaggedAsMissing()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("parent")!;

        var display = formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(string.Empty));
        Assert.Equal("#MISSING", display.Text);
    }

    [Fact]
    public void PercentDisplayMultipliesByHundred()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("weight")!;

        Assert.Equal("50%", formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(0.5)).Text);
        Assert.Equal("225%", formatter.Format(sheet, column, sheet.Lines[1], CdbValue.Of(2.25)).Text);
    }

    [Fact]
    public void IntegerDisplayHasNoDecimalPart()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("cost")!;

        Assert.Equal("10", formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(10)).Text);
    }

    [Fact]
    public void WrongScalarTypeIsFlagged()
    {
        var (db, sheet, formatter) = Setup();
        var column = sheet.GetColumn("cost")!;

        var display = formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of("not a number"));
        Assert.Equal(CellState.Error, display.State);
    }

    [Fact]
    public void ImageShowsItsPath()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        var column = new Column(sheet, new CdbObject());
        column.Name = "icon";
        column.Type = new ColumnType.Image();

        var formatter = new CellFormatter(db, new DatabaseIndex(db));
        var display = formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of("res/icon.png"));

        Assert.Equal("res/icon.png", display.Text);
        Assert.Equal(CellState.Ok, display.State);
    }

    [Fact]
    public void CustomValueRendersTheCaseName()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        var column = new Column(sheet, new CdbObject());
        column.Name = "v";
        column.Type = new ColumnType.Custom("Vector2");

        var formatter = new CellFormatter(db, new DatabaseIndex(db));

        var zero = formatter.Format(sheet, column, sheet.Lines[0],
            new CdbValue.Arr(new List<CdbValue> { CdbValue.Of(0) }));
        Assert.Equal("Zero", zero.Text);

        var xy = formatter.Format(sheet, column, sheet.Lines[0],
            new CdbValue.Arr(new List<CdbValue> { CdbValue.Of(1), CdbValue.Of(2), CdbValue.Of(3) }));
        Assert.Equal("XY(2,3)", xy.Text);
    }

    [Fact]
    public void UnknownCustomTypeIsFlagged()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        var column = new Column(sheet, new CdbObject());
        column.Name = "v";
        column.Type = new ColumnType.Custom("Missing");

        var formatter = new CellFormatter(db, new DatabaseIndex(db));
        var display = formatter.Format(sheet, column, sheet.Lines[0],
            new CdbValue.Arr(new List<CdbValue> { CdbValue.Of(0) }));

        Assert.Equal(CellState.Error, display.State);
        Assert.Contains("Missing", display.Detail);
    }

    [Fact]
    public void LongRawValuesAreTruncated()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        var column = new Column(sheet, new CdbObject());
        column.Name = "d";
        column.Type = new ColumnType.Dynamic();

        var formatter = new CellFormatter(db, new DatabaseIndex(db));
        var display = formatter.Format(sheet, column, sheet.Lines[0], CdbValue.Of(new string('x', 500)));

        Assert.EndsWith("...", display.Text);
        Assert.True(display.Text.Length <= 120);
    }
}
