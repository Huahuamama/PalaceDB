using PalaceDB.Core.Editing;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

public class SearchEngineTests
{
    /// <summary>Two visible tables that share a word, so scope can be told apart.</summary>
    private const string TwoSheets = """
{
	"sheets": [
		{
			"name": "A",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "1", "name": "text" }
			],
			"lines": [
				{ "id": "a", "text": "hello world" }
			]
		},
		{
			"name": "B",
			"columns": [
				{ "typeStr": "0", "name": "id" },
				{ "typeStr": "1", "name": "text" }
			],
			"lines": [
				{ "id": "b", "text": "hello again" }
			]
		}
	],
	"compress": false
}
""";

    private static Database TwoSheetDatabase() => CdbReader.ParseDatabase(TwoSheets);

    private static SearchQuery Query(
        string text,
        bool regex = false,
        bool matchCase = false,
        SearchScope scope = SearchScope.Document,
        string? sheet = null) =>
        new(text, regex, matchCase, scope, sheet);

    // ---------------------------------------------------------------- matching

    [Fact]
    public void LiteralSearchIsCaseInsensitiveByDefault()
    {
        var result = SearchEngine.Search(Fixtures.Sample(), Query("sword"));

        Assert.True(result.Matches.Count > 0);
        Assert.Contains(result.Matches, m => m.ColumnName == "name");
    }

    [Fact]
    public void MatchCaseNarrowsTheHits()
    {
        var insensitive = SearchEngine.Search(Fixtures.Sample(), Query("sword"));
        var sensitive = SearchEngine.Search(Fixtures.Sample(), Query("sword", matchCase: true));

        Assert.True(sensitive.Matches.Count < insensitive.Matches.Count);
        Assert.All(sensitive.Matches, m => Assert.Contains("sword", m.Text));
    }

    [Fact]
    public void RegexMatchesAndReportsTheSpan()
    {
        var result = SearchEngine.Search(Fixtures.Sample(), Query(@"S\w+rd", regex: true));

        Assert.NotEmpty(result.Matches);
        var first = result.Matches[0];
        // The first row's identifier is the lowercase "sword"; the pattern is case-insensitive.
        Assert.Equal("sword", first.Text.Substring(first.Start, first.Length));
    }

    [Fact]
    public void InvalidRegexThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            SearchEngine.Search(Fixtures.Sample(), Query("(", regex: true)));
    }

    [Fact]
    public void EnumColumnsMatchTheirLabelNotTheirIndex()
    {
        var result = SearchEngine.Search(Fixtures.Sample(), Query("Rare"));

        Assert.Single(result.Matches);
        Assert.Equal("rarity", result.Matches[0].ColumnName);
    }

    [Fact]
    public void BooleansMatchTrueAndFalse()
    {
        var result = SearchEngine.Search(Fixtures.Sample(), Query("false"));

        Assert.Single(result.Matches);
        Assert.Equal("enabled", result.Matches[0].ColumnName);
    }

    // ---------------------------------------------------------------- scope

    [Fact]
    public void TableScopeOnlyWalksThatTable()
    {
        var result = SearchEngine.Search(
            TwoSheetDatabase(), Query("hello", scope: SearchScope.Table, sheet: "A"));

        Assert.Single(result.Matches);
        Assert.Equal("A", result.Matches[0].SheetName);
    }

    [Fact]
    public void DocumentScopeWalksEveryVisibleTable()
    {
        var result = SearchEngine.Search(TwoSheetDatabase(), Query("hello"));

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(new[] { "A", "B" }, result.Matches.Select(m => m.SheetName).Distinct());
    }

    // ---------------------------------------------------------------- replacement

    [Fact]
    public void ReplaceOneOnlyChangesTheChosenSpan()
    {
        var query = Query("hello");
        var spans = SearchEngine.FindAll("hello hello", query, null);

        Assert.Equal(2, spans.Count);
        Assert.Equal("bye hello", SearchEngine.ReplaceOne("hello hello", spans, "bye", 0));
        Assert.Equal("hello bye", SearchEngine.ReplaceOne("hello hello", spans, "bye", 1));
    }

    [Fact]
    public void RegexReplacementExpandsGroups()
    {
        var query = Query(@"(\d+)", regex: true);
        var regex = SearchEngine.Compile(query);

        Assert.Equal("abc<123>", SearchEngine.ReplaceAll("abc123", query, regex, "<$1>"));
    }

    [Fact]
    public void PlainTextReplacementTreatsDollarLiterally()
    {
        var query = Query("123");

        Assert.Equal("abc$1", SearchEngine.ReplaceAll("abc123", query, null, "$1"));
    }

    [Fact]
    public void ReplaceAllRewritesEveryOccurrenceInACell()
    {
        var query = Query("o");

        Assert.Equal("f--", SearchEngine.ReplaceAll("foo", query, null, "-"));
    }

    [Fact]
    public void IdentifiersAndStructuredValuesAreNotReplaceable()
    {
        var db = Fixtures.Sample();
        var items = Fixtures.Items(db);

        Assert.False(SearchEngine.CanReplace(items.GetColumn("id")!));
        Assert.True(SearchEngine.CanReplace(items.GetColumn("name")!));
        Assert.True(SearchEngine.CanReplace(items.GetColumn("cost")!));
    }

    [Fact]
    public void EmptyQueryFindsNothing()
    {
        var result = SearchEngine.Search(Fixtures.Sample(), Query(""));

        Assert.Empty(result.Matches);
    }
}
