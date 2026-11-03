using PalaceDB.Core.Commands;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Regression tests for row persistence. Rows are edited through the typed
/// <see cref="SheetData.Lines"/> list, so the raw <c>lines</c> array has to be rebuilt
/// before writing; without that step inserts, deletes and moves never reached the file.
/// </summary>
public class RowPersistenceTests
{
    private const string Json = """
{
	"sheets": [
		{
			"name": "T",
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
				}
			],
			"props": {}
		}
	],
	"customTypes": [],
	"compress": false
}
""";

    private static Database Load() => CdbReader.ParseDatabase(Json);

    private static Database Reload(Database db) => CdbReader.ParseDatabase(CdbWriter.Write(db));

    private static string Ids(Database db) =>
        string.Join(",", db.GetSheet("T")!.Lines.Select(r => r.GetString("id")));

    [Fact]
    public void AnInsertedRowIsWritten()
    {
        var db = Load();
        var sheet = db.GetSheet("T")!;

        var row = new CdbObject();
        row.Set("id", new CdbValue.Str("d"));
        row.Set("name", new CdbValue.Str("D"));
        new RowInsertCommand(sheet, 3, new[] { row }).Execute();

        var reloaded = Reload(db);
        Assert.Equal("a,b,c,d", Ids(reloaded));
        Assert.Equal(4, reloaded.GetSheet("T")!.Lines.Count);
    }

    [Fact]
    public void ADeletedRowIsNotWritten()
    {
        var db = Load();

        new RowDeleteCommand(db.GetSheet("T")!, 1, 1).Execute();

        var reloaded = Reload(db);
        Assert.Equal("a,c", Ids(reloaded));
    }

    [Fact]
    public void ADeletedRowComesBackOnUndo()
    {
        var db = Load();
        var command = new RowDeleteCommand(db.GetSheet("T")!, 1, 1);

        command.Execute();
        command.Undo();

        Assert.Equal("a,b,c", Ids(Reload(db)));
    }

    [Fact]
    public void AMovedRowKeepsItsNewPosition()
    {
        var db = Load();

        new RowMoveCommand(db.GetSheet("T")!, 2, 0).Execute();

        Assert.Equal("c,a,b", Ids(Reload(db)));
    }

    [Fact]
    public void CellEditsAndRowOrderSurviveTogether()
    {
        var db = Load();
        var sheet = db.GetSheet("T")!;

        new RowMoveCommand(sheet, 0, 2).Execute();
        sheet.Lines[0].Set("name", new CdbValue.Str("edited"));

        var reloaded = Reload(db);
        Assert.Equal("b,c,a", Ids(reloaded));
        Assert.Equal("edited", reloaded.GetSheet("T")!.Lines[0].GetString("name"));
    }

    [Fact]
    public void SavingAnUntouchedDocumentIsByteIdentical()
    {
        var db = Load();

        Assert.Equal(Json, CdbWriter.Write(db));
    }

    [Fact]
    public void ASheetWithoutRowsKeepsItsLinesArray()
    {
        var db = Load();
        var sheet = db.GetSheet("T")!;
        sheet.Lines.Clear();

        var text = CdbWriter.Write(db);

        // The key must stay present (PalaceDB always writes it) but empty.
        Assert.Contains("\"lines\": []", text);
        Assert.Empty(Reload(db).GetSheet("T")!.Lines);
    }
}
