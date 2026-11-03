using System.Text;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using PalaceDB.Core.Storage;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Byte-stable round-trip is the single most important invariant of the project:
/// opening and saving a file without editing anything must not change one byte.
/// A failure here means the editor would create spurious diffs in every repository.
/// </summary>
public class RoundTripTests
{
    public static TheoryData<string> Files => new() { "TestData1.cdb", "TestData2.cdb" };

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadThenWriteIsByteIdentical(string fileName)
    {
        var path = Path.Combine(TestPaths.Root, fileName);
        var original = File.ReadAllBytes(path);
        var text = new UTF8Encoding(false).GetString(original);

        var database = CdbReader.ParseDatabase(text);
        var written = CdbWriter.Write(database);

        Assert.Equal(original.Length, Encoding.UTF8.GetByteCount(written));
        Assert.Equal(text, written);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadingTwiceProducesTheSameModel(string fileName)
    {
        var path = Path.Combine(TestPaths.Root, fileName);
        var text = File.ReadAllText(path, new UTF8Encoding(false));

        var first = CdbReader.ParseDatabase(text);
        var second = CdbReader.ParseDatabase(text);

        Assert.Equal(first.Sheets.Count, second.Sheets.Count);
        for (var i = 0; i < first.Sheets.Count; i++)
        {
            Assert.Equal(first.Sheets[i].Name, second.Sheets[i].Name);
            Assert.Equal(first.Sheets[i].Columns.Count, second.Sheets[i].Columns.Count);
            Assert.Equal(first.Sheets[i].Lines.Count, second.Sheets[i].Lines.Count);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void SaveThroughAtomicFileKeepsTheFileIntact(string fileName)
    {
        var source = Path.Combine(TestPaths.Root, fileName);
        var original = File.ReadAllBytes(source);

        using var temp = TestPaths.NewTempDirectory();
        var target = temp.At(fileName);
        File.WriteAllBytes(target, original);

        var database = CdbReader.ParseDatabase(AtomicFile.ReadText(target));
        AtomicFile.WriteText(target, CdbWriter.Write(database));

        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FileHasNoByteOrderMark(string fileName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestPaths.Root, fileName));
        Assert.True(bytes.Length > 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FileUsesLineFeedsOnly(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, fileName), new UTF8Encoding(false));
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void UnknownKeysSurviveTheRoundTrip()
    {
        // Forward compatibility: a field written by a future version must not be dropped.
        const string source = "{\n\t\"sheets\": [],\n\t\"customTypes\": [],\n\t\"compress\": false,\n\t\"futureKey\": \"kept\"\n}";
        var database = CdbReader.ParseDatabase(source);
        Assert.Equal(source, CdbWriter.Write(database));
    }

    [Fact]
    public void ExplicitNullsSurviveTheRoundTrip()
    {
        const string source = "{\n\t\"sheets\": [\n\t\t{\n\t\t\t\"name\": \"A\",\n\t\t\t\"columns\": [],\n\t\t\t\"lines\": [\n\t\t\t\t{\n\t\t\t\t\t\"id\": \"a\",\n\t\t\t\t\t\"note\": null\n\t\t\t\t}\n\t\t\t]\n\t\t}\n\t],\n\t\"customTypes\": []\n}";
        var database = CdbReader.ParseDatabase(source);
        Assert.Equal(source, CdbWriter.Write(database));
    }

    [Fact]
    public void SheetWithoutPropsDoesNotGainOne()
    {
        const string source = "{\n\t\"sheets\": [\n\t\t{\n\t\t\t\"name\": \"A\",\n\t\t\t\"columns\": [],\n\t\t\t\"lines\": []\n\t\t}\n\t],\n\t\"customTypes\": []\n}";
        var database = CdbReader.ParseDatabase(source);
        Assert.Equal(source, CdbWriter.Write(database));
    }

    [Fact]
    public void SheetWithoutSeparatorsDoesNotGainThem()
    {
        const string source = "{\n\t\"sheets\": [\n\t\t{\n\t\t\t\"name\": \"A\",\n\t\t\t\"columns\": [],\n\t\t\t\"lines\": []\n\t\t}\n\t],\n\t\"customTypes\": []\n}";
        var database = CdbReader.ParseDatabase(source);
        Assert.DoesNotContain("separators", CdbWriter.Write(database));
    }

    [Fact]
    public void NumberTextIsPreservedExactly()
    {
        // 0.0 and 1e21 are common in hand-edited files and must not be normalised.
        const string source = "{\n\t\"a\": 0.0,\n\t\"b\": 1e21,\n\t\"c\": -0.5\n}";
        var database = CdbReader.ParseDatabase(source);
        Assert.Equal(source, CdbWriter.Write(database));
    }
}
