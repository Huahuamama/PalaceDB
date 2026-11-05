using System.Text;
using System.Text.RegularExpressions;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Editing;

/// <summary>Where a search runs: the table on screen, or every visible table.</summary>
public enum SearchScope
{
    Table,
    Document,
}

/// <summary>A find/replace request. <see cref="Text"/> empty means "no search".</summary>
public sealed record SearchQuery(
    string Text,
    bool UseRegex,
    bool MatchCase,
    SearchScope Scope,
    string? SheetName)
{
    public bool IsEmpty => string.IsNullOrEmpty(Text);
}

/// <summary>One hit inside one cell. <see cref="Start"/>/<see cref="Length"/> index into <see cref="Text"/>.</summary>
public sealed record SearchMatch(
    string SheetName,
    int Row,
    int Column,
    string ColumnName,
    int Start,
    int Length,
    string Text);

/// <summary>All hits of one query, plus whether the cap stopped the walk early.</summary>
public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, bool Truncated);

/// <summary>A span of one cell's text. <see cref="Match"/> is set for regex hits so that
/// <c>$1</c> and friends expand the way the user expects.</summary>
public readonly record struct MatchSpan(int Start, int Length, Match? Match);

/// <summary>
/// Text search and replacement over the document's cells.
/// <para>
/// Search reads the same text the cell editor would be given — identifiers, numbers,
/// enum labels, flag names, paths — rather than the grid's formatted display, because
/// replacing is a write: matching "Y" for a boolean and then parsing "Y" back would not
/// round-trip. Enum columns match their label, which is what the user types.
/// </para>
/// <para>
/// Structured values (List, Properties, Polymorph) and identifiers are searchable but not
/// replaceable: a blind substring rewrite would either corrupt JSON or silently break every
/// reference to a renamed row.
/// </para>
/// </summary>
public static class SearchEngine
{
    /// <summary>Guards against a pathological pattern freezing the UI thread.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Stops a broad query from building a list large enough to stall the UI.</summary>
    public const int DefaultLimit = 5000;

    /// <summary>Compiles the query, or returns null for a plain-text search.</summary>
    public static Regex? Compile(SearchQuery query)
    {
        if (!query.UseRegex)
            return null;

        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!query.MatchCase)
            options |= RegexOptions.IgnoreCase;

        // An invalid pattern throws ArgumentException; the host turns that into a message.
        return new Regex(query.Text, options, RegexTimeout);
    }

    /// <summary>
    /// The text a cell contributes to the search. Mirrors what the editor would show when
    /// the cell is opened, not the grid's abbreviated display.
    /// </summary>
    public static string TextOf(Column column, CdbObject row)
    {
        var value = row.Get(column.Name);
        if (value is null or CdbValue.Null)
            return string.Empty;

        return column.Type switch
        {
            // The editor parses booleans from "true"/"false", not from the grid's Y/N.
            ColumnType.Bool => value is CdbValue.Bool b ? (b.Value ? "true" : "false") : RawText(value),
            // An index-based enum is edited by its label; an enum-as-string already is one.
            ColumnType.Enum e when !column.EnumAsString && value is CdbValue.Number n
                => n.Value >= 0 && n.Value < e.Values.Count ? e.Values[(int)n.Value] : RawText(value),
            _ => RawText(value),
        };
    }

    /// <summary>False for values a text replacement must not rewrite.</summary>
    public static bool CanReplace(Column column) =>
        column.Type is not (ColumnType.Id or ColumnType.List or ColumnType.Properties
            or ColumnType.Polymorph or ColumnType.Custom);

    /// <summary>All non-empty matches of the query in one cell.</summary>
    public static IReadOnlyList<MatchSpan> FindAll(string text, SearchQuery query, Regex? regex)
    {
        var spans = new List<MatchSpan>();
        if (string.IsNullOrEmpty(text) || query.IsEmpty)
            return spans;

        if (regex is not null)
        {
            foreach (Match match in regex.Matches(text))
            {
                // A zero-length match is a position, not a highlight, and would make the
                // cursor dance on the same spot. Replace-all still honours it via the regex.
                if (match.Length > 0)
                    spans.Add(new MatchSpan(match.Index, match.Length, match));
            }
            return spans;
        }

        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = 0;
        while (start <= text.Length - query.Text.Length)
        {
            var at = text.IndexOf(query.Text, start, comparison);
            if (at < 0)
                break;

            spans.Add(new MatchSpan(at, query.Text.Length, null));
            start = at + Math.Max(1, query.Text.Length);
        }
        return spans;
    }

    /// <summary>Rewrites one match of a cell, expanding regex groups when present.</summary>
    public static string ReplaceOne(
        string text,
        IReadOnlyList<MatchSpan> spans,
        string replacement,
        int matchIndex)
    {
        if (matchIndex < 0 || matchIndex >= spans.Count)
            return text;

        var span = spans[matchIndex];
        var expanded = span.Match is { } match ? match.Result(replacement) : replacement;
        return string.Concat(
            text.AsSpan(0, span.Start),
            expanded,
            text.AsSpan(span.Start + span.Length));
    }

    /// <summary>Rewrites every match of a cell in one pass.</summary>
    public static string ReplaceAll(string text, SearchQuery query, Regex? regex, string replacement)
    {
        if (string.IsNullOrEmpty(text) || query.IsEmpty)
            return text;

        // Regex.Replace expands $1 etc. and handles zero-length matches correctly.
        if (regex is not null)
            return regex.Replace(text, replacement);

        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var builder = new StringBuilder(text.Length);
        var start = 0;

        while (true)
        {
            var at = text.IndexOf(query.Text, start, comparison);
            if (at < 0)
            {
                builder.Append(text, start, text.Length - start);
                break;
            }

            builder.Append(text, start, at - start);
            // The replacement is literal here: "$1" in a plain-text replace is just text.
            builder.Append(replacement);
            start = at + query.Text.Length;
        }

        return builder.ToString();
    }

    /// <summary>Walks the requested sheets and collects every hit, up to <paramref name="limit"/>.</summary>
    public static SearchResult Search(Database database, SearchQuery query, int limit = DefaultLimit)
    {
        if (query.IsEmpty)
            return new SearchResult(Array.Empty<SearchMatch>(), false);

        var regex = Compile(query);
        var matches = new List<SearchMatch>();
        var sheets = TargetSheets(database, query);

        foreach (var sheet in sheets)
        {
            for (var rowIndex = 0; rowIndex < sheet.Lines.Count; rowIndex++)
            {
                var row = sheet.Lines[rowIndex];
                for (var columnIndex = 0; columnIndex < sheet.Columns.Count; columnIndex++)
                {
                    var column = sheet.Columns[columnIndex];
                    var text = TextOf(column, row);
                    if (text.Length == 0)
                        continue;

                    foreach (var span in FindAll(text, query, regex))
                    {
                        matches.Add(new SearchMatch(
                            sheet.Name, rowIndex, columnIndex, column.Name, span.Start, span.Length, text));

                        if (matches.Count >= limit)
                            return new SearchResult(matches, true);
                    }
                }
            }
        }

        return new SearchResult(matches, false);
    }

    /// <summary>
    /// Visible sheets only: a List/Properties value lives inside its parent cell, so walking
    /// the hidden backing tables would report the same content twice and let replace-all
    /// rewrite it in a place the user cannot see.
    /// </summary>
    private static IEnumerable<SheetData> TargetSheets(Database database, SearchQuery query)
    {
        if (query.Scope == SearchScope.Document)
            return database.VisibleSheets;

        var sheet = query.SheetName is null ? null : database.GetSheet(query.SheetName);
        return sheet is null || sheet.IsHidden
            ? Enumerable.Empty<SheetData>()
            : new[] { sheet };
    }

    private static string RawText(CdbValue value) => value switch
    {
        CdbValue.Str s => s.Value,
        CdbValue.Number n => n.ToString(),
        CdbValue.Bool b => b.Value ? "true" : "false",
        CdbValue.Null => string.Empty,
        CdbValue.Arr or CdbValue.Obj => Compact(value),
        _ => string.Empty,
    };

    private static string Compact(CdbValue value) => value switch
    {
        CdbValue.Arr a => "[" + string.Join(",", a.Items.Select(Compact)) + "]",
        CdbValue.Obj o => "{" + string.Join(",", o.Value.Keys.Select(k => k + ":" + Compact(o.Value.Get(k)!))) + "}",
        CdbValue.Str s => "\"" + s.Value + "\"",
        _ => RawText(value),
    };
}
