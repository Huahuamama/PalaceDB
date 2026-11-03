using PalaceDB.Core.Json;
using PalaceDB.Core.Storage;

namespace PalaceDB.Core.Document;

/// <summary>What the automatic backup file next to a database looks like.</summary>
public enum BackupState
{
    /// <summary>No backup file.</summary>
    None,

    /// <summary>A backup exists but it is not newer than the database: nothing to recover.</summary>
    Stale,

    /// <summary>The backup is newer than the database, so it holds work that was never saved.</summary>
    Newer,
}

/// <summary>
/// Crash recovery (requirement F19). The editor writes <c>X.cdb.autobackup</c> while a
/// document has unsaved changes; if the process dies, the next start finds a backup that
/// is newer than the database and can put it back.
/// </summary>
public static class BackupRecovery
{
    /// <summary>Backup file that belongs to a database file.</summary>
    public static string PathFor(string databasePath) => databasePath + DatabaseDocument.BackupSuffix;

    /// <summary>Compares the timestamps of the backup and the database.</summary>
    public static BackupState Inspect(string databasePath)
    {
        var backup = PathFor(databasePath);
        if (!File.Exists(backup))
            return BackupState.None;

        try
        {
            var backupTime = File.GetLastWriteTimeUtc(backup);
            var databaseTime = File.Exists(databasePath)
                ? File.GetLastWriteTimeUtc(databasePath)
                : DateTime.MinValue;

            return backupTime > databaseTime ? BackupState.Newer : BackupState.Stale;
        }
        catch
        {
            // An unreadable timestamp is treated as "nothing worth recovering".
            return BackupState.Stale;
        }
    }

    /// <summary>
    /// Replaces the database with the backup. The backup is parsed first, so a truncated
    /// or corrupted file can never destroy a good database.
    /// </summary>
    /// <exception cref="CdbFormatException">The backup is not a valid document.</exception>
    public static void Recover(string databasePath)
    {
        var backup = PathFor(databasePath);
        var text = AtomicFile.ReadText(backup);

        // Validation first: a broken backup must not overwrite the saved file.
        CdbReader.ParseDatabase(text);

        AtomicFile.WriteText(databasePath, text);
        AtomicFile.TryDelete(backup);
    }

    /// <summary>Deletes the backup without using it.</summary>
    public static void Discard(string databasePath) => AtomicFile.TryDelete(PathFor(databasePath));
}
