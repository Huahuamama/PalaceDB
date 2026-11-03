using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class CdbObjectTests
{
    [Fact]
    public void KeysFollowInsertionOrder()
    {
        var obj = new CdbObject();
        obj.Set("b", CdbValue.Of(1));
        obj.Set("a", CdbValue.Of(2));
        obj.Set("c", CdbValue.Of(3));

        Assert.Equal(new[] { "b", "a", "c" }, obj.Keys);
    }

    [Fact]
    public void SettingAnExistingKeyReplacesTheValueInPlace()
    {
        var obj = new CdbObject();
        obj.Set("a", CdbValue.Of(1));
        obj.Set("b", CdbValue.Of(2));
        obj.Set("a", CdbValue.Of(3));

        Assert.Equal(new[] { "a", "b" }, obj.Keys);
        Assert.Equal(3d, ((CdbValue.Number)obj.Get("a")!).Value);
        Assert.Equal(2, obj.Count);
    }

    [Fact]
    public void RemoveDropsTheKeyAndTheValue()
    {
        var obj = new CdbObject();
        obj.Set("a", CdbValue.Of(1));
        obj.Set("b", CdbValue.Of(2));

        Assert.True(obj.Remove("a"));
        Assert.False(obj.Remove("a"));
        Assert.Null(obj.Get("a"));
        Assert.Equal(new[] { "b" }, obj.Keys);
    }

    [Fact]
    public void SettingNullRemovesTheKey()
    {
        var obj = new CdbObject();
        obj.Set("a", CdbValue.Of(1));
        obj.Set("a", null);
        Assert.False(obj.ContainsKey("a"));
    }

    [Fact]
    public void CloneIsDeep()
    {
        var inner = new CdbObject();
        inner.Set("x", CdbValue.Of(1));

        var root = new CdbObject();
        root.Set("o", new CdbValue.Obj(inner));

        var clone = root.Clone();
        inner.Set("x", CdbValue.Of(99));

        Assert.Equal(1d, ((CdbValue.Number)((CdbValue.Obj)clone.Get("o")!).Value.Get("x")!).Value);
    }

    [Fact]
    public void TypedGettersReturnNullForMissingOrWrongType()
    {
        var obj = new CdbObject();
        obj.Set("s", new CdbValue.Str("text"));
        obj.Set("n", CdbValue.Of(4));
        obj.Set("b", CdbValue.Of(true));

        Assert.Equal("text", obj.GetString("s"));
        Assert.Null(obj.GetString("n"));
        Assert.Equal(4, obj.GetInt("n"));
        Assert.Null(obj.GetInt("s"));
        Assert.True(obj.GetBool("b"));
        Assert.Null(obj.GetBool("s"));
        Assert.Null(obj.GetArray("s"));
    }

    [Fact]
    public void IsNullOrMissingTreatsAbsentAndNullAlike()
    {
        var obj = new CdbObject();
        obj.Set("explicit", CdbValue.NullValue);
        obj.Set("value", CdbValue.Of(1));

        Assert.True(obj.IsNullOrMissing("explicit"));
        Assert.True(obj.IsNullOrMissing("absent"));
        Assert.False(obj.IsNullOrMissing("value"));
    }

    [Fact]
    public void ParsedDocumentKeepsTheOriginalKeyOrder()
    {
        const string source = "{\n\t\"z\": 1,\n\t\"y\": 2,\n\t\"x\": 3\n}";
        var obj = CdbReader.Parse(source);
        Assert.Equal(new[] { "z", "y", "x" }, obj.Keys);
    }
}

public class CdbValueTests
{
    [Fact]
    public void NumberWithoutRawTextUsesCanonicalFormatting()
    {
        Assert.Equal("3", new CdbValue.Number(3, null).ToString());
        Assert.Equal("3.5", new CdbValue.Number(3.5, null).ToString());
    }

    [Fact]
    public void NumberWithRawTextKeepsTheSourceSpelling()
    {
        Assert.Equal("3.0", new CdbValue.Number(3, "3.0").ToString());
    }

    [Fact]
    public void IsIntegerDetectsWholeNumbers()
    {
        Assert.True(new CdbValue.Number(5, null).IsInteger);
        Assert.False(new CdbValue.Number(5.5, null).IsInteger);
    }

    [Fact]
    public void NaNAndInfinityAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CdbNumber.Format(double.NaN));
        Assert.Throws<InvalidOperationException>(() => CdbNumber.Format(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(1234567890d, "1234567890")]
    [InlineData(0.125d, "0.125")]
    public void FormatIsStable(double value, string expected)
    {
        Assert.Equal(expected, CdbNumber.Format(value));
    }

    [Fact]
    public void NegativeZeroIsWrittenAsZero()
    {
        Assert.Equal("0", CdbNumber.Format(-0d));
    }
}
