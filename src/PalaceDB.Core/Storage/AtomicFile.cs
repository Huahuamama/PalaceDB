using System.Text;

namespace PalaceDB.Core.Storage;

/// <summary>
/// Crash-safe file writes: content is written to a temporary file in the same
/// directory and then swapped in atomically, so a failure never truncates the
/// original document.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteText(string path, string content) => WriteBytes(path, Utf8NoBom.GetBytes(content));

    public static void WriteBytes(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"Cannot resolve the directory of '{path}'.");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // Replace keeps the original file intact if the swap fails.
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static string ReadText(string path) => File.ReadAllText(path, Utf8NoBom);

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: a leftover temp file is harmless.
        }
    }
}
