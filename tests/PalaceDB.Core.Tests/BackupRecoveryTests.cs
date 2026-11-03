using PalaceDB.Core.Document;
using PalaceDB.Core.Json;
using PalaceDB.Core.Serialization;
using PalaceDB.Core.Storage;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>Crash recovery (F19) and external change detection (F15).</summary>
public class BackupRecoveryTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private string WriteSample(string name = "Sample.cdb") =>
        _temp.WriteFile(name, Fixtures.SampleJson);

    /// <summary>Writes a backup that is newer than the database by one second.</summary>
    private string WriteBackup(string databasePath, string content, int secondsAhead = 2)
    {
        var path = BackupRecovery.PathFor(databasePath);
        AtomicFile.WriteText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(secondsAhead));
        return path;
    }

    // ---------------------------------------------------------------- inspect

    [Fact]
    public void NoBackupMeansNothingToDo()
    {
        var path = WriteSample();

        Assert.Equal(BackupState.None, BackupRecovery.Inspect(path));
    }

    [Fact]
    public void AnOlderBackupIsStale()
    {
        var path = WriteSample();
        var backup = WriteBackup(path, Fixtures.SampleJson, secondsAhead: -60);

        Assert.Equal(BackupState.Stale, BackupRecovery.Inspect(path));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void ANewerBackupIsReported()
    {
        var path = WriteSample();
        WriteBackup(path, Fixtures.SampleJson);

        Assert.Equal(BackupState.Newer, BackupRecovery.Inspect(path));
    }

    [Fact]
    public void ABackupWithoutADatabaseCountsAsNewer()
    {
        var path = _temp.At("Gone.cdb");
        WriteBackup(path, Fixtures.SampleJson);

        Assert.Equal(BackupState.Newer, BackupRecovery.Inspect(path));
    }

    // ---------------------------------------------------------------- recover

    [Fact]
    public void RecoverPutsTheBackupInPlaceAndRemovesIt()
    {
        var path = WriteSample();
        var backup = WriteBackup(path, Fixtures.SampleJson.Replace("\"Sword\"", "\"Recovered\""));

        BackupRecovery.Recover(path);

        Assert.False(File.Exists(backup));
        Assert.Contains("Recovered", CdbReader.ParseDatabase(AtomicFile.ReadText(path)).Sheets[0].Lines[0].GetString("name"));
    }

    [Fact]
    public void RecoverKeepsTheOriginalFileWhenTheBackupIsBroken()
    {
        var path = WriteSample();
        var before = AtomicFile.ReadText(path);
        WriteBackup(path, "{ this is not a database");

        Assert.Throws<CdbFormatException>(() => BackupRecovery.Recover(path));

        Assert.Equal(before, AtomicFile.ReadText(path));
    }

    [Fact]
    public void DiscardRemovesTheBackupAndLeavesTheDatabaseAlone()
    {
        var path = WriteSample();
        var before = AtomicFile.ReadText(path);
        var backup = WriteBackup(path, "ignored");

        BackupRecovery.Discard(path);

        Assert.False(File.Exists(backup));
        Assert.Equal(before, AtomicFile.ReadText(path));
    }

    // ---------------------------------------------------------------- document stamps

    [Fact]
    public void AFreshDocumentHasNotChangedOnDisk()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        Assert.False(doc.HasChangedOnDisk);
    }

    [Fact]
    public void SavingDoesNotLookLikeAnExternalChange()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Commands.Execute(new PalaceDB.Core.Commands.CellEditCommand(
            Fixtures.Items(doc.Database).Lines[0], "name", PalaceDB.Core.Model.CdbValue.Of("Edited")));
        doc.Save();

        Assert.False(doc.HasChangedOnDisk);
    }

    [Fact]
    public void AWriteFromAnotherProgramIsDetected()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        // Simulate another editor saving the file.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.True(doc.HasChangedOnDisk);
    }

    [Fact]
    public void TouchStampAcceptsTheExternalVersion()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        doc.TouchStamp();

        Assert.False(doc.HasChangedOnDisk);
    }

    [Fact]
    public void WriteBackupCreatesTheFileOnlyWhileDirty()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        Assert.False(doc.WriteBackup());
        Assert.False(File.Exists(doc.BackupPath));

        // File timestamps come from the system clock, which is coarse: writing the backup
        // in the same tick as the sample left both times equal, and "newer" is a strict
        // comparison. The precondition of this check is a backup that is strictly newer,
        // so the database is back-dated instead of hoping the clock has moved on.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-2));

        doc.Commands.Execute(new PalaceDB.Core.Commands.CellEditCommand(
            Fixtures.Items(doc.Database).Lines[0], "name", PalaceDB.Core.Model.CdbValue.Of("Edited")));

        Assert.True(doc.WriteBackup());
        Assert.True(File.Exists(doc.BackupPath));
        Assert.Equal(BackupState.Newer, BackupRecovery.Inspect(path));
    }

    [Fact]
    public void SaveRemovesTheBackupWhenTheCallerAsksForIt()
    {
        var path = WriteSample();
        using var doc = DatabaseDocument.Open(path);

        doc.Commands.Execute(new PalaceDB.Core.Commands.CellEditCommand(
            Fixtures.Items(doc.Database).Lines[0], "name", PalaceDB.Core.Model.CdbValue.Of("Edited")));
        doc.WriteBackup();
        doc.Save();
        doc.DeleteBackup();

        Assert.False(File.Exists(doc.BackupPath));
        Assert.Equal(BackupState.None, BackupRecovery.Inspect(path));
    }
}
