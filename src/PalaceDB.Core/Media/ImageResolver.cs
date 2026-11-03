namespace PalaceDB.Core.Media;

/// <summary>Why an image path could not be turned into a thumbnail.</summary>
public enum ImageFailure
{
    None,

    /// <summary>The path is empty.</summary>
    Empty,

    /// <summary>No file exists at the resolved location.</summary>
    Missing,

    /// <summary>The extension is not a web-renderable image format.</summary>
    Unsupported,

    /// <summary>The file exceeds <see cref="ImageResolver.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>The file could not be read.</summary>
    Unreadable,
}

/// <summary>Outcome for one stored image path.</summary>
public sealed record ImageResult(string Path, string? DataUrl, ImageFailure Failure)
{
    public bool IsImage => DataUrl is not null;
}

/// <summary>
/// Turns the image paths stored in cells into data URLs the page can display.
/// <para>
/// PalaceDB stores only a path string (decision Q5), so the editor has to read the file
/// itself. A data URL keeps the page sandboxed - no extra virtual host, no CORS rules, and
/// absolute paths outside the database folder work exactly like relative ones. Results are
/// cached by path and invalidated when the file's size or timestamp changes.
/// </para>
/// </summary>
public sealed class ImageResolver
{
    /// <summary>Files above this size are not inlined; they would stall the message channel.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>Upper bound per request, so a wide grid cannot ask for thousands at once.</summary>
    public const int MaxPerRequest = 64;

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
    };

    private readonly Dictionary<string, (DateTime Stamp, long Length, ImageResult Result)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder used to resolve relative paths; normally the database's own folder.</summary>
    public string BaseDirectory { get; }

    public ImageResolver(string baseDirectory) => BaseDirectory = baseDirectory ?? string.Empty;

    /// <summary>Forgets every cached thumbnail; used when another document is opened.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>Resolves a stored path, reading and encoding the file on first use.</summary>
    public ImageResult Resolve(string? path)
    {
        var stored = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stored))
            return new ImageResult(stored, null, ImageFailure.Empty);

        var full = ResolveFullPath(stored);
        var extension = Path.GetExtension(full);
        if (!MimeTypes.TryGetValue(extension, out var mime))
            return new ImageResult(stored, null, ImageFailure.Unsupported);

        FileInfo info;
        try
        {
            info = new FileInfo(full);
            if (!info.Exists)
                return new ImageResult(stored, null, ImageFailure.Missing);
        }
        catch
        {
            return new ImageResult(stored, null, ImageFailure.Unreadable);
        }

        if (info.Length > MaxBytes)
            return new ImageResult(stored, null, ImageFailure.TooLarge);

        if (_cache.TryGetValue(full, out var cached) &&
            cached.Stamp == info.LastWriteTimeUtc &&
            cached.Length == info.Length)
        {
            return cached.Result with { Path = stored };
        }

        try
        {
            var bytes = File.ReadAllBytes(full);
            var url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            var result = new ImageResult(stored, url, ImageFailure.None);
            _cache[full] = (info.LastWriteTimeUtc, info.Length, result);
            return result;
        }
        catch
        {
            return new ImageResult(stored, null, ImageFailure.Unreadable);
        }
    }

    /// <summary>Resolves a batch, de-duplicating paths and keeping the request bounded.</summary>
    public List<ImageResult> ResolveMany(IEnumerable<string> paths)
    {
        var results = new List<ImageResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (results.Count >= MaxPerRequest)
                break;
            if (path is null || !seen.Add(path))
                continue;
            results.Add(Resolve(path));
        }

        return results;
    }

    /// <summary>Absolute location of a stored path, or the raw string when it is unusable.</summary>
    public string ResolveFullPath(string stored)
    {
        // PalaceDB writes forward slashes regardless of the platform.
        var normalized = stored.Replace('\\', '/');

        if (Path.IsPathRooted(normalized))
        {
            try
            {
                return Path.GetFullPath(normalized);
            }
            catch
            {
                return normalized;
            }
        }

        if (string.IsNullOrEmpty(BaseDirectory))
            return normalized;

        try
        {
            return Path.GetFullPath(Path.Combine(BaseDirectory, normalized));
        }
        catch
        {
            return Path.Combine(BaseDirectory, normalized);
        }
    }

    /// <summary>Human readable reason, used in cell tooltips.</summary>
    public static string Describe(ImageFailure failure) => failure switch
    {
        ImageFailure.Empty => "no image",
        ImageFailure.Missing => "file not found",
        ImageFailure.Unsupported => "unsupported image format",
        ImageFailure.TooLarge => "file too large to preview",
        ImageFailure.Unreadable => "file cannot be read",
        _ => "image",
    };
}
