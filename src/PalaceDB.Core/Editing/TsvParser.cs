namespace PalaceDB.Core.Editing;

/// <summary>
/// Parses tab separated text, the format spreadsheets put on the clipboard (R5).
/// <para>
/// Quoting follows the convention every spreadsheet uses: a field that starts with a
/// double quote may contain tabs, line breaks and doubled quotes (<c>""</c>), which is
/// what makes pasting a cell that holds a sentence with a newline work.
/// </para>
/// </summary>
public static class TsvParser
{
    private const char BOM = '\uFEFF';

    /// <summary>Splits text into rows of fields. A trailing line break adds no row.</summary>
    public static List<List<string>> Parse(string? text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text))
            return rows;

        if (text[0] == BOM)
            text = text[1..];

        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        var index = 0;

        while (index < text.Length)
        {
            var c = text[index];

            if (quoted)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is one literal quote.
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index += 2;
                        continue;
                    }

                    quoted = false;
                    index++;
                    continue;
                }

                field.Append(c);
                index++;
                continue;
            }

            switch (c)
            {
                case '"' when !fieldStarted:
                    quoted = true;
                    fieldStarted = true;
                    index++;
                    break;

                case '\t':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    index++;
                    break;

                case '\r':
                    // Consume CRLF as one break; a lone CR also ends the row.
                    index += index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                    row.Add(field.ToString());
                    rows.Add(row);
                    row = new List<string>();
                    field.Clear();
                    fieldStarted = false;
                    break;

                case '\n':
                    index++;
                    row.Add(field.ToString());
                    rows.Add(row);
                    row = new List<string>();
                    field.Clear();
                    fieldStarted = false;
                    break;

                default:
                    field.Append(c);
                    fieldStarted = true;
                    index++;
                    break;
            }
        }

        // The last row is only added when something was actually written to it, so a
        // trailing newline (which every editor adds) does not produce a blank row.
        // A lone quoted empty field ("") still counts as one cell.
        if (field.Length > 0 || row.Count > 0 || fieldStarted)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>True when the text looks like more than one field or more than one row.</summary>
    public static bool LooksLikeTable(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains('\t') || text.Contains('\n'));
}
