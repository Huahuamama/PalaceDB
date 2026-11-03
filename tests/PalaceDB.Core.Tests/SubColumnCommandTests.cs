using PalaceDB.Core.Commands;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class SubColumnCommandTests
{
    /// <summary>
    /// An Items table with a List column ("drops") whose sub-table has two fields, and one
    /// row that already holds two records. Editing the fields has to reach those records.
    /// </summary>
    private static (Database Db, SheetData Sheet, Column Column, SheetData Sub) Sample()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);

        var insert = new ColumnInsertCommand(
            sheet, sheet.Columns.Count, "drops", new ColumnType.List(), db,
            subColumns: new[]
            {
                new SubColumnSpec("item", new ColumnType.Text()),
                new SubColumnSpec("count", new ColumnType.Int()),
            });
        insert.Execute();

        var column = sheet.GetColumn("drops")!;
        var sub = sheet.GetSub(column)!;

        var first = new CdbObject();
        first.Set("item", new CdbValue.Str("gold"));
        first.Set("count", new CdbValue.Number(3, null));

        var second = new CdbObject();
        second.Set("item", new CdbValue.Str("gem"));
        second.Set("count", new CdbValue.Number(1, null));

        sheet.Lines[0].Set("drops", new CdbValue.Arr(new List<CdbValue>
        {
            new CdbValue.Obj(first),
            new CdbValue.Obj(second),
        }));

        return (db, sheet, column, sub);
    }

    private static CdbObject ItemAt(SheetData sheet, int row, int index) =>
        ((CdbValue.Arr)sheet.Lines[row].Get("drops")!).Items[index] is CdbValue.Obj o
            ? o.Value
            : throw new Xunit.Sdk.XunitException("the list item is not an object");

    [Fact]
    public void AddingAFieldGivesEveryRecordTheNewDefault()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubColumnsCommand(db, sub, sheet, column, new[]
        {
            new SubColumnSpec("item", new ColumnType.Text()),
            new SubColumnSpec("count", new ColumnType.Int()),
            new SubColumnSpec("quality", new ColumnType.Text()),
        });
        command.Execute();

        Assert.Equal(new[] { "item", "count", "quality" }, sub.Columns.Select(c => c.Name));
        Assert.Equal(string.Empty, ItemAt(sheet, 0, 0).GetString("quality"));
        Assert.Equal("gold", ItemAt(sheet, 0, 0).GetString("item"));
        Assert.Equal(2, ((CdbValue.Arr)sheet.Lines[0].Get("drops")!).Items.Count);
    }

    [Fact]
    public void RenamingAFieldKeepsTheValuesUnderTheNewName()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubColumnsCommand(db, sub, sheet, column, new[]
        {
            new SubColumnSpec("name", new ColumnType.Text(), "item"),
            new SubColumnSpec("count", new ColumnType.Int()),
        });
        command.Execute();

        Assert.Equal(new[] { "name", "count" }, sub.Columns.Select(c => c.Name));
        Assert.Equal("gold", ItemAt(sheet, 0, 0).GetString("name"));
        Assert.Null(ItemAt(sheet, 0, 0).GetString("item"));
        Assert.Equal(3, ItemAt(sheet, 0, 0).GetInt("count"));
    }

    [Fact]
    public void RemovingAFieldDropsItFromEveryRecord()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubColumnsCommand(db, sub, sheet, column, new[]
        {
            new SubColumnSpec("item", new ColumnType.Text()),
        });
        command.Execute();

        Assert.Equal(new[] { "item" }, sub.Columns.Select(c => c.Name));
        Assert.Null(ItemAt(sheet, 0, 0).Get("count"));
        Assert.Equal(new[] { "item" }, ItemAt(sheet, 0, 0).Keys);
    }

    [Fact]
    public void ReorderingFieldsReordersTheKeysOfEveryRecord()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubColumnsCommand(db, sub, sheet, column, new[]
        {
            new SubColumnSpec("count", new ColumnType.Int(), "count"),
            new SubColumnSpec("item", new ColumnType.Text(), "item"),
        });
        command.Execute();

        Assert.Equal(new[] { "count", "item" }, sub.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "count", "item" }, ItemAt(sheet, 0, 0).Keys);
        Assert.Equal(3, ItemAt(sheet, 0, 0).GetInt("count"));
    }

    [Fact]
    public void ChangingAFieldTypeConvertsWhatItHeld()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubColumnsCommand(db, sub, sheet, column, new[]
        {
            new SubColumnSpec("item", new ColumnType.Text()),
            new SubColumnSpec("count", new ColumnType.Text(), "count"),
        });
        command.Execute();

        Assert.Equal("3", ItemAt(sheet, 0, 0).GetString("count"));
        Assert.Equal(new ColumnType.Text(), sub.Columns[1].Type);
    }

    [Fact]
    public void UndoPutsTheFieldsAndTheValuesBack()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubColumnsCommand(db, sub, sheet, column, new[]
        {
            new SubColumnSpec("name", new ColumnType.Text(), "item"),
            new SubColumnSpec("quality", new ColumnType.Text()),
        });
        command.Execute();
        command.Undo();

        Assert.Equal(new[] { "item", "count" }, sub.Columns.Select(c => c.Name));
        Assert.Equal("gold", ItemAt(sheet, 0, 0).GetString("item"));
        Assert.Equal(3, ItemAt(sheet, 0, 0).GetInt("count"));
        Assert.Null(ItemAt(sheet, 0, 0).Get("name"));
        Assert.Null(ItemAt(sheet, 0, 0).Get("quality"));

        // Redo has to reach the same state as the first run.
        command.Execute();
        Assert.Equal(new[] { "name", "quality" }, sub.Columns.Select(c => c.Name));
        Assert.Equal("gold", ItemAt(sheet, 0, 0).GetString("name"));
    }

    [Fact]
    public void AnUnchangedFieldListIsANoOp()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubColumnsCommand(db, sub, sheet, column, new[]
        {
            new SubColumnSpec("item", new ColumnType.Text(), "item"),
            new SubColumnSpec("count", new ColumnType.Int(), "count"),
        });

        Assert.True(command.IsNoOp);
    }

    [Fact]
    public void ANewFieldNameIsNotANoOp()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubColumnsCommand(db, sub, sheet, column, new[]
        {
            new SubColumnSpec("name", new ColumnType.Text(), "item"),
            new SubColumnSpec("count", new ColumnType.Int(), "count"),
        });

        Assert.False(command.IsNoOp);
    }

    [Fact]
    public void RenamingASubTableMovesItsNameAndUndoBringsItBack()
    {
        var (db, sheet, column, sub) = Sample();

        var command = new SubSheetRenameCommand(sub, "Items@loot");
        command.Execute();
        Assert.Equal("Items@loot", sub.Name);
        Assert.NotNull(db.GetSheet("Items@loot"));

        command.Undo();
        Assert.Equal("Items@drops", sub.Name);
        Assert.Same(sub, db.GetSheet("Items@drops"));
    }

    [Fact]
    public void TheFieldsSurviveAColumnRename()
    {
        var (db, sheet, column, sub) = Sample();
        var index = sheet.Columns.IndexOf(column);

        var rename = new CompositeCommand("rename");
        rename.Add(new ColumnRenameCommand(sheet, index, "loot"));
        rename.Add(new SubSheetRenameCommand(sub, "Items@loot"));
        rename.Execute();

        Assert.Equal("loot", sheet.Columns[index].Name);
        Assert.NotNull(sheet.GetSub(sheet.Columns[index]));
        Assert.Equal(2, sheet.GetSub(sheet.Columns[index])!.Columns.Count);

        rename.Undo();
        Assert.Equal("drops", sheet.Columns[index].Name);
        Assert.NotNull(sheet.GetSub(sheet.Columns[index]));
    }
}
