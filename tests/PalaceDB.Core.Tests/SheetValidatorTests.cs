using PalaceDB.Core.Editing;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class SheetValidatorTests
{
    private static (Database Db, DatabaseIndex Index) Load(string json)
    {
        var db = CdbReader.ParseDatabase(json);
        return (db, new DatabaseIndex(db));
    }

    private static SheetIssue? First(Database db, DatabaseIndex index, string sheet) =>
        SheetValidator.FirstIssue(db.GetSheet(sheet)!, db, index);

    [Fact]
    public void ReportsTheFirstDuplicateIdentifier()
    {
        var db = Fixtures.Sample();
        var index = new DatabaseIndex(db);

        var issue = First(db, index, "Items");

        Assert.NotNull(issue);
        // Both rows carrying "sword" are flagged, so the first one is reported.
        Assert.Equal(0, issue!.Value.Row);
        Assert.Equal("id", issue.Value.Column);
        Assert.Contains("Duplicate", issue.Value.Detail);
    }

    [Fact]
    public void ReportsAMissingIdentifier()
    {
        var (db, index) = Load("""
{
	"sheets": [
		{
			"name": "Items",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "1", "name": "name" }
			],
			"lines": [
				{ "id": "sword", "name": "Sword" },
				{ "id": "", "name": "Nameless" }
			],
			"props": {}
		}
	],
	"customTypes": [],
	"compress": false
}
""");

        var issue = First(db, index, "Items");

        Assert.NotNull(issue);
        Assert.Equal(1, issue!.Value.Row);
        Assert.Equal("id", issue.Value.Column);
        Assert.Equal("Identifier is empty", issue.Value.Detail);
    }

    [Fact]
    public void ReportsAReferenceToARowThatDoesNotExist()
    {
        var (db, index) = Load("""
{
	"sheets": [
		{
			"name": "Items",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "6:Items", "name": "parent" }
			],
			"lines": [
				{ "id": "sword", "parent": "ghost" }
			],
			"props": {}
		}
	],
	"customTypes": [],
	"compress": false
}
""");

        var issue = First(db, index, "Items");

        Assert.NotNull(issue);
        Assert.Equal(0, issue!.Value.Row);
        Assert.Equal("parent", issue.Value.Column);
        Assert.Equal("Referenced row not found", issue.Value.Detail);
    }

    [Fact]
    public void ReportsAReferenceToAMissingTable()
    {
        var (db, index) = Load("""
{
	"sheets": [
		{
			"name": "Items",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "6:Nowhere", "name": "parent" }
			],
			"lines": [
				{ "id": "sword", "parent": "ghost" }
			],
			"props": {}
		}
	],
	"customTypes": [],
	"compress": false
}
""");

        var issue = First(db, index, "Items");

        Assert.NotNull(issue);
        Assert.Contains("Nowhere", issue!.Value.Detail);
    }

    [Fact]
    public void IgnoresAnEmptyOptionalReference()
    {
        var (db, index) = Load("""
{
	"sheets": [
		{
			"name": "Items",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "6:Items", "name": "parent", "opt": true }
			],
			"lines": [
				{ "id": "sword" }
			],
			"props": {}
		}
	],
	"customTypes": [],
	"compress": false
}
""");

        Assert.Null(First(db, index, "Items"));
    }

    [Fact]
    public void ACleanTableHasNoIssue()
    {
        var db = Fixtures.Sample();
        var index = new DatabaseIndex(db);

        Assert.Null(First(db, index, "Empty"));
    }
}
