using PalaceDB.Core.Commands;
using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Requirement R3: after undo, deletion or any structural edit the cursor must never
/// jump back to the first cell. These tests pin the resolution rules.
/// </summary>
public class CursorResolverTests
{
    private static (Database Db, SheetData Sheet) Setup()
    {
        var db = Fixtures.Sample();
        return (db, Fixtures.Items(db));
    }

    [Fact]
    public void AnchorRoundTripsToTheSameCell()
    {
        var (db, sheet) = Setup();
        var anchor = CursorResolver.CreateAnchor(sheet, 1, 2);
        var position = CursorResolver.Resolve(anchor, db);

        Assert.Equal("Items", position.SheetName);
        Assert.Equal(1, position.Row);
        Assert.Equal(2, position.Column);
    }

    [Fact]
    public void AnchorCapturesTheRowIdAndColumnName()
    {
        var (db, sheet) = Setup();
        var anchor = CursorResolver.CreateAnchor(sheet, 1, 2);

        Assert.Equal("shield", anchor.RowId);
        Assert.Equal("cost", anchor.ColumnId);
    }

    [Fact]
    public void RowIsFoundByIdEvenAfterReordering()
    {
        var (db, sheet) = Setup();
        var anchor = CursorResolver.CreateAnchor(sheet, 1, 0);
        Assert.Equal("shield", anchor.RowId);

        // Move "shield" to the end: its index changes but its id does not.
        var row = sheet.Lines[1];
        sheet.Lines.RemoveAt(1);
        sheet.Lines.Add(row);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal(2, position.Row);
        Assert.Equal("shield", sheet.Lines[position.Row].GetString("id"));
    }

    [Fact]
    public void RowFallsBackToTheRecordedIndexWhenTheIdIsGone()
    {
        var (db, sheet) = Setup();
        var anchor = new CursorAnchor("Items", "deleted-row", 2, "cost", 2);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal(2, position.Row);
    }

    [Fact]
    public void OutOfRangeRowIsClampedToTheLastRow()
    {
        var (db, sheet) = Setup();
        var anchor = new CursorAnchor("Items", null, 99, "cost", 2);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal(sheet.Lines.Count - 1, position.Row);
    }

    [Fact]
    public void NegativeRowFallsBackToTheFirstRow()
    {
        var (db, sheet) = Setup();
        var anchor = new CursorAnchor("Items", null, -5, "cost", 2);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal(0, position.Row);
    }

    [Fact]
    public void ColumnIsFoundByNameEvenAfterReordering()
    {
        var (db, sheet) = Setup();
        var anchor = new CursorAnchor("Items", "shield", 1, "cost", 2);

        // Move the first column to the end.
        var first = sheet.Columns[0];
        sheet.Columns.RemoveAt(0);
        sheet.Columns.Add(first);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal("cost", sheet.Columns[position.Column].Name);
    }

    [Fact]
    public void OutOfRangeColumnIsClampedToTheLastColumn()
    {
        var (db, sheet) = Setup();
        var anchor = new CursorAnchor("Items", "shield", 1, "renamed-away", 99);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal(sheet.Columns.Count - 1, position.Column);
    }

    [Fact]
    public void KnownColumnNameWinsOverAStaleIndex()
    {
        var (db, sheet) = Setup();
        var anchor = new CursorAnchor("Items", "shield", 1, "cost", 0);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal("cost", sheet.Columns[position.Column].Name);
    }

    [Fact]
    public void UnknownSheetResolvesToNone()
    {
        var (db, _) = Setup();
        var position = CursorResolver.Resolve(new CursorAnchor("Nope", null, 0, null, 0), db);
        Assert.True(position.IsNone);
    }

    [Fact]
    public void NullAnchorResolvesToNone()
    {
        var (db, _) = Setup();
        Assert.True(CursorResolver.Resolve(null, db).IsNone);
    }

    [Fact]
    public void EmptySheetResolvesToNoIndex()
    {
        var (db, _) = Setup();
        var empty = db.GetSheet("Empty")!;
        var anchor = new CursorAnchor("Empty", null, 0, null, 0);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal(CursorResolver.NoIndex, position.Row);
        Assert.Equal(CursorResolver.NoIndex, position.Column);
    }

    [Fact]
    public void SheetWithoutIdColumnStillResolvesByIndex()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "Plain",
			"columns": [
				{
					"typeStr": "1",
					"name": "label"
				},
				{
					"typeStr": "3",
					"name": "value"
				}
			],
			"lines": [
				{
					"label": "a",
					"value": 1
				},
				{
					"label": "b",
					"value": 2
				}
			]
		}
	],
	"customTypes": []
}
""";
        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var sheet = db.GetSheet("Plain")!;

        var anchor = CursorResolver.CreateAnchor(sheet, 1, 1);
        Assert.Null(anchor.RowId);
        Assert.Equal("value", anchor.ColumnId);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal(1, position.Row);
        Assert.Equal(1, position.Column);
    }

    [Fact]
    public void SelectionIsPreservedAndClamped()
    {
        var (db, sheet) = Setup();
        var range = new SelectionRange(0, 0, 2, 3);
        var anchor = CursorResolver.CreateAnchor(sheet, 2, 3, range);

        var position = CursorResolver.Resolve(anchor, db);
        Assert.Equal(range, position.Selection);
    }

    [Fact]
    public void SelectionBeyondTheSheetIsClamped()
    {
        var (db, sheet) = Setup();
        var range = new SelectionRange(0, 0, 99, 99);
        var anchor = new CursorAnchor("Items", null, 0, null, 0, range);

        var position = CursorResolver.Resolve(anchor, db);
        var clamped = position.Selection!.Value;
        Assert.Equal(sheet.Lines.Count - 1, clamped.Bottom);
        Assert.Equal(sheet.Columns.Count - 1, clamped.Right);
    }

    [Fact]
    public void UndoRestoresTheCursorToTheEditedCell()
    {
        var (db, sheet) = Setup();
        var stack = new CommandStack();
        var row = sheet.Lines[1];

        var command = new CellEditCommand(row, "cost", CdbValue.Of(99))
        {
            CursorBefore = CursorResolver.CreateAnchor(sheet, 1, 2),
            CursorAfter = CursorResolver.CreateAnchor(sheet, 1, 2),
        };
        stack.Execute(command);

        var undone = stack.Undo()!;
        var position = CursorResolver.Resolve(undone.CursorBefore, db);

        Assert.Equal(1, position.Row);
        Assert.Equal(2, position.Column);
        Assert.NotEqual(0, position.Row + position.Column);
    }
}

/// <summary>Requirement R3: "delete moves to the next row, otherwise to the previous one".</summary>
public class CursorRulesTests
{
    [Theory]
    [InlineData(0, 1, 3, 0)]   // first of four rows deleted -> next row takes index 0
    [InlineData(1, 1, 3, 1)]   // middle row deleted -> the following row moves up
    [InlineData(2, 1, 3, 2)]   // second to last -> next row
    [InlineData(3, 1, 3, 2)]   // last row deleted -> previous row
    [InlineData(0, 3, 0, -1)]  // everything deleted -> no cursor
    [InlineData(2, 5, 2, 1)]   // range beyond the end -> clamp
    public void RowAfterDeleteFollowsTheSpecifiedRules(int index, int count, int remaining, int expected)
    {
        Assert.Equal(expected, CursorRules.RowAfterDelete(index, count, remaining));
    }

    [Fact]
    public void RowAfterDeleteNeverReturnsZeroForANonEmptySheetUnlessItIsFirst()
    {
        // The bug being prevented: deleting the last row must select the new last row,
        // not jump to row 0.
        Assert.Equal(4, CursorRules.RowAfterDelete(5, 1, 5));
        Assert.Equal(0, CursorRules.RowAfterDelete(0, 1, 5));
    }

    [Theory]
    [InlineData(0, 1, 0, -1)]
    [InlineData(0, 1, 1, 0)]
    [InlineData(5, 1, 3, 2)]
    public void ColumnAfterDeleteClamps(int index, int count, int remaining, int expected)
    {
        Assert.Equal(expected, CursorRules.ColumnAfterDelete(index, count, remaining));
    }

    [Theory]
    [InlineData(0, 1, 5, 0)]
    [InlineData(5, 2, 7, 5)]
    [InlineData(9, 1, 5, 4)]
    public void RowAfterInsertSelectsTheFirstInsertedRow(int index, int count, int total, int expected)
    {
        Assert.Equal(expected, CursorRules.RowAfterInsert(index, count, total));
    }
}
