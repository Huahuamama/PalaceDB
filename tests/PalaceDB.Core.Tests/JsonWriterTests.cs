using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The writer must reproduce the original Haxe serialiser byte for byte:
/// tab indentation, LF line endings, no trailing newline and compact empty
/// containers. Every test here maps to a rule from the format specification.
/// </summary>
public class JsonWriterTests
{
    [Fact]
    public void EmptyObjectIsCompact()
    {
        Assert.Equal("{}", CdbWriter.Write(new CdbObject()));
    }

    [Fact]
    public void EmptyArrayIsCompact()
    {
        var root = new CdbObject();
        root.Set("a", new CdbValue.Arr());
        Assert.Equal("{\n\t\"a\": []\n}", CdbWriter.Write(root));
    }

    [Fact]
    public void UsesTabsForIndentation()
    {
        var inner = new CdbObject();
        inner.Set("x", new CdbValue.Number(1, null));

        var root = new CdbObject();
        root.Set("outer", new CdbValue.Obj(inner));

        var text = CdbWriter.Write(root);
        Assert.Contains("\n\t\"outer\": {\n\t\t\"x\": 1\n\t}\n", text);
        Assert.DoesNotContain("  ", text);
    }

    [Fact]
    public void UsesLineFeedsOnly()
    {
        var root = new CdbObject();
        root.Set("a", new CdbValue.Arr(new List<CdbValue> { new CdbValue.Number(1, null) }));

        var text = CdbWriter.Write(root);
        Assert.DoesNotContain('\r', text);
        Assert.Contains('\n', text);
    }

    [Fact]
    public void HasNoTrailingNewline()
    {
        var root = new CdbObject();
        root.Set("a", new CdbValue.Str("b"));

        var text = CdbWriter.Write(root);
        Assert.False(text.EndsWith("\n"));
        Assert.False(text.EndsWith("\r"));
    }

    [Fact]
    public void PreservesKeyInsertionOrder()
    {
        var root = new CdbObject();
        root.Set("zeta", new CdbValue.Number(1, null));
        root.Set("alpha", new CdbValue.Number(2, null));
        root.Set("mid", new CdbValue.Number(3, null));

        var text = CdbWriter.Write(root);
        Assert.True(text.IndexOf("zeta", StringComparison.Ordinal) < text.IndexOf("alpha", StringComparison.Ordinal));
        Assert.True(text.IndexOf("alpha", StringComparison.Ordinal) < text.IndexOf("mid", StringComparison.Ordinal));
    }

    [Fact]
    public void OverwritingAKeyKeepsItsPosition()
    {
        var root = new CdbObject();
        root.Set("a", new CdbValue.Number(1, null));
        root.Set("b", new CdbValue.Number(2, null));
        root.Set("a", new CdbValue.Number(9, null));

        Assert.Equal(new[] { "a", "b" }, root.Keys);
        Assert.Equal(9d, ((CdbValue.Number)root.Get("a")!).Value);
    }

    [Fact]
    public void SeparatorIsCommaThenNewline()
    {
        var root = new CdbObject();
        root.Set("a", new CdbValue.Number(1, null));
        root.Set("b", new CdbValue.Number(2, null));

        Assert.Equal("{\n\t\"a\": 1,\n\t\"b\": 2\n}", CdbWriter.Write(root));
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("line\nbreak", "\"line\\nbreak\"")]
    [InlineData("tab\there", "\"tab\\there\"")]
    [InlineData("cr\rhere", "\"cr\\rhere\"")]
    public void EscapesSpecialCharacters(string input, string expected)
    {
        var root = new CdbObject();
        root.Set("k", new CdbValue.Str(input));
        Assert.Equal("{\n\t\"k\": " + expected + "\n}", CdbWriter.Write(root));
    }

    [Fact]
    public void EscapesControlCharactersAsUnicode()
    {
        var root = new CdbObject();
        root.Set("k", new CdbValue.Str("\u0001"));
        Assert.Contains("\\u0001", CdbWriter.Write(root));
    }

    [Fact]
    public void KeepsNonAsciiTextVerbatim()
    {
        var root = new CdbObject();
        root.Set("k", new CdbValue.Str("café 中文"));
        Assert.Contains("café 中文", CdbWriter.Write(root));
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(1d, "1")]
    [InlineData(-1d, "-1")]
    [InlineData(42d, "42")]
    [InlineData(0.5d, "0.5")]
    [InlineData(-2.25d, "-2.25")]
    [InlineData(1e20d, "1e+20")]
    public void FormatsNumbersLikeTheOriginal(double value, string expected)
    {
        Assert.Equal(expected, new CdbValue.Number(value, null).ToString());
    }

    [Fact]
    public void PrefersRawSourceTextWhenAvailable()
    {
        // Raw text preserves values that would otherwise be reformatted (0.0 vs 0).
        Assert.Equal("0.0", new CdbValue.Number(0, "0.0").ToString());
        Assert.Equal("1.10", new CdbValue.Number(1.1, "1.10").ToString());
    }

    [Fact]
    public void WritesNullLiteral()
    {
        var root = new CdbObject();
        root.Set("k", CdbValue.NullValue);
        Assert.Equal("{\n\t\"k\": null\n}", CdbWriter.Write(root));
    }

    [Fact]
    public void DeeplyNestedContainersAreIndentedCorrectly()
    {
        var leaf = new CdbObject();
        leaf.Set("v", new CdbValue.Bool(true));

        var list = new CdbValue.Arr(new List<CdbValue> { new CdbValue.Obj(leaf) });

        var root = new CdbObject();
        root.Set("rows", list);

        var expected = "{\n\t\"rows\": [\n\t\t{\n\t\t\t\"v\": true\n\t\t}\n\t]\n}";
        Assert.Equal(expected, CdbWriter.Write(root));
    }
}
