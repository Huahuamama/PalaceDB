using System.Text;
using PalaceDB.Core.Json;
using PalaceDB.Core.Model;

namespace PalaceDB.Cli;

/// <summary>
/// Headless tool used for verification and batch operations.
/// Commands: roundtrip, info.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("PalaceDB CLI");
            Console.WriteLine("  palacedb roundtrip <file.cdb> [--write]   verify byte-stable load/save");
            Console.WriteLine("  palacedb info <file.cdb>                  print document summary");
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "roundtrip" => RoundTrip(args),
                "info" => Info(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        return 1;
    }

    private static int RoundTrip(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: roundtrip <file.cdb> [--write]");
            return 1;
        }

        var path = Path.GetFullPath(args[1]);
        var originalBytes = File.ReadAllBytes(path);
        var originalText = new UTF8Encoding(false).GetString(originalBytes);

        var database = CdbReader.ParseDatabase(originalText);
        var output = CdbWriter.Write(database);
        var outputBytes = new UTF8Encoding(false).GetBytes(output);

        var identical = originalBytes.AsSpan().SequenceEqual(outputBytes);
        Console.WriteLine($"file      : {path}");
        Console.WriteLine($"sheets    : {database.Sheets.Count} ({database.VisibleSheets.Count()} visible)");
        Console.WriteLine($"rows      : {database.Sheets.Sum(s => s.Lines.Count)}");
        Console.WriteLine($"in bytes  : {originalBytes.Length}");
        Console.WriteLine($"out bytes : {outputBytes.Length}");
        Console.WriteLine(identical ? "result    : IDENTICAL" : "result    : DIFFERENT");

        if (!identical)
        {
            var (line, column) = FindFirstDifference(originalText, output);
            Console.WriteLine($"first diff: line {line}, column {column}");
            Console.WriteLine("  expected: " + Preview(originalText, line, column));
            Console.WriteLine("  actual  : " + Preview(output, line, column));
            return 3;
        }

        if (args.Contains("--write"))
        {
            File.WriteAllBytes(path, outputBytes);
            Console.WriteLine("written   : yes");
        }

        return 0;
    }

    private static int Info(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: info <file.cdb>");
            return 1;
        }

        var database = CdbReader.ParseDatabase(File.ReadAllText(args[1], Encoding.UTF8));
        Console.WriteLine($"compress    : {database.Compress}");
        Console.WriteLine($"customTypes : {database.CustomTypes.Count}");
        foreach (var sheet in database.Sheets)
        {
            var flags = new List<string>();
            if (sheet.Props.Hide) flags.Add("hidden");
            if (sheet.Props.HasIndex) flags.Add("index");
            if (sheet.Props.HasGroup) flags.Add("group");
            if (sheet.Props.DisplayColumn is { } dc) flags.Add("display=" + dc);
            Console.WriteLine($"  {sheet.Name,-24} cols={sheet.Columns.Count,-3} rows={sheet.Lines.Count,-5} " +
                              $"seps={sheet.Separators.Count,-3} {string.Join(" ", flags)}");
            foreach (var column in sheet.Columns)
                Console.WriteLine($"      {column.Name,-20} {column.Type.TypeName}" +
                                  (column.Optional ? " (optional)" : ""));
        }

        return 0;
    }

    private static (int Line, int Column) FindFirstDifference(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var index = 0;
        while (index < limit && a[index] == b[index])
            index++;

        var line = 1;
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (a[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return (line, column);
    }

    private static string Preview(string text, int line, int column)
    {
        var lines = text.Split('\n');
        if (line - 1 >= lines.Length)
            return "<eof>";
        var value = lines[line - 1];
        var start = Math.Max(0, column - 30);
        var length = Math.Min(value.Length - start, 80);
        return "«" + value.Substring(start, Math.Max(0, length)) + "»";
    }
}
