using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class SheetIndexTests
{
    [Fact]
    public void IndexesRowsByTheirIdentifier()
    {
        var db = Fixtures.Sample();
        var index = new SheetIndex(Fixtures.Items(db));

        Assert.True(index.ById.ContainsKey("shield"));
        Assert.Equal("Shield", index.Get("shield")!.GetString("name"));
    }

    [Fact]
    public void MissingIdentifierReturnsNull()
    {
        var db = Fixtures.Sample();
        var index = new SheetIndex(Fixtures.Items(db));

        Assert.Null(index.Get("nope"));
        Assert.Null(index.Get(null));
        Assert.Null(index.Get(string.Empty));
    }

    [Fact]
    public void DuplicateIdentifiersAreReported()
    {
        var db = Fixtures.Sample();
        var index = new SheetIndex(Fixtures.Items(db));

        Assert.Contains("sword", index.Duplicates);
        Assert.True(index.IsDuplicate("sword"));
        Assert.False(index.IsDuplicate("shield"));
    }

    [Fact]
    public void FirstRowWinsWhenAnIdentifierIsDuplicated()
    {
        var db = Fixtures.Sample();
        var index = new SheetIndex(Fixtures.Items(db));

        Assert.Equal("Sword", index.Get("sword")!.GetString("name"));
    }

    [Fact]
    public void OrderedIsSortedByDisplayName()
    {
        var db = Fixtures.Sample();
        var index = new SheetIndex(Fixtures.Items(db));

        Assert.Equal(
            new[] { "Shield", "Sword", "Sword duplicate" },
            index.Ordered.Select(r => index.GetDisplay(r)));
    }

    [Fact]
    public void DisplayFallsBackToTheIdentifierWhenNoDisplayColumnIsSet()
    {
        const string json = """
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
					"name": "label"
				}
			],
			"lines": [
				{
					"id": "abc",
					"label": "Label"
				}
			]
		}
	],
	"customTypes": []
}
""";
        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var sheet = db.GetSheet("T")!;
        var index = new SheetIndex(sheet);

        Assert.Equal("abc", index.GetDisplay(sheet.Lines[0]));
    }

    [Fact]
    public void RowsWithoutAnIdentifierAreIgnored()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "T",
			"columns": [
				{
					"typeStr": "0",
					"name": "id"
				}
			],
			"lines": [
				{
					"id": ""
				},
				{
					"id": "ok"
				}
			]
		}
	],
	"customTypes": []
}
""";
        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var index = new SheetIndex(db.GetSheet("T")!);

        Assert.Single(index.ById);
        Assert.Single(index.Ordered);
    }

    [Fact]
    public void DatabaseIndexRebuildsAfterInvalidation()
    {
        var db = Fixtures.Sample();
        var indexes = new DatabaseIndex(db);

        var before = indexes.For("Items")!;
        Assert.NotNull(before.Get("sword"));

        var row = new CdbObject();
        row.Set("id", new CdbValue.Str("potion"));
        Fixtures.Items(db).Lines.Add(row);

        Assert.Null(before.Get("potion"));

        indexes.Invalidate("Items");
        var after = indexes.For("Items")!;
        Assert.NotSame(before, after);
        Assert.NotNull(after.Get("potion"));
    }

    [Fact]
    public void DatabaseIndexReturnsNullForUnknownSheet()
    {
        var db = Fixtures.Sample();
        var indexes = new DatabaseIndex(db);
        Assert.Null(indexes.For("DoesNotExist"));
    }

    [Fact]
    public void HiddenSheetsAreExcludedFromVisibleSheets()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        sheet.Props.Hide = true;

        Assert.DoesNotContain(db.VisibleSheets, s => s.Name == "Items");
        Assert.Contains(db.VisibleSheets, s => s.Name == "Empty");
    }
}
