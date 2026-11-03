using System.Text.Json;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Editing;

/// <summary>Payload exchanged through the system clipboard.</summary>
public sealed class ClipboardPayload
{
    public string Sheet { get; init; } = string.Empty;

    /// <summary>Column names in the order they were copied.</summary>
    public List<string> Columns { get; init; } = new();

    /// <summary>One list of raw values per row, parallel to <see cref="Columns"/>.</summary>
    public List<List<CdbValue>> Rows { get; init; } = new();

    public int RowCount => Rows.Count;

    public int ColumnCount => Columns.Count;
}

/// <summary>
/// Copies and pastes cells as JSON on the system clipboard.
/// <para>
/// Values are stored as raw CDB values rather than display text, so numbers stay
/// numbers and nested lists or property objects survive a copy/paste cycle. The
/// payload is plain JSON, which also makes it usable from external tools.
/// </para>
/// </summary>
public static class ClipboardService
{
    public const string FormatId = "palacedb/cells";
    public const int CurrentVersion = 1;

    /// <summary>Serialises a rectangular region. The identifier column is optional.</summary>
    public static string Serialize(
        SheetData sheet,
        IEnumerable<int> rowIndices,
        IEnumerable<string> columnNames,
        bool includeIdColumn)
    {
        var columns = columnNames
            .Where(name => includeIdColumn || sheet.GetColumn(name)?.Type is not ColumnType.Id)
            .ToList();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", FormatId);
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteString("sheet", sheet.Name);

            writer.WriteStartArray("columns");
            foreach (var name in columns)
                writer.WriteStringValue(name);
            writer.WriteEndArray();

            writer.WriteStartArray("rows");
            foreach (var index in rowIndices)
            {
                if (index < 0 || index >= sheet.Lines.Count)
                    continue;

                var row = sheet.Lines[index];
                writer.WriteStartArray();
                foreach (var name in columns)
                    WriteValue(writer, row.Get(name) ?? CdbValue.NullValue);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parses clipboard text. Returns null when it is not a PalaceDB payload.</summary>
    public static ClipboardPayload? Deserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != FormatId)
                return null;

            var columns = new List<string>();
            if (root.TryGetProperty("columns", out var columnsElement) && columnsElement.ValueKind == JsonValueKind.Array)
                foreach (var column in columnsElement.EnumerateArray())
                    columns.Add(column.GetString() ?? string.Empty);

            var rows = new List<List<CdbValue>>();
            if (root.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rowElement in rowsElement.EnumerateArray())
                {
                    if (rowElement.ValueKind != JsonValueKind.Array)
                        continue;
                    var values = new List<CdbValue>();
                    foreach (var value in rowElement.EnumerateArray())
                        values.Add(ReadValue(value));
                    rows.Add(values);
                }
            }

            return new ClipboardPayload
            {
                Sheet = root.TryGetProperty("sheet", out var sheet) ? sheet.GetString() ?? string.Empty : string.Empty,
                Columns = columns,
                Rows = rows,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, CdbValue value)
    {
        switch (value)
        {
            case CdbValue.Null:
                writer.WriteNullValue();
                break;
            case CdbValue.Bool b:
                writer.WriteBooleanValue(b.Value);
                break;
            case CdbValue.Number n:
                // Raw text keeps values such as 1.0 intact; otherwise the number is written as-is.
                if (n.Raw is not null && double.TryParse(n.Raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    writer.WriteNumberValue(parsed);
                else
                    writer.WriteNumberValue(n.Value);
                break;
            case CdbValue.Str s:
                writer.WriteStringValue(s.Value);
                break;
            case CdbValue.Arr a:
                writer.WriteStartArray();
                foreach (var item in a.Items)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            case CdbValue.Obj o:
                writer.WriteStartObject();
                foreach (var key in o.Value.Keys)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, o.Value.Get(key) ?? CdbValue.NullValue);
                }
                writer.WriteEndObject();
                break;
        }
    }

    private static CdbValue ReadValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return CdbValue.NullValue;
            case JsonValueKind.True:
                return new CdbValue.Bool(true);
            case JsonValueKind.False:
                return new CdbValue.Bool(false);
            case JsonValueKind.String:
                return new CdbValue.Str(element.GetString() ?? string.Empty);
            case JsonValueKind.Number:
                return new CdbValue.Number(element.GetDouble(), element.GetRawText());
            case JsonValueKind.Array:
                var items = element.EnumerateArray().Select(ReadValue).ToList();
                return new CdbValue.Arr(items);
            case JsonValueKind.Object:
                var obj = new CdbObject();
                foreach (var property in element.EnumerateObject())
                    obj.Set(property.Name, ReadValue(property.Value));
                return new CdbValue.Obj(obj);
            default:
                return CdbValue.NullValue;
        }
    }
}
