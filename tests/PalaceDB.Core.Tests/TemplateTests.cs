using PalaceDB.Core.Commands;
using PalaceDB.Core.Json;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Storage;
using PalaceDB.Core.Templates;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Table templates: a set of column definitions several tables keep identical.
/// <para>
/// The rules pinned here are the ones the feature is judged by: an edit on one table
/// reaches the others, a table can only join a template when nothing but a missing column
/// or a different order stands in the way, and a destructive edit knows what it costs the
/// other tables before it runs.
/// </para>
/// </summary>
public class TemplateTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private (Database Database, MetaStore Store) Build(string json = Fixtures.SampleJson)
    {
        var path = _temp.WriteFile("Sample.cdb", json);
        var database = CdbReader.ParseDatabase(AtomicFile.ReadText(path));
        return (database, MetaStore.Load(path, database));
    }

    private static CdbObject Node(string name, string typeStr)
    {
        var node = new CdbObject();
        node.Set("name", new CdbValue.Str(name));
        node.Set("typeStr", new CdbValue.Str(typeStr));
        return node;
    }

    private static CdbObject Row(params (string Key, CdbValue Value)[] values)
    {
        var row = new CdbObject();
        foreach (var (key, value) in values)
            row.Set(key, value);
        return row;
    }

    private static void AddRow(SheetData sheet, params (string Key, CdbValue Value)[] values) =>
        new RowInsertCommand(sheet, sheet.Lines.Count, new[] { Row(values) }).Execute();

    private static double? Number(CdbValue? value) => (value as CdbValue.Number)?.Value;

    private static string[] Names(SheetData sheet) => sheet.Columns.Select(c => c.Name).ToArray();

    /// <summary>An "Item" template taken from Items, plus a second table built from it.</summary>
    private (Database Database, MetaStore Store, SheetData First, SheetData Second) Group()
    {
        var (database, store) = Build();
        var first = database.GetSheet("Items")!;

        var id = store.CreateTemplate("Item", TemplateSpec.Snapshot(first));
        store.SetTableTemplate(first.Name, id);

        var create = TemplateService.CreateTable(
            database, database.Sheets.Count, "Items2", store.GetTemplate(id)!.ToSpecs());
        create.Execute();
        store.SetTableTemplate("Items2", id);

        return (database, store, first, database.GetSheet("Items2")!);
    }

    // ---------------------------------------------------------------- creating

    [Fact]
    public void ATableCreatedFromATemplateGetsItsColumns()
    {
        var (database, store) = Build();
        var source = database.GetSheet("Items")!;
        var id = store.CreateTemplate("Item", TemplateSpec.Snapshot(source));

        var command = TemplateService.CreateTable(
            database, database.Sheets.Count, "Copy", store.GetTemplate(id)!.ToSpecs());
        command.Execute();

        var copy = database.GetSheet("Copy")!;
        Assert.Equal(Names(source), Names(copy));
        Assert.Empty(copy.Lines);
    }

    [Fact]
    public void CreatingFromATemplateIsOneUndoStepIncludingTheSubTables()
    {
        var (database, _) = Build();

        var list = new ColumnSpec { Name = "drops", TypeStr = "8" };
        list.Fields.Add(new SubFieldSpec("item", "1"));
        list.Fields.Add(new SubFieldSpec("chance", "4"));

        var command = TemplateService.CreateTable(database, database.Sheets.Count, "Loot", new[] { list });
        command.Execute();

        Assert.NotNull(database.GetSheet("Loot"));
        Assert.NotNull(database.GetSheet("Loot@drops"));

        command.Undo();

        Assert.Null(database.GetSheet("Loot"));
        Assert.Null(database.GetSheet("Loot@drops"));
    }

    [Fact]
    public void TheLinkOfATableIsRecordedInTheSideCar()
    {
        var (database, store) = Build();
        var id = store.CreateTemplate("Item", TemplateSpec.Snapshot(database.GetSheet("Items")!));

        new TemplateLinkCommand(store, "Empty", id).Execute();

        Assert.Equal(id, store.GetTableTemplate("Empty"));
        Assert.Equal("Item", store.TemplateName(id));
    }

    [Fact]
    public void ALinkIsUndoable()
    {
        var (database, store) = Build();
        var id = store.CreateTemplate("Item", TemplateSpec.Snapshot(database.GetSheet("Items")!));

        var command = new TemplateLinkCommand(store, "Empty", id);
        command.Execute();
        command.Undo();

        Assert.Null(store.GetTableTemplate("Empty"));
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public void ATemplateBuiltFromATableMatchesThatTable()
    {
        var (database, store, first, _) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        var audit = TemplateService.Audit(first, template);

        Assert.True(audit.IsInSync);
        Assert.Empty(audit.Conflicts);
    }

    [Fact]
    public void AMissingColumnIsAllowed()
    {
        var (database, store, first, _) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        var audit = TemplateService.Audit(database.GetSheet("Empty")!, template);

        Assert.True(audit.CanApply);
        Assert.NotEmpty(audit.Missing);
    }

    [Fact]
    public void ADifferentOrderIsAllowed()
    {
        var (database, store, first, second) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        new ColumnMoveCommand(second, second.Columns.Count - 1, 0).Execute();

        var audit = TemplateService.Audit(second, template);

        Assert.True(audit.CanApply);
        Assert.NotEmpty(audit.Reordered);
    }

    [Fact]
    public void AnExtraColumnIsAConflict()
    {
        var (database, store, first, second) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        second.InsertColumn(second.Columns.Count, Node("extra", "1"));

        var audit = TemplateService.Audit(second, template);

        Assert.False(audit.CanApply);
        var conflict = Assert.Single(audit.Conflicts);
        Assert.Equal(TemplateConflictKind.ExtraColumn, conflict.Kind);
        Assert.Equal("extra", conflict.Column);
    }

    [Fact]
    public void ADifferentTypeIsAConflict()
    {
        var (database, store, first, second) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        second.GetColumn("cost")!.Type = new ColumnType.Text();

        var audit = TemplateService.Audit(second, template);

        Assert.False(audit.CanApply);
        Assert.Contains(audit.Conflicts, c => c.Column == "cost" && c.Kind == TemplateConflictKind.TypeMismatch);
    }

    [Fact]
    public void ATypeDifferenceNamesBothTypesInTheReport()
    {
        var (database, store, first, second) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        second.GetColumn("cost")!.Type = new ColumnType.Text();

        var conflict = Assert.Single(TemplateService.Audit(second, template).Conflicts);

        Assert.Contains("Text", conflict.Detail);
        Assert.Contains("Int", conflict.Detail);
    }

    /// <summary>
    /// "5" and "5:" are the same type written two ways: an enum with no values. Comparing
    /// the raw strings refused to apply a template over that difference, and reported it as
    /// "'x' is Enum in this table but Enum in the template", which reads like nonsense.
    /// </summary>
    [Fact]
    public void TheSameEnumWrittenTwoWaysIsNotAConflict()
    {
        var (database, _) = Build();
        var sheet = database.GetSheet("Items")!;
        sheet.GetColumn("rarity")!.Type = new ColumnType.Enum(new[] { string.Empty });

        var template = new TemplateSpec
        {
            Id = "tpl_test",
            Name = "Rarity",
            Columns = { new ColumnSpec { Name = "rarity", TypeStr = "5" } },
        };

        var audit = TemplateService.Audit(sheet, template);

        Assert.DoesNotContain(audit.Conflicts, c => c.Column == "rarity");
        Assert.Empty(audit.Missing);
    }

    /// <summary>When the enum values really differ, the report says which ones.</summary>
    [Fact]
    public void ADifferentEnumListsBothSetsOfValues()
    {
        var (database, store, first, second) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        second.GetColumn("rarity")!.Type = new ColumnType.Enum(new[] { "Common", "Rare" });

        var conflict = Assert.Single(TemplateService.Audit(second, template).Conflicts);

        Assert.Equal(TemplateConflictKind.TypeMismatch, conflict.Kind);
        Assert.Contains("'Common', 'Rare'", conflict.Detail);
        Assert.Contains("'Common', 'Rare', 'Epic'", conflict.Detail);
        Assert.DoesNotContain("is Enum in this table but Enum", conflict.Detail);
    }

    /// <summary>A reference that points at another table names both tables.</summary>
    [Fact]
    public void ADifferentReferenceNamesBothTables()
    {
        var (database, store, first, second) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        second.GetColumn("parent")!.Type = new ColumnType.Ref("Empty");

        var conflict = Assert.Single(TemplateService.Audit(second, template).Conflicts);

        Assert.Equal(TemplateConflictKind.TypeMismatch, conflict.Kind);
        Assert.Contains("'Empty'", conflict.Detail);
        Assert.Contains("'Items'", conflict.Detail);
    }

    [Fact]
    public void DifferentFieldsOfAListColumnAreAConflict()
    {
        var (database, store) = Build();

        var list = new ColumnSpec { Name = "drops", TypeStr = "8" };
        list.Fields.Add(new SubFieldSpec("item", "1"));

        var id = store.CreateTemplate("Loot", new[] { list });
        var command = TemplateService.CreateTable(
            database, database.Sheets.Count, "Loot", store.GetTemplate(id)!.ToSpecs());
        command.Execute();
        store.SetTableTemplate("Loot", id);

        // The sub-table gains a field the template does not declare.
        database.GetSheet("Loot@drops")!.InsertColumn(1, Node("extra", "1"));

        var sheet = database.GetSheet("Loot")!;
        var audit = TemplateService.Audit(sheet, TemplateService.TemplateOf(store, sheet)!);

        Assert.False(audit.CanApply);
        Assert.Contains(audit.Conflicts, c => c.Column == "drops" && c.Kind == TemplateConflictKind.FieldMismatch);
    }

    [Fact]
    public void ASecondIdColumnIsAConflict()
    {
        var (database, store, first, _) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        // A table whose own ID column has another name cannot take an "id" column.
        var other = database.GetSheet("Empty")!;
        other.GetColumn("id")!.Name = "key";

        var audit = TemplateService.Audit(other, template);

        Assert.False(audit.CanApply);
        Assert.Contains(audit.Conflicts, c => c.Kind == TemplateConflictKind.IdColumn);
    }

    // ---------------------------------------------------------------- applying

    [Fact]
    public void ApplyingAddsTheMissingColumnsAndKeepsTheValues()
    {
        var (database, store, first, _) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        var other = database.GetSheet("Empty")!;
        other.InsertColumn(1, Node("note", "1"));
        AddRow(other, ("id", new CdbValue.Str("row1")), ("note", new CdbValue.Str("keep me")));

        TemplateService.BuildApply(other, template, database)!.Execute();

        Assert.Equal(Names(first), Names(other));
        Assert.Equal("keep me", other.Lines[0].GetString("note"));
    }

    [Fact]
    public void ApplyingDoesNotWriteDefaultsIntoExistingRows()
    {
        var (database, store, first, _) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        var other = database.GetSheet("Empty")!;
        AddRow(other, ("id", new CdbValue.Str("row1")));

        TemplateService.BuildApply(other, template, database)!.Execute();

        // The columns arrive, the values do not: a shared schema is not a shared value.
        Assert.NotNull(other.GetColumn("cost"));
        Assert.Null(other.Lines[0].Get("cost"));
        Assert.Null(other.Lines[0].Get("name"));
    }

    [Fact]
    public void ApplyingPutsTheColumnsInTheTemplateOrder()
    {
        var (database, store, first, second) = Group();
        var template = TemplateService.TemplateOf(store, first)!;

        new ColumnMoveCommand(second, second.Columns.Count - 1, 0).Execute();
        Assert.NotEqual(Names(first), Names(second));

        TemplateService.BuildApply(second, template, database)!.Execute();

        Assert.Equal(Names(first), Names(second));
    }

    // ---------------------------------------------------------------- mirroring

    [Fact]
    public void RenamingAColumnRenamesItInEveryTableOfTheGroup()
    {
        var (database, store, first, second) = Group();

        var command = TemplateService.BuildMirrored(
            database, store, first, new[] { new RenameColumnOp("cost", "price") }, "Rename");
        command!.Execute();

        Assert.NotNull(first.GetColumn("price"));
        Assert.NotNull(second.GetColumn("price"));
        Assert.Null(second.GetColumn("cost"));
    }

    [Fact]
    public void RenamingKeepsTheValuesOfEveryTable()
    {
        var (database, store, first, second) = Group();
        AddRow(second, ("id", new CdbValue.Str("x")), ("cost", new CdbValue.Number(42, null)));

        var command = TemplateService.BuildMirrored(
            database, store, first, new[] { new RenameColumnOp("cost", "price") }, "Rename");
        command!.Execute();

        Assert.Equal(42, Number(second.Lines[0].Get("price")));
    }

    [Fact]
    public void AddingAColumnAddsItToEveryTableWithoutWritingValues()
    {
        var (database, store, first, second) = Group();
        AddRow(second, ("id", new CdbValue.Str("x")));

        var spec = new ColumnSpec { Name = "weightClass", TypeStr = "1" };
        var command = TemplateService.BuildMirrored(
            database, store, first, new[] { (ColumnOp)new InsertColumnOp("weightClass", 1, spec) }, "Add");
        command!.Execute();

        Assert.NotNull(first.GetColumn("weightClass"));
        Assert.NotNull(second.GetColumn("weightClass"));
        Assert.Null(second.Lines[0].Get("weightClass"));
    }

    [Fact]
    public void DeletingAColumnDeletesItFromEveryTable()
    {
        var (database, store, first, second) = Group();

        var command = TemplateService.BuildMirrored(
            database, store, first, new[] { (ColumnOp)new DeleteColumnOp("note") }, "Delete");
        command!.Execute();

        Assert.Null(first.GetColumn("note"));
        Assert.Null(second.GetColumn("note"));
    }

    [Fact]
    public void ChangingTheTypeConvertsTheValuesOfEveryTable()
    {
        var (database, store, first, second) = Group();
        AddRow(second, ("id", new CdbValue.Str("x")), ("cost", new CdbValue.Number(7, null)));

        var spec = new ColumnSpec { Name = "cost", TypeStr = "4" };
        var command = TemplateService.BuildMirrored(
            database, store, first, new[] { new RetypeColumnOp("cost", spec) }, "Retype");
        command!.Execute();

        Assert.Equal("4", second.GetColumn("cost")!.TypeStr);
        Assert.Equal(7, Number(second.Lines[0].Get("cost")));
    }

    [Fact]
    public void MovingAColumnMovesItInEveryTable()
    {
        var (database, store, first, second) = Group();

        var command = TemplateService.BuildMirrored(
            database, store, first, new[] { new MoveColumnOp("cost", 1) }, "Move");
        command!.Execute();

        Assert.Equal(1, first.Columns.IndexOf(first.GetColumn("cost")!));
        Assert.Equal(1, second.Columns.IndexOf(second.GetColumn("cost")!));
    }

    [Fact]
    public void UndoOfAMirroredEditRestoresEveryTable()
    {
        var (database, store, first, second) = Group();
        var stack = new CommandStack();

        stack.Execute(TemplateService.BuildMirrored(
            database, store, first, new[] { new RenameColumnOp("cost", "price") }, "Rename")!);
        Assert.NotNull(second.GetColumn("price"));

        stack.Undo();

        Assert.NotNull(first.GetColumn("cost"));
        Assert.NotNull(second.GetColumn("cost"));
        Assert.Null(second.GetColumn("price"));
    }

    [Fact]
    public void ATableOutsideTheGroupIsNotTouched()
    {
        var (database, store, first, _) = Group();
        var other = database.GetSheet("Empty")!;

        var command = TemplateService.BuildMirrored(
            database, store, first, new[] { new RenameColumnOp("cost", "price") }, "Rename");
        command!.Execute();

        Assert.Null(other.GetColumn("price"));
        Assert.Single(other.Columns);
    }

    // ---------------------------------------------------------------- impact

    [Fact]
    public void TheImpactOfADeleteCountsTheValuesOfEveryTable()
    {
        var (database, store, first, second) = Group();

        AddRow(second, ("id", new CdbValue.Str("a")), ("note", new CdbValue.Str("one")));
        AddRow(second, ("id", new CdbValue.Str("b")), ("note", new CdbValue.Str("two")));

        var impact = TemplateService.MeasureDelete(database, store, first, "note");

        Assert.True(impact.Shared);
        Assert.Equal(2, impact.Tables.Single(t => t.Table == "Items2").Values);
        Assert.Equal(impact.Tables.Sum(t => t.Values), impact.Total);
    }

    [Fact]
    public void TheImpactOfATypeChangeCountsTheValuesThatCannotBeConverted()
    {
        var (database, store, first, second) = Group();

        AddRow(second, ("id", new CdbValue.Str("a")), ("note", new CdbValue.Str("not a number")));
        AddRow(second, ("id", new CdbValue.Str("b")), ("note", new CdbValue.Str("12")));

        var target = new ColumnSpec { Name = "note", TypeStr = "3" };
        var impact = TemplateService.MeasureRetype(database, store, first, target);

        // "not a number" is lost, "12" converts cleanly.
        Assert.Equal(1, impact.Tables.Single(t => t.Table == "Items2").Values);
        Assert.True(impact.Lossy);
    }

    [Fact]
    public void ATableWithoutATemplateHasNothingToShare()
    {
        var (database, store) = Build();

        var impact = TemplateService.MeasureDelete(database, store, database.GetSheet("Items")!, "note");

        Assert.False(impact.Shared);
        Assert.Single(impact.Tables);
    }

    // ---------------------------------------------------------------- storage

    [Fact]
    public void TemplatesSurviveASaveAndReload()
    {
        var (database, store) = Build();
        var first = database.GetSheet("Items")!;
        var id = store.CreateTemplate("Item", TemplateSpec.Snapshot(first));
        store.SetTableTemplate(first.Name, id);
        store.Save(force: true);

        var path = Path.Combine(_temp.Path, "Sample.cdb");
        var reloadedDatabase = CdbReader.ParseDatabase(AtomicFile.ReadText(path));
        var reloaded = MetaStore.Load(path, reloadedDatabase);

        var template = TemplateService.TemplateOf(reloaded, reloadedDatabase.GetSheet("Items")!);

        Assert.NotNull(template);
        Assert.Equal("Item", template!.Name);
        Assert.Equal(Names(first), template.Columns.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void TheSideCarIsVersionTwo()
    {
        var (database, store) = Build();
        store.CreateTemplate("Item", TemplateSpec.Snapshot(database.GetSheet("Items")!));
        store.Save(force: true);

        var text = File.ReadAllText(store.Path2);
        Assert.Contains("\"metaVersion\": 2", text);
        Assert.Contains("\"templates\"", text);
    }

    [Fact]
    public void DeletingATemplateUnlinksItsTablesButKeepsTheirColumns()
    {
        var (database, store, first, second) = Group();
        var id = store.GetTableTemplate(first.Name)!;

        store.DeleteTemplate(id);

        Assert.Null(store.GetTableTemplate(first.Name));
        Assert.Null(store.GetTableTemplate(second.Name));
        Assert.Equal(10, second.Columns.Count);
    }

    [Fact]
    public void TheDefinitionFollowsTheTablesAfterAnEdit()
    {
        var (database, store, first, _) = Group();
        var id = store.GetTableTemplate(first.Name)!;

        new ColumnRenameCommand(first, first.Columns.IndexOf(first.GetColumn("cost")!), "price").Execute();
        store.RefreshTemplate(id, first);

        var template = store.GetTemplate(id)!;
        Assert.Contains(template.Columns, c => c.Name == "price");
        Assert.DoesNotContain(template.Columns, c => c.Name == "cost");
    }
}
