using PalaceDB.Core.Commands;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class SeparatorCommandTests
{
    private static (Database Db, SheetData Sheet) Sample()
    {
        var db = Fixtures.Sample();
        return (db, Fixtures.Items(db));
    }

    [Fact]
    public void AddingASeparatorInsertsTheRowIndex()
    {
        var (db, sheet) = Sample();

        var command = new SeparatorToggleCommand(sheet, 2);
        command.Execute();

        Assert.True(command.Added);
        Assert.Equal(new[] { 2 }, sheet.Separators);
    }

    [Fact]
    public void TogglingTheSameRowRemovesIt()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 2).Execute();

        var command = new SeparatorToggleCommand(sheet, 2);
        command.Execute();

        Assert.False(command.Added);
        Assert.Empty(sheet.Separators);
    }

    [Fact]
    public void ToggleUndoRestoresThePreviousSeparators()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 0).Execute();
        new SeparatorToggleCommand(sheet, 3).Execute();

        var command = new SeparatorToggleCommand(sheet, 1);
        command.Execute();
        command.Undo();

        Assert.Equal(new[] { 0, 3 }, sheet.Separators);
    }

    [Fact]
    public void SeparatorsAreWrittenInSortedOrder()
    {
        var (db, sheet) = Sample();

        new SeparatorToggleCommand(sheet, 3).Execute();
        new SeparatorToggleCommand(sheet, 1).Execute();

        Assert.Equal(new[] { 1, 3 }, sheet.Separators);
    }

    [Fact]
    public void TitlesStayParallelToTheSortedSeparators()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 0).Execute();
        new SeparatorTitleCommand(sheet, 0, "First").Execute();

        // Inserting a separator in front of the titled one must push the title along.
        new SeparatorToggleCommand(sheet, 1).Execute();
        new SeparatorTitleCommand(sheet, 1, "Second").Execute();

        Assert.Equal(new[] { 0, 1 }, sheet.Separators);
        Assert.Equal(new[] { "First", "Second" }, Titles(sheet));
    }

    [Fact]
    public void RenamingASeparatorIsUndoable()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 2).Execute();

        var command = new SeparatorTitleCommand(sheet, 2, "Group");
        command.Execute();
        Assert.Equal(new[] { "Group" }, Titles(sheet));

        command.Undo();
        Assert.Equal(new[] { string.Empty }, Titles(sheet));
    }

    [Fact]
    public void ClearingEveryTitleRemovesThePropertyKey()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 2).Execute();
        new SeparatorTitleCommand(sheet, 2, "Group").Execute();

        new SeparatorTitleCommand(sheet, 2, string.Empty).Execute();

        Assert.Null(sheet.Props.SeparatorTitles);
    }

    [Fact]
    public void RemovingTheLastSeparatorLeavesAnEmptyList()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();

        new SeparatorToggleCommand(sheet, 1).Execute();

        Assert.Empty(sheet.Separators);
    }

    [Fact]
    public void SeparatorsSurviveARoundTripThroughTheWriter()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();
        new SeparatorTitleCommand(sheet, 1, "Chapter").Execute();

        var text = PalaceDB.Core.Json.CdbWriter.Write(db);
        var reloaded = PalaceDB.Core.Json.CdbReader.ParseDatabase(text);
        var pairs = Fixtures.Items(reloaded).GetSeparators();

        Assert.Single(pairs);
        Assert.Equal(1, pairs[0].Index);
        Assert.Equal("Chapter", pairs[0].Title);
    }

    [Fact]
    public void ASheetWithoutSeparatorsDoesNotGainTheKey()
    {
        const string json = """
{
	"sheets": [
		{ "name": "A", "columns": [ { "typeStr": "0", "name": "id" } ], "lines": [ { "id": "x" } ] }
	],
	"customTypes": [],
	"compress": false
}
""";

        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        Assert.DoesNotContain("\"separators\"", PalaceDB.Core.Json.CdbWriter.Write(db));

        new SeparatorToggleCommand(db.GetSheet("A")!, 0).Execute();
        Assert.Contains("\"separators\"", PalaceDB.Core.Json.CdbWriter.Write(db));
    }

    private static string[] Titles(SheetData sheet) =>
        sheet.GetSeparators().Select(p => p.Title ?? string.Empty).ToArray();
}
