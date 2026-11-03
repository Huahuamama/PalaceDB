using PalaceDB.Core.Settings;
using PalaceDB.Core.Storage;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The recent files list (F03) belongs to the installation rather than to a file, so it
/// lives in the application settings. These tests pin the ordering, the de-duplication,
/// the cap and the corruption tolerance.
/// </summary>
public class AppSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => AppSettings.PathFor(_temp.Path);

    private string Touch(string name)
    {
        var path = _temp.WriteFile(name, "{}");
        return path;
    }

    [Fact]
    public void FirstRunHasNoRecentFiles()
    {
        var settings = AppSettings.Load(SettingsPath);

        Assert.Empty(settings.RecentFiles);
        Assert.Null(settings.LoadWarning);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void TheNewestFileComesFirst()
    {
        var settings = new AppSettings();

        settings.AddRecent(Touch("a.cdb"));
        settings.AddRecent(Touch("b.cdb"));

        Assert.Equal("b.cdb", Path.GetFileName(settings.RecentFiles[0]));
        Assert.Equal("a.cdb", Path.GetFileName(settings.RecentFiles[1]));
    }

    [Fact]
    public void ReopeningMovesAFileToTheFrontWithoutDuplicatingIt()
    {
        var settings = new AppSettings();
        var a = Touch("a.cdb");
        settings.AddRecent(a);
        settings.AddRecent(Touch("b.cdb"));

        settings.AddRecent(a);

        Assert.Equal(2, settings.RecentFiles.Count);
        Assert.Equal("a.cdb", Path.GetFileName(settings.RecentFiles[0]));
    }

    [Fact]
    public void OpeningTheSameFileTwiceReportsNoChange()
    {
        var settings = new AppSettings();
        var a = Touch("a.cdb");
        settings.AddRecent(a);

        Assert.False(settings.AddRecent(a));
    }

    [Fact]
    public void TheListIsCapped()
    {
        var settings = new AppSettings();

        for (var i = 0; i < AppSettings.MaxRecentFiles + 6; i++)
            settings.AddRecent(Touch($"db{i}.cdb"));

        Assert.Equal(AppSettings.MaxRecentFiles, settings.RecentFiles.Count);
        Assert.Equal($"db{AppSettings.MaxRecentFiles + 5}.cdb", Path.GetFileName(settings.RecentFiles[0]));
    }

    [Fact]
    public void AnEmptyPathIsIgnored()
    {
        var settings = new AppSettings();

        Assert.False(settings.AddRecent(null));
        Assert.False(settings.AddRecent("   "));
        Assert.Empty(settings.RecentFiles);
    }

    [Fact]
    public void AStoredPathIsMadeAbsolute()
    {
        var settings = new AppSettings();

        settings.AddRecent("relative.cdb");

        Assert.True(Path.IsPathRooted(settings.RecentFiles[0]));
    }

    [Fact]
    public void EntriesSurviveASaveAndReload()
    {
        var settings = new AppSettings();
        settings.AddRecent(Touch("a.cdb"));
        settings.AddRecent(Touch("b.cdb"));
        Assert.True(settings.Save(SettingsPath));

        var reloaded = AppSettings.Load(SettingsPath);

        Assert.Equal(2, reloaded.RecentFiles.Count);
        Assert.Equal("b.cdb", Path.GetFileName(reloaded.RecentFiles[0]));
    }

    [Fact]
    public void ACorruptSettingsFileIsReplacedByDefaults()
    {
        File.WriteAllText(SettingsPath, "{ this is not json");

        var settings = AppSettings.Load(SettingsPath);

        Assert.Empty(settings.RecentFiles);
        Assert.NotNull(settings.LoadWarning);
    }

    [Fact]
    public void DuplicatesInAHandEditedFileAreCollapsed()
    {
        var a = Touch("a.cdb");
        File.WriteAllText(SettingsPath,
            "{\"recentFiles\":[" + string.Join(",",
                Enumerable.Repeat("\"" + a.Replace("\\", "\\\\") + "\"", 3)) + "]}");

        var settings = AppSettings.Load(SettingsPath);

        Assert.Single(settings.RecentFiles);
    }

    [Fact]
    public void AnOversizedHandEditedListIsTrimmedOnLoad()
    {
        var entries = Enumerable.Range(0, AppSettings.MaxRecentFiles + 5)
            .Select(i => "\"C:\\\\data\\\\db" + i + ".cdb\"");
        File.WriteAllText(SettingsPath, "{\"recentFiles\":[" + string.Join(",", entries) + "]}");

        var settings = AppSettings.Load(SettingsPath);

        Assert.Equal(AppSettings.MaxRecentFiles, settings.RecentFiles.Count);
    }

    [Fact]
    public void RemovingAnEntryDropsOnlyThatOne()
    {
        var settings = new AppSettings();
        var a = Touch("a.cdb");
        settings.AddRecent(a);
        settings.AddRecent(Touch("b.cdb"));

        Assert.True(settings.RemoveRecent(a));
        Assert.Single(settings.RecentFiles);
        Assert.False(settings.RemoveRecent(a));
    }

    [Fact]
    public void PruningRemovesFilesThatNoLongerExist()
    {
        var settings = new AppSettings();
        var a = Touch("a.cdb");
        settings.AddRecent(a);
        settings.AddRecent(Touch("b.cdb"));

        File.Delete(a);

        Assert.Equal(1, settings.PruneMissing());
        Assert.Single(settings.RecentFiles);
        Assert.Equal("b.cdb", Path.GetFileName(settings.RecentFiles[0]));
    }

    [Fact]
    public void ClearingEmptiesTheList()
    {
        var settings = new AppSettings();
        settings.AddRecent(Touch("a.cdb"));

        settings.ClearRecent();

        Assert.Empty(settings.RecentFiles);
    }

    [Fact]
    public void DescribeReportsNameFolderAndExistence()
    {
        var settings = new AppSettings();
        var a = Touch("a.cdb");
        settings.AddRecent(a);
        settings.AddRecent(_temp.At("gone.cdb"));

        var described = settings.Describe();

        Assert.Equal("gone.cdb", described[0].Name);
        Assert.False(described[0].Exists);
        Assert.Equal("a.cdb", described[1].Name);
        Assert.True(described[1].Exists);
        // The temp folder is long enough to be shortened, so the menu shows its tail.
        var folder = described[1].Folder;
        Assert.True(folder.StartsWith("\u2026"), folder);
        Assert.True(_temp.Path.EndsWith(folder[1..]), folder);
    }

    [Fact]
    public void ADeepFolderIsShortenedInTheMenu()
    {
        var settings = new AppSettings();
        var deep = Path.Combine(_temp.Path, string.Join("\\", Enumerable.Repeat("segment", 8)));
        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "a.cdb");
        File.WriteAllText(file, "{}");
        settings.AddRecent(file);

        var described = settings.Describe().Single();

        Assert.True(described.Folder.Length <= 45, described.Folder);
        Assert.StartsWith("\u2026", described.Folder);
    }

    [Fact]
    public void SavingCreatesTheFolderWhenItIsMissing()
    {
        var nested = Path.Combine(_temp.Path, "sub", "deeper");
        var settings = new AppSettings();
        settings.AddRecent(Touch("a.cdb"));

        Assert.True(settings.Save(AppSettings.PathFor(nested)));
        Assert.True(File.Exists(AppSettings.PathFor(nested)));
    }

    [Fact]
    public void SavingNeverThrowsOnAnImpossiblePath()
    {
        var settings = new AppSettings();

        // A path whose parent is a file cannot be created.
        var blocker = _temp.WriteFile("blocker", "x");
        Assert.False(settings.Save(Path.Combine(blocker, "settings.json")));
    }

    [Fact]
    public void TheSettingsFileIsReadableJson()
    {
        var settings = new AppSettings();
        settings.AddRecent(Touch("a.cdb"));
        settings.Save(SettingsPath);

        var text = AtomicFile.ReadText(SettingsPath);

        Assert.Contains("\"recentFiles\"", text);
        Assert.Contains("a.cdb", text);
    }
}
