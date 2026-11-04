using System;
using System.Collections.Generic;
using System.IO;

namespace PalaceDB.Core.Document;

/// <summary>
/// Tracks which database files are currently open for writing by this process.
///
/// Several windows may show the same database, but only one of them is allowed to
/// write to it. The first window to open a file acquires the slot and stays
/// editable; every later window opens the same file read-only, so two editors can
/// never silently overwrite each other's changes.
/// </summary>
public static class OpenFileRegistry
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Paths released while a batch was open. They are announced when the batch closes,
    /// so a window that releases a file only to reopen it does not lose the slot to
    /// another window in between.
    /// </summary>
    private static readonly List<string> Pending = new();

    private static int _batches;

    /// <summary>
    /// Raised when the write slot of a file is released, i.e. when the window that held
    /// it closed or opened something else. A window showing that file read-only listens
    /// for this to take the slot over instead of staying read-only for the rest of the
    /// session.
    /// </summary>
    public static event Action<string>? Released;

    /// <summary>Absolute form of a path, used as the registry key.</summary>
    public static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>Takes the write slot for a file. Returns false when it is already taken.</summary>
    public static bool TryAcquire(string path)
    {
        var key = Normalize(path);
        lock (Gate)
        {
            if (!Held.Add(key))
                return false;

            // Re-acquiring a path whose release is still queued cancels the announcement:
            // nobody else may take a slot that is held again.
            Pending.RemoveAll(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
            return true;
        }
    }

    /// <summary>Releases the write slot, if this process holds it.</summary>
    public static void Release(string path)
    {
        var key = Normalize(path);
        lock (Gate)
        {
            if (!Held.Remove(key))
                return;

            if (_batches > 0)
            {
                Pending.Add(key);
                return;
            }
        }

        Announced(key);
    }

    /// <summary>
    /// Holds back release announcements until the returned scope is disposed. Used when a
    /// window swaps one document for another: the slot it gives up is the slot it is
    /// about to ask for again, and another window must not slip in between.
    /// </summary>
    public static IDisposable DeferNotifications()
    {
        lock (Gate)
        {
            _batches += 1;
        }

        return new Batch();
    }

    private sealed class Batch : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;

            List<string>? due = null;
            lock (Gate)
            {
                _batches -= 1;
                if (_batches == 0 && Pending.Count > 0)
                {
                    due = new List<string>(Pending);
                    Pending.Clear();
                }
            }

            if (due is null)
                return;

            foreach (var path in due)
                Announced(path);
        }
    }

    private static void Announced(string path)
    {
        var handler = Released;
        if (handler is null)
            return;

        try
        {
            handler(path);
        }
        catch (Exception)
        {
            // A window that has already closed must not take the registry down with it.
        }
    }

    /// <summary>True when any window of this process has the file open for writing.</summary>
    public static bool IsHeld(string path)
    {
        var key = Normalize(path);
        lock (Gate)
        {
            return Held.Contains(key);
        }
    }

    /// <summary>Number of files currently held. Used by tests.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Held.Count;
            }
        }
    }

    /// <summary>Forgets every slot. Only used by tests.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Held.Clear();
            Pending.Clear();
            _batches = 0;
        }
    }
}
