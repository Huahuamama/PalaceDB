using System.Text;

namespace PalaceDB.Core.Tests;

/// <summary>Shared helpers for locating the sample databases used by the tests.</summary>
internal static class TestPaths
{
    private static readonly Lazy<string> FixturesPath = new(LocateFixtures);

    /// <summary>The folder that holds the sample databases — <c>tests/fixtures</c>.</summary>
    public static string Root => FixturesPath.Value;

    public static string TestData1 => Path.Combine(Root, "TestData1.cdb");

    public static string TestData2 => Path.Combine(Root, "TestData2.cdb");

    public static bool HasTestData => File.Exists(TestData1) && File.Exists(TestData2);

    /// <summary>Creates an empty scratch directory that is removed when disposed.</summary>
    public static TempDirectory NewTempDirectory() => new();

    /// <summary>
    /// Walks up from the test binary until the sample databases are found. They live in
    /// <c>tests/fixtures</c> and are not committed, so a checkout without them fails here
    /// with a message that says where to put them rather than a bare file-not-found.
    /// </summary>
    private static string LocateFixtures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var nested = Path.Combine(dir.FullName, "tests", "fixtures");
            if (File.Exists(Path.Combine(nested, "TestData1.cdb")))
                return nested;

            // Older checkouts kept the samples at the repository root.
            if (File.Exists(Path.Combine(dir.FullName, "TestData1.cdb")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the sample databases. Put TestData1.cdb and TestData2.cdb in " +
            Path.Combine("tests", "fixtures") + " (see README, 'Test data'). Searched upwards from " +
            AppContext.BaseDirectory + ".");
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "palacedb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string At(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes text as UTF-8 without BOM, matching the CDB on-disk encoding.</summary>
    public string WriteFile(string name, string content)
    {
        var full = At(name);
        System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // A locked temp folder must never fail a test run.
        }
    }
}
