namespace PalaceDB.Core.Model;

/// <summary>
/// Computes the default value of a column, matching the original
/// <c>Database.getDefault</c> behaviour.
/// </summary>
public static class TypeDefaults
{
    public static CdbValue? Create(Column column, Database database, SheetData? owner = null, bool ignoreOptional = false)
    {
        if (column.Optional && !ignoreOptional)
            return null;

        if (column.DefaultValue is { } explicitDefault)
            return CdbValueCloner.Clone(explicitDefault);

        return column.Type switch
        {
            ColumnType.Enum e when column.EnumAsString => new CdbValue.Str(e.Values.Count > 0 ? e.Values[0] : string.Empty),
            ColumnType.Int or ColumnType.Float or ColumnType.Enum or ColumnType.Flags or ColumnType.Color
                => new CdbValue.Number(0, null),
            ColumnType.Text or ColumnType.Id or ColumnType.Image or ColumnType.File
                => new CdbValue.Str(string.Empty),
            ColumnType.Guid => new CdbValue.Str(GuidGenerator.New()),
            ColumnType.Ref r => new CdbValue.Str(FirstIdOf(r.Sheet, database)),
            ColumnType.Bool => new CdbValue.Bool(false),
            ColumnType.List => new CdbValue.Arr(),
            ColumnType.Properties => BuildProperties(column, database, owner),
            ColumnType.Polymorph => new CdbValue.Obj(new CdbObject()),
            ColumnType.Formula => new CdbValue.Str(string.Empty),
            _ => null,
        };
    }

    private static string FirstIdOf(string sheetName, Database database)
    {
        var sheet = database.GetSheet(sheetName);
        var idColumn = sheet?.IdColumn;
        if (sheet is null || idColumn is null || sheet.Lines.Count == 0)
            return string.Empty;
        return sheet.Lines[0].GetString(idColumn.Name) ?? string.Empty;
    }

    private static CdbValue BuildProperties(Column column, Database database, SheetData? owner)
    {
        var obj = new CdbObject();
        var sub = owner?.GetSub(column);
        if (sub is null)
            return new CdbValue.Obj(obj);

        foreach (var subColumn in sub.Columns)
        {
            if (subColumn.Optional)
                continue;
            var value = Create(subColumn, database, sub);
            if (value is not null)
                obj.Set(subColumn.Name, value);
        }
        return new CdbValue.Obj(obj);
    }
}

/// <summary>
/// Numbers the rows of an auto-increment column. The column itself is a plain
/// <see cref="ColumnType.Int"/> in the .cdb — only the side-car knows it numbers itself —
/// so the value is chosen here and written like any other integer.
/// </summary>
public static class AutoNumber
{
    /// <summary>The value a new row gets: one more than the highest number already there.</summary>
    public static int Next(SheetData sheet, string columnName) => Highest(sheet, columnName) + 1;

    /// <summary>Highest number stored in the column, or 0 when it holds none yet.</summary>
    public static int Highest(SheetData sheet, string columnName)
    {
        var highest = 0;
        foreach (var row in sheet.Lines)
        {
            var number = row.Get(columnName) switch
            {
                CdbValue.Number n => (long)Math.Round(n.Value),
                CdbValue.Str s when long.TryParse(s.Value, out var parsed) => parsed,
                _ => 0L,
            };
            if (number > highest)
                highest = (int)Math.Min(number, int.MaxValue);
        }
        return highest;
    }
}

/// <summary>
/// Generates the 13 character identifiers used by <see cref="ColumnType.Guid"/> columns.
/// The encoding matches <c>cdb.Types.Guid.ofInt</c> so that generated values stay
/// compatible with the original runtime library.
/// </summary>
public static class GuidGenerator
{
    private const string Chars = "#&0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Epoch = 0x60000000;

    private static int _increment = Random.Shared.Next(1024 + 512);
    private static long _previousTime = -1;
    private static readonly object Gate = new();

    public static string New()
    {
        lock (Gate)
        {
            var time = (long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Epoch);
            _increment++;
            _increment %= 2048;
            if (_increment == 0)
            {
                while (time == _previousTime)
                    time = (long)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Epoch);
                _increment = Random.Shared.Next(1024);
            }
            _previousTime = time;

            var random = Random.Shared.Next(1 << 20);
            var value = (time << 32) | (long)((_increment << 20) | random);
            return Encode(value);
        }
    }

    public static string Encode(long value)
    {
        var buffer = new char[13];
        var position = 0;
        for (var i = 0; i < 11; i++)
        {
            if (i == 4 || i == 8)
                buffer[position++] = '-';
            buffer[position++] = Chars[(int)((value >> (60 - i * 6)) & 63)];
        }
        return new string(buffer, 0, position);
    }
}
