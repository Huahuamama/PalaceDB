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

    // ---------------------------------------------------------------- row inserts

    [Fact]
    public void InsertingRowsAboveASeparatorMovesItDown()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();

        new RowInsertCommand(sheet, 0, new[] { new CdbObject() }).Execute();

        Assert.Equal(new[] { 2 }, sheet.Separators);
    }

    [Fact]
    public void InsertingRowsAtTheSeparatorKeepsItAboveTheNewRows()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();

        new RowInsertCommand(sheet, 1, new[] { new CdbObject(), new CdbObject() }).Execute();

        // The new rows belong to the section the heading introduces, so the heading stays.
        Assert.Equal(new[] { 1 }, sheet.Separators);
    }

    [Fact]
    public void InsertingRowsBelowASeparatorLeavesItAlone()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();

        new RowInsertCommand(sheet, 2, new[] { new CdbObject() }).Execute();

        Assert.Equal(new[] { 1 }, sheet.Separators);
    }

    [Fact]
    public void InsertingBelowTheLastRowOfAGroupKeepsTheRowInThatGroup()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();
        new SeparatorToggleCommand(sheet, 2).Execute();

        // Row 1 is all the first group owns, and the index the new row lands on is the one
        // the next heading sits in. "Below" is anchored to row 1, so it joins that group
        // and the second heading moves down instead of adopting it.
        new RowInsertCommand(sheet, 2, new[] { new CdbObject() }, after: true).Execute();

        Assert.Equal(new[] { 1, 3 }, sheet.Separators);
    }

    [Fact]
    public void InsertUndoRestoresTheSeparatorIndex()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();

        var command = new RowInsertCommand(sheet, 0, new[] { new CdbObject() });
        command.Execute();
        command.Undo();

        Assert.Equal(new[] { 1 }, sheet.Separators);
    }

    [Fact]
    public void ATitleMovesWithItsSeparator()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 2).Execute();
        new SeparatorTitleCommand(sheet, 2, "Group").Execute();

        new RowInsertCommand(sheet, 0, new[] { new CdbObject() }).Execute();

        var pairs = sheet.GetSeparators();
        Assert.Single(pairs);
        Assert.Equal(3, pairs[0].Index);
        Assert.Equal("Group", pairs[0].Title);
    }

    // ---------------------------------------------------------------- row deletes

    [Fact]
    public void DeletingRowsAboveASeparatorMovesItUp()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 2).Execute();

        new RowDeleteCommand(sheet, 0, 1).Execute();

        Assert.Equal(new[] { 1 }, sheet.Separators);
    }

    [Fact]
    public void DeletingTheRowUnderASeparatorKeepsTheHeadingAboveTheNextRow()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();

        new RowDeleteCommand(sheet, 1, 1).Execute();

        Assert.Equal(new[] { 1 }, sheet.Separators);
    }

    [Fact]
    public void DeletingTheLastRowDoesNotLeaveATrailingSeparator()
    {
        var (db, sheet) = Sample();
        var last = sheet.Lines.Count - 1;
        new SeparatorToggleCommand(sheet, last).Execute();

        new RowDeleteCommand(sheet, last, 1).Execute();

        Assert.Empty(sheet.Separators);
    }

    [Fact]
    public void DeleteUndoRestoresTheSeparators()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 2).Execute();
        new SeparatorTitleCommand(sheet, 2, "Group").Execute();

        var command = new RowDeleteCommand(sheet, 0, 1);
        command.Execute();
        command.Undo();

        var pairs = sheet.GetSeparators();
        Assert.Single(pairs);
        Assert.Equal(2, pairs[0].Index);
        Assert.Equal("Group", pairs[0].Title);
    }

    [Fact]
    public void TwoHeadingsThatCollideAfterADeleteBecomeOne()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();
        new SeparatorToggleCommand(sheet, 2).Execute();

        // Row 1 goes away: the heading on it had nothing left under it, so it goes too and
        // the one on row 2 moves up onto row 1. The list stays sorted and free of duplicates.
        new RowDeleteCommand(sheet, 1, 1).Execute();

        Assert.Equal(new[] { 1 }, sheet.Separators);
    }

    [Fact]
    public void DeletingTheLastRowOfAGroupRemovesThatHeadingAndKeepsTheNextOne()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();
        new SeparatorTitleCommand(sheet, 1, "First").Execute();
        new SeparatorToggleCommand(sheet, 2).Execute();
        new SeparatorTitleCommand(sheet, 2, "Second").Execute();

        // Row 1 was all that "First" owned, so the heading goes with it. "Second" moves up
        // to take its place instead of being swallowed by a heading that has no rows left.
        new RowDeleteCommand(sheet, 1, 1).Execute();

        var pairs = sheet.GetSeparators();
        Assert.Single(pairs);
        Assert.Equal(1, pairs[0].Index);
        Assert.Equal("Second", pairs[0].Title);
    }

    // ---------------------------------------------------------------- row moves

    [Fact]
    public void DraggingTheFirstRowOfAGroupLeavesTheHeadingBehind()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();

        // The heading owns the rows under it, so dragging the row it happens to start with
        // away does not move the heading: the group keeps the rows it still owns.
        new RowMoveCommand(sheet, 1, 2).Execute();

        Assert.Equal(new[] { 1 }, sheet.Separators);
    }

    [Fact]
    public void DraggingARowDownMovesTheSeparatorItPassedUp()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 2).Execute();

        // Row 0 goes below row 2, so the heading that was on row 2 now heads row 1.
        new RowMoveCommand(sheet, 0, 2).Execute();

        Assert.Equal(new[] { 1 }, sheet.Separators);
    }

    [Fact]
    public void DraggingARowUpMovesTheSeparatorItPassedDown()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();

        new RowMoveCommand(sheet, 2, 0).Execute();

        Assert.Equal(new[] { 2 }, sheet.Separators);
    }

    [Fact]
    public void DraggingARowWithoutASeparatorLeavesTheOtherHeadingsAlone()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 0).Execute();

        new RowMoveCommand(sheet, 1, 2).Execute();

        Assert.Equal(new[] { 0 }, sheet.Separators);
    }

    [Fact]
    public void DraggingTheOnlyRowOfAGroupTakesItsHeadingWithIt()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 2).Execute();
        new SeparatorTitleCommand(sheet, 2, "Last").Execute();

        // Nothing is left under the heading, so it goes as well.
        new RowMoveCommand(sheet, 2, 0).Execute();

        Assert.Empty(sheet.Separators);
    }

    [Fact]
    public void MoveUndoRestoresTheSeparatorIndex()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();
        new SeparatorTitleCommand(sheet, 1, "Group").Execute();

        var command = new RowMoveCommand(sheet, 1, 2);
        command.Execute();
        command.Undo();

        var pairs = sheet.GetSeparators();
        Assert.Single(pairs);
        Assert.Equal(1, pairs[0].Index);
        Assert.Equal("Group", pairs[0].Title);
    }

    [Fact]
    public void TheSideOfTheHeadingDecidesTheGroupTheDroppedRowJoins()
    {
        // A heading is drawn inside the gap in front of its first row, so one landing gap
        // means two different groups. Under the heading the row joins it; above it the row
        // stays with the rows before, and the heading moves down to the row it still owns.
        var (_, under) = Sample();
        new SeparatorToggleCommand(under, 1).Execute();
        new SeparatorTitleCommand(under, 1, "First").Execute();
        new RowMoveCommand(under, 2, 1, below: true).Execute();

        var (_, above) = Sample();
        new SeparatorToggleCommand(above, 1).Execute();
        new SeparatorTitleCommand(above, 1, "First").Execute();
        new RowMoveCommand(above, 2, 1, below: false).Execute();

        Assert.Equal(1, under.GetSeparators()[0].Index);
        Assert.Equal(2, above.GetSeparators()[0].Index);
        Assert.Equal(under.Groups.Items[0].Rows, above.Groups.Items[0].Rows + 1);
    }

    [Fact]
    public void DroppingARowOnTheUpperHalfOfItsOwnHeadingTakesItOutOfTheGroup()
    {
        var (db, sheet) = Sample();
        new SeparatorToggleCommand(sheet, 1).Execute();
        new SeparatorTitleCommand(sheet, 1, "First").Execute();

        // Row 1 is the row the heading introduces. Dropped on the heading itself it does
        // not move, but it leaves the group, so the heading slides down to the rows it
        // still owns. That is a move with the same index at both ends.
        new RowMoveCommand(sheet, 1, 1, below: false).Execute();

        var pairs = sheet.GetSeparators();
        Assert.Single(pairs);
        Assert.Equal(2, pairs[0].Index);
        Assert.Equal("First", pairs[0].Title);
    }

    // ---------------------------------------------------------------- empty groups

    /// <summary>
    /// A heading may own no rows at all. A file says so by naming the same row twice, which
    /// is what the sample database carries at the end of one of its tables, so every part of
    /// the model has to keep working when one of the groups is empty.
    /// </summary>
    private static SheetData WithEmptyGroup()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "A",
			"columns": [ { "typeStr": "0", "name": "id" } ],
			"lines": [ { "id": "a" }, { "id": "b" }, { "id": "c" }, { "id": "d" } ],
			"separators": [1, 1, 3, 4],
			"props": { "separatorTitles": ["Empty", "Filled", "Next", "Trailing"] }
		}
	],
	"customTypes": [],
	"compress": false
}
""";
        return PalaceDB.Core.Json.CdbReader.ParseDatabase(json).GetSheet("A")!;
    }

    [Fact]
    public void AnEmptyGroupStillOpensAGroupOfItsOwn()
    {
        var sheet = WithEmptyGroup();
        Assert.Equal(new[] { 1, 1, 3, 4 }, sheet.Separators);

        DerivedFields.ApplyGroup(sheet);

        // Row 0 has no heading. "Empty" and "Filled" both sit in front of row 1, so the
        // first of them opens a group that owns nothing and the second one opens the group
        // the rows actually belong to. Consuming one heading per row left "Next" and
        // "Trailing" pointing at the wrong number.
        Assert.Equal(new double?[] { 0, 2, 2, 3 }, GroupColumn(sheet));
    }

    [Fact]
    public void AnEmptyHeadingIsSkippedWhenARowIsTakenOutOfItsNeighbour()
    {
        var sheet = WithEmptyGroup();

        // Row 1 belongs to "Filled"; "Empty" in front of it owns nothing and must not
        // absorb the removal, or it would start reporting a negative row count.
        new RowDeleteCommand(sheet, 1, 1).Execute();

        Assert.Equal(new[] { 1, 1, 2, 3 }, sheet.Separators);
        Assert.Equal(0, sheet.Groups.Items[0].Rows);
        Assert.Equal(1, sheet.Groups.Items[1].Rows);
    }

    private static List<double?> GroupColumn(SheetData sheet) =>
        sheet.Lines.Select(row => (row.Get(DerivedFields.GroupKey) as CdbValue.Number)?.Value).ToList();

    private static string[] Titles(SheetData sheet) =>
        sheet.GetSeparators().Select(p => p.Title ?? string.Empty).ToArray();
}
