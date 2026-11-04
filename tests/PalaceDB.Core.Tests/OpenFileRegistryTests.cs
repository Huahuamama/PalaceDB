using System.Collections.Generic;
using PalaceDB.Core.Document;
using Xunit;

namespace PalaceDB.Core.Tests;

/// <summary>
/// The registry is what makes a second window read-only. These tests cover the
/// announcement a closing window makes, which is how the read-only window learns that it
/// may start editing.
/// </summary>
public class OpenFileRegistryTests : IDisposable
{
    private readonly TempDirectory _temp = TestPaths.NewTempDirectory();
    private readonly List<string> _announced = new();

    public OpenFileRegistryTests()
    {
        OpenFileRegistry.Released += Record;
    }

    public void Dispose()
    {
        OpenFileRegistry.Released -= Record;
        _temp.Dispose();
    }

    private void Record(string path) => _announced.Add(OpenFileRegistry.Normalize(path));

    private string NewPath(string name) => _temp.At(name);

    [Fact]
    public void ReleasingAHeldFileAnnouncesIt()
    {
        var path = NewPath("Held.cdb");
        Assert.True(OpenFileRegistry.TryAcquire(path));

        OpenFileRegistry.Release(path);

        Assert.Equal(new[] { OpenFileRegistry.Normalize(path) }, _announced);
        Assert.False(OpenFileRegistry.IsHeld(path));
    }

    [Fact]
    public void ReleasingAFileNobodyHoldsAnnouncesNothing()
    {
        OpenFileRegistry.Release(NewPath("Never.cdb"));

        Assert.Empty(_announced);
    }

    [Fact]
    public void ABatchHoldsTheAnnouncementBackUntilItCloses()
    {
        var path = NewPath("Batched.cdb");
        Assert.True(OpenFileRegistry.TryAcquire(path));

        using (OpenFileRegistry.DeferNotifications())
        {
            OpenFileRegistry.Release(path);
            // The window that let go of the file is about to ask for it again; another
            // window must not hear about it in the meantime.
            Assert.Empty(_announced);
        }

        Assert.Single(_announced);
        Assert.Equal(OpenFileRegistry.Normalize(path), _announced[0]);
    }

    [Fact]
    public void TakingTheFileBackDuringABatchCancelsTheAnnouncement()
    {
        var path = NewPath("Reopened.cdb");
        Assert.True(OpenFileRegistry.TryAcquire(path));

        using (OpenFileRegistry.DeferNotifications())
        {
            OpenFileRegistry.Release(path);
            Assert.True(OpenFileRegistry.TryAcquire(path));
        }

        Assert.Empty(_announced);
        Assert.True(OpenFileRegistry.IsHeld(path));
    }
}
