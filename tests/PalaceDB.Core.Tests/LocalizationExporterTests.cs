using PalaceDB.Core.Export;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Export of the localizable text (F08). The XML shape has to match what the original
/// PalaceDB produced, because translation pipelines parse it directly.
/// </summary>
public class LocalizationExporterTests
{
    private static Database Load() => CdbReader.ParseDatabase(Fixtures.SampleJson);

    private static SheetData Items(Database database) => database.GetSheet("Items")!;

    /// <summary>Marks the named columns of "Items" as localizable.</summary>
    private static Database WithLocalizable(params string[] names)
    {
        var database = Load();
        var sheet = Items(database);
        foreach (var name in names)
        {
            var column = sheet.GetColumn(name);
            Assert.NotNull(column);
            column!.Kind = ColumnKind.Localizable;
        }
        return database;
    }

    [Fact]
    public void OnlyLocalizableColumnsAreExported()
    {
        var database = WithLocalizable("name");

        var export = LocalizationExporter.Export(database);

        Assert.All(export.Texts, t => Assert.Equal("name", t.Column));
        Assert.DoesNotContain("cost", export.Xml);
        Assert.Contains("<name>Sword</name>", export.Xml);
    }

    [Fact]
    public void ADatabaseWithoutLocalizableColumnsExportsAnEmptyDocument()
    {
        var database = Load();

        var export = LocalizationExporter.Export(database);

        Assert.Empty(export.Texts);
        Assert.Equal("<cdb>\n</cdb>\n", export.Xml);
        Assert.False(LocalizationExporter.HasLocalizableText(database));
    }

    [Fact]
    public void TheDocumentWrapsEachSheetInItsOwnElement()
    {
        var database = WithLocalizable("name");

        var export = LocalizationExporter.Export(database);

        Assert.StartsWith("<cdb>\n", export.Xml);
        Assert.EndsWith("</cdb>\n", export.Xml);
        Assert.Contains("\t<sheet name=\"Items\">\n", export.Xml);
        Assert.Contains("\t</sheet>\n", export.Xml);
    }

    [Fact]
    public void EachRowBecomesAnElementNamedAfterItsIdentifier()
    {
        var database = WithLocalizable("name");

        var export = LocalizationExporter.Export(database);

        Assert.Contains("\t\t<sword>\n", export.Xml);
        Assert.Contains("\t\t</sword>\n", export.Xml);
    }

    [Fact]
    public void TheExportCountsWhatItWrote()
    {
        var database = WithLocalizable("name");

        var export = LocalizationExporter.Export(database);

        Assert.Equal(export.Texts.Count, export.Xml.Split('\n').Count(l => l.Contains("<name>")));
        Assert.All(export.Texts, t => Assert.Equal("Items", t.Sheet));
    }

    [Fact]
    public void EmptyValuesAreSkipped()
    {
        var database = Load();
        var sheet = Items(database);
        sheet.GetColumn("note")!.Kind = ColumnKind.Localizable;

        // Fixtures leaves "note" empty on some rows.
        var export = LocalizationExporter.Export(database);

        Assert.DoesNotContain("<note></note>", export.Xml);
        Assert.DoesNotContain("<note/>", export.Xml);
    }

    [Fact]
    public void ARowWithoutAnyTextIsOmitted()
    {
        var database = Load();
        var sheet = Items(database);
        var column = sheet.GetColumn("note")!;
        column.Kind = ColumnKind.Localizable;

        foreach (var row in sheet.Lines)
            row.Set("note", CdbValue.NullValue);

        var export = LocalizationExporter.Export(database);

        Assert.Empty(export.Texts);
        Assert.DoesNotContain("<sheet", export.Xml);
    }

    [Fact]
    public void MarkupInTheTextIsEscaped()
    {
        var database = WithLocalizable("name");
        Items(database).Lines[0].Set("name", new CdbValue.Str("A & B <tag> \"q\""));

        var export = LocalizationExporter.Export(database);

        Assert.Contains("A &amp; B &lt;tag&gt; \"q\"", export.Xml);
        Assert.DoesNotContain("<tag>", export.Xml);
    }

    [Fact]
    public void AMultiLineValueIsWrittenAsIndentedContent()
    {
        var database = WithLocalizable("name");
        Items(database).Lines[0].Set("name", new CdbValue.Str("line one\nline two"));

        var export = LocalizationExporter.Export(database);

        Assert.Contains("\t\t\t<name>\n\t\t\t\tline one\nline two\n\t\t\t</name>\n", export.Xml);
    }

    [Fact]
    public void ASingleLineValueStaysOnOneLine()
    {
        var database = WithLocalizable("name");

        var export = LocalizationExporter.Export(database);

        Assert.Contains("\t\t\t<name>Sword</name>\n", export.Xml);
    }

    [Fact]
    public void ASheetWithoutLocalizableColumnsIsOmitted()
    {
        var database = WithLocalizable("name");
        var sheet = Items(database);
        sheet.Props.Hide = false;
        sheet.GetColumn("name")!.Kind = ColumnKind.None;

        var export = LocalizationExporter.Export(database);

        Assert.DoesNotContain("<sheet", export.Xml);
    }

    [Fact]
    public void HiddenSheetsAreNeverExported()
    {
        var database = WithLocalizable("name");
        Items(database).Props.Hide = true;

        var export = LocalizationExporter.Export(database);

        Assert.Empty(export.Texts);
    }

    [Fact]
    public void OnlyTheRequestedSheetsCanBeExported()
    {
        var database = WithLocalizable("name");

        Assert.Empty(LocalizationExporter.Export(database, new[] { "Nope" }).Texts);
        Assert.NotEmpty(LocalizationExporter.Export(database, new[] { "Items" }).Texts);
    }

    [Fact]
    public void ARowWithoutAnIdentifierFallsBackToItsPosition()
    {
        var database = Load();
        var sheet = Items(database);
        var column = sheet.GetColumn("note")!;
        column.Kind = ColumnKind.Localizable;
        sheet.Lines[0].Set("note", new CdbValue.Str("orphan"));

        // Remove the id column so the sheet has no identifier to name rows with.
        sheet.Columns.Remove(sheet.GetColumn("id")!);

        var export = LocalizationExporter.Export(database);

        Assert.Contains("<line0>", export.Xml);
    }

    [Theory]
    [InlineData("ok", "ok")]
    [InlineData("with space", "with_space")]
    [InlineData("9lives", "_9lives")]
    [InlineData("-dash", "_-dash")]
    [InlineData("a.b-c_d", "a.b-c_d")]
    public void ElementNamesAreSanitized(string input, string expected)
    {
        Assert.Equal(expected, LocalizationExporter.SanitizeName(input));
    }

    [Fact]
    public void AnEmptyNameFallsBackToLine()
    {
        Assert.Equal("line", LocalizationExporter.SanitizeName(string.Empty));
    }

    [Fact]
    public void TheByteOrderMarkIsTheCharacterTheOriginalWrote()
    {
        Assert.Equal('\uFEFF', LocalizationExporter.Bom);
    }

    [Fact]
    public void NonTextColumnsCannotBeExported()
    {
        var database = Load();
        var sheet = Items(database);
        sheet.GetColumn("cost")!.Kind = ColumnKind.Localizable;

        var export = LocalizationExporter.Export(database);

        // The kind is set but the type is numeric, so it is not collected.
        Assert.Empty(export.Texts);
    }
}
