using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class TypeDefaultsTests
{
    [Fact]
    public void OptionalColumnDefaultsToNull()
    {
        var db = Fixtures.Sample();
        var column = Fixtures.Column(db, "Items", "note");
        Assert.Null(TypeDefaults.Create(column, db));
    }

    [Fact]
    public void OptionalColumnCanBeForced()
    {
        var db = Fixtures.Sample();
        var column = Fixtures.Column(db, "Items", "note");
        var value = TypeDefaults.Create(column, db, ignoreOptional: true);
        Assert.Equal(string.Empty, Assert.IsType<CdbValue.Str>(value).Value);
    }

    [Fact]
    public void TextDefaultsToEmptyString()
    {
        var db = Fixtures.Sample();
        var value = TypeDefaults.Create(Fixtures.Column(db, "Items", "name"), db);
        Assert.Equal(string.Empty, Assert.IsType<CdbValue.Str>(value).Value);
    }

    [Fact]
    public void NumericTypesDefaultToZero()
    {
        var db = Fixtures.Sample();
        foreach (var name in new[] { "cost", "weight", "rarity", "tags", "color" })
        {
            var value = TypeDefaults.Create(Fixtures.Column(db, "Items", name), db);
            Assert.Equal(0d, Assert.IsType<CdbValue.Number>(value).Value);
        }
    }

    [Fact]
    public void BoolDefaultsToFalse()
    {
        var db = Fixtures.Sample();
        var value = TypeDefaults.Create(Fixtures.Column(db, "Items", "enabled"), db);
        Assert.False(Assert.IsType<CdbValue.Bool>(value).Value);
    }

    [Fact]
    public void ReferenceDefaultsToTheFirstRowOfTheTargetSheet()
    {
        var db = Fixtures.Sample();
        var value = TypeDefaults.Create(Fixtures.Column(db, "Items", "parent"), db, ignoreOptional: true);
        Assert.Equal("sword", Assert.IsType<CdbValue.Str>(value).Value);
    }

    [Fact]
    public void ReferenceToAnEmptySheetDefaultsToEmptyString()
    {
        var db = Fixtures.Sample();
        var column = new CdbObject();
        column.Set("name", new CdbValue.Str("emptyRef"));
        column.Set("typeStr", new CdbValue.Str("6:Empty"));

        var sheet = Fixtures.Items(db);
        var refColumn = new Column(sheet, column);
        var value = TypeDefaults.Create(refColumn, db);
        Assert.Equal(string.Empty, Assert.IsType<CdbValue.Str>(value).Value);
    }

    [Fact]
    public void ExplicitDefaultValueWins()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "T",
			"columns": [
				{
					"typeStr": "3",
					"name": "count",
					"defaultValue": 42
				}
			],
			"lines": []
		}
	],
	"customTypes": []
}
""";
        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var value = TypeDefaults.Create(db.GetSheet("T")!.GetColumn("count")!, db);
        Assert.Equal(42d, Assert.IsType<CdbValue.Number>(value).Value);
    }

    [Fact]
    public void ExplicitDefaultValueIsCloned()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "T",
			"columns": [
				{
					"typeStr": "8",
					"name": "list",
					"defaultValue": [
						1
					]
				}
			],
			"lines": []
		}
	],
	"customTypes": []
}
""";
        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var column = db.GetSheet("T")!.GetColumn("list")!;

        var first = Assert.IsType<CdbValue.Arr>(TypeDefaults.Create(column, db));
        first.Items.Add(CdbValue.Of(2));

        var second = Assert.IsType<CdbValue.Arr>(TypeDefaults.Create(column, db));
        Assert.Single(second.Items);
    }

    [Fact]
    public void ListDefaultsToEmptyArray()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "T",
			"columns": [
				{
					"typeStr": "8",
					"name": "list"
				}
			],
			"lines": []
		}
	],
	"customTypes": []
}
""";
        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var value = Assert.IsType<CdbValue.Arr>(TypeDefaults.Create(db.GetSheet("T")!.GetColumn("list")!, db));
        Assert.Empty(value.Items);
    }

    [Fact]
    public void PropertiesDefaultContainsRequiredSubColumns()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "T",
			"columns": [
				{
					"typeStr": "17",
					"name": "stats"
				}
			],
			"lines": []
		},
		{
			"name": "T@stats",
			"columns": [
				{
					"typeStr": "3",
					"name": "hp"
				},
				{
					"typeStr": "3",
					"name": "mp",
					"opt": true
				}
			],
			"lines": [],
			"props": {
				"hide": true,
				"isProps": true
			}
		}
	],
	"customTypes": []
}
""";
        var db = PalaceDB.Core.Json.CdbReader.ParseDatabase(json);
        var sheet = db.GetSheet("T")!;
        var value = Assert.IsType<CdbValue.Obj>(TypeDefaults.Create(sheet.GetColumn("stats")!, db, sheet));

        Assert.True(value.Value.ContainsKey("hp"));
        Assert.False(value.Value.ContainsKey("mp"));
        Assert.Equal(0d, ((CdbValue.Number)value.Value.Get("hp")!).Value);
    }
}
