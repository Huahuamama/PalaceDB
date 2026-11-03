using PalaceDB.Core.Json;
using PalaceDB.Core.Merge;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The resolver (F16) is the part that looks at the folder rather than the data: it finds
/// the <c>.mine</c> and <c>.r&lt;N&gt;</c> files git leaves behind, feeds the three versions
/// to the merge engine, and removes the leftovers only once the result has been saved.
/// </summary>
public class MergeResolverTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private string DatabasePath => _temp.At("Game.cdb");

    private const string Base = """
        {"sheets":[{"name":"Items","lines":[{"id":"sword","Title":"Sword"},{"id":"shield","Title":"Shield"}],"columns":[{"name":"id","typeStr":"0"},{"name":"Title","typeStr":"1"}]}]}
        """;

    private string Write(string name, string content) => _temp.WriteFile(name, content);

    private void WriteDatabase(string content) => Write("Game.cdb", content);

    private string MinePath => MergeResolver.MinePathFor(DatabasePath);

    private static string RowTitles(string text)
    {
        var root = CdbReader.Parse(text);
        var sheet = ((CdbValue.Arr)root.Get("sheets")!).Items[0];
        var lines = ((CdbValue.Arr)((CdbValue.Obj)sheet).Value.Get("lines")!).Items;
        return string.Join(",", lines.Select(l => ((CdbValue.Obj)l).Value.GetString("Title")));
    }

    // ---------------------------------------------------------------- detection

    [Fact]
    public void AFreshFileHasNoPendingMerge()
    {
        WriteDatabase(Base);

        Assert.False(MergeResolver.HasPendingMerge(DatabasePath));
        Assert.Equal(MergeFileState.None, MergeResolver.Prepare(DatabasePath).State);
    }

    [Fact]
    public void AMineFileIsDetected()
    {
        WriteDatabase(Base);
        Write("Game.cdb.mine", Base);

        Assert.True(MergeResolver.HasPendingMerge(DatabasePath));
    }

    [Fact]
    public void ConflictMarkersInTheFileAreDetected()
    {
        WriteDatabase("<<<<<<< HEAD\n{\"a\":1}\n=======\n{\"a\":2}\n>>>>>>> other\n");

        Assert.True(MergeResolver.HasPendingMerge(DatabasePath));
    }

    [Fact]
    public void AMissingFileHasNoPendingMerge()
    {
        Assert.False(MergeResolver.HasPendingMerge(DatabasePath));
    }

    [Fact]
    public void TheMinePathSitsNextToTheDatabase()
    {
        Assert.Equal(DatabasePath + ".mine", MinePath);
        Assert.EndsWith("Game.cdb.mine", MinePath);
    }

    // ---------------------------------------------------------------- revision files

    [Fact]
    public void RevisionsAreListedOldestFirst()
    {
        WriteDatabase(Base);
        Write("Game.cdb.r10", Base);
        Write("Game.cdb.r2", Base);
        Write("Game.cdb.r1", Base);

        var revisions = MergeResolver.RevisionFiles(DatabasePath);

        Assert.Equal(3, revisions.Count);
        Assert.EndsWith("Game.cdb.r1", revisions[0]);
        Assert.EndsWith("Game.cdb.r2", revisions[1]);
        Assert.EndsWith("Game.cdb.r10", revisions[2]);
    }

    [Fact]
    public void UnrelatedFilesAreNotRevisions()
    {
        WriteDatabase(Base);
        Write("Game.cdb.mine", Base);
        Write("Game.cdb.backup", Base);
        Write("Other.cdb.r1", Base);

        Assert.Empty(MergeResolver.RevisionFiles(DatabasePath));
    }

    // ---------------------------------------------------------------- marker stripping

    [Fact]
    public void StrippingKeepsTheLocalHalf()
    {
        var text = "a\n<<<<<<< HEAD\nlocal\n=======\nincoming\n>>>>>>> other\nz";

        Assert.Equal("a\nlocal\nz", MergeResolver.StripMarkers(text, MergeSide.Mine));
    }

    [Fact]
    public void StrippingKeepsTheIncomingHalf()
    {
        var text = "a\n<<<<<<< HEAD\nlocal\n=======\nincoming\n>>>>>>> other\nz";

        Assert.Equal("a\nincoming\nz", MergeResolver.StripMarkers(text, MergeSide.Theirs));
    }

    [Fact]
    public void StrippingHandlesSeveralBlocks()
    {
        var text = "<<<<<<< a\n1\n=======\n2\n>>>>>>> b\nmid\n<<<<<<< a\n3\n=======\n4\n>>>>>>> b";

        Assert.Equal("1\nmid\n3", MergeResolver.StripMarkers(text, MergeSide.Mine));
        Assert.Equal("2\nmid\n4", MergeResolver.StripMarkers(text, MergeSide.Theirs));
    }

    [Fact]
    public void StrippingNormalisesLineEndings()
    {
        var text = "a\r\n<<<<<<< HEAD\r\nlocal\r\n=======\r\nincoming\r\n>>>>>>> other\r\nz";

        Assert.Equal("a\nlocal\nz", MergeResolver.StripMarkers(text, MergeSide.Mine));
    }

    [Fact]
    public void TextWithoutMarkersIsReturnedUnchanged()
    {
        Assert.Equal("plain", MergeResolver.StripMarkers("plain", MergeSide.Mine));
    }

    // ---------------------------------------------------------------- preparing

    [Fact]
    public void AMineFileIsMergedIntoTheWorkingFile()
    {
        // Base and incoming both say "Sword"; local renamed it to "Sabre".
        Write("Game.cdb.r1", Base);
        WriteDatabase(Base);
        Write("Game.cdb.mine", """
            {"sheets":[{"name":"Items","lines":[{"id":"sword","Title":"Sabre"},{"id":"shield","Title":"Shield"}],"columns":[{"name":"id","typeStr":"0"},{"name":"Title","typeStr":"1"}]}]}
            """);

        var outcome = MergeResolver.Prepare(DatabasePath);

        Assert.Equal(MergeFileState.Merged, outcome.State);
        Assert.Null(outcome.Error);
        Assert.False(outcome.Conflicts.Count > 0);
        Assert.Equal("Sabre,Shield", RowTitles(outcome.MergedText!));
        Assert.Contains(MinePath, outcome.SideFiles);
    }

    [Fact]
    public void AConflictIsReportedButTheLocalValueIsKept()
    {
        // Base says "Sword", local says "Katana", incoming says "Sabre".
        Write("Game.cdb.r1", Base);
        WriteDatabase("""
            {"sheets":[{"name":"Items","lines":[{"id":"sword","Title":"Sabre"},{"id":"shield","Title":"Shield"}],"columns":[{"name":"id","typeStr":"0"},{"name":"Title","typeStr":"1"}]}]}
            """);
        Write("Game.cdb.mine", """
            {"sheets":[{"name":"Items","lines":[{"id":"sword","Title":"Katana"},{"id":"shield","Title":"Shield"}],"columns":[{"name":"id","typeStr":"0"},{"name":"Title","typeStr":"1"}]}]}
            """);

        var outcome = MergeResolver.Prepare(DatabasePath);

        Assert.Equal(MergeFileState.Conflicted, outcome.State);
        var conflict = Assert.Single(outcome.Conflicts);
        Assert.Contains("sword", conflict.Path);
        Assert.Equal(MergeSide.Mine, conflict.Kept);
        Assert.Equal("Katana,Shield", RowTitles(outcome.MergedText!));
    }

    [Fact]
    public void TheOldestRevisionIsUsedAsTheBase()
    {
        // base says Sword; local changed it to Sabre; incoming left it alone.
        Write("Game.cdb.r1", Base);
        Write("Game.cdb.r2", Base);
        WriteDatabase(Base);
        Write("Game.cdb.mine", """
            {"sheets":[{"name":"Items","lines":[{"id":"sword","Title":"Sabre"},{"id":"shield","Title":"Shield"}],"columns":[{"name":"id","typeStr":"0"},{"name":"Title","typeStr":"1"}]}]}
            """);

        var outcome = MergeResolver.Prepare(DatabasePath);

        Assert.Equal(MergeFileState.Merged, outcome.State);
        Assert.Equal("Sabre,Shield", RowTitles(outcome.MergedText!));
        Assert.Equal(2, outcome.SideFiles.Count(r => r.Contains(".r")));
    }

    [Fact]
    public void ConflictMarkersInsideTheFileAreResolved()
    {
        // No .mine file: the two halves of each block are the two sides.
        WriteDatabase("""
            {"sheets":[{"name":"Items","lines":[{"id":"sword","Title":"Sword"}],"columns":[{"name":"id","typeStr":"0"},{"name":"Title","typeStr":"1"}]}]}
            """);

        var marked = string.Join("\n",
            "<<<<<<< HEAD",
            "{\"sheets\":[{\"name\":\"Items\",\"lines\":[{\"id\":\"sword\",\"Title\":\"Sabre\"}],\"columns\":[{\"name\":\"id\",\"typeStr\":\"0\"},{\"name\":\"Title\",\"typeStr\":\"1\"}]}]}",
            "=======",
            "{\"sheets\":[{\"name\":\"Items\",\"lines\":[{\"id\":\"sword\",\"Title\":\"Sword\"}],\"columns\":[{\"name\":\"id\",\"typeStr\":\"0\"},{\"name\":\"Title\",\"typeStr\":\"1\"}]}]}",
            ">>>>>>> other");

        WriteDatabase(marked);

        var outcome = MergeResolver.Prepare(DatabasePath);

        Assert.Equal(MergeFileState.Conflicted, outcome.State);
        Assert.Equal("Sabre", RowTitles(outcome.MergedText!));
    }

    [Fact]
    public void UnparsableSideFilesFailWithoutThrowing()
    {
        WriteDatabase("not json at all");
        Write("Game.cdb.mine", "also not json");

        var outcome = MergeResolver.Prepare(DatabasePath);

        Assert.Equal(MergeFileState.Failed, outcome.State);
        Assert.Null(outcome.MergedText);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
    }

    [Fact]
    public void PreparingWritesNothing()
    {
        WriteDatabase(Base);
        Write("Game.cdb.mine", Base);
        Write("Game.cdb.r1", Base);

        var before = Directory.GetFiles(_temp.Path).OrderBy(f => f).ToArray();
        var databaseBefore = File.ReadAllText(DatabasePath);

        MergeResolver.Prepare(DatabasePath);

        Assert.Equal(before, Directory.GetFiles(_temp.Path).OrderBy(f => f).ToArray());
        Assert.Equal(databaseBefore, File.ReadAllText(DatabasePath));
    }

    [Fact]
    public void TheMergedTextIsByteStable()
    {
        // Write the canonical form on both sides: merging identical versions must not
        // reformat the file, otherwise every merge would show up as a whole-file diff.
        var canonical = CdbWriter.Write(CdbReader.Parse(Base));
        WriteDatabase(canonical);
        Write("Game.cdb.mine", canonical);

        var outcome = MergeResolver.Prepare(DatabasePath);

        Assert.Equal(canonical, outcome.MergedText);
    }

    // ---------------------------------------------------------------- cleanup

    [Fact]
    public void CleanupRemovesEveryLeftover()
    {
        WriteDatabase(Base);
        Write("Game.cdb.mine", Base);
        Write("Game.cdb.r1", Base);
        Write("Game.cdb.r2", Base);

        var removed = MergeResolver.Cleanup(DatabasePath);

        Assert.Equal(3, removed);
        Assert.False(File.Exists(MinePath));
        Assert.Empty(MergeResolver.RevisionFiles(DatabasePath));
        Assert.True(File.Exists(DatabasePath));
        Assert.False(MergeResolver.HasPendingMerge(DatabasePath));
    }

    [Fact]
    public void CleanupOnACleanFileRemovesNothing()
    {
        WriteDatabase(Base);

        Assert.Equal(0, MergeResolver.Cleanup(DatabasePath));
        Assert.True(File.Exists(DatabasePath));
    }

    [Fact]
    public void CleanupLeavesOtherFilesAlone()
    {
        WriteDatabase(Base);
        Write("Game.cdb.mine", Base);
        Write("Game.cdb.backup", "keep me");

        MergeResolver.Cleanup(DatabasePath);

        Assert.True(File.Exists(_temp.At("Game.cdb.backup")));
    }
}
