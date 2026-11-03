using PalaceDB.Core.Commands;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>Table order (S04) and the generated index / group fields (S05 / S06).</summary>
public class SheetFeatureTests
{
    private const string Json = """
{
	"sheets": [
		{
			"name": "Items",
			"columns": [
				{
					"typeStr": "0",
					"name": "id"
				},
				{
					"typeStr": "1",
					"name": "name"
				}
			],
			"lines": [
				{
					"id": "a",
					"name": "A"
				},
				{
					"id": "b",
					"name": "B"
				},
				{
					"id": "c",
					"name": "C"
				},
				{
					"id": "d",
					"name": "D"
				},
				{
					"id": "e",
					"name": "E"
				}
			],
			"separators": [
				0,
				2,
				4
			],
			"props": {
				"separatorTitles": [
					"Head",
					null,
					"Tail"
				]
			}
		},
		{
			"name": "Items@Detail",
			"columns": [
				{
					"typeStr": "0",
					"name": "id"
				}
			],
			"lines": [],
			"props": {
				"hide": true
			}
		},
		{
			"name": "Other",
			"columns": [
				{
					"typeStr": "0",
					"name": "id"
				}
			],
			"lines": [],
			"props": {}
		}
	],
	"customTypes": [],
	"compress": false
}
""";

    private static Database Load() => CdbReader.ParseDatabase(Json);

    private static SheetData Items(Database db) => db.GetSheet("Items")!;

    private static List<double?> Column(SheetData sheet, string key) =>
        sheet.Lines.Select(row => (row.Get(key) as CdbValue.Number)?.Value).ToList();

    // ---------------------------------------------------------------- index

    [Fact]
    public void IndexIsWrittenOnEveryRow()
    {
        var db = Load();
        var sheet = Items(db);

        new SheetFeatureCommand(sheet, SheetFeature.Index, true).Execute();

        Assert.True(sheet.Props.HasIndex);
        Assert.Equal(new double?[] { 0, 1, 2, 3, 4 }, Column(sheet, "index"));
    }

    [Fact]
    public void IndexIsRecomputedOnSave()
    {
        var db = Load();
        var sheet = Items(db);
        new SheetFeatureCommand(sheet, SheetFeature.Index, true).Execute();

        // Move the last row to the top; the saved file must show the new order.
        var last = sheet.Lines[4];
        sheet.Lines.RemoveAt(4);
        sheet.Lines.Insert(0, last);

        var saved = CdbReader.ParseDatabase(CdbWriter.Write(db));
        var reloaded = Items(saved);

        Assert.Equal(new double?[] { 0, 1, 2, 3, 4 }, Column(reloaded, "index"));
        Assert.Equal("e", reloaded.Lines[0].GetString("id"));
    }

    [Fact]
    public void TurningIndexOffRemovesTheField()
    {
        var db = Load();
        var sheet = Items(db);

        new SheetFeatureCommand(sheet, SheetFeature.Index, true).Execute();
        new SheetFeatureCommand(sheet, SheetFeature.Index, false).Execute();

        Assert.False(sheet.Props.HasIndex);
        Assert.All(sheet.Lines, row => Assert.Null(row.Get("index")));
    }

    [Fact]
    public void IndexToggleIsUndoable()
    {
        var db = Load();
        var before = CdbWriter.Write(db);

        var command = new SheetFeatureCommand(Items(db), SheetFeature.Index, true);
        command.Execute();
        command.Undo();

        Assert.False(Items(db).Props.HasIndex);
        Assert.Equal(before, CdbWriter.Write(db));
    }

    [Fact]
    public void IndexIsNotWrittenWhenTheFeatureIsOff()
    {
        var db = Load();

        var saved = CdbReader.ParseDatabase(CdbWriter.Write(db));

        Assert.All(Items(saved).Lines, row => Assert.Null(row.Get("index")));
        Assert.False(Items(saved).Props.HasIndex);
    }

    // ---------------------------------------------------------------- group

    [Fact]
    public void GroupFollowsTheTitledSeparators()
    {
        var db = Load();
        var sheet = Items(db);

        new SheetFeatureCommand(sheet, SheetFeature.Group, true).Execute();

        // "Head" sits in front of row 0 and opens group 0; the untitled separator in
        // front of row 2 does not start a new group; "Tail" starts group 1 at row 4.
        Assert.True(sheet.Props.HasGroup);
        Assert.Equal(new double?[] { 0, 0, 0, 0, 1 }, Column(sheet, "group"));
    }

    [Fact]
    public void GroupIsRecomputedOnSave()
    {
        var db = Load();
        new SheetFeatureCommand(Items(db), SheetFeature.Group, true).Execute();

        var saved = CdbReader.ParseDatabase(CdbWriter.Write(db));

        Assert.Equal(new double?[] { 0, 0, 0, 0, 1 }, Column(Items(saved), "group"));
    }

    [Fact]
    public void GroupWithoutTitlesWritesNothing()
    {
        var db = Load();
        var sheet = Items(db);
        sheet.Props.SeparatorTitles = null;

        new SheetFeatureCommand(sheet, SheetFeature.Group, true).Execute();

        Assert.True(sheet.Props.HasGroup);
        Assert.All(sheet.Lines, row => Assert.Null(row.Get("group")));
    }

    [Fact]
    public void GroupStartsAtZeroWhenTheFirstSeparatorHasNoTitle()
    {
        var db = Load();
        var sheet = Items(db);

        // Separators in front of row 0 and row 2, only the second one has a title.
        sheet.Props.SeparatorTitles = new CdbValue.Arr
        {
            Items = { CdbValue.NullValue, new CdbValue.Str("Mid"), CdbValue.NullValue },
        };

        new SheetFeatureCommand(sheet, SheetFeature.Group, true).Execute();

        Assert.Equal(new double?[] { 0, 0, 1, 1, 1 }, Column(sheet, "group"));
    }

    [Fact]
    public void GroupToggleIsUndoable()
    {
        var db = Load();
        var before = CdbWriter.Write(db);

        var command = new SheetFeatureCommand(Items(db), SheetFeature.Group, true);
        command.Execute();
        command.Undo();

        Assert.False(Items(db).Props.HasGroup);
        Assert.Equal(before, CdbWriter.Write(db));
    }

    [Fact]
    public void TurningGroupOffRemovesTheField()
    {
        var db = Load();
        var sheet = Items(db);

        new SheetFeatureCommand(sheet, SheetFeature.Group, true).Execute();
        new SheetFeatureCommand(sheet, SheetFeature.Group, false).Execute();

        Assert.False(sheet.Props.HasGroup);
        Assert.All(sheet.Lines, row => Assert.Null(row.Get("group")));
    }

    // ---------------------------------------------------------------- conflicts

    [Fact]
    public void IndexIsRefusedWhenAColumnUsesTheName()
    {
        var db = Load();
        var sheet = Items(db);
        new ColumnInsertCommand(sheet, 2, "index", new ColumnType.Int(), db).Execute();

        var error = Assert.Throws<InvalidOperationException>(
            () => new SheetFeatureCommand(sheet, SheetFeature.Index, true));

        Assert.Contains("index", error.Message);
    }

    [Fact]
    public void GroupIsRefusedWhenAColumnUsesTheName()
    {
        var db = Load();
        var sheet = Items(db);
        new ColumnInsertCommand(sheet, 2, "group", new ColumnType.Int(), db).Execute();

        Assert.Throws<InvalidOperationException>(
            () => new SheetFeatureCommand(sheet, SheetFeature.Group, true));
    }

    [Fact]
    public void TogglingTwiceIsANoOp()
    {
        var db = Load();
        var sheet = Items(db);

        var command = new SheetFeatureCommand(sheet, SheetFeature.Index, false);

        Assert.True(command.IsNoOp);
    }

    // ---------------------------------------------------------------- table order

    private static string Order(Database db) => string.Join(",", db.Sheets.Select(s => s.Name));

    [Fact]
    public void MovingATableDownKeepsItsSubSheets()
    {
        var db = Load();
        var sheet = Items(db);

        new SheetMoveCommand(db, sheet, 1).Execute();

        Assert.Equal("Other,Items,Items@Detail", Order(db));
    }

    [Fact]
    public void MovingATableUpKeepsItsSubSheets()
    {
        var db = Load();

        new SheetMoveCommand(db, db.GetSheet("Other")!, -1).Execute();

        Assert.Equal("Other,Items,Items@Detail", Order(db));
    }

    [Fact]
    public void MoveIsUndoable()
    {
        var db = Load();
        var before = Order(db);

        var command = new SheetMoveCommand(db, Items(db), 1);
        command.Execute();
        command.Undo();

        Assert.Equal(before, Order(db));
    }

    [Fact]
    public void TheFirstTableCannotMoveUp()
    {
        var db = Load();

        var error = Assert.Throws<InvalidOperationException>(
            () => new SheetMoveCommand(db, Items(db), -1));

        Assert.Contains("first", error.Message);
    }

    [Fact]
    public void TheLastTableCannotMoveDown()
    {
        var db = Load();

        Assert.Throws<InvalidOperationException>(
            () => new SheetMoveCommand(db, db.GetSheet("Other")!, 1));
    }

    [Fact]
    public void MoveKeepsTheRawSheetArrayInStep()
    {
        var db = Load();

        new SheetMoveCommand(db, Items(db), 1).Execute();

        var saved = CdbReader.ParseDatabase(CdbWriter.Write(db));
        Assert.Equal("Other,Items,Items@Detail", Order(saved));
    }

    [Fact]
    public void SubSheetsAreNotTreatedAsTables()
    {
        var db = Load();

        // "Items@Detail" is hidden, so it is skipped when looking for the neighbour of
        // "Items"; moving "Items" down jumps over the whole block.
        var command = new SheetMoveCommand(db, Items(db), 1);
        command.Execute();

        Assert.Equal(0, db.Sheets.IndexOf(db.GetSheet("Other")!));
        Assert.Equal(1, db.Sheets.IndexOf(Items(db)));
        Assert.Equal(2, db.Sheets.IndexOf(db.GetSheet("Items@Detail")!));
    }
}
