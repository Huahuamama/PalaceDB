using System.Text;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Export;

/// <summary>One exported string: which column of which row it came from.</summary>
public sealed record ExportedText(string Sheet, string RowId, string Column, string Value);

/// <summary>Result of an export run.</summary>
public sealed record LocalizationExport(string Xml, IReadOnlyList<ExportedText> Texts)
{
    public int Count => Texts.Count;
}

/// <summary>
/// Exports the localizable text of a database as an XML sheet (F08).
/// <para>
/// Only columns whose <c>kind</c> is <c>localizable</c> are collected, which is how
/// PalaceDB marks text that a translator should see. The document shape follows the
/// original PalaceDB export so existing translation pipelines keep working:
/// </para>
/// <code>
/// &lt;cdb&gt;
/// 	&lt;sheet name="EventCard"&gt;
/// 		&lt;vocational&gt;
/// 			&lt;Title&gt;Vocational training&lt;/Title&gt;
/// 		&lt;/vocational&gt;
/// 	&lt;/sheet&gt;
/// &lt;/cdb&gt;
/// </code>
/// <para>
/// The element name of a row is its identifier; rows without one fall back to
/// <c>line&lt;index&gt;</c>. A row with no non-empty localizable value is skipped, so the
/// file only ever contains strings that actually need translating.
/// </para>
/// </summary>
public static class LocalizationExporter
{
    /// <summary>Byte order mark the original tool writes in front of the XML.</summary>
    public const char Bom = '\uFEFF';

    public static LocalizationExport Export(Database database) => Export(database, null);

    /// <summary>
    /// Exports the whole database, or only the listed sheets when
    /// <paramref name="onlySheets"/> is given.
    /// </summary>
    public static LocalizationExport Export(Database database, IReadOnlyCollection<string>? onlySheets)
    {
        var builder = new StringBuilder();
        var texts = new List<ExportedText>();

        builder.Append("<cdb>\n");

        foreach (var sheet in database.VisibleSheets)
        {
            if (onlySheets is not null && !onlySheets.Contains(sheet.Name))
                continue;

            var columns = LocalizableColumns(sheet);
            if (columns.Count == 0)
                continue;

            var body = new StringBuilder();
            var rowIndex = 0;

            foreach (var row in sheet.Lines)
            {
                var id = RowId(sheet, row, rowIndex);
                rowIndex++;

                if (string.IsNullOrEmpty(id))
                    continue;

                var values = new List<(string Column, string Value)>();
                foreach (var column in columns)
                {
                    var value = row.GetString(column.Name);
                    if (!string.IsNullOrEmpty(value))
                        values.Add((column.Name, value));
                }

                if (values.Count == 0)
                    continue;

                var tag = SanitizeName(id);
                body.Append("\t\t<").Append(tag).Append(">\n");

                foreach (var (column, value) in values)
                {
                    var name = SanitizeName(column);
                    texts.Add(new ExportedText(sheet.Name, id, column, value));

                    // A single-line value stays on one line; anything with a line break is
                    // written as indented block content, exactly like the original export.
                    if (!value.Contains('\n') && !value.Contains('\r'))
                    {
                        body.Append("\t\t\t<").Append(name).Append('>')
                            .Append(Escape(value))
                            .Append("</").Append(name).Append(">\n");
                    }
                    else
                    {
                        body.Append("\t\t\t<").Append(name).Append(">\n");
                        body.Append("\t\t\t\t").Append(Escape(value.Trim())).Append('\n');
                        body.Append("\t\t\t</").Append(name).Append(">\n");
                    }
                }

                body.Append("\t\t</").Append(tag).Append(">\n");
            }

            if (body.Length == 0)
                continue;

            builder.Append("\t<sheet name=\"").Append(EscapeAttribute(sheet.Name)).Append("\">\n");
            builder.Append(body);
            builder.Append("\t</sheet>\n");
        }

        builder.Append("</cdb>\n");

        return new LocalizationExport(builder.ToString(), texts);
    }

    /// <summary>Columns that carry translatable text, in document order.</summary>
    public static List<Column> LocalizableColumns(SheetData sheet) =>
        sheet.Columns
            .Where(c => c.Kind == ColumnKind.Localizable && c.Type is ColumnType.Text or ColumnType.Id)
            .ToList();

    /// <summary>True when the database has at least one localizable column.</summary>
    public static bool HasLocalizableText(Database database) =>
        database.VisibleSheets.Any(s => LocalizableColumns(s).Count > 0);

    private static string RowId(SheetData sheet, CdbObject row, int index)
    {
        var idColumn = sheet.IdColumn;
        if (idColumn is null)
            return "line" + index;
        return row.GetString(idColumn.Name) ?? string.Empty;
    }

    /// <summary>
    /// Turns an arbitrary identifier into a usable XML element name. Identifiers in a
    /// PalaceDB file are usually already valid, but spaces and leading digits are not.
    /// </summary>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "line";

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            var valid = char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.';
            builder.Append(valid ? ch : '_');
        }

        // An element name may not start with a digit, a dot or a hyphen.
        if (char.IsDigit(builder[0]) || builder[0] is '.' or '-')
            builder.Insert(0, '_');

        return builder.ToString();
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string EscapeAttribute(string value) => Escape(value).Replace("\"", "&quot;");
}
