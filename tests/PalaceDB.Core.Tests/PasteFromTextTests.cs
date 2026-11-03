using PalaceDB.Core.Commands;
using PalaceDB.Core.Editing;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Pasting plain text from a spreadsheet (R5): the block is placed at the cursor and
/// every cell is parsed with the rules of the column it lands in. A block that starts on
/// the identifier column writes identifiers; one that starts further right never touches
/// them. A single bad cell rejects the whole block.
/// </summary>
public class PasteFromTextTests
{
    private static (Database Db, SheetData Sheet) Sample()
    {
        var db = Fixtures.Sample();
        return (db, Fixtures.Items(db));
    }

    private static List<List<string>> Grid(params string[] lines) =>
        TsvParser.Parse(string.Join("\n", lines));

    private static int IndexOf(SheetData sheet, string name) => sheet.Columns.FindIndex(c => c.Name == name);

    [Fact]
    public void CellsLandInTheColumnsAfterTheCursor()
    {
        var (db, sheet) = Sample();
        var start = IndexOf(sheet, "name");

        var payload = PasteService.FromText(sheet, start, Grid("Alpha\t7"), out var error);

        Assert.Null(error);
        Assert.NotNull(payload);
        Assert.Equal(new[] { "name", "cost" }, payload!.Columns);
        Assert.Equal("Alpha", ((CdbValue.Str)payload.Rows[0][0]).Value);
        Assert.Equal(7, ((CdbValue.Number)payload.Rows[0][1]).Value);
    }

    [Fact]
    public void StartingOnTheIdentifierPastesIdentifiers()
    {
        var (db, sheet) = Sample();

        // The cursor is on the id column, so that is where the block starts.
        var payload = PasteService.FromText(sheet, 0, Grid("Alpha\t7"), out var error);

        Assert.Null(error);
        Assert.Equal(new[] { "id", "name" }, payload!.Columns);
        Assert.Equal("Alpha", ((CdbValue.Str)payload.Rows[0][0]).Value);
    }

    [Fact]
    public void AnIdentifierColumnBeforeTheBlockIsStillSkipped()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);

        // A block starting left of the identifier cannot land on it.
        var payload = PasteService.FromText(sheet, 0, Grid("Alpha\t7"), out var error);
        Assert.Null(error);

        // Column 0 of this fixture is the identifier, so pasting there includes it; a
        // block placed one column further right never reaches it.
        var shifted = PasteService.FromText(sheet, 1, Grid("Alpha\t7"), out var shiftedError);
        Assert.Null(shiftedError);
        Assert.DoesNotContain("id", shifted!.Columns);
    }

    [Fact]
    public void TwoRowsBecomeTwoPayloadRows()
    {
        var (db, sheet) = Sample();

        var payload = PasteService.FromText(sheet, IndexOf(sheet, "name"), Grid("A\t1", "B\t2"), out _);

        Assert.Equal(2, payload!.Rows.Count);
        Assert.Equal("B", ((CdbValue.Str)payload.Rows[1][0]).Value);
        Assert.Equal(2, ((CdbValue.Number)payload.Rows[1][1]).Value);
    }

    [Fact]
    public void AShortRowFillsFewerCells()
    {
        var (db, sheet) = Sample();

        var payload = PasteService.FromText(sheet, IndexOf(sheet, "name"), Grid("A\t1", "B"), out _);

        Assert.Equal(2, payload!.Rows[0].Count);
        Assert.Single(payload.Rows[1]);
    }

    [Fact]
    public void AnUnparsableCellRejectsTheBlockAndNamesIt()
    {
        var (db, sheet) = Sample();

        // "cost" is an integer column.
        var payload = PasteService.FromText(sheet, IndexOf(sheet, "name"), Grid("A\tnot-a-number"), out var error);

        Assert.Null(payload);
        Assert.Contains("cost", error);
        Assert.Contains("Row 1", error);
    }

    [Fact]
    public void ABlockWiderThanTheTableIsRejected()
    {
        var (db, sheet) = Sample();

        // "note" is the last column, so only one column is available from there.
        var payload = PasteService.FromText(sheet, IndexOf(sheet, "note"), Grid("1\t2"), out var error);

        Assert.Null(payload);
        Assert.Contains("wide", error);
    }

    [Fact]
    public void AnEmptyBlockIsRejected()
    {
        var (db, sheet) = Sample();

        Assert.Null(PasteService.FromText(sheet, 1, new List<List<string>>(), out var error));
        Assert.Contains("any cells", error);
    }

    [Fact]
    public void AnEmptyCellInAnOptionalColumnRemovesTheValue()
    {
        var (db, sheet) = Sample();
        // One empty cell (a spreadsheet copies it as ""), not an empty clipboard.
        var payload = PasteService.FromText(sheet, IndexOf(sheet, "note"), Grid("\"\""), out var error);

        Assert.Null(error);
        // Null means "remove the key", which the paste commands understand.
        Assert.Null(payload!.Rows[0][0]);
    }

    [Fact]
    public void TheParsedValuesCanBePastedOverExistingRows()
    {
        var (db, sheet) = Sample();
        var start = IndexOf(sheet, "name");
        var payload = PasteService.FromText(sheet, start, Grid("Alpha\t7"), out _)!;

        var command = PasteService.BuildOverwrite(sheet, 0, start, payload);
        Assert.NotNull(command);
        command!.Execute();

        Assert.Equal("Alpha", sheet.Lines[0].GetString("name"));
        Assert.Equal(7, sheet.Lines[0].GetDouble("cost"));

        command.Undo();
        Assert.NotEqual("Alpha", sheet.Lines[0].GetString("name"));
    }

    [Fact]
    public void TheParsedValuesCanBeInsertedAsNewRows()
    {
        var (db, sheet) = Sample();
        var start = IndexOf(sheet, "name");
        var before = sheet.Lines.Count;
        var payload = PasteService.FromText(sheet, start, Grid("Fresh\t3"), out _)!;

        var command = PasteService.BuildInsert(sheet, 0, payload, db);
        Assert.NotNull(command);
        command!.Execute();

        Assert.Equal(before + 1, sheet.Lines.Count);
        Assert.Equal("Fresh", sheet.Lines[0].GetString("name"));
        Assert.False(string.IsNullOrEmpty(sheet.Lines[0].GetString("id")));
    }

    [Fact]
    public void EnumTextIsAcceptedByName()
    {
        var (db, sheet) = Sample();
        var payload = PasteService.FromText(sheet, IndexOf(sheet, "rarity"), Grid("Epic"), out var error);

        Assert.Null(error);
        Assert.Equal(2, ((CdbValue.Number)payload!.Rows[0][0]).Value);
    }

    [Fact]
    public void QuotedMultiLineTextSurvivesTheRoundTrip()
    {
        var (db, sheet) = Sample();
        var payload = PasteService.FromText(sheet, IndexOf(sheet, "note"), Grid("\"first\nsecond\""), out var error);

        Assert.Null(error);
        Assert.Equal("first\nsecond", ((CdbValue.Str)payload!.Rows[0][0]).Value);
    }
}
