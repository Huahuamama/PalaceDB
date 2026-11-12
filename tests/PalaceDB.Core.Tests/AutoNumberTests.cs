using PalaceDB.Core.Commands;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class AutoNumberTests
{
    [Fact]
    public void NextContinuesFromTheHighestNumber()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        new ColumnInsertCommand(sheet, 1, "n", new ColumnType.Int(), db, autoIncrement: true).Execute();

        Assert.Equal(4, AutoNumber.Next(sheet, "n"));
    }

    [Fact]
    public void HighestIgnoresMissingAndUnreadableValues()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        sheet.Lines[0].Set("n", new CdbValue.Number(7, null));
        sheet.Lines[1].Set("n", new CdbValue.Str("not a number"));
        sheet.Lines[2].Remove("n");

        Assert.Equal(7, AutoNumber.Highest(sheet, "n"));
        Assert.Equal(8, AutoNumber.Next(sheet, "n"));
    }

    [Fact]
    public void AnAutoIncrementColumnNumbersTheRowsItArrivesWith()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);

        new ColumnInsertCommand(sheet, 1, "n", new ColumnType.Int(), db, autoIncrement: true).Execute();

        Assert.Equal(1, ((CdbValue.Number)sheet.Lines[0].Get("n")!).Value);
        Assert.Equal(2, ((CdbValue.Number)sheet.Lines[1].Get("n")!).Value);
        Assert.Equal(3, ((CdbValue.Number)sheet.Lines[2].Get("n")!).Value);
    }

    [Fact]
    public void AnAutoIncrementColumnStartsAtOneEvenWhenTheTableIsEmpty()
    {
        var db = Fixtures.Sample();
        var sheet = db.GetSheet("Empty")!;

        new ColumnInsertCommand(sheet, 1, "n", new ColumnType.Int(), db, autoIncrement: true).Execute();

        Assert.Equal(1, AutoNumber.Next(sheet, "n"));
    }

    [Fact]
    public void AGuidColumnGivesEveryExistingRowItsOwnValue()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);

        new ColumnInsertCommand(sheet, 1, "g", new ColumnType.Guid(), db).Execute();

        var values = sheet.Lines
            .Select(row => ((CdbValue.Str)row.Get("g")!).Value)
            .ToList();

        Assert.Equal(3, values.Count);
        Assert.All(values, value => Assert.Equal(13, value.Length));
        Assert.Equal(values.Count, values.Distinct().Count());
    }
}
