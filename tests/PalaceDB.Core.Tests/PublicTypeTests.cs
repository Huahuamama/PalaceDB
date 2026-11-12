using PalaceDB.Core.Commands;
using PalaceDB.Core.Json;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;
using Xunit;

namespace PalaceDB.Core.Tests;

public class PublicTypeTests
{
    /// <summary>Two tables that both declare the same enum column.</summary>
    private const string TwoTablesJson = """
{
	"sheets": [
		{
			"name": "Items",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "5:Common,Rare,Epic", "name": "rarity" }
			],
			"lines": [
				{ "id": "sword", "rarity": 2 },
				{ "id": "shield", "rarity": 0 }
			],
			"props": {}
		},
		{
			"name": "Monsters",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "5:Common,Rare,Epic", "name": "rarity" }
			],
			"lines": [
				{ "id": "rat", "rarity": 1 }
			],
			"props": {}
		}
	],
	"customTypes": [],
	"compress": false
}
""";

    private static (Database Db, MetaDocument Meta) Bound()
    {
        var db = CdbReader.ParseDatabase(TwoTablesJson);
        var meta = new MetaDocument();
        meta.SyncWith(db);
        meta.PublicTypes["Rarity"] = new PublicTypeMeta
        {
            Kind = "enum",
            Values = new List<string> { "Common", "Rare", "Epic" },
        };
        meta.SetColumnPublicType("Items", "rarity", "Rarity");
        meta.SetColumnPublicType("Monsters", "rarity", "Rarity");
        return (db, meta);
    }

    [Fact]
    public void EveryBoundColumnGetsTheNewValues()
    {
        var (db, meta) = Bound();

        PublicTypeService.BuildUpdate(db, meta, "Rarity", "enum", new[] { "Common", "Epic" }).Execute();

        Assert.Equal("5:Common,Epic", db.GetSheet("Items")!.GetColumn("rarity")!.TypeStr);
        Assert.Equal("5:Common,Epic", db.GetSheet("Monsters")!.GetColumn("rarity")!.TypeStr);
    }

    [Fact]
    public void RemovingAValueMovesTheIndexesThatFollowIt()
    {
        var (db, meta) = Bound();

        PublicTypeService.BuildUpdate(db, meta, "Rarity", "enum", new[] { "Common", "Epic" }).Execute();

        var items = db.GetSheet("Items")!;
        // Epic was index 2 and is now 1; Common stays 0.
        Assert.Equal(1, ((CdbValue.Number)items.Lines[0].Get("rarity")!).Value);
        Assert.Equal(0, ((CdbValue.Number)items.Lines[1].Get("rarity")!).Value);
        // Rare is gone, so the row that used it falls back to the first value.
        Assert.Equal(0, ((CdbValue.Number)db.GetSheet("Monsters")!.Lines[0].Get("rarity")!).Value);
    }

    [Fact]
    public void RenamingAValueKeepsTheRowOnIt()
    {
        var (db, meta) = Bound();

        PublicTypeService.BuildUpdate(db, meta, "Rarity", "enum", new[] { "Common", "Uncommon", "Epic" })
            .Execute();

        // The name changed, the position did not: the stored index is still 1.
        Assert.Equal(1, ((CdbValue.Number)db.GetSheet("Monsters")!.Lines[0].Get("rarity")!).Value);
    }

    [Fact]
    public void UndoRestoresTheDefinitionAndTheData()
    {
        var (db, meta) = Bound();
        var command = PublicTypeService.BuildUpdate(db, meta, "Rarity", "enum", new[] { "Common", "Epic" });

        command.Execute();
        command.Undo();

        Assert.Equal("5:Common,Rare,Epic", db.GetSheet("Items")!.GetColumn("rarity")!.TypeStr);
        Assert.Equal(2, ((CdbValue.Number)db.GetSheet("Items")!.Lines[0].Get("rarity")!).Value);
        Assert.Equal(new[] { "Common", "Rare", "Epic" }, meta.PublicTypes["Rarity"].Values);
    }

    [Fact]
    public void RenamingTheTypeMovesEveryBinding()
    {
        var (db, meta) = Bound();

        PublicTypeService.BuildUpdate(db, meta, "Quality", "enum", new[] { "Common", "Rare", "Epic" }, "Rarity")
            .Execute();

        Assert.False(meta.PublicTypes.ContainsKey("Rarity"));
        Assert.True(meta.PublicTypes.ContainsKey("Quality"));
        Assert.Equal("Quality", meta.GetColumnPublicType("Items", "rarity"));
        Assert.Equal("Quality", meta.GetColumnPublicType("Monsters", "rarity"));
    }

    [Fact]
    public void FlagsFollowTheirNamesInsteadOfTheirBits()
    {
        var db = Fixtures.Sample();
        var meta = new MetaDocument();
        meta.SyncWith(db);
        meta.PublicTypes["Tag"] = new PublicTypeMeta
        {
            Kind = "flags",
            Values = new List<string> { "A", "B", "C" },
        };
        meta.SetColumnPublicType("Items", "tags", "Tag");

        // Reordered and with B removed: A was bit 0, C was bit 2.
        PublicTypeService.BuildUpdate(db, meta, "Tag", "flags", new[] { "C", "A" }).Execute();

        var tags = Fixtures.Items(db).GetColumn("tags")!;
        Assert.Equal("10:C,A", tags.TypeStr);
        // Sword had A|C (5) and now has C|A (3); the duplicate had B (2), which is gone.
        Assert.Equal(3, ((CdbValue.Number)Fixtures.Items(db).Lines[0].Get("tags")!).Value);
        Assert.Equal(0, ((CdbValue.Number)Fixtures.Items(db).Lines[2].Get("tags")!).Value);
    }

    [Fact]
    public void EnumStoredAsTextFollowsTheLabel()
    {
        var db = CdbReader.ParseDatabase("""
{
	"sheets": [
		{
			"name": "Items",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "5:Common,Rare,Epic", "name": "rarity", "enumStr": true }
			],
			"lines": [ { "id": "sword", "rarity": "Epic" } ],
			"props": {}
		}
	],
	"customTypes": [],
	"compress": false
}
""");
        var meta = new MetaDocument();
        meta.SyncWith(db);
        meta.PublicTypes["Rarity"] = new PublicTypeMeta
        {
            Kind = "enum",
            Values = new List<string> { "Common", "Rare", "Epic" },
        };
        meta.SetColumnPublicType("Items", "rarity", "Rarity");

        PublicTypeService.BuildUpdate(db, meta, "Rarity", "enum", new[] { "Common", "Legendary" }).Execute();

        var value = (CdbValue.Str)db.GetSheet("Items")!.Lines[0].Get("rarity")!;
        // The label is gone, so the value falls back to the first entry.
        Assert.Equal("Common", value.Value);
    }

    [Fact]
    public void RepairBringsADriftedColumnBack()
    {
        var (db, meta) = Bound();
        var column = db.GetSheet("Items")!.GetColumn("rarity")!;

        // Simulate a file edited elsewhere: same kind of type, different values.
        column.Type = new ColumnType.Enum(new[] { "A", "B" });
        column.FlushType();

        var repaired = PublicTypeService.Repair(db, meta);

        Assert.Equal(1, repaired);
        Assert.Equal("5:Common,Rare,Epic", column.TypeStr);
    }

    [Fact]
    public void BindingSurvivesAColumnRename()
    {
        var (db, meta) = Bound();

        db.GetSheet("Items")!.GetColumn("rarity")!.Name = "quality";
        meta.SyncWith(db);

        Assert.Equal("Rarity", meta.GetColumnPublicType("Items", "quality"));
    }
}
