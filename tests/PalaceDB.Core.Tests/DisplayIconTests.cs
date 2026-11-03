using PalaceDB.Core.Commands;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;
using PalaceDB.Core.Storage;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The display icon column (C06): a per-table property naming the column whose value is an
/// image path, shown next to the label of a referenced row.
/// </summary>
public class DisplayIconTests
{
    private static Database Load() => CdbReader.ParseDatabase(Fixtures.SampleJson);

    private static SheetData Items(Database database) => database.GetSheet("Items")!;

    [Fact]
    public void SettingTheIconWritesTheProperty()
    {
        var database = Load();
        var sheet = Items(database);

        new DisplayIconCommand(sheet, "name").Execute();

        Assert.Equal("name", sheet.Props.DisplayIcon);
        Assert.Equal("name", sheet.Props.Raw.GetString("displayIcon"));
    }

    [Fact]
    public void UndoRemovesTheProperty()
    {
        var database = Load();
        var sheet = Items(database);
        var command = new DisplayIconCommand(sheet, "name");

        command.Execute();
        command.Undo();

        Assert.Null(sheet.Props.DisplayIcon);
        // Cleared means "absent", not "empty string", so the file stays byte-stable.
        Assert.Null(sheet.Props.Raw.GetString("displayIcon"));
    }

    [Fact]
    public void ReplacingTheIconRestoresThePreviousOneOnUndo()
    {
        var database = Load();
        var sheet = Items(database);
        new DisplayIconCommand(sheet, "name").Execute();

        var command = new DisplayIconCommand(sheet, "note");
        command.Execute();
        Assert.Equal("note", sheet.Props.DisplayIcon);

        command.Undo();
        Assert.Equal("name", sheet.Props.DisplayIcon);
    }

    [Fact]
    public void ClearingWithAnEmptyNameRemovesTheProperty()
    {
        var database = Load();
        var sheet = Items(database);
        new DisplayIconCommand(sheet, "name").Execute();

        new DisplayIconCommand(sheet, "   ").Execute();

        Assert.Null(sheet.Props.DisplayIcon);
    }

    [Fact]
    public void SettingTheSameColumnTwiceIsANoOp()
    {
        var database = Load();
        var sheet = Items(database);
        new DisplayIconCommand(sheet, "name").Execute();

        Assert.True(new DisplayIconCommand(sheet, "name").IsNoOp);
        Assert.False(new DisplayIconCommand(sheet, "note").IsNoOp);
    }

    [Fact]
    public void TheLabelNamesTheColumn()
    {
        var database = Load();
        var sheet = Items(database);

        Assert.Equal("Display Icon 'name'", new DisplayIconCommand(sheet, "name").Label);
        Assert.Equal("Clear Display Icon", new DisplayIconCommand(sheet, null).Label);
    }

    [Fact]
    public void IconAndDisplayColumnAreIndependent()
    {
        var database = Load();
        var sheet = Items(database);

        new DisplayColumnCommand(sheet, "name").Execute();
        new DisplayIconCommand(sheet, "note").Execute();

        Assert.Equal("name", sheet.Props.DisplayColumn);
        Assert.Equal("note", sheet.Props.DisplayIcon);

        // Clearing one must not touch the other.
        new DisplayIconCommand(sheet, null).Execute();
        Assert.Equal("name", sheet.Props.DisplayColumn);
        Assert.Null(sheet.Props.DisplayIcon);
    }

    [Fact]
    public void TheSheetIndexReadsTheIconOfARow()
    {
        var database = Load();
        var sheet = Items(database);
        var index = new SheetIndex(sheet);
        var row = sheet.Lines[0];

        Assert.Null(index.GetIcon(row));

        new DisplayIconCommand(sheet, "note").Execute();

        var refreshed = new SheetIndex(sheet);
        Assert.Equal(row.GetString("note"), refreshed.GetIcon(row));
    }

    [Fact]
    public void ThePropertySurvivesASaveAndReload()
    {
        var database = Load();
        var sheet = Items(database);
        new DisplayIconCommand(sheet, "name").Execute();

        var text = CdbWriter.Write(database);
        var reloaded = CdbReader.ParseDatabase(text);

        Assert.Equal("name", reloaded.GetSheet("Items")!.Props.DisplayIcon);
        Assert.Contains("\"displayIcon\": \"name\"", text);
    }

    [Fact]
    public void ADatabaseWithoutIconsWritesNothingExtra()
    {
        var database = Load();

        var text = CdbWriter.Write(database);

        Assert.DoesNotContain("displayIcon", text);
    }
}
