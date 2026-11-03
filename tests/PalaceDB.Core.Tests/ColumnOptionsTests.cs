using PalaceDB.Core.Commands;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The optional parts of a column definition (C01 / C02): required or optional, default
/// value, display format and the localizable flag. They are edited in one dialog and
/// therefore undo as one step.
/// </summary>
public class ColumnOptionsTests
{
    private static (Database Db, SheetData Sheet) Sample()
    {
        var db = Fixtures.Sample();
        return (db, Fixtures.Items(db));
    }

    [Fact]
    public void TheOptionsCommandAppliesEveryField()
    {
        var (db, sheet) = Sample();
        var column = sheet.GetColumn("cost")!;

        var command = new ColumnOptionsCommand(
            column,
            optional: true,
            defaultValue: new CdbValue.Number(42, null),
            display: DisplayType.Percent,
            kind: ColumnKind.None);

        Assert.False(command.IsNoOp);
        command.Execute();

        Assert.True(column.Optional);
        Assert.Equal(42, ((CdbValue.Number)column.DefaultValue!).Value);
        Assert.Equal(DisplayType.Percent, column.Display);
    }

    [Fact]
    public void TheOptionsCommandUndoesEveryField()
    {
        var (db, sheet) = Sample();
        var column = sheet.GetColumn("cost")!;
        var before = (column.Optional, column.Display, column.Kind, column.DefaultValue);

        var command = new ColumnOptionsCommand(
            column, true, new CdbValue.Number(1, null), DisplayType.Percent, ColumnKind.Localizable);
        command.Execute();
        command.Undo();

        Assert.Equal(before.Optional, column.Optional);
        Assert.Equal(before.Display, column.Display);
        Assert.Equal(before.Kind, column.Kind);
        Assert.Equal(before.DefaultValue, column.DefaultValue);
    }

    [Fact]
    public void UnchangedOptionsAreANoOp()
    {
        var (db, sheet) = Sample();
        var column = sheet.GetColumn("name")!;

        var command = new ColumnOptionsCommand(
            column, column.Optional, column.DefaultValue, column.Display, column.Kind);

        Assert.True(command.IsNoOp);
    }

    [Fact]
    public void ClearingTheDefaultValueRemovesTheKey()
    {
        var (db, sheet) = Sample();
        var column = sheet.GetColumn("cost")!;

        new ColumnOptionsCommand(column, false, new CdbValue.Number(5, null), DisplayType.Default, ColumnKind.None)
            .Execute();
        Assert.NotNull(column.Raw.Get("defaultValue"));

        new ColumnOptionsCommand(column, false, null, DisplayType.Default, ColumnKind.None).Execute();
        Assert.Null(column.Raw.Get("defaultValue"));
    }

    [Fact]
    public void DefaultValuesCompareByTheirTextNotTheirSourceText()
    {
        var (db, sheet) = Sample();
        var column = sheet.GetColumn("cost")!;

        new ColumnOptionsCommand(column, false, new CdbValue.Number(1, "1"), DisplayType.Default, ColumnKind.None)
            .Execute();

        // 1 and 1.0 are the same number, so this must not look like a change.
        var again = new ColumnOptionsCommand(column, false, new CdbValue.Number(1, "1.0"), DisplayType.Default, ColumnKind.None);
        Assert.True(again.IsNoOp);
    }

    [Fact]
    public void ANewColumnGetsTheDeclaredDefaultInEveryRow()
    {
        var (db, sheet) = Sample();

        var command = new ColumnInsertCommand(
            sheet, 1, "score", new ColumnType.Int(), db,
            optional: false,
            options: new ColumnOptions(DisplayType.Default, ColumnKind.None, new CdbValue.Number(7, null)));
        command.Execute();

        foreach (var row in sheet.Lines)
            Assert.Equal(7, row.GetDouble("score"));

        var column = sheet.GetColumn("score")!;
        Assert.Equal(7, ((CdbValue.Number)column.DefaultValue!).Value);
    }

    [Fact]
    public void ANewColumnCanBeOptionalAndLocalizable()
    {
        var (db, sheet) = Sample();

        var command = new ColumnInsertCommand(
            sheet, 1, "label", new ColumnType.Text(), db,
            optional: true,
            options: new ColumnOptions(DisplayType.Default, ColumnKind.Localizable, null));
        command.Execute();

        var column = sheet.GetColumn("label")!;
        Assert.True(column.Optional);
        Assert.Equal(ColumnKind.Localizable, column.Kind);
        foreach (var row in sheet.Lines)
            Assert.Null(row.Get("label"));
    }

    [Fact]
    public void TheDisplayFormatIsWrittenToTheColumnDefinition()
    {
        var (db, sheet) = Sample();
        var column = sheet.GetColumn("cost")!;

        new ColumnOptionsCommand(column, false, null, DisplayType.Percent, ColumnKind.None).Execute();

        Assert.Equal(1, column.Raw.GetInt("display"));

        // The default format is the absence of the key, so a round trip stays clean.
        new ColumnOptionsCommand(column, false, null, DisplayType.Default, ColumnKind.None).Execute();
        Assert.Null(column.Raw.Get("display"));
    }

    [Fact]
    public void DeletingTheDisplayColumnClearsTheTableProperty()
    {
        var (db, sheet) = Sample();
        Assert.Equal("name", sheet.Props.DisplayColumn);

        var index = sheet.Columns.FindIndex(c => c.Name == "name");
        var command = new ColumnDeleteCommand(sheet, index);
        command.Execute();

        Assert.Null(sheet.Props.DisplayColumn);

        command.Undo();
        Assert.Equal("name", sheet.Props.DisplayColumn);
    }

    [Fact]
    public void DeletingAnotherColumnKeepsTheDisplayColumn()
    {
        var (db, sheet) = Sample();

        var index = sheet.Columns.FindIndex(c => c.Name == "cost");
        new ColumnDeleteCommand(sheet, index).Execute();

        Assert.Equal("name", sheet.Props.DisplayColumn);
    }

    [Fact]
    public void DeletingTheIconColumnClearsTheTableProperty()
    {
        var (db, sheet) = Sample();
        sheet.Props.DisplayIcon = "color";

        var index = sheet.Columns.FindIndex(c => c.Name == "color");
        var command = new ColumnDeleteCommand(sheet, index);
        command.Execute();

        Assert.Null(sheet.Props.DisplayIcon);

        command.Undo();
        Assert.Equal("color", sheet.Props.DisplayIcon);
    }

    [Fact]
    public void TheOptionsSurviveASaveAndReload()
    {
        var (db, sheet) = Sample();
        var column = sheet.GetColumn("cost")!;

        new ColumnOptionsCommand(column, true, new CdbValue.Number(3, null), DisplayType.Percent, ColumnKind.None)
            .Execute();

        var reloaded = CdbReader.ParseDatabase(CdbWriter.Write(db));
        var again = Fixtures.Items(reloaded).GetColumn("cost")!;

        Assert.True(again.Optional);
        Assert.Equal(DisplayType.Percent, again.Display);
        Assert.Equal(3, ((CdbValue.Number)again.DefaultValue!).Value);
    }
}
