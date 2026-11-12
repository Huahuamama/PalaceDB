using PalaceDB.Core.Commands;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class ColumnCommandTests
{
    private static (Database Db, SheetData Sheet) Sample()
    {
        var db = Fixtures.Sample();
        return (db, Fixtures.Items(db));
    }

    [Fact]
    public void InsertAddsTheColumnToTheHeaderAndEveryRow()
    {
        var (db, sheet) = Sample();
        var countBefore = sheet.Columns.Count;

        var command = new ColumnInsertCommand(sheet, 1, "note2", new ColumnType.Text(), db);
        command.Execute();

        Assert.Equal(1, sheet.Columns.FindIndex(c => c.Name == "note2"));
        Assert.Equal(countBefore + 1, sheet.Columns.Count);
        foreach (var row in sheet.Lines)
            Assert.Equal(string.Empty, row.GetString("note2"));
    }

    [Fact]
    public void InsertingAListColumnCreatesItsHiddenSubTable()
    {
        var (db, sheet) = Sample();

        var command = new ColumnInsertCommand(
            sheet, sheet.Columns.Count, "drops", new ColumnType.List(), db,
            subColumns: new[] { new SubColumnSpec("item", new ColumnType.Text()) });

        command.Execute();

        var sub = db.GetSheet("Items@drops");
        Assert.NotNull(sub);
        Assert.True(sub!.Props.Hide);
        Assert.Equal(new[] { "item" }, sub.Columns.Select(c => c.Name));
        Assert.Equal("Items@drops", command.SubSheetName);
    }

    [Fact]
    public void UndoingAListColumnRemovesTheSubTableToo()
    {
        var (db, sheet) = Sample();

        var command = new ColumnInsertCommand(
            sheet, sheet.Columns.Count, "drops", new ColumnType.List(), db,
            subColumns: new[] { new SubColumnSpec("item", new ColumnType.Text()) });

        command.Execute();
        command.Undo();

        Assert.Null(sheet.GetColumn("drops"));
        Assert.Null(db.GetSheet("Items@drops"));
    }

    [Fact]
    public void RedoingAListColumnBringsTheSubTableBack()
    {
        var (db, sheet) = Sample();

        var command = new ColumnInsertCommand(
            sheet, sheet.Columns.Count, "drops", new ColumnType.List(), db,
            subColumns: new[] { new SubColumnSpec("item", new ColumnType.Text()) });

        command.Execute();
        command.Undo();
        command.Execute();

        Assert.NotNull(sheet.GetColumn("drops"));
        Assert.NotNull(db.GetSheet("Items@drops"));
    }

    [Fact]
    public void InsertUndoRemovesTheColumnAndItsValues()
    {
        var (db, sheet) = Sample();
        var before = sheet.Lines.Select(r => r.Pairs().Select(p => p.Key).ToArray()).ToList();

        var command = new ColumnInsertCommand(sheet, 1, "note2", new ColumnType.Text(), db);
        command.Execute();
        command.Undo();

        Assert.Null(sheet.GetColumn("note2"));
        Assert.Equal(before, sheet.Lines.Select(r => r.Pairs().Select(p => p.Key).ToArray()).ToList());
    }

    [Fact]
    public void InsertOfAnOptionalColumnLeavesTheRowsAlone()
    {
        var (db, sheet) = Sample();
        var command = new ColumnInsertCommand(sheet, 0, "extra", new ColumnType.Int(), db, optional: true);

        command.Execute();

        Assert.NotNull(sheet.GetColumn("extra"));
        Assert.All(sheet.Lines, row => Assert.Null(row.Get("extra")));
    }

    [Fact]
    public void InsertPutsTheDefaultValueOfTheTypeInEveryRow()
    {
        var (db, sheet) = Sample();
        var command = new ColumnInsertCommand(sheet, 0, "count", new ColumnType.Int(), db);

        command.Execute();

        Assert.All(sheet.Lines, row => Assert.Equal(0, row.GetInt("count")));
    }

    [Fact]
    public void DeleteRemovesTheColumnAndUndoRestoresEveryValue()
    {
        var (db, sheet) = Sample();
        var values = sheet.Lines.Select(r => r.GetInt("cost")).ToList();

        var command = new ColumnDeleteCommand(sheet, sheet.Columns.FindIndex(c => c.Name == "cost"));
        command.Execute();

        Assert.Null(sheet.GetColumn("cost"));
        Assert.All(sheet.Lines, row => Assert.Null(row.Get("cost")));

        command.Undo();

        Assert.NotNull(sheet.GetColumn("cost"));
        Assert.Equal(values, sheet.Lines.Select(r => r.GetInt("cost")).ToList());
    }

    [Fact]
    public void RenameUpdatesTheHeaderAndEveryRowWithoutMovingTheColumn()
    {
        var (db, sheet) = Sample();
        var index = sheet.Columns.FindIndex(c => c.Name == "cost");

        var command = new ColumnRenameCommand(sheet, index, "price");
        command.Execute();

        Assert.Equal(index, sheet.Columns.FindIndex(c => c.Name == "price"));
        Assert.Null(sheet.GetColumn("cost"));
        Assert.Equal(10, sheet.Lines[0].GetInt("price"));

        command.Undo();

        Assert.Equal(index, sheet.Columns.FindIndex(c => c.Name == "cost"));
        Assert.Equal(10, sheet.Lines[0].GetInt("cost"));
    }

    [Fact]
    public void RenameKeepsTheKeyPositionInsideTheRows()
    {
        var (db, sheet) = Sample();
        var index = sheet.Columns.FindIndex(c => c.Name == "cost");
        var keysBefore = sheet.Lines[0].Pairs().Select(p => p.Key).ToList();

        new ColumnRenameCommand(sheet, index, "price").Execute();

        var keysAfter = sheet.Lines[0].Pairs().Select(p => p.Key).ToList();
        Assert.Equal(keysBefore.Count, keysAfter.Count);
        Assert.Equal(keysBefore.IndexOf("cost"), keysAfter.IndexOf("price"));
    }

    [Fact]
    public void MoveReordersTheHeaderAndUndoPutsItBack()
    {
        var (db, sheet) = Sample();
        var names = sheet.Columns.Select(c => c.Name).ToList();

        var command = new ColumnMoveCommand(sheet, 1, 3);
        command.Execute();

        Assert.Equal("name", sheet.Columns[3].Name);
        Assert.Equal(names[2], sheet.Columns[1].Name);

        command.Undo();

        Assert.Equal(names, sheet.Columns.Select(c => c.Name).ToList());
    }

    [Fact]
    public void TypeChangeConvertsTheValuesAndUndoRestoresThem()
    {
        var (db, sheet) = Sample();
        var index = sheet.Columns.FindIndex(c => c.Name == "cost");
        var before = sheet.Lines.Select(r => r.GetInt("cost")).ToList();

        var command = new ColumnTypeChangeCommand(sheet, index, new ColumnType.Text(), db);
        command.Execute();

        Assert.Equal("Text", sheet.Columns[index].Type.TypeName);
        Assert.Equal("10", sheet.Lines[0].GetString("cost"));

        command.Undo();

        Assert.Equal("Int", sheet.Columns[index].Type.TypeName);
        Assert.Equal(before, sheet.Lines.Select(r => r.GetInt("cost")).ToList());
    }

    [Fact]
    public void TypeChangeToIntUsesTheDefaultForValuesThatAreNotNumbers()
    {
        var (db, sheet) = Sample();
        var index = sheet.Columns.FindIndex(c => c.Name == "name");

        new ColumnTypeChangeCommand(sheet, index, new ColumnType.Int(), db).Execute();

        Assert.Equal(0, sheet.Lines[0].GetInt("name"));
    }

    // ------------------------------------------------------------ conversions

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-3.5", -3.5)]
    [InlineData("1e3", 1000)]
    public void TextToFloatKeepsWhatItCanParse(string text, double expected)
    {
        var result = ValueConverter.Convert(new CdbValue.Str(text), new ColumnType.Float(), null);
        Assert.Equal(expected, Assert.IsType<CdbValue.Number>(result).Value);
    }

    [Fact]
    public void TextThatIsNotANumberFallsBackToTheGivenDefault()
    {
        var result = ValueConverter.Convert(
            new CdbValue.Str("abc"), new ColumnType.Float(), new CdbValue.Number(0, null));

        Assert.Equal(0, Assert.IsType<CdbValue.Number>(result).Value);
    }

    [Fact]
    public void IntToTextUsesTheCanonicalFormatting()
    {
        var result = ValueConverter.Convert(new CdbValue.Number(7, "7"), new ColumnType.Text(), null);
        Assert.Equal("7", Assert.IsType<CdbValue.Str>(result).Value);
    }

    [Fact]
    public void TextToBoolUnderstandsTheUsualSpellings()
    {
        Assert.True(Assert.IsType<CdbValue.Bool>(
            ValueConverter.Convert(new CdbValue.Str("true"), new ColumnType.Bool(), null)).Value);
        Assert.False(Assert.IsType<CdbValue.Bool>(
            ValueConverter.Convert(new CdbValue.Str("false"), new ColumnType.Bool(), null)).Value);
    }

    [Fact]
    public void TextToEnumMatchesTheValueByName()
    {
        var type = new ColumnType.Enum(new List<string> { "Common", "Rare", "Epic" });
        var result = ValueConverter.Convert(new CdbValue.Str("Rare"), type, null);

        Assert.Equal(1, Assert.IsType<CdbValue.Number>(result).Value);
    }

    [Fact]
    public void AnIdColumnCanComeBackAfterTheOldOneIsDeleted()
    {
        var (db, sheet) = Sample();
        var idColumn = sheet.IdColumn!;
        var index = sheet.Columns.IndexOf(idColumn);

        var delete = ColumnOps.Build(sheet, new DeleteColumnOp(idColumn.Name), db);
        Assert.NotNull(delete);
        delete!.Execute();
        Assert.Null(sheet.IdColumn);

        var spec = new ColumnSpec { Name = "id", TypeStr = "0" };
        var insert = ColumnOps.Build(sheet, new InsertColumnOp("id", index, spec), db, fillInsertedRows: true);
        Assert.NotNull(insert);
        insert!.Execute();

        Assert.NotNull(sheet.IdColumn);
        Assert.Equal("id", sheet.IdColumn!.Name);
        Assert.Equal(0, sheet.Columns.IndexOf(sheet.IdColumn));
    }

    [Fact]
    public void ATableNeverGetsASecondIdColumn()
    {
        var (db, sheet) = Sample();
        Assert.NotNull(sheet.IdColumn);

        var spec = new ColumnSpec { Name = "other", TypeStr = "0" };
        var insert = ColumnOps.Build(sheet, new InsertColumnOp("other", 0, spec), db, fillInsertedRows: true);

        Assert.Null(insert);
    }

    [Fact]
    public void AColumnCannotBeRetypedIntoASecondIdColumn()
    {
        var (db, sheet) = Sample();
        Assert.NotNull(sheet.IdColumn);

        var spec = new ColumnSpec { Name = "name", TypeStr = "0" };
        var retype = ColumnOps.Build(sheet, new RetypeColumnOp("name", spec), db);

        Assert.Null(retype);
    }

    [Fact]
    public void AColumnCanBecomeTheIdColumnOnceTheOldOneIsGone()
    {
        var (db, sheet) = Sample();
        ColumnOps.Build(sheet, new DeleteColumnOp("id"), db)!.Execute();
        Assert.Null(sheet.IdColumn);

        var spec = new ColumnSpec { Name = "name", TypeStr = "0" };
        var retype = ColumnOps.Build(sheet, new RetypeColumnOp("name", spec), db);
        Assert.NotNull(retype);
        retype!.Execute();

        Assert.Equal("name", sheet.IdColumn!.Name);
    }

    [Fact]
    public void ANewIdColumnLeavesExistingRowsWithoutAnIdentifier()
    {
        var (db, sheet) = Sample();
        var delete = ColumnOps.Build(sheet, new DeleteColumnOp("id"), db);
        delete!.Execute();

        var spec = new ColumnSpec { Name = "id", TypeStr = "0" };
        ColumnOps.Build(sheet, new InsertColumnOp("id", 0, spec), db, fillInsertedRows: true)!.Execute();

        // Empty strings in every row would look like identifiers nobody typed; a row
        // without one is reported by the status bar and is the author's to fill in.
        foreach (var row in sheet.Lines)
            Assert.False(row.ContainsKey("id"));
    }

    [Fact]
    public void AMissingValueStaysMissing()
    {
        Assert.Null(ValueConverter.Convert(null, new ColumnType.Int(), new CdbValue.Number(0, null)));
    }
}
