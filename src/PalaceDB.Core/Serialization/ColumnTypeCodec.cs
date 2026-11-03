using PalaceDB.Core.Model;

namespace PalaceDB.Core.Serialization;

/// <summary>
/// Encodes and decodes the <c>typeStr</c> field used on disk
/// (<c>"3"</c>, <c>"5:A,B,C"</c>, <c>"6:SheetName"</c>, ...).
/// </summary>
public static class ColumnTypeCodec
{
    /// <summary>Types removed together with the map feature (requirement R1).</summary>
    public static readonly IReadOnlyDictionary<int, string> RemovedMapTypes = new Dictionary<int, string>
    {
        [12] = "Layer",
        [14] = "TilePos",
        [15] = "TileLayer",
    };

    public static ColumnType Decode(string typeStr)
    {
        if (string.IsNullOrWhiteSpace(typeStr))
            throw new CdbFormatException("Empty column type string.");

        var colon = typeStr.IndexOf(':');
        var idText = colon < 0 ? typeStr : typeStr[..colon];
        var payload = colon < 0 ? string.Empty : typeStr[(colon + 1)..];

        if (!int.TryParse(idText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var id))
            throw new CdbFormatException($"Invalid column type '{typeStr}'.");

        if (RemovedMapTypes.TryGetValue(id, out var removedName))
            throw new CdbFormatException(
                $"Map-related column type \"{removedName}\" (type {id}) is not supported in this version. " +
                "This file was created with a version of PalaceDB that still had the map editor.");

        return id switch
        {
            0 => new ColumnType.Id(),
            1 => new ColumnType.Text(),
            2 => new ColumnType.Bool(),
            3 => new ColumnType.Int(),
            4 => new ColumnType.Float(),
            5 => new ColumnType.Enum(SplitValues(payload)),
            6 => new ColumnType.Ref(payload),
            7 => new ColumnType.Image(),
            8 => new ColumnType.List(),
            9 => new ColumnType.Custom(payload),
            10 => new ColumnType.Flags(SplitValues(payload)),
            11 => new ColumnType.Color(),
            13 => new ColumnType.File(),
            16 => new ColumnType.Dynamic(),
            17 => new ColumnType.Properties(),
            18 => new ColumnType.Gradient(),
            19 => new ColumnType.Curve(),
            20 => new ColumnType.Guid(),
            21 => new ColumnType.Polymorph(),
            22 => new ColumnType.Formula(),
            _ => throw new CdbFormatException($"Unknown column type id {id}."),
        };
    }

    public static string Encode(ColumnType type) => type switch
    {
        ColumnType.Enum e => "5:" + string.Join(",", e.Values),
        ColumnType.Flags f => "10:" + string.Join(",", f.Values),
        ColumnType.Ref r => "6:" + r.Sheet,
        ColumnType.Custom c => "9:" + c.TypeName2,
        _ => type.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static IReadOnlyList<string> SplitValues(string payload) =>
        payload.Length == 0 ? new[] { string.Empty } : payload.Split(',');
}

/// <summary>Raised when a file cannot be parsed as a valid CDB document.</summary>
public sealed class CdbFormatException : Exception
{
    public CdbFormatException(string message) : base(message) { }
    public CdbFormatException(string message, Exception inner) : base(message, inner) { }
}
