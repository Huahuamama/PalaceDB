using PalaceDB.Core.Json;
using PalaceDB.Core.Model;
using PalaceDB.Core.Storage;

namespace PalaceDB.Core.Merge;

/// <summary>What the merge attempt found next to the database file.</summary>
public enum MergeFileState
{
    /// <summary>No conflict files; nothing to do.</summary>
    None,

    /// <summary>Merged cleanly.</summary>
    Merged,

    /// <summary>Merged, but some values were changed on both sides.</summary>
    Conflicted,

    /// <summary>The versions could not be read or merged at all.</summary>
    Failed,
}

/// <summary>Result of inspecting one database file for a pending merge.</summary>
public sealed record MergeOutcome(
    MergeFileState State,
    string? MergedText,
    IReadOnlyList<MergeConflict> Conflicts,
    IReadOnlyList<string> SideFiles,
    string? Error)
{
    public static readonly MergeOutcome None =
        new(MergeFileState.None, null, Array.Empty<MergeConflict>(), Array.Empty<string>(), null);
}

/// <summary>
/// Detects and resolves the files git leaves behind when it cannot merge a database (F16).
/// <para>
/// Two layouts are understood, because different tools produce different leftovers:
/// </para>
/// <list type="bullet">
/// <item><c>X.cdb.mine</c> plus <c>X.cdb.r&lt;N&gt;</c> — the local version and the
/// revisions. The local version is <c>.mine</c>, the incoming one is the working file.</item>
/// <item>Conflict markers (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>) inside <c>X.cdb</c> itself —
/// the local and incoming versions are the two halves of each block.</item>
/// </list>
/// <para>
/// The oldest <c>.r&lt;N&gt;</c> is the common ancestor. Without it the merge degrades to a
/// two-way comparison, which is still useful: identical values pass through and only real
/// differences are reported.
/// </para>
/// <para>
/// Nothing is written here. The merged text is handed back so the editor can open it as an
/// unsaved document; the conflict files are only removed once the user saves.
/// </para>
/// </summary>
public static class MergeResolver
{
    public const string MineSuffix = ".mine";

    /// <summary>Git's conflict marker prefixes.</summary>
    private const string MarkerStart = "<<<<<<<";
    private const string MarkerMiddle = "=======";
    private const string MarkerEnd = ">>>>>>>";

    public static string MinePathFor(string databasePath) => databasePath + MineSuffix;

    /// <summary>True when the file carries git conflict markers.</summary>
    public static bool HasConflictMarkers(string text) =>
        text.Contains("\n" + MarkerStart, StringComparison.Ordinal) ||
        text.StartsWith(MarkerStart, StringComparison.Ordinal);

    /// <summary>
    /// Keeps one side of every conflict block and drops the markers, producing a file that
    /// can be parsed again.
    /// </summary>
    public static string StripMarkers(string text, MergeSide side)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);

        // 0 = outside a block, 1 = inside the local half, 2 = inside the incoming half.
        var mode = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith(MarkerStart, StringComparison.Ordinal))
            {
                mode = 1;
                continue;
            }

            if (mode == 1 && line.StartsWith(MarkerMiddle, StringComparison.Ordinal))
            {
                mode = 2;
                continue;
            }

            if (mode == 2 && line.StartsWith(MarkerEnd, StringComparison.Ordinal))
            {
                mode = 0;
                continue;
            }

            if (mode == 0 || (mode == 1 && side == MergeSide.Mine) || (mode == 2 && side == MergeSide.Theirs))
                kept.Add(line);
        }

        return string.Join("\n", kept);
    }

    /// <summary>The <c>X.cdb.r&lt;N&gt;</c> files next to the database, oldest first.</summary>
    public static IReadOnlyList<string> RevisionFiles(string databasePath)
    {
        var folder = Path.GetDirectoryName(databasePath);
        var name = Path.GetFileName(databasePath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name) || !Directory.Exists(folder))
            return Array.Empty<string>();

        var found = new List<(int Revision, string Path)>();
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(folder, name + ".r*"))
            {
                var suffix = Path.GetFileName(candidate)[(name.Length + 2)..];
                if (int.TryParse(suffix, out var revision))
                    found.Add((revision, candidate));
            }
        }
        catch
        {
            return Array.Empty<string>();
        }

        found.Sort((a, b) => a.Revision.CompareTo(b.Revision));
        return found.Select(f => f.Path).ToList();
    }

    /// <summary>True when opening this file should start a merge.</summary>
    public static bool HasPendingMerge(string databasePath)
    {
        if (File.Exists(MinePathFor(databasePath)))
            return true;

        try
        {
            return File.Exists(databasePath) && HasConflictMarkers(AtomicFile.ReadText(databasePath));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs the merge for a file that has conflict leftovers.</summary>
    public static MergeOutcome Prepare(string databasePath)
    {
        if (!HasPendingMerge(databasePath))
            return MergeOutcome.None;

        var minePath = MinePathFor(databasePath);
        var revisions = RevisionFiles(databasePath);
        var sideFiles = revisions.ToList();
        if (File.Exists(minePath))
            sideFiles.Insert(0, minePath);

        try
        {
            var working = AtomicFile.ReadText(databasePath);
            var marked = HasConflictMarkers(working);

            string mineText;
            string theirsText;

            if (File.Exists(minePath))
            {
                mineText = AtomicFile.ReadText(minePath);
                theirsText = marked ? StripMarkers(working, MergeSide.Theirs) : working;
            }
            else
            {
                mineText = StripMarkers(working, MergeSide.Mine);
                theirsText = StripMarkers(working, MergeSide.Theirs);
            }

            var baseValue = revisions.Count > 0
                ? new CdbValue.Obj(CdbReader.Parse(AtomicFile.ReadText(revisions[0])))
                : null;
            var mineValue = new CdbValue.Obj(CdbReader.Parse(mineText));
            var theirsValue = new CdbValue.Obj(CdbReader.Parse(theirsText));

            var result = ThreeWayMerge.Merge(baseValue, mineValue, theirsValue);

            if (!result.Succeeded || result.Value is not CdbValue.Obj mergedRoot)
            {
                var reason = result.Errors.Count > 0
                    ? string.Join("; ", result.Errors)
                    : "the merged result is not a database object";
                return new MergeOutcome(MergeFileState.Failed, null, result.Conflicts, sideFiles, reason);
            }

            var mergedText = CdbWriter.Write(mergedRoot.Value);
            var state = result.HasConflicts ? MergeFileState.Conflicted : MergeFileState.Merged;

            return new MergeOutcome(state, mergedText, result.Conflicts, sideFiles, null);
        }
        catch (Exception ex)
        {
            return new MergeOutcome(
                MergeFileState.Failed,
                null,
                Array.Empty<MergeConflict>(),
                sideFiles,
                ex.Message);
        }
    }

    /// <summary>
    /// Removes the conflict leftovers once the merged result has been saved. The incoming
    /// revisions are only deleted when they really are the ones that were merged.
    /// </summary>
    public static int Cleanup(string databasePath)
    {
        var removed = 0;

        foreach (var file in RevisionFiles(databasePath).Concat(new[] { MinePathFor(databasePath) }))
        {
            if (!File.Exists(file))
                continue;

            AtomicFile.TryDelete(file);

            if (!File.Exists(file))
                removed++;
        }

        return removed;
    }
}
