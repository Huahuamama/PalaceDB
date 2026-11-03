using PalaceDB.Core.Commands;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Bulk column conversions (requirement C07). The fixture is built here instead of being
/// shared because the conversion tests need unique IDs and a reference between tables.
/// </summary>
public class ColumnConvertTests
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
				},
				{
					"typeStr": "3",
					"name": "cost"
				},
				{
					"typeStr": "4",
					"name": "weight"
				},
				{
					"typeStr": "5:Common,Rare,Epic",
					"name": "rarity"
				},
				{
					"typeStr": "10:A,B,C",
					"name": "tags"
				},
				{
					"typeStr": "1",
					"name": "note",
					"opt": true
				}
			],
			"lines": [
				{
					"id": "Sword",
					"name": "Long Sword",
					"cost": 10,
					"weight": 0.5,
					"rarity": 1,
					"tags": 5
				},
				{
					"id": "Shield",
					"name": "Round Shield",
					"cost": 5,
					"weight": 2.25,
					"rarity": 0,
					"tags": 0,
					"note": ""
				}
			],
			"props": {}
		},
		{
			"name": "Shops",
			"columns": [
				{
					"typeStr": "0",
					"name": "id"
				},
				{
					"typeStr": "6:Items",
					"name": "item"
				},
				{
					"typeStr": "6:Items",
					"name": "backup",
					"opt": true
				}
			],
			"lines": [
				{
					"id": "a",
					"item": "Sword",
					"backup": "Shield"
				},
				{
					"id": "b",
					"item": "Shield"
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

    private static SheetData Sheet(Database db, string name) => db.GetSheet(name)!;

    private static Column Column(Database db, string sheet, string column) =>
        Sheet(db, sheet).GetColumn(column)!;

    private static ColumnConvertCommand Convert(
        Database db,
        string sheet,
        string column,
        ColumnConversion conversion) =>
        new(db, Sheet(db, sheet), Column(db, sheet, column), conversion);

    // ---------------------------------------------------------------- text

    [Fact]
    public void LowercaseRewritesEveryCell()
    {
        var db = Load();
        var command = Convert(db, "Items", "name", ColumnConversion.Lowercase);

        command.Execute();

        Assert.Equal(new[] { "long sword", "round shield" },
            Sheet(db, "Items").Lines.Select(r => r.GetString("name")).ToArray());
        Assert.Equal(2, command.ChangedValues);
    }

    [Fact]
    public void LowercaseIsUndoable()
    {
        var db = Load();
        var before = CdbWriter.Write(db);

        var command = Convert(db, "Items", "name", ColumnConversion.Lowercase);
        command.Execute();
        command.Undo();

        Assert.Equal(before, CdbWriter.Write(db));
    }

    [Fact]
    public void UpperIdentOnlyTouchesTheFirstCharacter()
    {
        var db = Load();
        Convert(db, "Items", "name", ColumnConversion.LowerIdent).Execute();

        Assert.Equal(new[] { "long Sword", "round Shield" },
            Sheet(db, "Items").Lines.Select(r => r.GetString("name")).ToArray());
    }

    [Fact]
    public void EmptyCellsAreLeftAlone()
    {
        var db = Load();
        var command = Convert(db, "Items", "note", ColumnConversion.Uppercase);

        command.Execute();

        Assert.Equal(0, command.ChangedValues);
        Assert.Equal(string.Empty, Sheet(db, "Items").Lines[1].GetString("note"));
    }

    [Fact]
    public void ValuesThatDoNotChangeAreNotPartOfTheCommand()
    {
        var db = Load();
        var command = Convert(db, "Items", "name", ColumnConversion.Uppercase);

        // "Long Sword" is already upper-ident, but UPPERCASE still changes it.
        Assert.Equal(2, command.ChangedValues);

        var noop = Convert(db, "Items", "name", ColumnConversion.Lowercase);
        noop.Execute();
        var again = Convert(db, "Items", "name", ColumnConversion.Lowercase);
        Assert.Equal(0, again.ChangedValues);
    }

    // ---------------------------------------------------------------- ids and references

    [Fact]
    public void IdConversionRetargetsReferences()
    {
        var db = Load();
        var command = Convert(db, "Items", "id", ColumnConversion.Lowercase);

        command.Execute();

        Assert.Equal(new[] { "sword", "shield" },
            Sheet(db, "Items").Lines.Select(r => r.GetString("id")).ToArray());

        var shops = Sheet(db, "Shops").Lines;
        Assert.Equal("sword", shops[0].GetString("item"));
        Assert.Equal("shield", shops[0].GetString("backup"));
        Assert.Equal("shield", shops[1].GetString("item"));
        Assert.Equal(3, command.ChangedReferences);
    }

    [Fact]
    public void IdConversionIsUndoableIncludingReferences()
    {
        var db = Load();
        var before = CdbWriter.Write(db);

        var command = Convert(db, "Items", "id", ColumnConversion.Uppercase);
        command.Execute();
        command.Undo();

        Assert.Equal(before, CdbWriter.Write(db));
    }

    [Fact]
    public void IdConversionLeavesDanglingReferencesUntouched()
    {
        var db = Load();
        Sheet(db, "Shops").Lines[1].Set("item", new CdbValue.Str("Ghost"));

        Convert(db, "Items", "id", ColumnConversion.Lowercase).Execute();

        Assert.Equal("Ghost", Sheet(db, "Shops").Lines[1].GetString("item"));
    }

    [Fact]
    public void IdConversionThatWouldCreateDuplicatesIsRefused()
    {
        var db = Load();
        Sheet(db, "Items").Lines[1].Set("id", new CdbValue.Str("SWORD"));

        var command = Assert.Throws<InvalidOperationException>(
            () => Convert(db, "Items", "id", ColumnConversion.Uppercase));

        Assert.Contains("SWORD", command.Message);
        // Nothing was applied.
        Assert.Equal("Sword", Sheet(db, "Items").Lines[0].GetString("id"));
    }

    [Fact]
    public void IdConversionDoesNotTouchItsOwnRowsAsReferences()
    {
        var db = Load();
        var command = Convert(db, "Items", "id", ColumnConversion.Uppercase);

        command.Execute();

        // The Items sheet has no reference column, so only the Shops sheet is rewritten.
        Assert.Equal(3, command.ChangedReferences);
        Assert.All(Sheet(db, "Items").Lines,
            row => Assert.Equal(row.GetString("id")!.ToUpperInvariant(), row.GetString("id")));
    }

    // ---------------------------------------------------------------- numbers

    [Fact]
    public void IntMultiplyScalesEveryNumber()
    {
        var db = Load();
        var command = Convert(db, "Items", "cost", ColumnConversion.Multiply10);

        command.Execute();

        Assert.Equal(new[] { 100.0, 50.0 },
            Sheet(db, "Items").Lines.Select(r => ((CdbValue.Number)r.Get("cost")!).Value).ToArray());
        Assert.Equal(2, command.ChangedValues);
    }

    [Fact]
    public void IntDivisionTruncatesTowardZero()
    {
        var db = Load();
        Convert(db, "Items", "cost", ColumnConversion.Divide10).Execute();

        // 10 / 10 = 1, 5 / 10 = 0.5 which truncates to 0.
        Assert.Equal(new[] { 1.0, 0.0 },
            Sheet(db, "Items").Lines.Select(r => ((CdbValue.Number)r.Get("cost")!).Value).ToArray());
    }

    [Fact]
    public void FloatDivisionKeepsFractions()
    {
        var db = Load();
        Convert(db, "Items", "weight", ColumnConversion.Multiply10).Execute();

        Assert.Equal(new[] { 5.0, 22.5 },
            Sheet(db, "Items").Lines.Select(r => ((CdbValue.Number)r.Get("weight")!).Value).ToArray());
    }

    [Fact]
    public void NumericConversionIsUndoable()
    {
        var db = Load();
        var before = CdbWriter.Write(db);

        var command = Convert(db, "Items", "weight", ColumnConversion.Plus1);
        command.Execute();
        command.Undo();

        Assert.Equal(before, CdbWriter.Write(db));
    }

    [Fact]
    public void NumericConversionSkipsCellsThatAreNotNumbers()
    {
        var db = Load();
        Sheet(db, "Items").Lines[0].Set("cost", new CdbValue.Str("n/a"));

        var command = Convert(db, "Items", "cost", ColumnConversion.Plus1);
        command.Execute();

        Assert.Equal("n/a", Sheet(db, "Items").Lines[0].GetString("cost"));
        Assert.Equal(1, command.ChangedValues);
    }

    [Fact]
    public void NumericConversionSkipsMissingOptionalCells()
    {
        var db = Load();
        Sheet(db, "Items").Lines[1].Remove("cost");

        var command = Convert(db, "Items", "cost", ColumnConversion.Plus1);
        command.Execute();

        Assert.Null(Sheet(db, "Items").Lines[1].Get("cost"));
        Assert.Equal(1, command.ChangedValues);
    }

    // ---------------------------------------------------------------- enums and flags

    [Fact]
    public void EnumConversionRenamesTheValueListOnly()
    {
        var db = Load();
        var rowsBefore = Sheet(db, "Items").Lines
            .Select(r => r.Get("rarity")).ToArray();

        var command = Convert(db, "Items", "rarity", ColumnConversion.Lowercase);
        command.Execute();

        var type = Assert.IsType<ColumnType.Enum>(Column(db, "Items", "rarity").Type);
        Assert.Equal(new[] { "common", "rare", "epic" }, type.Values);
        Assert.Equal(rowsBefore, Sheet(db, "Items").Lines.Select(r => r.Get("rarity")).ToArray());
        Assert.Equal(3, command.ChangedValues);
    }

    [Fact]
    public void FlagsConversionRenamesTheValueListOnly()
    {
        var db = Load();
        Convert(db, "Items", "tags", ColumnConversion.Uppercase).Execute();

        var type = Assert.IsType<ColumnType.Flags>(Column(db, "Items", "tags").Type);
        Assert.Equal(new[] { "A", "B", "C" }, type.Values);
        Assert.Equal("10:A,B,C", Column(db, "Items", "tags").TypeStr);
    }

    [Fact]
    public void EnumConversionIsUndoable()
    {
        var db = Load();
        var before = CdbWriter.Write(db);

        var command = Convert(db, "Items", "rarity", ColumnConversion.Uppercase);
        command.Execute();
        command.Undo();

        Assert.Equal(before, CdbWriter.Write(db));
        var type = Assert.IsType<ColumnType.Enum>(Column(db, "Items", "rarity").Type);
        Assert.Equal(new[] { "Common", "Rare", "Epic" }, type.Values);
    }

    [Fact]
    public void EnumConversionReportsOnlyTheValuesThatMoved()
    {
        var db = Load();
        var command = Convert(db, "Items", "rarity", ColumnConversion.UpperIdent);

        // "Common", "Rare" and "Epic" already start with an upper case letter.
        Assert.Equal(0, command.ChangedValues);
    }

    // ---------------------------------------------------------------- scope

    [Fact]
    public void NumericConversionOnATextColumnDoesNothing()
    {
        var db = Load();

        // The UI hides these conversions; the command must stay harmless if it is built
        // with a mismatched type anyway.
        var command = Convert(db, "Items", "id", ColumnConversion.Multiply10);

        Assert.Equal(0, command.ChangedValues);
        Assert.Equal(0, command.ChangedReferences);
    }

    [Fact]
    public void TextConversionOnANumericColumnDoesNothing()
    {
        var db = Load();
        var command = Convert(db, "Items", "cost", ColumnConversion.Lowercase);

        Assert.Equal(0, command.ChangedValues);
    }
}
