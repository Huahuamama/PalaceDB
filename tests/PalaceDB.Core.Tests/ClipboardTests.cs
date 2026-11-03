using PalaceDB.Core.Commands;
using PalaceDB.Core.Editing;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class ClipboardTests
{
    private static (Database Db, SheetData Sheet) Setup()
    {
        var db = Fixtures.Sample();
        return (db, Fixtures.Items(db));
    }

    [Fact]
    public void SerializeThenDeserializeRoundTrips()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 0, 1 }, new[] { "id", "name", "cost" }, includeIdColumn: true);

        var payload = ClipboardService.Deserialize(text);
        Assert.NotNull(payload);
        Assert.Equal("Items", payload!.Sheet);
        Assert.Equal(new[] { "id", "name", "cost" }, payload.Columns);
        Assert.Equal(2, payload.Rows.Count);
        Assert.Equal("sword", ((CdbValue.Str)payload.Rows[0][0]).Value);
        Assert.Equal(10d, ((CdbValue.Number)payload.Rows[0][2]).Value);
    }

    [Fact]
    public void IdentifierColumnIsSkippedWhenRequested()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 0 }, new[] { "id", "name" }, includeIdColumn: false);

        var payload = ClipboardService.Deserialize(text)!;
        Assert.Equal(new[] { "name" }, payload.Columns);
    }

    [Fact]
    public void NestedValuesSurvive()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "T",
			"columns": [
				{
					"typeStr": "16",
					"name": "data"
				}
			],
			"lines": [
				{
					"data": {
						"a": [
							1,
							2
						]
					}
				}
			]
		}
	],
	"customTypes": []
}
""";
        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var sheet = db.GetSheet("T")!;

        var text = ClipboardService.Serialize(sheet, new[] { 0 }, new[] { "data" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var value = Assert.IsType<CdbValue.Obj>(payload.Rows[0][0]);
        var list = Assert.IsType<CdbValue.Arr>(value.Value.Get("a"));
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void DeserializeRejectsForeignText()
    {
        Assert.Null(ClipboardService.Deserialize("hello"));
        Assert.Null(ClipboardService.Deserialize(""));
        Assert.Null(ClipboardService.Deserialize(null));
        Assert.Null(ClipboardService.Deserialize("""{"kind":"other"}"""));
    }

    [Fact]
    public void DeserializeRejectsBrokenJson()
    {
        Assert.Null(ClipboardService.Deserialize("{ not json"));
    }

    [Fact]
    public void OverwriteWritesTheIdentifierWhenTheBlockCarriesIt()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 0 }, new[] { "id", "name", "cost" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var stack = new CommandStack();
        var command = PasteService.BuildOverwrite(sheet, 1, 0, payload);
        Assert.NotNull(command);
        stack.Execute(command!);

        // Copying the identifier column and pasting it back replaces the keys, which is
        // how a column of ids is pasted in.
        Assert.Equal("sword", sheet.Lines[1].GetString("id"));
        Assert.Equal("Sword", sheet.Lines[1].GetString("name"));
        Assert.Equal(10d, ((CdbValue.Number)sheet.Lines[1].Get("cost")!).Value);
    }

    [Fact]
    public void OverwriteWithoutTheIdentifierLeavesItAlone()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 0 }, new[] { "name", "cost" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var stack = new CommandStack();
        stack.Execute(PasteService.BuildOverwrite(sheet, 1, 1, payload)!);

        // Row 1 is "shield"; a block without identifiers must not touch its key.
        Assert.Equal("shield", sheet.Lines[1].GetString("id"));
        Assert.Equal("Sword", sheet.Lines[1].GetString("name"));
    }

    [Fact]
    public void OverwriteIsUndoable()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 0 }, new[] { "name" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var stack = new CommandStack();
        stack.Execute(PasteService.BuildOverwrite(sheet, 1, 0, payload)!);
        Assert.Equal("Sword", sheet.Lines[1].GetString("name"));

        stack.Undo();
        Assert.Equal("Shield", sheet.Lines[1].GetString("name"));
    }

    [Fact]
    public void OverwriteClipsAtTheEndOfTheSheet()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 0, 1, 2 }, new[] { "name" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var stack = new CommandStack();
        var command = PasteService.BuildOverwrite(sheet, 2, 0, payload);
        stack.Execute(command!);

        Assert.Equal(3, sheet.Lines.Count);
        Assert.Equal("Sword", sheet.Lines[2].GetString("name"));
    }

    [Fact]
    public void InsertCreatesNewRowsAboveTheCursor()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 0 }, new[] { "id", "name" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var stack = new CommandStack();
        stack.Execute(PasteService.BuildInsert(sheet, 1, payload, db)!);

        Assert.Equal(4, sheet.Lines.Count);
        Assert.Equal("Sword", sheet.Lines[1].GetString("name"));
        // The copied identifier already existed, so a suffix was appended.
        Assert.Equal("sword_2", sheet.Lines[1].GetString("id"));
    }

    [Fact]
    public void InsertIsUndoable()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 0 }, new[] { "id", "name" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var stack = new CommandStack();
        stack.Execute(PasteService.BuildInsert(sheet, 1, payload, db)!);
        Assert.Equal(4, sheet.Lines.Count);

        stack.Undo();
        Assert.Equal(3, sheet.Lines.Count);
        Assert.Equal("shield", sheet.Lines[1].GetString("id"));
    }

    [Fact]
    public void InsertGeneratesIdentifiersWhenThePayloadHasNone()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 1 }, new[] { "name" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var stack = new CommandStack();
        stack.Execute(PasteService.BuildInsert(sheet, 0, payload, db)!);

        Assert.Equal(4, sheet.Lines.Count);
        var id = sheet.Lines[0].GetString("id");
        Assert.Equal("row1", id);
        Assert.NotEqual("sword", id);
        Assert.NotEqual("shield", id);
    }

    [Fact]
    public void InsertFillsRequiredColumnsWithDefaults()
    {
        var (db, sheet) = Setup();
        var text = ClipboardService.Serialize(sheet, new[] { 1 }, new[] { "name" }, true);
        var payload = ClipboardService.Deserialize(text)!;

        var stack = new CommandStack();
        stack.Execute(PasteService.BuildInsert(sheet, 0, payload, db)!);

        var row = sheet.Lines[0];
        Assert.True(row.ContainsKey("cost"));
        Assert.True(row.ContainsKey("enabled"));
    }

    [Fact]
    public void MakeUniqueAppendsIncreasingSuffixes()
    {
        var existing = new HashSet<string> { "a", "a_2" };
        Assert.Equal("a_3", PasteService.MakeUnique("a", existing));
        Assert.Equal("b", PasteService.MakeUnique("b", existing));
    }

    [Fact]
    public void PasteIgnoresColumnsThatTheTargetSheetDoesNotHave()
    {
        var (db, sheet) = Setup();
        var payload = new ClipboardPayload
        {
            Sheet = "Other",
            Columns = new List<string> { "name", "unknown" },
            Rows = new List<List<CdbValue>>
            {
                new() { CdbValue.Of("Pasted"), CdbValue.Of("ignored") },
            },
        };

        var stack = new CommandStack();
        stack.Execute(PasteService.BuildOverwrite(sheet, 0, 0, payload)!);

        Assert.Equal("Pasted", sheet.Lines[0].GetString("name"));
        Assert.Null(sheet.Lines[0].Get("unknown"));
    }
}
