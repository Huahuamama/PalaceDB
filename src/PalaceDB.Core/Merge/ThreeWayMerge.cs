using PalaceDB.Core.Model;

namespace PalaceDB.Core.Merge;

/// <summary>Which side a conflicting value was taken from.</summary>
public enum MergeSide
{
    Mine,
    Theirs,
}

/// <summary>One value that both sides changed, and the side that won.</summary>
public sealed record MergeConflict(string Path, string Base, string Mine, string Theirs, MergeSide Kept);

/// <summary>Outcome of merging three versions of the same JSON tree.</summary>
public sealed record MergeResult(
    CdbValue? Value,
    IReadOnlyList<MergeConflict> Conflicts,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;

    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>
/// Three-way merge of PalaceDB JSON trees (F16).
/// <para>
/// The rules are the classic ones: a value changed by only one side wins, a value changed
/// by both sides is a conflict, and an unchanged value keeps the incoming version. Objects
/// are merged field by field; arrays are merged by identity (the <c>id</c> field of a row,
/// or <c>name</c> for the sheet and column lists) so that inserting a row on one side does
/// not shift every later row, which is what makes a database merge usable at all.
/// </para>
/// <para>
/// When both sides changed the same value the local version is kept and the conflict is
/// reported instead of being applied silently. The caller decides what to do with it; the
/// merged tree itself is always safe to show, because nothing is written to disk here.
/// </para>
/// </summary>
public static class ThreeWayMerge
{
    /// <summary>Fields tried, in order, as the identity of an array element.</summary>
    private static readonly string[] IdentityFields = { "id", "name" };
    public static MergeResult Merge(CdbValue? baseValue, CdbValue? mine, CdbValue? theirs)
    {
        var conflicts = new List<MergeConflict>();
        var errors = new List<string>();

        var merged = MergeValue(baseValue, mine, theirs, string.Empty, conflicts, errors);

        return new MergeResult(merged, conflicts, errors);
    }

    // ---------------------------------------------------------------- recursion

    private static CdbValue? MergeValue(
        CdbValue? baseValue,
        CdbValue? mine,
        CdbValue? theirs,
        string path,
        List<MergeConflict> conflicts,
        List<string> errors)
    {
        // Both sides ended up with the same value, or only one side moved away from base.
        if (Equal(mine, theirs))
            return theirs;
        if (Equal(mine, baseValue))
            return theirs;
        if (Equal(theirs, baseValue))
            return mine;

        if (mine is CdbValue.Obj mineObject && theirs is CdbValue.Obj theirsObject)
            return MergeObject(baseValue as CdbValue.Obj, mineObject, theirsObject, path, conflicts, errors);

        if (mine is CdbValue.Arr mineArray && theirs is CdbValue.Arr theirsArray)
            return MergeArray(baseValue as CdbValue.Arr, mineArray, theirsArray, path, conflicts, errors);

        conflicts.Add(new MergeConflict(
            DescribePath(path),
            Describe(baseValue),
            Describe(mine),
            Describe(theirs),
            MergeSide.Mine));

        return mine;
    }

    private static CdbValue MergeObject(
        CdbValue.Obj? baseValue,
        CdbValue.Obj mine,
        CdbValue.Obj theirs,
        string path,
        List<MergeConflict> conflicts,
        List<string> errors)
    {
        var baseObject = baseValue?.Value;
        var result = new CdbObject();

        // The incoming order is the skeleton, so a merged file looks like the version that
        // would have landed without the conflict.
        foreach (var key in theirs.Value.Keys)
        {
            var merged = MergeValue(
                baseObject?.Get(key),
                mine.Value.Get(key),
                theirs.Value.Get(key),
                Join(path, key),
                conflicts,
                errors);

            if (merged is not null)
                result.Set(key, merged);
        }

        // Fields that only the local side still has: either added here, or deleted there.
        foreach (var key in mine.Value.Keys)
        {
            if (theirs.Value.ContainsKey(key))
                continue;

            var merged = MergeValue(
                baseObject?.Get(key),
                mine.Value.Get(key),
                null,
                Join(path, key),
                conflicts,
                errors);

            if (merged is not null)
                result.Set(key, merged);
        }

        return new CdbValue.Obj(result);
    }

    private static CdbValue MergeArray(
        CdbValue.Arr? baseValue,
        CdbValue.Arr mine,
        CdbValue.Arr theirs,
        string path,
        List<MergeConflict> conflicts,
        List<string> errors)
    {
        var identity = IdentityField(mine, theirs, baseValue);
        return identity is not null
            ? MergeKeyedArray(baseValue, mine, theirs, identity, path, conflicts, errors)
            : MergeIndexedArray(baseValue, mine, theirs, path, conflicts, errors);
    }

    /// <summary>
    /// Merges element by element using a stable identity field. Rows added on either side
    /// are appended, rows deleted on either side disappear, and a row deleted on one side
    /// while edited on the other becomes a conflict.
    /// </summary>
    private static CdbValue MergeKeyedArray(
        CdbValue.Arr? baseValue,
        CdbValue.Arr mine,
        CdbValue.Arr theirs,
        string identity,
        string path,
        List<MergeConflict> conflicts,
        List<string> errors)
    {
        var baseMap = IndexByIdentity(baseValue, identity);
        var mineMap = IndexByIdentity(mine, identity);
        var theirsMap = IndexByIdentity(theirs, identity);
        var items = new List<CdbValue>();

        foreach (var element in theirs.Items)
        {
            var id = IdentityValue(element, identity);
            if (id is null)
            {
                items.Add(element);
                continue;
            }

            var merged = MergeValue(
                baseMap.GetValueOrDefault(id),
                mineMap.GetValueOrDefault(id),
                element,
                Join(path, id),
                conflicts,
                errors);

            if (merged is not null)
                items.Add(merged);
        }

        // Elements that only the local side has (added here, or deleted there).
        foreach (var element in mine.Items)
        {
            var id = IdentityValue(element, identity);
            if (id is null)
                continue;
            if (theirsMap.ContainsKey(id))
                continue;

            var merged = MergeValue(
                baseMap.GetValueOrDefault(id),
                element,
                null,
                Join(path, id),
                conflicts,
                errors);

            if (merged is not null)
                items.Add(merged);
        }

        return new CdbValue.Arr(items);
    }

    /// <summary>
    /// Fallback for arrays without identities (separator indexes, flags masks, custom
    /// value lists). A changed length cannot be attributed to one side, so it is a conflict.
    /// </summary>
    private static CdbValue MergeIndexedArray(
        CdbValue.Arr? baseValue,
        CdbValue.Arr mine,
        CdbValue.Arr theirs,
        string path,
        List<MergeConflict> conflicts,
        List<string> errors)
    {
        if (baseValue is not null &&
            (baseValue.Items.Count != mine.Items.Count || baseValue.Items.Count != theirs.Items.Count))
        {
            conflicts.Add(new MergeConflict(
                DescribePath(path),
                Describe(baseValue),
                Describe(mine),
                Describe(theirs),
                MergeSide.Mine));

            return mine;
        }

        var items = new List<CdbValue>(mine.Items.Count);
        for (var i = 0; i < mine.Items.Count; i++)
        {
            var merged = MergeValue(
                baseValue is not null && i < baseValue.Items.Count ? baseValue.Items[i] : null,
                mine.Items[i],
                theirs.Items[i],
                Join(path, "[" + i + "]"),
                conflicts,
                errors);

            if (merged is not null)
                items.Add(merged);
        }

        return new CdbValue.Arr(items);
    }

    // ---------------------------------------------------------------- identity

    /// <summary>
    /// The field that identifies an element, or null when the array has to be merged by
    /// position. Every element of both sides must carry a non-empty value for it.
    /// </summary>
    private static string? IdentityField(CdbValue.Arr mine, CdbValue.Arr theirs, CdbValue.Arr? baseValue)
    {
        if (mine.Items.Count == 0 || theirs.Items.Count == 0)
            return null;

        foreach (var field in IdentityFields)
        {
            if (AllHaveIdentity(mine, field) &&
                AllHaveIdentity(theirs, field) &&
                (baseValue is null || AllHaveIdentity(baseValue, field)))
            {
                return field;
            }
        }

        return null;
    }

    private static bool AllHaveIdentity(CdbValue.Arr array, string field)
    {
        foreach (var element in array.Items)
        {
            if (element is not CdbValue.Obj obj)
                return false;
            if (IdentityValue(obj, field) is not { Length: > 0 })
                return false;
        }

        return true;
    }

    /// <summary>
    /// The identity of one element, or null when it has none. The key is matched without
    /// regard to case because the databases in the wild use both <c>id</c> and <c>Id</c>
    /// for the same thing.
    /// </summary>
    private static string? IdentityValue(CdbValue element, string field) =>
        element is CdbValue.Obj obj ? IdentityValue(obj, field) : null;

    private static string? IdentityValue(CdbValue.Obj element, string field) =>
        element.Value.Get(KeyFor(element.Value, field)) is CdbValue.Str str ? str.Value : null;

    /// <summary>The key actually used by this object for <paramref name="field"/>.</summary>
    private static string KeyFor(CdbObject obj, string field)
    {
        if (obj.ContainsKey(field))
            return field;

        foreach (var key in obj.Keys)
        {
            if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return field;
    }

    private static Dictionary<string, CdbValue> IndexByIdentity(CdbValue.Arr? array, string field)
    {
        var map = new Dictionary<string, CdbValue>(StringComparer.Ordinal);
        if (array is null)
            return map;

        foreach (var element in array.Items)
        {
            var id = IdentityValue(element, field);
            if (id is not null)
                map.TryAdd(id, element);
        }

        return map;
    }

    // ---------------------------------------------------------------- comparison

    /// <summary>Structural equality; key order does not matter for two objects.</summary>
    public static bool Equal(CdbValue? a, CdbValue? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;

        return (a, b) switch
        {
            (CdbValue.Null, CdbValue.Null) => true,
            (CdbValue.Bool x, CdbValue.Bool y) => x.Value == y.Value,
            (CdbValue.Number x, CdbValue.Number y) => x.Value.Equals(y.Value),
            (CdbValue.Str x, CdbValue.Str y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal),
            (CdbValue.Arr x, CdbValue.Arr y) => EqualArray(x, y),
            (CdbValue.Obj x, CdbValue.Obj y) => EqualObject(x.Value, y.Value),
            _ => false,
        };
    }

    private static bool EqualArray(CdbValue.Arr a, CdbValue.Arr b)
    {
        if (a.Items.Count != b.Items.Count)
            return false;
        for (var i = 0; i < a.Items.Count; i++)
        {
            if (!Equal(a.Items[i], b.Items[i]))
                return false;
        }

        return true;
    }

    private static bool EqualObject(CdbObject a, CdbObject b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var key in a.Keys)
        {
            if (!b.ContainsKey(key))
                return false;
            if (!Equal(a.Get(key), b.Get(key)))
                return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- display helpers

    private static string Join(string path, string segment) =>
        string.IsNullOrEmpty(path) ? segment : path + "/" + segment;

    /// <summary>Reads as <c>sheets/Items/sword/Title</c> in the conflict report.</summary>
    private static string DescribePath(string path) => string.IsNullOrEmpty(path) ? "(root)" : path;

    /// <summary>Short rendering of a value for the conflict report.</summary>
    public static string Describe(CdbValue? value)
    {
        const int limit = 60;

        var text = value switch
        {
            null => "(absent)",
            CdbValue.Null => "null",
            CdbValue.Str s => s.Value,
            CdbValue.Bool b => b.Value ? "true" : "false",
            CdbValue.Number n => n.ToString(),
            CdbValue.Arr a => "[" + a.Items.Count + " items]",
            CdbValue.Obj o => "{" + o.Value.Count + " fields}",
            _ => value.ToString() ?? string.Empty,
        };

        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= limit ? text : text[..limit] + "\u2026";
    }
}
