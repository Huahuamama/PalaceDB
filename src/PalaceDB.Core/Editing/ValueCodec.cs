using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Editing;

/// <summary>Raised when user input cannot be converted to the column type.</summary>
public sealed class CdbValidationException : Exception
{
    public CdbValidationException(string message) : base(message) { }
}

/// <summary>
/// Converts text typed by the user into a typed <see cref="CdbValue"/>,
/// mirroring the validation rules of the original editor.
/// </summary>
public static partial class ValueCodec
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^[0-9]+$")]
    private static partial Regex NumericIdRegex();

    /// <summary>
    /// True when the text can be a row identifier: the classic
    /// <c>[A-Za-z_][A-Za-z0-9_]*</c> form, or an all-digit id. Numeric ids occur in
    /// hand-written and generated data (levels "0", "1", ...), and rejecting them made
    /// those files impossible to edit.
    /// </summary>
    public static bool IsValidId(string text) =>
        IdentifierRegex().IsMatch(text) || NumericIdRegex().IsMatch(text);

    /// <summary>True for an all-digit identifier ("0", "123", "007").</summary>
    public static bool IsNumericId(string text) => NumericIdRegex().IsMatch(text);

    [GeneratedRegex(@"^-?[0-9]+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^#?[0-9A-Fa-f]{6,8}$")]
    private static partial Regex ColorRegex();

    public static CdbValue? ParseText(Column column, string text) =>
        Parse(column.Type, column.Optional, column.EnumAsString, text);

    /// <summary>
    /// Parses the "default value" field of the column dialog (C01 / C02), where the
    /// column may not exist yet. Enum defaults accept both an index and a value name.
    /// </summary>
    public static CdbValue? ParseDefaultValue(ColumnType type, bool optional, string text) =>
        Parse(type, optional, enumAsString: false, text);

    private static CdbValue? Parse(ColumnType type, bool optional, bool enumAsString, string text)
    {
        if (optional && text.Length == 0)
            return null;

        switch (type)
        {
            case ColumnType.Text or ColumnType.Image or ColumnType.File or ColumnType.Formula:
                return new CdbValue.Str(text);

            case ColumnType.Id:
                if (!IsValidId(text))
                    throw new CdbValidationException(
                        "An identifier must match [A-Za-z_][A-Za-z0-9_]* or be all digits.");
                return new CdbValue.Str(text);

            case ColumnType.Ref:
                if (text.Length == 0)
                    return optional ? null : new CdbValue.Str(string.Empty);
                // A reference must accept whatever the target table can use as an id,
                // including an all-digit one.
                if (!IsValidId(text))
                    throw new CdbValidationException("A reference must be a valid identifier.");
                return new CdbValue.Str(text);

            case ColumnType.Int:
                if (!IntegerRegex().IsMatch(text))
                    throw new CdbValidationException("An integer is required.");
                return new CdbValue.Number(long.Parse(text, CultureInfo.InvariantCulture), null);

            case ColumnType.Float:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new CdbValidationException("A number is required.");
                return new CdbValue.Number(number, null);

            case ColumnType.Bool:
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1")
                    return new CdbValue.Bool(true);
                if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" || text.Length == 0)
                    return new CdbValue.Bool(false);
                throw new CdbValidationException("true or false expected.");

            case ColumnType.Enum e:
                if (enumAsString)
                {
                    var index = e.Values.ToList().IndexOf(text);
                    if (index < 0)
                        throw new CdbValidationException($"'{text}' is not one of: {string.Join(", ", e.Values)}");
                    return new CdbValue.Str(text);
                }
                // The label wins over the index: an enum whose values are "1,2,3" must
                // read the text "2" as the entry labelled 2, not as the third one.
                var byName = e.Values.ToList().IndexOf(text);
                if (byName >= 0)
                    return new CdbValue.Number(byName, null);

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ei))
                {
                    if (ei < 0 || ei >= e.Values.Count)
                        throw new CdbValidationException($"Enum index must be between 0 and {e.Values.Count - 1}.");
                    return new CdbValue.Number(ei, null);
                }
                throw new CdbValidationException($"Unknown enum value '{text}'.");

            case ColumnType.Flags flagsType:
            {
                // Names first, for the same reason as enums: a flag literally called "1"
                // must not be read as the mask 1.
                var byFlagNames = TryParseFlagNames(flagsType, text);
                if (byFlagNames is not null)
                    return new CdbValue.Number(byFlagNames.Value, null);

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask))
                    return new CdbValue.Number(mask, null);

                throw new CdbValidationException($"Unknown flag '{text}'.");
            }

            case ColumnType.Color:
                return new CdbValue.Number(ParseColor(text), null);

            case ColumnType.Dynamic:
                return ParseJson(text);

            case ColumnType.Guid:
                return new CdbValue.Str(text);

            case ColumnType.List:
                if (text.Length == 0)
                    return new CdbValue.Arr();
                throw new CdbValidationException("Lists cannot be edited as text.");

            case ColumnType.Properties or ColumnType.Polymorph:
                if (text.Length == 0)
                    return new CdbValue.Obj(new CdbObject());
                return ParseJson(text) as CdbValue.Obj
                       ?? throw new CdbValidationException("An object is required.");

            case ColumnType.Gradient or ColumnType.Curve:
                return ParseJson(text);

            case ColumnType.Custom:
                return ParseCustom(text);

            default:
                return new CdbValue.Str(text);
        }
    }

    public static int ParseColor(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('#'))
            value = value[1..];
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        if (!ColorRegex().IsMatch(value))
            throw new CdbValidationException("A color must be written as #RRGGBB.");
        return (int)long.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses a list of flag names, or returns null when any token is not a flag of this
    /// column, so the caller can fall back to a numeric mask.
    /// <para>
    /// A flag may have no name at all (<c>"10:A,,C"</c>), and then the empty token is
    /// that flag rather than a stray separator. A type without an unnamed flag keeps
    /// ignoring empty tokens, as it always did.
    /// </para>
    /// </summary>
    public static int? TryParseFlagNames(ColumnType.Flags type, string text)
    {
        var mask = 0;
        foreach (var raw in text.Split(new[] { ',', '|' }, StringSplitOptions.None))
        {
            var name = raw.Trim();
            var index = type.Values.ToList().IndexOf(name);
            if (index < 0)
            {
                if (name.Length == 0)
                    continue;
                return null;
            }
            mask |= 1 << index;
        }
        return mask;
    }

    public static int ParseFlagNames(ColumnType.Flags type, string text)
    {
        var mask = 0;
        foreach (var raw in text.Split(new[] { ',', '|' }, StringSplitOptions.None))
        {
            var name = raw.Trim();
            var index = type.Values.ToList().IndexOf(name);
            if (index < 0)
            {
                if (name.Length == 0)
                    continue;
                throw new CdbValidationException($"Unknown flag '{name}'.");
            }
            mask |= 1 << index;
        }
        return mask;
    }

    /// <summary>Parses JSON, allowing unquoted object keys as the original editor does.</summary>
    public static CdbValue ParseJson(string text)
    {
        var normalised = Regex.Replace(text, @"([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)\s*:", "$1\"$2\":");
        try
        {
            using var doc = JsonDocument.Parse(normalised);
            return ReadElement(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new CdbValidationException("Invalid JSON: " + ex.Message);
        }
    }

    private static CdbValue ReadElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => CdbValue.NullValue,
        JsonValueKind.True => new CdbValue.Bool(true),
        JsonValueKind.False => new CdbValue.Bool(false),
        JsonValueKind.String => new CdbValue.Str(element.GetString() ?? string.Empty),
        JsonValueKind.Number => new CdbValue.Number(element.GetDouble(), null),
        JsonValueKind.Array => new CdbValue.Arr(element.EnumerateArray().Select(ReadElement).ToList()),
        JsonValueKind.Object => ReadObject(element),
        _ => CdbValue.NullValue,
    };

    private static CdbValue ReadObject(JsonElement element)
    {
        var obj = new CdbObject();
        foreach (var property in element.EnumerateObject())
            obj.Set(property.Name, ReadElement(property.Value));
        return new CdbValue.Obj(obj);
    }

    /// <summary>Parses <c>Case(arg, arg)</c> into the mixed array representation.</summary>
    private static CdbValue ParseCustom(string text)
    {
        throw new CdbValidationException(
            "Custom type values are edited through the structured editor.");
    }
}
