using PalaceDB.Core.Commands;
using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class CommandStackTests
{
    private static CellEditCommand Edit(CdbObject row, string column, CdbValue value) =>
        new(row, column, value);

    [Fact]
    public void ExecutesAndRecords()
    {
        var row = new CdbObject();
        row.Set("a", CdbValue.Of(1));

        var stack = new CommandStack();
        stack.Execute(Edit(row, "a", CdbValue.Of(2)));

        Assert.Equal(2d, ((CdbValue.Number)row.Get("a")!).Value);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Equal(1, stack.UndoCount);
    }

    [Fact]
    public void UndoRestoresThePreviousValue()
    {
        var row = new CdbObject();
        row.Set("a", CdbValue.Of(1));

        var stack = new CommandStack();
        stack.Execute(Edit(row, "a", CdbValue.Of(2)));
        var undone = stack.Undo();

        Assert.NotNull(undone);
        Assert.Equal(1d, ((CdbValue.Number)row.Get("a")!).Value);
        Assert.True(stack.CanRedo);
    }

    [Fact]
    public void RedoReapplies()
    {
        var row = new CdbObject();
        row.Set("a", CdbValue.Of(1));

        var stack = new CommandStack();
        stack.Execute(Edit(row, "a", CdbValue.Of(2)));
        stack.Undo();
        stack.Redo();

        Assert.Equal(2d, ((CdbValue.Number)row.Get("a")!).Value);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void UndoOfACreatedFieldRemovesItAgain()
    {
        var row = new CdbObject();

        var stack = new CommandStack();
        stack.Execute(Edit(row, "newField", CdbValue.Of(1)));
        Assert.True(row.ContainsKey("newField"));

        stack.Undo();
        Assert.False(row.ContainsKey("newField"));
    }

    [Fact]
    public void UndoOfADeletedFieldRestoresAbsenceNotNull()
    {
        var row = new CdbObject();
        row.Set("a", CdbValue.Of(1));

        var stack = new CommandStack();
        stack.Execute(new CellEditCommand(row, "a", null));
        Assert.False(row.ContainsKey("a"));

        stack.Undo();
        Assert.True(row.ContainsKey("a"));
        Assert.Equal(1d, ((CdbValue.Number)row.Get("a")!).Value);
    }

    [Fact]
    public void NewCommandClearsTheRedoHistory()
    {
        var row = new CdbObject();
        row.Set("a", CdbValue.Of(1));

        var stack = new CommandStack();
        stack.Execute(Edit(row, "a", CdbValue.Of(2)));
        stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Execute(Edit(row, "a", CdbValue.Of(3)));
        Assert.False(stack.CanRedo);
        Assert.Equal(3d, ((CdbValue.Number)row.Get("a")!).Value);
    }

    [Fact]
    public void UndoAndRedoReturnNullWhenEmpty()
    {
        var stack = new CommandStack();
        Assert.Null(stack.Undo());
        Assert.Null(stack.Redo());
    }

    [Fact]
    public void LabelsAreExposedForTheMenu()
    {
        var row = new CdbObject();
        var stack = new CommandStack();
        stack.Execute(new CellEditCommand(row, "cost", CdbValue.Of(1)));

        Assert.Equal("Edit 'cost'", stack.UndoLabel);
        stack.Undo();
        Assert.Null(stack.UndoLabel);
        Assert.Equal("Edit 'cost'", stack.RedoLabel);
    }

    [Fact]
    public void CapacityDropsTheOldestSteps()
    {
        var row = new CdbObject();
        var stack = new CommandStack { Capacity = 3 };

        for (var i = 0; i < 10; i++)
            stack.Execute(Edit(row, "a", CdbValue.Of(i)));

        Assert.Equal(3, stack.UndoCount);
    }

    [Fact]
    public void ChangedFiresOnEveryMutation()
    {
        var row = new CdbObject();
        var stack = new CommandStack();
        var count = 0;
        stack.Changed += (_, _) => count++;

        stack.Execute(Edit(row, "a", CdbValue.Of(1)));
        stack.Undo();
        stack.Redo();
        stack.Clear();

        Assert.Equal(4, count);
    }

    [Fact]
    public void ClearEmptiesBothStacks()
    {
        var row = new CdbObject();
        var stack = new CommandStack();
        stack.Execute(Edit(row, "a", CdbValue.Of(1)));
        stack.Undo();

        stack.Clear();

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void TransactionGroupsCommandsIntoOneStep()
    {
        var row = new CdbObject();
        row.Set("a", CdbValue.Of(1));
        row.Set("b", CdbValue.Of(1));

        var stack = new CommandStack();
        using (stack.BeginTransaction("Fill"))
        {
            stack.Execute(Edit(row, "a", CdbValue.Of(9)));
            stack.Execute(Edit(row, "b", CdbValue.Of(9)));
        }

        Assert.Equal(1, stack.UndoCount);
        Assert.Equal(9d, ((CdbValue.Number)row.Get("a")!).Value);

        stack.Undo();
        Assert.Equal(1d, ((CdbValue.Number)row.Get("a")!).Value);
        Assert.Equal(1d, ((CdbValue.Number)row.Get("b")!).Value);
    }

    [Fact]
    public void EmptyTransactionIsNotRecorded()
    {
        var stack = new CommandStack();
        using (stack.BeginTransaction("Nothing"))
        {
        }
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void NestedTransactionsFoldIntoTheOuterOne()
    {
        var row = new CdbObject();
        row.Set("a", CdbValue.Of(1));

        var stack = new CommandStack();
        using (stack.BeginTransaction("Outer"))
        {
            stack.Execute(Edit(row, "a", CdbValue.Of(2)));
            using (stack.BeginTransaction("Inner"))
                stack.Execute(Edit(row, "a", CdbValue.Of(3)));
        }

        Assert.Equal(1, stack.UndoCount);
        stack.Undo();
        Assert.Equal(1d, ((CdbValue.Number)row.Get("a")!).Value);
    }

    [Fact]
    public void PushAppliedRecordsWithoutExecuting()
    {
        var row = new CdbObject();
        row.Set("a", CdbValue.Of(5));

        var stack = new CommandStack();
        var command = new CellEditCommand(row, "a", CdbValue.Of(7));
        command.Execute();
        stack.PushApplied(command);

        Assert.Equal(1, stack.UndoCount);
        Assert.Equal(7d, ((CdbValue.Number)row.Get("a")!).Value);
    }

    [Fact]
    public void CursorAnchorsTravelWithTheCommand()
    {
        var row = new CdbObject();
        var before = new CursorAnchor("Items", "sword", 3, "cost", 2);
        var after = new CursorAnchor("Items", "sword", 3, "cost", 2);

        var command = new CellEditCommand(row, "cost", CdbValue.Of(1))
        {
            CursorBefore = before,
            CursorAfter = after,
        };

        var stack = new CommandStack();
        stack.Execute(command);

        var undone = stack.Undo();
        Assert.Same(before, undone!.CursorBefore);
        Assert.Same(after, undone.CursorAfter);
    }
}

public class MultiCellEditTests
{
    private static (SheetData Sheet, CdbObject Row) Setup()
    {
        var db = Fixtures.Sample();
        var sheet = Fixtures.Items(db);
        return (sheet, sheet.Lines[0]);
    }

    [Fact]
    public void BuilderSkipsUnchangedCells()
    {
        var (sheet, row) = Setup();
        var builder = new MultiCellEditCommand.Builder("Fill");
        builder.Add(row, "name", row.Get("name"));
        builder.Add(row, "cost", CdbValue.Of(99));

        var command = builder.Build();
        Assert.NotNull(command);
        Assert.Equal(1, command!.Count);
    }

    [Fact]
    public void BuilderReturnsNullWhenNothingChanges()
    {
        var (sheet, row) = Setup();
        var builder = new MultiCellEditCommand.Builder("Fill");
        builder.Add(row, "name", row.Get("name"));
        Assert.Null(builder.Build());
    }

    [Fact]
    public void UndoOfAMultiCellEditIsOneStep()
    {
        var (sheet, row) = Setup();
        var stack = new CommandStack();

        var builder = new MultiCellEditCommand.Builder("Fill");
        builder.Add(row, "name", CdbValue.Of("Changed"));
        builder.Add(row, "cost", CdbValue.Of(99));
        builder.Add(row, "enabled", CdbValue.Of(false));

        stack.Execute(builder.Build()!);

        Assert.Equal("Changed", row.GetString("name"));
        Assert.Equal(99d, ((CdbValue.Number)row.Get("cost")!).Value);
        Assert.False(((CdbValue.Bool)row.Get("enabled")!).Value);

        Assert.Equal(1, stack.UndoCount);
        stack.Undo();

        Assert.Equal("Sword", row.GetString("name"));
        Assert.Equal(10d, ((CdbValue.Number)row.Get("cost")!).Value);
        Assert.True(((CdbValue.Bool)row.Get("enabled")!).Value);
    }

    [Fact]
    public void RedoOfAMultiCellEditReappliesEveryChange()
    {
        var (sheet, row) = Setup();
        var stack = new CommandStack();

        var builder = new MultiCellEditCommand.Builder("Fill");
        builder.Add(row, "name", CdbValue.Of("Changed"));
        builder.Add(row, "cost", CdbValue.Of(99));
        stack.Execute(builder.Build()!);

        stack.Undo();
        stack.Redo();

        Assert.Equal("Changed", row.GetString("name"));
        Assert.Equal(99d, ((CdbValue.Number)row.Get("cost")!).Value);
    }
}

public class RowCommandTests
{
    private static SheetData Sheet()
    {
        var db = Fixtures.Sample();
        return Fixtures.Items(db);
    }

    private static List<string> Ids(SheetData sheet) =>
        sheet.Lines.Select(r => r.GetString("id") ?? string.Empty).ToList();

    [Fact]
    public void InsertAddsRowsAtTheIndex()
    {
        var sheet = Sheet();
        var stack = new CommandStack();

        var row = new CdbObject();
        row.Set("id", new CdbValue.Str("potion"));
        stack.Execute(new RowInsertCommand(sheet, 1, new[] { row }));

        Assert.Equal(new[] { "sword", "potion", "shield", "sword" }, Ids(sheet));
    }

    [Fact]
    public void InsertUndoRemovesTheRows()
    {
        var sheet = Sheet();
        var stack = new CommandStack();
        var row = new CdbObject();
        row.Set("id", new CdbValue.Str("potion"));

        stack.Execute(new RowInsertCommand(sheet, 1, new[] { row }));
        stack.Undo();

        Assert.Equal(new[] { "sword", "shield", "sword" }, Ids(sheet));
    }

    [Fact]
    public void InsertAtEndIsSupported()
    {
        var sheet = Sheet();
        var stack = new CommandStack();
        var row = new CdbObject();
        row.Set("id", new CdbValue.Str("last"));

        stack.Execute(new RowInsertCommand(sheet, sheet.Lines.Count, new[] { row }));
        Assert.Equal("last", Ids(sheet)[^1]);
    }

    [Fact]
    public void DeleteRemovesTheRequestedRange()
    {
        var sheet = Sheet();
        var stack = new CommandStack();

        stack.Execute(new RowDeleteCommand(sheet, 1, 1));
        Assert.Equal(new[] { "sword", "sword" }, Ids(sheet));
    }

    [Fact]
    public void DeleteUndoRestoresTheSameInstancesInOrder()
    {
        var sheet = Sheet();
        var original = sheet.Lines.ToList();

        var stack = new CommandStack();
        stack.Execute(new RowDeleteCommand(sheet, 0, 2));
        stack.Undo();

        Assert.Equal(original.Count, sheet.Lines.Count);
        for (var i = 0; i < original.Count; i++)
            Assert.Same(original[i], sheet.Lines[i]);
    }

    [Fact]
    public void DeleteRedoRemovesThemAgain()
    {
        var sheet = Sheet();
        var stack = new CommandStack();
        stack.Execute(new RowDeleteCommand(sheet, 0, 1));
        stack.Undo();
        stack.Redo();

        Assert.Equal(new[] { "shield", "sword" }, Ids(sheet));
    }

    [Fact]
    public void DeleteRejectsAnOutOfRangeRequest()
    {
        var sheet = Sheet();
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowDeleteCommand(sheet, 2, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowDeleteCommand(sheet, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowDeleteCommand(sheet, 0, 0));
    }

    [Fact]
    public void MoveReordersAndUndoRestores()
    {
        var sheet = Sheet();
        var stack = new CommandStack();

        stack.Execute(new RowMoveCommand(sheet, 0, 2));
        Assert.Equal(new[] { "shield", "sword", "sword" }, Ids(sheet));

        stack.Undo();
        Assert.Equal(new[] { "sword", "shield", "sword" }, Ids(sheet));
    }

    [Fact]
    public void MoveDownThenUpIsSymmetric()
    {
        var sheet = Sheet();
        var before = Ids(sheet);

        var stack = new CommandStack();
        stack.Execute(new RowMoveCommand(sheet, 0, 2));
        stack.Execute(new RowMoveCommand(sheet, 2, 0));

        Assert.Equal(before, Ids(sheet));
    }
}
