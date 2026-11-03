using PalaceDB.Core.Json;
using PalaceDB.Core.Merge;
using PalaceDB.Core.Model;
using PalaceDB.Core.Storage;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The merge engine run against the real sample database (F16). The synthetic cases in
/// <see cref="ThreeWayMergeTests"/> use the field name <c>id</c>; this file pins the
/// behaviour on the actual files, whose key is <c>Id</c>.
/// </summary>
public class RealDataMergeTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private string DatabasePath => _temp.At("Game.cdb");

    private static CdbObject Clone(CdbObject source) => source.Clone();
    /// <summary>The first sheet's rows as an array of row objects.</summary>
    private static List<CdbValue> Rows(CdbObject root) =>
        ((CdbValue.Arr)((CdbValue.Obj)((CdbValue.Arr)root.Get("sheets")!).Items[0]).Value.Get("lines")!).Items;

    private static void SetRowId(CdbObject root, int index, string id) =>
        ((CdbValue.Obj)Rows(root)[index]).Value.Set("Id", new CdbValue.Str(id));

    private static IEnumerable<string?> RowIds(CdbObject root) =>
        Rows(root).Select(r => ((CdbValue.Obj)r).Value.GetString("Id"));

    private static IEnumerable<string?> RowIds(string text) => RowIds(CdbReader.Parse(text));

    [Fact]
    public void TheRealDatabaseMergesARenameAndAnAddedRow()
    {
        var baseText = AtomicFile.ReadText(TestPaths.TestData2);
        var baseRoot = CdbReader.Parse(baseText);

        var mineRoot = Clone(baseRoot);
        SetRowId(mineRoot, 0, "LOCAL_ROW");

        var theirsRoot = Clone(baseRoot);
        var added = ((CdbValue.Obj)Rows(theirsRoot)[0]).Value.Clone();
        added.Set("Id", new CdbValue.Str("INCOMING_ROW"));
        Rows(theirsRoot).Add(new CdbValue.Obj(added));

        AtomicFile.WriteText(DatabasePath, CdbWriter.Write(theirsRoot));
        AtomicFile.WriteText(MergeResolver.MinePathFor(DatabasePath), CdbWriter.Write(mineRoot));
        AtomicFile.WriteText(DatabasePath + ".r1", CdbWriter.Write(baseRoot));

        var outcome = MergeResolver.Prepare(DatabasePath);
        var ids = RowIds(outcome.MergedText!).ToList();

        Assert.Equal(MergeFileState.Merged, outcome.State);
        Assert.Contains("INCOMING_ROW", ids);
        Assert.Contains("LOCAL_ROW", ids);
        Assert.Equal(RowIds(baseRoot).Count() + 1, ids.Count);
    }
}
