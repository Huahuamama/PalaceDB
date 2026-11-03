namespace PalaceDB.Core.Model;

/// <summary>
/// Immutable JSON-like value used for every piece of CDB data.
/// A dedicated union (instead of <see cref="object"/>) keeps number formatting and
/// key ordering under our control, which is required for byte-stable round-trips.
/// </summary>
public abstract record CdbValue
{
    public static readonly Null NullValue = new();

    public sealed record Null : CdbValue
    {
        public override string ToString() => "null";
    }

    public sealed record Bool(bool Value) : CdbValue
    {
        public override string ToString() => Value ? "true" : "false";
    }

    /// <summary>
    /// A JSON number. <paramref name="Raw"/> keeps the exact source text so that
    /// reading then writing a file does not alter values such as <c>1.0</c> or <c>1e21</c>.
    /// Values produced by the editor have <paramref name="Raw"/> set to <c>null</c>
    /// and are formatted canonically.
    /// </summary>
    public sealed record Number(double Value, string? Raw = null) : CdbValue
    {
        public bool IsInteger => Value == Math.Floor(Value) && !double.IsInfinity(Value);

        public override string ToString() => Raw ?? CdbNumber.Format(Value);
    }

    public sealed record Str(string Value) : CdbValue
    {
        public override string ToString() => Value;
    }

    public sealed record Arr(List<CdbValue> Items) : CdbValue
    {
        public Arr() : this(new List<CdbValue>()) { }
        public override string ToString() => "[" + string.Join(", ", Items) + "]";
    }

    public sealed record Obj(CdbObject Value) : CdbValue
    {
        public override string ToString() => "{" + Value + "}";
    }

    public static CdbValue Of(string? s) => s is null ? NullValue : new Str(s);
    public static CdbValue Of(bool b) => new Bool(b);
    public static CdbValue Of(int i) => new Number(i, null);
    public static CdbValue Of(double d) => new Number(d, null);
}

/// <summary>Canonical number formatting shared by the writer.</summary>
public static class CdbNumber
{
    public static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException("CDB does not support NaN or Infinity.");

        // Integral values are written without a decimal part, matching the original
        // Haxe/JavaScript serializer (JSON.stringify(3.0) === "3").
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Shortest round-trippable representation (same as .NET Core 3.0+ default).
        var s = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        // Normalise exponent casing: 1E+21 -> 1e+21 (JavaScript style).
        if (s.Contains('E'))
            s = s.Replace("E", "e");
        return s;
    }
}
