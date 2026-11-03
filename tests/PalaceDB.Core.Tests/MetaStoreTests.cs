using PalaceDB.Core.Json;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Storage;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The metadata side-car holds presentation state only. These tests pin the
/// behaviours the specification calls out: auto-creation, corruption tolerance,
/// rename following, deletion cleanup and version gating.
/// </summary>
public class MetaStoreTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private string WriteDatabase(string json = Fixtures.SampleJson)
    {
        var path = _temp.WriteFile("Sample.cdb", json);
        return path;
    }

    private static Database Load(string path) => CdbReader.ParseDatabase(AtomicFile.ReadText(path));

    [Fact]
    public void LoadingWithoutAnExistingFileCreatesAnEmptyDocument()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));

        Assert.Null(store.LoadWarning);
        Assert.False(store.IsDirty);
        Assert.False(File.Exists(MetaStore.PathFor(path)));
        Assert.True(store.Document.Tables.ContainsKey("Items"));
    }

    [Fact]
    public void EveryColumnGetsAStableIdentifier()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));

        var id = store.Document.FindColumnId("Items", "cost");
        Assert.NotNull(id);

        // Reloading must not mint a new identifier.
        store.MarkDirty();
        store.Save();
        var reloaded = MetaStore.Load(path, Load(path));
        Assert.Equal(id, reloaded.Document.FindColumnId("Items", "cost"));
    }

    [Fact]
    public void ColumnWidthsSurviveASaveAndReload()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnWidth("Items", "name", 240);
        Assert.True(store.Save());

        var reloaded = MetaStore.Load(path, Load(path));
        Assert.Equal(240, reloaded.GetColumnWidth("Items", "name"));
    }

    [Fact]
    public void WidthsAreRoundedToOneDecimal()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnWidth("Items", "name", 123.456);
        Assert.Equal(123.5, store.GetColumnWidth("Items", "name"));
    }

    [Fact]
    public void WidthOfAnUnknownColumnIsIgnored()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnWidth("Items", "doesNotExist", 100);
        Assert.Null(store.GetColumnWidth("Items", "doesNotExist"));
    }

    [Fact]
    public void RowColumnAndCellStylesAreStoredSeparately()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));

        store.SetRowBackground("Items", "sword", "#ff0000");
        store.SetColumnBackground("Items", "name", "#00ff00");
        store.SetCellBackground("Items", "shield", "cost", "#0000ff");
        store.Save();

        var reloaded = MetaStore.Load(path, Load(path));
        var table = reloaded.Document.Tables["Items"];
        var nameId = reloaded.Document.FindColumnId("Items", "name")!;
        var costId = reloaded.Document.FindColumnId("Items", "cost")!;

        Assert.Equal("#ff0000", table.RowStyles["sword"].Background);
        Assert.Equal("#00ff00", table.ColumnStyles[nameId].Background);
        Assert.Equal("#0000ff", table.CellStyles["shield"][costId].Background);
    }

    [Fact]
    public void ClearingAStyleRemovesTheEntry()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));

        store.SetRowBackground("Items", "sword", "#ff0000");
        store.SetRowBackground("Items", "sword", null);
        Assert.Empty(store.Document.Tables["Items"].RowStyles);

        store.SetCellBackground("Items", "sword", "name", "#ff0000");
        store.SetCellBackground("Items", "sword", "name", null);
        Assert.Empty(store.Document.Tables["Items"].CellStyles);
    }

    [Fact]
    public void CorruptedMetadataIsQuarantinedAndReset()
    {
        var path = WriteDatabase();
        File.WriteAllText(MetaStore.PathFor(path), "{ this is not json");

        var store = MetaStore.Load(path, Load(path));

        Assert.NotNull(store.LoadWarning);
        Assert.True(File.Exists(MetaStore.PathFor(path) + ".bad"));
        Assert.True(store.Document.Tables.ContainsKey("Items"));
    }

    [Fact]
    public void MetadataFromANewerVersionIsNeverOverwritten()
    {
        var path = WriteDatabase();
        File.WriteAllText(MetaStore.PathFor(path), """{"metaVersion": 99}""");

        var store = MetaStore.Load(path, Load(path));
        Assert.True(store.IsReadOnly);

        store.MarkDirty();
        Assert.False(store.Save());
        Assert.Equal("""{"metaVersion": 99}""", File.ReadAllText(MetaStore.PathFor(path)));
    }

    [Fact]
    public void SavingWithoutChangesDoesNothing()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        Assert.False(store.Save());
    }

    [Fact]
    public void ExternallyRenamedColumnKeepsItsWidth()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnWidth("Items", "cost", 180);
        store.Save();

        // Simulate an external editor renaming the column.
        var renamed = Fixtures.SampleJson.Replace("\"name\": \"cost\"", "\"name\": \"price\"");
        var renamedPath = _temp.WriteFile("Sample.cdb", renamed);

        var reloaded = MetaStore.Load(renamedPath, Load(renamedPath));
        Assert.Equal(180, reloaded.GetColumnWidth("Items", "price"));
    }

    [Fact]
    public void DeletedColumnDropsItsMetadata()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnWidth("Items", "cost", 180);
        store.SetColumnBackground("Items", "cost", "#ff0000");
        store.Save();

        var withoutCost = Fixtures.SampleJson.Replace("""
				{
					"typeStr": "3",
					"name": "cost"
				},
""", string.Empty);
        Assert.NotEqual(Fixtures.SampleJson, withoutCost);
        var trimmedPath = _temp.WriteFile("Sample.cdb", withoutCost);

        var reloaded = MetaStore.Load(trimmedPath, Load(trimmedPath));
        Assert.Null(reloaded.Document.FindColumnId("Items", "cost"));
    }

    [Fact]
    public void RenamingATableMovesItsMetadata()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnWidth("Items", "name", 300);

        store.Document.RenameTable("Items", "Gear");

        Assert.False(store.Document.Tables.ContainsKey("Items"));
        Assert.True(store.Document.Tables.ContainsKey("Gear"));
        Assert.Equal(300, store.GetColumnWidth("Gear", "name"));
    }

    [Fact]
    public void RenamingAColumnKeepsItsWidth()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnWidth("Items", "cost", 150);

        store.Document.RenameColumn("Items", "cost", "price");

        Assert.Equal(150, store.GetColumnWidth("Items", "price"));
        Assert.Null(store.GetColumnWidth("Items", "cost"));
    }

    [Fact]
    public void RemovingAColumnAlsoRemovesItsStyles()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnBackground("Items", "cost", "#ff0000");
        store.SetCellBackground("Items", "sword", "cost", "#00ff00");

        store.Document.RemoveColumn("Items", "cost");

        var table = store.Document.Tables["Items"];
        Assert.DoesNotContain(table.ColumnStyles.Keys, k => k == store.Document.FindColumnId("Items", "cost"));
        Assert.All(table.CellStyles.Values, cells => Assert.Empty(cells));
    }

    [Fact]
    public void ShiftRowKeysMovesIndexedStyles()
    {
        const string json = """
{
	"sheets": [
		{
			"name": "T",
			"columns": [
				{
					"typeStr": "1",
					"name": "label"
				}
			],
			"lines": [
				{
					"label": "a"
				},
				{
					"label": "b"
				}
			]
		}
	],
	"customTypes": []
}
""";
        var path = _temp.WriteFile("NoId.cdb", json);
        var store = MetaStore.Load(path, Load(path));
        var sheet = Load(path).GetSheet("T")!;

        store.SetRowBackground("T", "0", "#ff0000");
        store.SetRowBackground("T", "1", "#00ff00");

        store.ShiftRowKeys("T", fromIndex: 0, delta: 1, sheet);

        var table = store.Document.Tables["T"];
        Assert.Equal("#ff0000", table.RowStyles["1"].Background);
        Assert.Equal("#00ff00", table.RowStyles["2"].Background);
    }

    [Fact]
    public void ShiftRowKeysDoesNothingForIdKeyedSheets()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        var sheet = Load(path).GetSheet("Items")!;

        store.SetRowBackground("Items", "sword", "#ff0000");
        store.ShiftRowKeys("Items", fromIndex: 0, delta: 1, sheet);

        Assert.True(store.Document.Tables["Items"].RowStyles.ContainsKey("sword"));
    }

    [Fact]
    public void MetadataFileIsWrittenNextToTheDatabase()
    {
        var path = WriteDatabase();
        Assert.Equal(path + ".meta.json", MetaStore.PathFor(path));
    }

    [Fact]
    public void MetadataFileIsValidJsonWithAVersion()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetColumnWidth("Items", "name", 100);
        store.Save();

        var text = File.ReadAllText(MetaStore.PathFor(path));
        Assert.Contains($"\"metaVersion\": {MetaDocument.CurrentVersion}", text);

        var parsed = MetaDocument.Parse(text);
        Assert.Equal(MetaDocument.CurrentVersion, parsed.MetaVersion);
    }

    // ---- palette of a file (R12) ---------------------------------------

    [Fact]
    public void AFreshFileHasNoPaletteOfItsOwn()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));

        Assert.Null(store.Scheme);
        Assert.Null(store.Document.Appearance);
    }

    [Fact]
    public void ThePaletteIsWrittenToTheSideCarAndComesBack()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));

        Assert.True(store.SetScheme("mint"));
        Assert.True(store.Save());

        var text = File.ReadAllText(MetaStore.PathFor(path));
        Assert.Contains("\"scheme\": \"mint\"", text);
        // Presentation state must never leak into the database itself.
        Assert.DoesNotContain("scheme", File.ReadAllText(path));

        Assert.Equal("mint", MetaStore.Load(path, Load(path)).Scheme);
    }

    [Fact]
    public void SettingTheSamePaletteAgainDoesNotDirtyTheFile()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetScheme("rose");
        store.Save();

        Assert.False(store.SetScheme("rose"));
        Assert.False(store.IsDirty);
    }

    [Fact]
    public void ClearingThePaletteRemovesTheBlockEntirely()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetScheme("lilac");
        store.Save();

        Assert.True(store.SetScheme(null));
        store.Save();

        Assert.Null(store.Document.Appearance);
        Assert.DoesNotContain("appearance", File.ReadAllText(MetaStore.PathFor(path)));
    }

    [Fact]
    public void ReadingTheWindowStateDoesNotNeedTheDatabase()
    {
        var path = WriteDatabase();
        var store = MetaStore.Load(path, Load(path));
        store.SetWindowState(new WindowState { X = 300, Y = 200, Width = 900, Height = 700 });
        store.Save();

        var state = MetaStore.ReadWindowState(path);

        Assert.NotNull(state);
        Assert.Equal(300, state!.X);
        Assert.Equal(900, state.Width);
        Assert.True(state.HasPosition);
    }

    [Fact]
    public void ReadingTheWindowStateOfAMissingOrBrokenSideCarReturnsNull()
    {
        var path = WriteDatabase();
        Assert.Null(MetaStore.ReadWindowState(path));

        File.WriteAllText(MetaStore.PathFor(path), "{ not json");
        Assert.Null(MetaStore.ReadWindowState(path));
    }
}
