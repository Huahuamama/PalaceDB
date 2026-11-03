using PalaceDB.Core.Commands;
using PalaceDB.Core.Document;
using PalaceDB.Core.Json;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Storage;
using Xunit;

namespace PalaceDB.Core.Tests;

public class DatabaseDocumentTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private string WriteSample(string name = "Sample.cdb") =>
        _temp.WriteFile(name, Fixtures.SampleJson);

    private static CdbObject FirstRow(DatabaseDocument doc) => Fixtures.Items(doc.Database).Lines[0];

    [Fact]
    public void OpenLoadsTheDatabaseAndItsMetadata()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        Assert.Equal("Sample.cdb", doc.Title);
        Assert.Equal("Sample", doc.DisplayName);
        Assert.Equal(2, doc.Database.Sheets.Count);
        Assert.NotNull(doc.Meta);
        Assert.False(doc.IsDirty);
        Assert.False(doc.IsReadOnly);
    }

    [Fact]
    public void OpenFailsWithAClearMessageForAMissingFile()
    {
        var ex = Assert.Throws<DocumentOpenException>(() => DatabaseDocument.Open(_temp.At("nope.cdb")));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void OpenFailsWithAClearMessageForBrokenJson()
    {
        var path = _temp.WriteFile("broken.cdb", "{ not json");
        var ex = Assert.Throws<DocumentOpenException>(() => DatabaseDocument.Open(path));
        Assert.Contains("not a valid PalaceDB file", ex.Message);
    }

    [Fact]
    public void EditingMarksTheDocumentDirty()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("Changed")));

        Assert.True(doc.IsDirty);
        Assert.EndsWith("Sample.cdb *", doc.Title);
    }

    [Fact]
    public void SavingClearsTheDirtyFlag()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("Changed")));
        Assert.True(doc.Save());

        Assert.False(doc.IsDirty);
        Assert.Equal("Sample.cdb", doc.Title);
    }

    [Fact]
    public void UndoingBackToTheSavedPointMarksTheDocumentClean()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("Changed")));
        doc.Commands.Undo();

        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void SaveWritesTheEditToDisk()
    {
        var path = WriteSample();
        using (var doc = DatabaseDocument.Open(path))
        {
            doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("Changed")));
            doc.Save();
        }

        var reloaded = CdbReader.ParseDatabase(AtomicFile.ReadText(path));
        Assert.Equal("Changed", Fixtures.Items(reloaded).Lines[0].GetString("name"));
    }

    [Fact]
    public void SaveKeepsTheFileFormatStable()
    {
        var path = WriteSample();
        using (var doc = DatabaseDocument.Open(path))
            doc.Save();

        Assert.Equal(Fixtures.SampleJson, AtomicFile.ReadText(path));
    }

    [Fact]
    public void MetadataChangesArePersistedWithoutMarkingTheDataDirty()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Meta.SetColumnWidth("Items", "name", 200);
        doc.NotifyMetaChanged();
        doc.SaveMeta();

        // Presentation state lives in the side-car, so the database is still clean.
        Assert.False(doc.IsDirty);
        Assert.True(File.Exists(MetaStore.PathFor(path)));

        var reloaded = MetaStore.Load(path, CdbReader.ParseDatabase(AtomicFile.ReadText(path)));
        Assert.Equal(200, reloaded.GetColumnWidth("Items", "name"));

        // The .cdb itself was not rewritten.
        Assert.Equal(Fixtures.SampleJson, AtomicFile.ReadText(path));
    }

    [Fact]
    public void DirtyChangedFiresOnlyOnTransitions()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        var events = 0;
        doc.DirtyChanged += (_, _) => events++;

        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("a")));
        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("b")));
        doc.Save();

        Assert.Equal(2, events);
    }

    [Fact]
    public void SaveAsMovesTheDocumentAndItsMetadata()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Meta.SetColumnWidth("Items", "name", 210);
        doc.NotifyMetaChanged();

        var target = _temp.At("Copy.cdb");
        doc.SaveAs(target);

        Assert.Equal(target, doc.Path);
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(MetaStore.PathFor(target)));
        Assert.Equal(210, MetaStore.Load(target, CdbReader.ParseDatabase(AtomicFile.ReadText(target)))
            .GetColumnWidth("Items", "name"));
    }

    [Fact]
    public void ReadOnlyFileIsNeverOverwritten()
    {
        var path = WriteSample();
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);

        try
        {
            using var doc = DatabaseDocument.Open(path);
            Assert.True(doc.IsReadOnly);

            doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("Changed")));
            Assert.False(doc.Save());
        }
        finally
        {
            File.SetAttributes(path, attributes);
        }
    }

    [Fact]
    public void BackupIsOnlyWrittenWhenDirty()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        Assert.False(doc.WriteBackup());
        Assert.False(doc.HasBackup);

        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("Changed")));
        Assert.True(doc.WriteBackup());
        Assert.True(doc.HasBackup);
    }

    [Fact]
    public void BackupContainsTheUnsavedState()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("Unsaved")));
        doc.WriteBackup();

        var backup = CdbReader.ParseDatabase(AtomicFile.ReadText(doc.BackupPath));
        Assert.Equal("Unsaved", Fixtures.Items(backup).Lines[0].GetString("name"));

        // The original file must still hold the saved state.
        var original = CdbReader.ParseDatabase(AtomicFile.ReadText(path));
        Assert.Equal("Sword", Fixtures.Items(original).Lines[0].GetString("name"));
    }

    [Fact]
    public void OnlyOneBackupFileExists()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("one")));
        doc.WriteBackup();
        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("two")));
        doc.WriteBackup();

        var files = Directory.GetFiles(_temp.Path, "*" + DatabaseDocument.BackupSuffix);
        Assert.Single(files);

        var backup = CdbReader.ParseDatabase(AtomicFile.ReadText(files[0]));
        Assert.Equal("two", Fixtures.Items(backup).Lines[0].GetString("name"));
    }

    [Fact]
    public void DeleteBackupRemovesTheFile()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);
        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("x")));
        doc.WriteBackup();

        doc.DeleteBackup();
        Assert.False(doc.HasBackup);
    }

    [Fact]
    public void SavingDeletesNoBackupAutomatically()
    {
        // The backup is only removed after the user confirms a successful save.
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);
        doc.Commands.Execute(new CellEditCommand(FirstRow(doc), "name", CdbValue.Of("x")));
        doc.WriteBackup();
        doc.Save();

        Assert.True(doc.HasBackup);
        doc.DeleteBackup();
        Assert.False(doc.HasBackup);
    }

    [Fact]
    public void NewDocumentStartsEmptyAndClean()
    {
        var path = _temp.At("New.cdb");
        using var doc = DatabaseDocument.CreateNew(path);

        Assert.Empty(doc.Database.Sheets);
        Assert.False(doc.IsDirty);
        Assert.Equal("New.cdb", doc.Title);
    }

    [Fact]
    public void CorruptedMetadataProducesAWarningButOpensTheDocument()
    {
        var path = WriteSample();
        File.WriteAllText(MetaStore.PathFor(path), "garbage");

        using var doc = DatabaseDocument.Open(path);
        Assert.NotNull(doc.LoadWarning);
        Assert.Equal(2, doc.Database.Sheets.Count);
    }

    // ------------------------------------------------------------ multi-window

    [Fact]
    public void TheSecondWindowOpensTheSameFileReadOnly()
    {
        var path = WriteSample();

        using var first = DatabaseDocument.Open(path);
        using var second = DatabaseDocument.Open(path);

        Assert.False(first.IsReadOnly);
        Assert.Null(first.ReadOnlyReason);

        Assert.True(second.IsReadOnly);
        Assert.Contains("already open", second.ReadOnlyReason);
        Assert.False(second.Save());
    }

    [Fact]
    public void ClosingTheFirstWindowLetsTheNextOneEdit()
    {
        var path = WriteSample();

        using (var first = DatabaseDocument.Open(path))
        {
            Assert.False(first.IsReadOnly);
        }

        using var second = DatabaseDocument.Open(path);
        Assert.False(second.IsReadOnly);
    }

    [Fact]
    public void SaveAsMovesTheWriteSlotToTheNewPath()
    {
        var path = WriteSample();
        var target = _temp.At("Copy.cdb");

        using (var doc = DatabaseDocument.Open(path))
        {
            doc.SaveAs(target);
            Assert.False(doc.IsReadOnly);
            Assert.True(OpenFileRegistry.IsHeld(target));
            Assert.False(OpenFileRegistry.IsHeld(path));
        }

        // The original path is free again, the copy is free once the document closes.
        Assert.False(OpenFileRegistry.IsHeld(target));

        using var original = DatabaseDocument.Open(path);
        Assert.False(original.IsReadOnly);
    }

    [Fact]
    public void SaveAsRefusesAPathAnotherWindowIsEditing()
    {
        var path = WriteSample();
        var busy = _temp.WriteFile("Busy.cdb", Fixtures.SampleJson);

        using var other = DatabaseDocument.Open(busy);
        using var doc = DatabaseDocument.Open(path);

        var ex = Assert.Throws<DocumentOpenException>(() => doc.SaveAs(busy));
        Assert.Contains("already open", ex.Message);
    }

    // ---------------------------------------------------------------- merged content (F16)

    [Fact]
    public void AMergedDocumentStartsDirtyWithoutEdits()
    {
        var path = WriteSample();
        var merged = CdbWriter.Write(CdbReader.Parse(Fixtures.SampleJson));

        using var doc = DatabaseDocument.OpenMerged(path, merged);

        Assert.True(doc.IsDirty);
        Assert.True(doc.HasPendingMerge);
        Assert.EndsWith("Sample.cdb *", doc.Title);
        Assert.Empty(doc.Commands.UndoHistory);
    }

    [Fact]
    public void SavingAMergedDocumentClearsThePendingFlag()
    {
        var path = WriteSample();
        var merged = CdbWriter.Write(CdbReader.Parse(Fixtures.SampleJson));

        using var doc = DatabaseDocument.OpenMerged(path, merged);
        Assert.True(doc.Save());

        Assert.False(doc.IsDirty);
        Assert.False(doc.HasPendingMerge);
        Assert.Equal("Sample.cdb", doc.Title);
    }

    [Fact]
    public void AMergedDocumentRefusesBrokenContent()
    {
        var path = WriteSample();

        var ex = Assert.Throws<DocumentOpenException>(
            () => DatabaseDocument.OpenMerged(path, "{ not json"));

        Assert.Contains("not a valid PalaceDB file", ex.Message);
    }

    [Fact]
    public void AMergedDocumentKeepsTheMetadataOfTheOriginalFile()
    {
        var path = WriteSample();
        var merged = CdbWriter.Write(CdbReader.Parse(Fixtures.SampleJson));

        using var doc = DatabaseDocument.OpenMerged(path, merged);

        Assert.NotNull(doc.Meta);
        Assert.Equal(path, doc.Path);
    }

    // ------------------------------------------------------------ new database (F01)

    [Fact]
    public void AnUntitledDocumentIsEmptyAndLooksUnsaved()
    {
        using var doc = DatabaseDocument.CreateUntitled(_temp.At("scratch-1.cdb"));

        Assert.True(doc.IsUntitled);
        Assert.Empty(doc.Database.Sheets);
        Assert.True(doc.IsDirty);
        Assert.Equal("Untitled *", doc.Title);
        Assert.Equal("Untitled", doc.DisplayName);
    }

    [Fact]
    public void AnUntitledDocumentRefusesAPlainSave()
    {
        using var doc = DatabaseDocument.CreateUntitled(_temp.At("scratch-2.cdb"));

        var ex = Assert.Throws<DocumentOpenException>(() => doc.Save());
        Assert.Contains("Save As", ex.Message);
        Assert.False(File.Exists(doc.Path));
    }

    [Fact]
    public void SavingAnUntitledDocumentUnderANewNameMakesItAnOrdinaryFile()
    {
        var target = _temp.At("Real.cdb");
        using var doc = DatabaseDocument.CreateUntitled(_temp.At("scratch-3.cdb"));

        doc.SaveAs(target);

        Assert.False(doc.IsUntitled);
        Assert.Equal(target, doc.Path);
        Assert.Equal("Real.cdb", doc.Title);
        Assert.False(doc.IsDirty);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void AnUntitledDocumentWritesNoBackup()
    {
        using var doc = DatabaseDocument.CreateUntitled(_temp.At("scratch-4.cdb"));

        // Dirty, yet there is no file to recover into, so no scratch backup appears.
        Assert.False(doc.WriteBackup());
        Assert.False(doc.HasBackup);
    }

    [Fact]
    public void AnUntitledDocumentNeverReportsAnExternalChange()
    {
        using var doc = DatabaseDocument.CreateUntitled(_temp.At("scratch-5.cdb"));

        Assert.False(doc.HasChangedOnDisk);
    }
}
