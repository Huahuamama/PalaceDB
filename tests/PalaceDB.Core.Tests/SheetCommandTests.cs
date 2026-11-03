using PalaceDB.Core.Commands;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class SheetCommandTests
{
    [Fact]
    public void InsertCreatesATableWithAnIdColumn()
    {
        var db = Fixtures.Sample();

        var command = new SheetInsertCommand(db, db.Sheets.Count, "Quest");
        command.Execute();

        var sheet = db.GetSheet("Quest");
        Assert.NotNull(sheet);
        Assert.Single(sheet!.Columns);
        Assert.Equal("id", sheet.Columns[0].Name);
        Assert.IsType<ColumnType.Id>(sheet.Columns[0].Type);
        Assert.Empty(sheet.Lines);
    }

    [Fact]
    public void InsertUndoRemovesTheTable()
    {
        var db = Fixtures.Sample();
        var before = db.Sheets.Count;

        var command = new SheetInsertCommand(db, 0, "Quest");
        command.Execute();
        command.Undo();

        Assert.Equal(before, db.Sheets.Count);
        Assert.Null(db.GetSheet("Quest"));
    }

    [Fact]
    public void DeleteRemovesTheTableAndItsHiddenSubTables()
    {
        var db = Fixtures.Sample();

        var sub = new SheetInsertCommand(db, db.Sheets.Count, "Items@extras");
        sub.Execute();

        var command = new SheetDeleteCommand(db, "Items");
        command.Execute();

        Assert.Null(db.GetSheet("Items"));
        Assert.Null(db.GetSheet("Items@extras"));
        Assert.Equal(2, command.RemovedCount);
    }

    [Fact]
    public void DeleteTurnsReferenceColumnsIntoTextAndUndoRestoresThem()
    {
        const string json = """
{
	"sheets": [
		{ "name": "A", "columns": [ { "typeStr": "0", "name": "id" }, { "typeStr": "6:B", "name": "target" } ], "lines": [ { "id": "a1", "target": "b1" } ] },
		{ "name": "B", "columns": [ { "typeStr": "0", "name": "id" } ], "lines": [ { "id": "b1" } ] }
	],
	"customTypes": [],
	"compress": false
}
""";

        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var a = db.GetSheet("A")!;
        Assert.IsType<ColumnType.Ref>(a.GetColumn("target")!.Type);

        var command = new SheetDeleteCommand(db, "B");
        command.Execute();

        Assert.IsType<ColumnType.Text>(a.GetColumn("target")!.Type);
        Assert.Equal("b1", a.Lines[0].GetString("target"));

        command.Undo();

        Assert.IsType<ColumnType.Ref>(a.GetColumn("target")!.Type);
        Assert.Equal("B", Assert.IsType<ColumnType.Ref>(a.GetColumn("target")!.Type).Sheet);
    }

    [Fact]
    public void DeleteUndoRestoresTheTableAndItsRows()
    {
        var db = Fixtures.Sample();
        var rowCount = Fixtures.Items(db).Lines.Count;

        var command = new SheetDeleteCommand(db, "Items");
        command.Execute();
        command.Undo();

        var sheet = db.GetSheet("Items");
        Assert.NotNull(sheet);
        Assert.Equal(rowCount, sheet!.Lines.Count);
        Assert.Equal(10, sheet.Lines[0].GetInt("cost"));
    }

    [Fact]
    public void RenameAlsoRenamesTheHiddenSubTables()
    {
        var db = Fixtures.Sample();
        new SheetInsertCommand(db, db.Sheets.Count, "Items@extras").Execute();

        var command = new SheetRenameCommand(db, "Items", "Things");
        command.Execute();

        Assert.NotNull(db.GetSheet("Things"));
        Assert.NotNull(db.GetSheet("Things@extras"));
        Assert.Null(db.GetSheet("Items@extras"));

        command.Undo();

        Assert.NotNull(db.GetSheet("Items@extras"));
    }

    [Fact]
    public void RenameUpdatesTheReferenceColumnsThatPointAtTheTable()
    {
        var db = Fixtures.Sample();
        var items = Fixtures.Items(db);

        new SheetRenameCommand(db, "Items", "Things").Execute();

        var parent = items.GetColumn("parent")!;
        var type = Assert.IsType<ColumnType.Ref>(parent.Type);
        Assert.Equal("Things", type.Sheet);
    }

    [Fact]
    public void TheRootArrayStaysInSyncWithTheTypedList()
    {
        var db = Fixtures.Sample();
        var rootCount = db.Root.GetArray("sheets")!.Items.Count;

        new SheetInsertCommand(db, 1, "Quest").Execute();

        Assert.Equal(db.Sheets.Count, db.Root.GetArray("sheets")!.Items.Count);
        Assert.Equal(rootCount + 1, db.Sheets.Count);
    }

    // ------------------------------------------------------------ display column

    [Fact]
    public void DisplayColumnCanBeSetAndCleared()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);

        var command = new DisplayColumnCommand(sheet, "name");
        command.Execute();
        Assert.Equal("name", sheet.Props.DisplayColumn);

        var clear = new DisplayColumnCommand(sheet, string.Empty);
        clear.Execute();
        Assert.Null(sheet.Props.DisplayColumn);
    }

    [Fact]
    public void ClearingTheDisplayColumnRemovesThePropertyKey()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        new DisplayColumnCommand(sheet, "name").Execute();

        new DisplayColumnCommand(sheet, null).Execute();

        Assert.False(sheet.Props.Raw.ContainsKey("displayColumn"));
    }

    [Fact]
    public void DisplayColumnIsUndoable()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        var original = sheet.Props.DisplayColumn;

        var command = new DisplayColumnCommand(sheet, "cost");
        command.Execute();
        Assert.Equal("cost", sheet.Props.DisplayColumn);

        command.Undo();

        Assert.Equal(original, sheet.Props.DisplayColumn);
    }
}
