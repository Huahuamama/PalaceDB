using System.Text;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Json;

/// <summary>
/// Serialises a <see cref="CdbObject"/> tree back to CDB text.
/// The output is deliberately byte-compatible with the original Haxe serializer:
/// tab indentation, LF line endings, UTF-8 without BOM and no trailing newline.
/// </summary>
public static class CdbWriter
{
    public static string Write(CdbObject root)
    {
        var sb = new StringBuilder(1 << 16);
        WriteObject(sb, root, 0);
        return sb.ToString();
    }

    public static string Write(Database database)
    {
        database.PrepareForSave();
        return Write(database.Root);
    }

    /// <summary>Serialises with a trailing newline (used by tools that prefer it).</summary>
    public static string WriteWithTrailingNewline(CdbObject root) => Write(root) + "\n";

    /// <summary>
    /// Serialises one value on a single line. Used where a value has to fit in a text
    /// field (the default value of a column), so containers are written compactly.
    /// </summary>
    public static string WriteValue(CdbValue value)
    {
        var sb = new StringBuilder(64);
        WriteCompact(sb, value);
        return sb.ToString();
    }

    private static void WriteCompact(StringBuilder sb, CdbValue value)
    {
        switch (value)
        {
            case CdbValue.Null:
                sb.Append("null");
                break;
            case CdbValue.Bool b:
                sb.Append(b.Value ? "true" : "false");
                break;
            case CdbValue.Number n:
                sb.Append(n.ToString());
                break;
            case CdbValue.Str s:
                WriteString(sb, s.Value);
                break;
            case CdbValue.Arr a:
                sb.Append('[');
                for (var i = 0; i < a.Items.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    WriteCompact(sb, a.Items[i]);
                }
                sb.Append(']');
                break;
            case CdbValue.Obj o:
                sb.Append('{');
                var first = true;
                foreach (var key in o.Value.Keys)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    WriteCompact(sb, o.Value.Get(key)!);
                }
                sb.Append('}');
                break;
            default:
                throw new InvalidOperationException($"Unknown value type {value.GetType().Name}.");
        }
    }

    private static void WriteValue(StringBuilder sb, CdbValue value, int depth)
    {
        switch (value)
        {
            case CdbValue.Null:
                sb.Append("null");
                break;
            case CdbValue.Bool b:
                sb.Append(b.Value ? "true" : "false");
                break;
            case CdbValue.Number n:
                sb.Append(n.ToString());
                break;
            case CdbValue.Str s:
                WriteString(sb, s.Value);
                break;
            case CdbValue.Arr a:
                WriteArray(sb, a, depth);
                break;
            case CdbValue.Obj o:
                WriteObject(sb, o.Value, depth);
                break;
            default:
                throw new InvalidOperationException($"Unknown value type {value.GetType().Name}.");
        }
    }

    private static void WriteObject(StringBuilder sb, CdbObject obj, int depth)
    {
        if (obj.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        sb.Append('{').Append('\n');
        var first = true;
        foreach (var key in obj.Keys)
        {
            if (!first)
                sb.Append(',').Append('\n');
            first = false;

            Indent(sb, depth + 1);
            WriteString(sb, key);
            sb.Append(": ");
            WriteValue(sb, obj.Get(key)!, depth + 1);
        }
        sb.Append('\n');
        Indent(sb, depth);
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, CdbValue.Arr arr, int depth)
    {
        if (arr.Items.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append('[').Append('\n');
        for (var i = 0; i < arr.Items.Count; i++)
        {
            if (i > 0)
                sb.Append(',').Append('\n');
            Indent(sb, depth + 1);
            WriteValue(sb, arr.Items[i], depth + 1);
        }
        sb.Append('\n');
        Indent(sb, depth);
        sb.Append(']');
    }

    private static void Indent(StringBuilder sb, int depth)
    {
        for (var i = 0; i < depth; i++)
            sb.Append('\t');
    }

    private static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u00").Append(((int)c).ToString("x2"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
