using PalaceDB.Core.Media;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Image cells store a path, so the editor reads the file itself and hands the page a data
/// URL. These tests cover path resolution, the guards that keep a broken path from
/// breaking the grid, and the cache.
/// </summary>
public class ImageResolverTests : IDisposable
{
    /// <summary>A 1x1 transparent PNG.</summary>
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private string WritePng(string name = "icon.png", string base64 = OnePixelPng)
    {
        var path = _temp.At(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return path;
    }

    private ImageResolver Resolver() => new(_temp.Path);

    [Fact]
    public void ARelativePathResolvesAgainstTheDatabaseFolder()
    {
        WritePng();
        var resolver = Resolver();

        var result = resolver.Resolve("icon.png");

        Assert.True(result.IsImage);
        Assert.Equal(ImageFailure.None, result.Failure);
        Assert.StartsWith("data:image/png;base64,", result.DataUrl);
        Assert.Equal(OnePixelPng, result.DataUrl!.Substring("data:image/png;base64,".Length));
    }

    [Fact]
    public void ANestedRelativePathUsesForwardSlashes()
    {
        WritePng("sprites/hero.png");

        var result = Resolver().Resolve("sprites/hero.png");

        Assert.True(result.IsImage);
    }

    [Fact]
    public void ABackslashPathIsAcceptedToo()
    {
        WritePng("sprites/hero.png");

        var result = Resolver().Resolve("sprites\\hero.png");

        Assert.True(result.IsImage);
    }

    [Fact]
    public void AnAbsolutePathIsUsedAsIs()
    {
        var path = WritePng();

        var result = Resolver().Resolve(path);

        Assert.True(result.IsImage);
    }

    [Fact]
    public void AMissingFileIsReportedWithoutThrowing()
    {
        var result = Resolver().Resolve("nope.png");

        Assert.False(result.IsImage);
        Assert.Null(result.DataUrl);
        Assert.Equal(ImageFailure.Missing, result.Failure);
    }

    [Fact]
    public void AnEmptyPathIsReported()
    {
        var result = Resolver().Resolve("   ");

        Assert.Equal(ImageFailure.Empty, result.Failure);
    }

    [Fact]
    public void ANullPathIsReported()
    {
        var result = Resolver().Resolve(null);

        Assert.Equal(ImageFailure.Empty, result.Failure);
        Assert.Equal(string.Empty, result.Path);
    }

    [Fact]
    public void ANonImageExtensionIsRefused()
    {
        _temp.WriteFile("readme.txt", "hello");

        var result = Resolver().Resolve("readme.txt");

        Assert.Equal(ImageFailure.Unsupported, result.Failure);
    }

    [Fact]
    public void AnOversizedFileIsRefused()
    {
        var path = _temp.At("huge.png");
        using (var stream = File.Create(path))
            stream.SetLength(ImageResolver.MaxBytes + 1);

        var result = Resolver().Resolve("huge.png");

        Assert.Equal(ImageFailure.TooLarge, result.Failure);
    }

    [Fact]
    public void EverySupportedExtensionGetsTheRightMimeType()
    {
        var resolver = Resolver();
        var expectations = new Dictionary<string, string>
        {
            ["a.png"] = "image/png",
            ["a.jpg"] = "image/jpeg",
            ["a.jpeg"] = "image/jpeg",
            ["a.gif"] = "image/gif",
            ["a.webp"] = "image/webp",
            ["a.bmp"] = "image/bmp",
            ["a.svg"] = "image/svg+xml",
        };

        foreach (var name in expectations.Keys)
            File.WriteAllBytes(_temp.At(name), Convert.FromBase64String(OnePixelPng));

        foreach (var (name, mime) in expectations)
            Assert.StartsWith($"data:{mime};base64,", resolver.Resolve(name).DataUrl);
    }

    [Fact]
    public void TheSameFileIsOnlyEncodedOnce()
    {
        WritePng();
        var resolver = Resolver();

        var first = resolver.Resolve("icon.png");
        var second = resolver.Resolve("icon.png");

        Assert.Same(first.DataUrl, second.DataUrl);
    }

    [Fact]
    public void RewritingTheFileInvalidatesTheCache()
    {
        var path = WritePng();
        var resolver = Resolver();
        var first = resolver.Resolve("icon.png");

        // A different payload of the same length, written with a newer timestamp.
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPng.Replace('A', 'B')));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var second = resolver.Resolve("icon.png");

        Assert.NotEqual(first.DataUrl, second.DataUrl);
    }

    [Fact]
    public void ClearingForgetsEverything()
    {
        WritePng();
        var resolver = Resolver();
        resolver.Resolve("icon.png");

        resolver.Clear();

        // Still resolvable, but the work is done again.
        Assert.True(resolver.Resolve("icon.png").IsImage);
    }

    [Fact]
    public void ABatchDeduplicatesPaths()
    {
        WritePng();

        var results = Resolver().ResolveMany(new[] { "icon.png", "icon.png", "icon.png" });

        Assert.Single(results);
    }

    [Fact]
    public void ABatchIsCappedSoTheMessageStaysSmall()
    {
        var names = Enumerable.Range(0, ImageResolver.MaxPerRequest + 20)
            .Select(i => $"missing{i}.png")
            .ToList();

        var results = Resolver().ResolveMany(names);

        Assert.Equal(ImageResolver.MaxPerRequest, results.Count);
    }

    [Fact]
    public void ABatchKeepsTheFailuresAlongsideTheImages()
    {
        WritePng();

        var results = Resolver().ResolveMany(new[] { "icon.png", "gone.png", "readme.txt" });

        Assert.Equal(3, results.Count);
        Assert.True(results[0].IsImage);
        Assert.Equal(ImageFailure.Missing, results[1].Failure);
        Assert.Equal(ImageFailure.Unsupported, results[2].Failure);
    }

    [Fact]
    public void TheStoredPathIsEchoedBackUnchanged()
    {
        WritePng("sprites/hero.png");

        var result = Resolver().Resolve("sprites/hero.png");

        // The page keys its cache by the exact string stored in the file.
        Assert.Equal("sprites/hero.png", result.Path);
    }

    [Fact]
    public void FailuresAreDescribedForTooltips()
    {
        Assert.Equal("file not found", ImageResolver.Describe(ImageFailure.Missing));
        Assert.Equal("unsupported image format", ImageResolver.Describe(ImageFailure.Unsupported));
        Assert.Equal("file too large to preview", ImageResolver.Describe(ImageFailure.TooLarge));
        Assert.Equal("image", ImageResolver.Describe(ImageFailure.None));
    }
}
