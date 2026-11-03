using PalaceDB.Core.Json;
using PalaceDB.Core.Merge;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The merge engine (F16) decides what a database looks like after two people edited the
/// same file. These tests pin the rules: one-sided changes win silently, two-sided changes
/// keep the local value and are reported, and rows are matched by identity so that
/// inserting a row on one side does not shift every later row on the other.
/// </summary>
public class ThreeWayMergeTests
{
    private static CdbValue Parse(string json) => new CdbValue.Obj(CdbReader.Parse(json));

    /// <summary>A one-sheet database with the given row and column JSON.</summary>
    private static string Db(string lines, string columns = DefaultColumns) =>
        "{\"sheets\":[{\"name\":\"Items\",\"lines\":" + lines + ",\"columns\":" + columns + "}]}";

    private const string DefaultColumns =
        "[{\"name\":\"id\",\"typeStr\":\"0\"},{\"name\":\"Title\",\"typeStr\":\"1\"}]";

    private static MergeResult Merge(string? baseJson, string mineJson, string theirsJson) =>
        ThreeWayMerge.Merge(
            baseJson is null ? null : Parse(baseJson),
            Parse(mineJson),
            Parse(theirsJson));

    // ---------------------------------------------------------------- one-sided changes

    [Fact]
    public void IdenticalSidesProduceNoConflict()
    {
        var same = "{\"a\":1}";
        var result = Merge(same, same, same);

        Assert.True(result.Succeeded);
        Assert.False(result.HasConflicts);
        Assert.True(ThreeWayMerge.Equal(Parse(same), result.Value));
    }

    [Fact]
    public void AValueChangedOnlyLocallyIsKept()
    {
        var result = Merge("{\"a\":1}", "{\"a\":2}", "{\"a\":1}");

        Assert.False(result.HasConflicts);
        Assert.Equal(2, ((CdbValue.Number)((CdbValue.Obj)result.Value!).Value.Get("a")!).Value);
    }

    [Fact]
    public void AValueChangedOnlyOnTheOtherSideIsTaken()
    {
        var result = Merge("{\"a\":1}", "{\"a\":1}", "{\"a\":3}");

        Assert.False(result.HasConflicts);
        Assert.Equal(3, ((CdbValue.Number)((CdbValue.Obj)result.Value!).Value.Get("a")!).Value);
    }

    [Fact]
    public void BothSidesReachingTheSameValueIsNotAConflict()
    {
        var result = Merge("{\"a\":1}", "{\"a\":7}", "{\"a\":7}");

        Assert.False(result.HasConflicts);
        Assert.Equal(7, ((CdbValue.Number)((CdbValue.Obj)result.Value!).Value.Get("a")!).Value);
    }

    [Fact]
    public void BothSidesChangingTheSameValueKeepsLocalAndReportsIt()
    {
        var result = Merge("{\"a\":1}", "{\"a\":2}", "{\"a\":3}");

        Assert.True(result.HasConflicts);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("a", conflict.Path);
        Assert.Equal(MergeSide.Mine, conflict.Kept);
        Assert.Equal("2", conflict.Mine);
        Assert.Equal("3", conflict.Theirs);
        Assert.Equal(2, ((CdbValue.Number)((CdbValue.Obj)result.Value!).Value.Get("a")!).Value);
    }

    // ---------------------------------------------------------------- objects

    [Fact]
    public void ObjectsAreMergedFieldByField()
    {
        var result = Merge(
            "{\"a\":1,\"b\":2}",
            "{\"a\":10,\"b\":2}",
            "{\"a\":1,\"b\":20}");

        var merged = ((CdbValue.Obj)result.Value!).Value;
        Assert.False(result.HasConflicts);
        Assert.Equal(10, ((CdbValue.Number)merged.Get("a")!).Value);
        Assert.Equal(20, ((CdbValue.Number)merged.Get("b")!).Value);
    }

    [Fact]
    public void TheIncomingKeyOrderIsTheSkeleton()
    {
        var result = Merge(
            "{\"a\":1,\"b\":2}",
            "{\"b\":2,\"a\":1}",
            "{\"b\":9,\"a\":1}");

        Assert.Equal(new[] { "b", "a" }, ((CdbValue.Obj)result.Value!).Value.Keys);
    }

    [Fact]
    public void AFieldAddedLocallySurvives()
    {
        var result = Merge("{\"a\":1}", "{\"a\":1,\"b\":2}", "{\"a\":1}");

        Assert.False(result.HasConflicts);
        Assert.Equal(2, ((CdbValue.Number)((CdbValue.Obj)result.Value!).Value.Get("b")!).Value);
    }

    [Fact]
    public void AFieldDeletedOnTheOtherSideIsDeleted()
    {
        var result = Merge("{\"a\":1,\"b\":2}", "{\"a\":1,\"b\":2}", "{\"a\":1}");

        Assert.False(result.HasConflicts);
        Assert.Null(((CdbValue.Obj)result.Value!).Value.Get("b"));
    }

    [Fact]
    public void AFieldDeletedLocallyIsDeleted()
    {
        var result = Merge("{\"a\":1,\"b\":2}", "{\"a\":1}", "{\"a\":1,\"b\":2}");

        Assert.False(result.HasConflicts);
        Assert.Null(((CdbValue.Obj)result.Value!).Value.Get("b"));
    }

    [Fact]
    public void NestedObjectsMergeIndependently()
    {
        var result = Merge(
            "{\"s\":{\"x\":1,\"y\":2}}",
            "{\"s\":{\"x\":10,\"y\":2}}",
            "{\"s\":{\"x\":1,\"y\":20}}");

        var nested = ((CdbValue.Obj)((CdbValue.Obj)result.Value!).Value.Get("s")!).Value;
        Assert.Equal(10, ((CdbValue.Number)nested.Get("x")!).Value);
        Assert.Equal(20, ((CdbValue.Number)nested.Get("y")!).Value);
    }

    [Fact]
    public void AConflictPathNamesTheEnclosingFields()
    {
        var result = Merge(
            "{\"s\":{\"v\":[1]}}",
            "{\"s\":{\"v\":[2]}}",
            "{\"s\":{\"v\":{\"x\":1}}}");

        // An array replaced by an object cannot be merged element by element.
        Assert.True(result.HasConflicts);
        Assert.Equal("s/v", result.Conflicts[0].Path);
    }

    // ---------------------------------------------------------------- keyed arrays

    [Fact]
    public void RowsAreMatchedByIdentityNotPosition()
    {
        var result = Merge(
            Db("[{\"id\":\"sword\",\"Title\":\"Sword\"}]"),
            Db("[{\"id\":\"shield\",\"Title\":\"Shield\"},{\"id\":\"sword\",\"Title\":\"Sword\"}]"),
            Db("[{\"id\":\"sword\",\"Title\":\"Sabre\"}]"));

        Assert.False(result.HasConflicts);

        var rows = ((CdbValue.Arr)((CdbValue.Obj)((CdbValue.Arr)((CdbValue.Obj)result.Value!).Value.Get("sheets")!).Items[0]).Value.Get("lines")!).Items;
        Assert.Equal(2, rows.Count);

        var byId = rows.ToDictionary(
            r => ((CdbValue.Obj)r).Value.GetString("id")!,
            r => ((CdbValue.Obj)r).Value.GetString("Title")!);

        Assert.Equal("Sabre", byId["sword"]);
        Assert.Equal("Shield", byId["shield"]);
    }

    [Fact]
    public void ARowDeletedOnOneSideIsDropped()
    {
        var result = Merge(
            Db("[{\"id\":\"sword\",\"Title\":\"Sword\"},{\"id\":\"shield\",\"Title\":\"Shield\"}]"),
            Db("[{\"id\":\"sword\",\"Title\":\"Sabre\"},{\"id\":\"shield\",\"Title\":\"Shield\"}]"),
            Db("[{\"id\":\"sword\",\"Title\":\"Sword\"}]"));

        Assert.False(result.HasConflicts);

        var rows = ((CdbValue.Arr)((CdbValue.Obj)((CdbValue.Arr)((CdbValue.Obj)result.Value!).Value.Get("sheets")!).Items[0]).Value.Get("lines")!).Items;
        var only = Assert.Single(rows);
        Assert.Equal("Sabre", ((CdbValue.Obj)only).Value.GetString("Title"));
    }

    [Fact]
    public void ARowDeletedOnOneSideAndEditedOnTheOtherIsAConflict()
    {
        var result = Merge(
            Db("[{\"id\":\"sword\",\"Title\":\"Sword\"},{\"id\":\"shield\",\"Title\":\"Shield\"}]"),
            Db("[{\"id\":\"sword\",\"Title\":\"Sword\"},{\"id\":\"shield\",\"Title\":\"Buckler\"}]"),
            Db("[{\"id\":\"sword\",\"Title\":\"Sword\"}]"));

        Assert.True(result.HasConflicts);
        Assert.Contains("shield", result.Conflicts[0].Path);

        // The edited row is kept so that no work disappears silently.
        var rows = ((CdbValue.Arr)((CdbValue.Obj)((CdbValue.Arr)((CdbValue.Obj)result.Value!).Value.Get("sheets")!).Items[0]).Value.Get("lines")!).Items;
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ColumnsAreMatchedByTheirName()
    {
        var result = Merge(
            Db("[]", "[{\"name\":\"id\",\"typeStr\":\"0\"},{\"name\":\"Title\",\"typeStr\":\"1\"}]"),
            Db("[]", "[{\"name\":\"Title\",\"typeStr\":\"1\"},{\"name\":\"id\",\"typeStr\":\"0\"},{\"name\":\"Cost\",\"typeStr\":\"3\"}]"),
            Db("[]", "[{\"name\":\"id\",\"typeStr\":\"0\"},{\"name\":\"Title\",\"typeStr\":\"6:Items\"}]"));

        Assert.False(result.HasConflicts);

        var columns = ((CdbValue.Arr)((CdbValue.Obj)((CdbValue.Arr)((CdbValue.Obj)result.Value!).Value.Get("sheets")!).Items[0]).Value.Get("columns")!).Items;
        var byName = columns.ToDictionary(
            c => ((CdbValue.Obj)c).Value.GetString("name")!,
            c => ((CdbValue.Obj)c).Value.GetString("typeStr")!);

        Assert.Equal("6:Items", byName["Title"]);
        Assert.Equal("0", byName["id"]);
        Assert.Equal("3", byName["Cost"]);
    }

    // ---------------------------------------------------------------- unkeyed arrays

    [Fact]
    public void ArraysWithoutIdentityMergeByIndex()
    {
        var result = Merge("{\"v\":[1,2,3]}", "{\"v\":[9,2,3]}", "{\"v\":[1,8,3]}");

        Assert.False(result.HasConflicts);
        var items = ((CdbValue.Arr)((CdbValue.Obj)result.Value!).Value.Get("v")!).Items;
        Assert.Equal(3, items.Count);
        Assert.Equal(9, ((CdbValue.Number)items[0]).Value);
        Assert.Equal(8, ((CdbValue.Number)items[1]).Value);
        Assert.Equal(3, ((CdbValue.Number)items[2]).Value);
    }

    [Fact]
    public void ALengthChangeWithoutIdentityIsAConflict()
    {
        // Both sides added an element, so neither version can be attributed the change.
        var result = Merge("{\"v\":[1,2]}", "{\"v\":[1,2,3]}", "{\"v\":[1,2,4,5]}");

        Assert.True(result.HasConflicts);
        var items = ((CdbValue.Arr)((CdbValue.Obj)result.Value!).Value.Get("v")!).Items;
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void AnArrayOfStringsIsNotTreatedAsKeyed()
    {
        var result = Merge("{\"v\":[\"a\",\"b\"]}", "{\"v\":[\"x\",\"b\"]}", "{\"v\":[\"a\",\"y\"]}");

        Assert.False(result.HasConflicts);
        var items = ((CdbValue.Arr)((CdbValue.Obj)result.Value!).Value.Get("v")!).Items;
        Assert.Equal("x", ((CdbValue.Str)items[0]).Value);
        Assert.Equal("y", ((CdbValue.Str)items[1]).Value);
    }

    // ---------------------------------------------------------------- degraded cases

    [Fact]
    public void WithoutABaseVersionOnlyRealDifferencesAreReported()
    {
        var result = Merge(null, "{\"a\":1,\"b\":2}", "{\"a\":1,\"b\":3}");

        Assert.True(result.HasConflicts);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("b", conflict.Path);
        Assert.Equal("(absent)", conflict.Base);
    }

    [Fact]
    public void WithoutABaseVersionAgreeingFieldsPassThrough()
    {
        var result = Merge(null, "{\"a\":1,\"b\":2}", "{\"a\":1,\"b\":2}");

        Assert.False(result.HasConflicts);
        Assert.True(ThreeWayMerge.Equal(Parse("{\"a\":1,\"b\":2}"), result.Value));
    }

    [Fact]
    public void AOneSidedTypeChangeIsKept()
    {
        var result = Merge("{\"a\":1}", "{\"a\":\"one\"}", "{\"a\":1}");

        Assert.False(result.HasConflicts);
        Assert.IsType<CdbValue.Str>(((CdbValue.Obj)result.Value!).Value.Get("a"));
    }

    [Fact]
    public void TwoDifferentTypeChangesAreAConflict()
    {
        var result = Merge("{\"a\":1}", "{\"a\":\"one\"}", "{\"a\":true}");

        Assert.True(result.HasConflicts);
        Assert.IsType<CdbValue.Str>(((CdbValue.Obj)result.Value!).Value.Get("a"));
    }

    [Fact]
    public void AMergedTreeCanBeWrittenAndReadAgain()
    {
        var result = Merge(
            Db("[{\"id\":\"sword\",\"Title\":\"Sword\"}]"),
            Db("[{\"id\":\"sword\",\"Title\":\"Sabre\"}]"),
            Db("[{\"id\":\"sword\",\"Title\":\"Sword\"},{\"id\":\"shield\",\"Title\":\"Shield\"}]"));

        Assert.True(result.Succeeded);

        var text = CdbWriter.Write(((CdbValue.Obj)result.Value!).Value);
        var reparsed = new CdbValue.Obj(CdbReader.Parse(text));

        Assert.True(ThreeWayMerge.Equal(result.Value, reparsed));
        Assert.Contains("\"shield\"", text);
        Assert.Contains("\"Sabre\"", text);
    }

    // ---------------------------------------------------------------- helpers

    [Fact]
    public void EqualIgnoresObjectKeyOrder()
    {
        Assert.True(ThreeWayMerge.Equal(Parse("{\"a\":1,\"b\":2}"), Parse("{\"b\":2,\"a\":1}")));
    }

    [Fact]
    public void EqualComparesNumbersByValue()
    {
        // 1 and 1.0 are the same number even though their source text differs.
        Assert.True(ThreeWayMerge.Equal(Parse("{\"a\":1}"), Parse("{\"a\":1.0}")));
    }

    [Fact]
    public void EqualDistinguishesDifferentShapes()
    {
        Assert.False(ThreeWayMerge.Equal(Parse("{\"a\":1}"), Parse("{\"a\":[1]}")));
        Assert.False(ThreeWayMerge.Equal(Parse("{\"a\":1}"), null));
    }

    [Fact]
    public void DescribeClipsLongValues()
    {
        var longText = new string('x', 200);
        var described = ThreeWayMerge.Describe(new CdbValue.Str(longText));

        Assert.Equal(61, described.Length);
        Assert.EndsWith("\u2026", described);
    }

    [Fact]
    public void DescribeFlattensNewlines()
    {
        Assert.Equal("a b", ThreeWayMerge.Describe(new CdbValue.Str("a\nb")));
    }

    [Fact]
    public void DescribeSummarisesContainers()
    {
        var array = new CdbValue.Arr(new List<CdbValue> { CdbValue.Of(1), CdbValue.Of(2) });

        Assert.Equal("[2 items]", ThreeWayMerge.Describe(array));
        Assert.Equal("{2 fields}", ThreeWayMerge.Describe(Parse("{\"a\":1,\"b\":2}")));
        Assert.Equal("(absent)", ThreeWayMerge.Describe(null));
    }
}
