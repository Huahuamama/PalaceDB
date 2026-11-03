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

    /// <summary>Absolute form of a path, used as the registry key.</summary>
    public static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>Takes the write slot for a file. Returns false when it is already taken.</summary>
    public static bool TryAcquire(string path)
    {
        var key = Normalize(path);
        lock (Gate)
        {
            return Held.Add(key);
        }
    }

    /// <summary>Releases the write slot, if this process holds it.</summary>
    public static void Release(string path)
    {
        var key = Normalize(path);
        lock (Gate)
        {
            Held.Remove(key);
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
        }
    }
}
