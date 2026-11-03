using PalaceDB.Core.Metadata;
using PalaceDB.Core.Storage;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Window geometry remembered per file (F17). The rules are pure arithmetic, so the
/// interesting cases - a monitor that went away, a window larger than the screen, a
/// side-car that never stored a position - can be pinned without opening a window.
/// </summary>
public class WindowPlacementTests
{
    /// <summary>A 1920x1080 primary screen starting at the origin.</summary>
    private static readonly PlacementRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void AStoredPositionIsKeptWhenItIsVisible()
    {
        var state = new WindowState { X = 300, Y = 200, Width = 1000, Height = 700 };

        var rect = WindowPlacement.Fit(state, Screen);

        Assert.Equal(300, rect.Left);
        Assert.Equal(200, rect.Top);
        Assert.Equal(1000, rect.Width);
        Assert.Equal(700, rect.Height);
    }

    [Fact]
    public void AStateWithoutAPositionIsCentred()
    {
        var state = new WindowState { Width = 1000, Height = 600 };

        Assert.False(state.HasPosition);

        var rect = WindowPlacement.Fit(state, Screen);

        Assert.Equal(460, rect.Left); // (1920 - 1000) / 2
        Assert.Equal(240, rect.Top);  // (1080 - 600) / 2
    }

    [Fact]
    public void AWindowFromARemovedMonitorIsBroughtBack()
    {
        // A second screen to the right is gone, so x = 3000 is off the desktop.
        var state = new WindowState { X = 3000, Y = 200, Width = 1000, Height = 700 };

        var rect = WindowPlacement.Fit(state, Screen);

        // Recentred rather than pushed against the right edge.
        Assert.Equal(460, rect.Left);
        Assert.Equal(190, rect.Top);
    }

    [Fact]
    public void AWindowAboveTheDesktopIsBroughtBack()
    {
        var state = new WindowState { X = 200, Y = -900, Width = 900, Height = 600 };

        var rect = WindowPlacement.Fit(state, Screen);

        Assert.Equal(510, rect.Left);
        Assert.Equal(240, rect.Top);
    }

    [Fact]
    public void AWindowHangingOverAnEdgeIsKeptWhenEnoughStaysVisible()
    {
        var state = new WindowState { X = 1800, Y = 100, Width = 1000, Height = 700 };

        var rect = WindowPlacement.Fit(state, Screen);

        // 120 px of the window remain on screen, which is the threshold.
        Assert.Equal(1800, rect.Left);
        Assert.Equal(100, rect.Top);
    }

    [Fact]
    public void AWindowBarelyVisibleIsRecentred()
    {
        var state = new WindowState { X = 1900, Y = 100, Width = 1000, Height = 700 };

        var rect = WindowPlacement.Fit(state, Screen);

        Assert.Equal(460, rect.Left);
    }

    [Fact]
    public void AWindowLargerThanTheScreenIsShrunk()
    {
        var state = new WindowState { X = 0, Y = 0, Width = 3000, Height = 2000 };

        var rect = WindowPlacement.Fit(state, Screen);

        Assert.Equal(1920, rect.Width);
        Assert.Equal(1080, rect.Height);
        Assert.Equal(0, rect.Left);
        Assert.Equal(0, rect.Top);
    }

    [Fact]
    public void AWindowSmallerThanTheMinimumGrows()
    {
        var state = new WindowState { X = 10, Y = 10, Width = 100, Height = 100 };

        var rect = WindowPlacement.Fit(state, Screen);

        Assert.Equal(WindowPlacement.MinimumWidth, rect.Width);
        Assert.Equal(WindowPlacement.MinimumHeight, rect.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void AnUnusableSizeFallsBackToTheDefault(double size)
    {
        var state = new WindowState { Width = size, Height = size };

        var rect = WindowPlacement.Fit(state, Screen);

        Assert.Equal(WindowPlacement.DefaultWidth, rect.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, rect.Height);
    }

    [Fact]
    public void ASmallScreenNeverForcesAWindowBiggerThanIt()
    {
        var laptop = new PlacementRect(0, 0, 800, 600);
        var state = new WindowState { Width = 1280, Height = 800 };

        var rect = WindowPlacement.Fit(state, laptop);

        Assert.Equal(800, rect.Width);
        Assert.Equal(600, rect.Height);
    }

    [Fact]
    public void ANegativeOriginIsRespected()
    {
        // A second monitor placed left of the primary one.
        var desktop = new PlacementRect(-1920, 0, 3840, 1080);
        var state = new WindowState { X = -1500, Y = 100, Width = 900, Height = 700 };

        var rect = WindowPlacement.Fit(state, desktop);

        Assert.Equal(-1500, rect.Left);
        Assert.Equal(100, rect.Top);
    }

    [Fact]
    public void CaptureRoundsAndKeepsTheRestoreSize()
    {
        var state = WindowPlacement.Capture(new PlacementRect(10.4, 20.6, 1000.5, 700.4), maximized: true);

        Assert.Equal(10, state.X);
        Assert.Equal(21, state.Y);
        Assert.Equal(1001, state.Width);
        Assert.Equal(700, state.Height);
        Assert.True(state.Maximized);
        Assert.True(state.HasPosition);
    }

    [Fact]
    public void CaptureNeverStoresAWindowBelowTheMinimum()
    {
        var state = WindowPlacement.Capture(new PlacementRect(0, 0, 10, 10), maximized: false);

        Assert.Equal(WindowPlacement.MinimumWidth, state.Width);
        Assert.Equal(WindowPlacement.MinimumHeight, state.Height);
        Assert.False(state.Maximized);
    }

    [Fact]
    public void CaptureThenFitReturnsTheSameRectangle()
    {
        var original = new PlacementRect(250, 150, 1100, 720);

        var rect = WindowPlacement.Fit(WindowPlacement.Capture(original, false), Screen);

        Assert.Equal(original, rect);
    }
}

/// <summary>Persistence of the remembered geometry in the metadata side-car.</summary>
public class WindowStateStoreTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();

    public void Dispose() => _temp.Dispose();

    private MetaStore LoadStore()
    {
        var path = _temp.WriteFile("Sample.cdb", Fixtures.SampleJson);
        return MetaStore.Load(path, PalaceDB.Core.Json.CdbReader.ParseDatabase(AtomicFile.ReadText(path)));
    }

    [Fact]
    public void AFreshSideCarHasNoPosition()
    {
        var store = LoadStore();

        Assert.False(store.WindowState.HasPosition);
        Assert.Equal(WindowPlacement.DefaultWidth, store.WindowState.Width);
    }

    [Fact]
    public void StoringTheGeometryDirtiestheSideCar()
    {
        var store = LoadStore();

        var changed = store.SetWindowState(new WindowState { X = 100, Y = 50, Width = 900, Height = 600 });

        Assert.True(changed);
        Assert.True(store.IsDirty);
        Assert.Equal(100, store.WindowState.X);
    }

    [Fact]
    public void StoringTheSameGeometryAgainChangesNothing()
    {
        var store = LoadStore();
        store.SetWindowState(new WindowState { X = 100, Y = 50, Width = 900, Height = 600 });
        store.Save();

        var changed = store.SetWindowState(new WindowState { X = 100, Y = 50, Width = 900, Height = 600 });

        Assert.False(changed);
        Assert.False(store.IsDirty);
    }

    [Fact]
    public void TheGeometrySurvivesAReload()
    {
        var store = LoadStore();
        store.SetWindowState(new WindowState { X = 120, Y = 60, Width = 980, Height = 640, Maximized = true });
        store.Save();

        var reloaded = LoadStore();

        Assert.Equal(120, reloaded.WindowState.X);
        Assert.Equal(60, reloaded.WindowState.Y);
        Assert.Equal(980, reloaded.WindowState.Width);
        Assert.Equal(640, reloaded.WindowState.Height);
        Assert.True(reloaded.WindowState.Maximized);
    }

    [Fact]
    public void TheStoredGeometryNeverReachesTheDatabaseFile()
    {
        var path = _temp.WriteFile("Sample.cdb", Fixtures.SampleJson);
        var before = AtomicFile.ReadText(path);
        var store = MetaStore.Load(path, PalaceDB.Core.Json.CdbReader.ParseDatabase(before));

        store.SetWindowState(new WindowState { X = 42, Y = 24, Width = 800, Height = 500 });
        store.Save();

        Assert.Equal(before, AtomicFile.ReadText(path));
        Assert.Contains("\"windowState\"", AtomicFile.ReadText(MetaStore.PathFor(path)));
    }

    [Fact]
    public void TheStoredGeometryIsReadableJson()
    {
        var store = LoadStore();
        store.SetWindowState(new WindowState { X = 10, Y = 20, Width = 800, Height = 500 });
        store.Save();

        var text = AtomicFile.ReadText(MetaStore.PathFor(_temp.At("Sample.cdb")));

        Assert.Contains("\"x\": 10", text);
        Assert.Contains("\"y\": 20", text);
        Assert.Contains("\"width\": 800", text);
        Assert.Contains("\"maximized\": false", text);
    }
}
