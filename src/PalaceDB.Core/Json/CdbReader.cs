using System.Text.Json;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.Core.Json;

/// <summary>Parses CDB JSON text into the ordered <see cref="CdbObject"/> tree.</summary>
public static class CdbReader
{
    public static CdbObject Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CdbFormatException("The file is empty.");

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 256,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new CdbFormatException("The root of a CDB file must be a JSON object.");

            return ReadObject(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new CdbFormatException(
                $"Invalid JSON at line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}: {ex.Message}", ex);
        }
    }

    /// <summary>Parses text and builds a <see cref="Database"/>.</summary>
    public static Database ParseDatabase(string json) => new(Parse(json));

    /// <summary>
    /// Parses a single JSON value (not a whole document). Used by the side-car, which
    /// stores column defaults as plain JSON.
    /// </summary>
    public static CdbValue ParseValue(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadValue(doc.RootElement);
    }

    private static CdbObject ReadObject(JsonElement element)
    {
        var obj = new CdbObject();
        foreach (var property in element.EnumerateObject())
            obj.Set(property.Name, ReadValue(property.Value));
        return obj;
    }

    private static CdbValue ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => CdbValue.NullValue,
        JsonValueKind.True => new CdbValue.Bool(true),
        JsonValueKind.False => new CdbValue.Bool(false),
        JsonValueKind.String => new CdbValue.Str(element.GetString() ?? string.Empty),
        JsonValueKind.Number => ReadNumber(element),
        JsonValueKind.Array => ReadArray(element),
        JsonValueKind.Object => new CdbValue.Obj(ReadObject(element)),
        _ => throw new CdbFormatException($"Unsupported JSON token {element.ValueKind}."),
    };

    private static CdbValue ReadNumber(JsonElement element)
    {
        // Keep the exact source text so that a load/save cycle never rewrites
        // values such as 1.0, 1e21 or 0.10000000000000001.
        var raw = element.GetRawText();
        var value = element.TryGetDouble(out var d)
            ? d
            : double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        return new CdbValue.Number(value, raw);
    }

    private static CdbValue ReadArray(JsonElement element)
    {
        var items = new List<CdbValue>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
            items.Add(ReadValue(item));
        return new CdbValue.Arr(items);
    }
}
