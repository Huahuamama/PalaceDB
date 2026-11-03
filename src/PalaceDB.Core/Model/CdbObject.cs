using System.Diagnostics.CodeAnalysis;

namespace PalaceDB.Core.Model;

/// <summary>
/// Insertion-ordered string to <see cref="CdbValue"/> map.
/// The order of keys is the order used when writing the file, which is what keeps
/// "open then save" byte-identical and keeps git diffs minimal.
/// </summary>
public sealed class CdbObject
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, CdbValue> _map = new(StringComparer.Ordinal);

    public CdbObject() { }

    public CdbObject(IEnumerable<KeyValuePair<string, CdbValue>> values)
    {
        foreach (var kv in values)
            Set(kv.Key, kv.Value);
    }

    public int Count => _order.Count;

    public IReadOnlyList<string> Keys => _order;

    public IReadOnlyDictionary<string, CdbValue> Map => _map;

    public bool ContainsKey(string key) => _map.ContainsKey(key);

    public CdbValue? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Sets a value. An existing key keeps its position; a new key is appended,
    /// mirroring how the original editor appends newly created fields.
    /// Passing <c>null</c> removes the key - use <see cref="CdbValue.NullValue"/>
    /// when an explicit JSON <c>null</c> has to be written.
    /// </summary>
    public void Set(string key, CdbValue? value)
    {
        if (value is null)
        {
            Remove(key);
            return;
        }

        if (_map.ContainsKey(key))
        {
            _map[key] = value;
            return;
        }
        _map[key] = value;
        _order.Add(key);
    }

    /// <summary>Removes a key. Returns true when the key existed.</summary>
    public bool Remove(string key)
    {
        if (!_map.Remove(key))
            return false;
        _order.Remove(key);
        return true;
    }

    /// <summary>
    /// Renames a key without moving it, so a renamed column keeps its position in
    /// every row. Returns false when the old key is missing or the new one is taken.
    /// </summary>
    public bool Rename(string oldKey, string newKey)
    {
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
            return _map.ContainsKey(oldKey);

        if (!_map.TryGetValue(oldKey, out var value))
            return false;

        if (_map.ContainsKey(newKey))
            return false;

        _map.Remove(oldKey);
        _map[newKey] = value;

        var index = _order.IndexOf(oldKey);
        if (index >= 0)
            _order[index] = newKey;

        return true;
    }

    /// <summary>Replaces the whole content while keeping the given key order.</summary>
    public void ReplaceAll(IEnumerable<KeyValuePair<string, CdbValue>> values)
    {
        _order.Clear();
        _map.Clear();
        foreach (var kv in values)
            Set(kv.Key, kv.Value);
    }

    public CdbObject Clone()
    {
        var copy = new CdbObject();
        foreach (var key in _order)
            copy.Set(key, CdbValueCloner.Clone(_map[key]));
        return copy;
    }

    public IEnumerable<KeyValuePair<string, CdbValue>> Pairs()
    {
        foreach (var key in _order)
            yield return new KeyValuePair<string, CdbValue>(key, _map[key]);
    }

    public override string ToString() => string.Join(", ", _order.Select(k => k + ": " + _map[k]));

    // ---- typed helpers -------------------------------------------------

    public string? GetString(string key) => Get(key) switch
    {
        CdbValue.Str s => s.Value,
        _ => null,
    };

    public string GetString(string key, string fallback) => GetString(key) ?? fallback;

    public bool? GetBool(string key) => Get(key) switch
    {
        CdbValue.Bool b => b.Value,
        _ => null,
    };

    public bool GetBool(string key, bool fallback) => GetBool(key) ?? fallback;

    public int? GetInt(string key) => Get(key) switch
    {
        CdbValue.Number n when n.IsInteger => (int)n.Value,
        _ => null,
    };

    public double? GetDouble(string key) => Get(key) switch
    {
        CdbValue.Number n => n.Value,
        _ => null,
    };

    public CdbValue.Arr? GetArray(string key) => Get(key) as CdbValue.Arr;

    public CdbObject? GetObject(string key) => (Get(key) as CdbValue.Obj)?.Value;

    public bool IsNullOrMissing(string key) => Get(key) is null or CdbValue.Null;

    public static CdbObject Empty() => new();
}

/// <summary>Deep clone helper for <see cref="CdbValue"/> trees.</summary>
public static class CdbValueCloner
{
    public static CdbValue Clone(CdbValue value) => value switch
    {
        CdbValue.Arr a => new CdbValue.Arr(a.Items.Select(Clone).ToList()),
        CdbValue.Obj o => new CdbValue.Obj(o.Value.Clone()),
        _ => value,
    };

    /// <summary>Clones a value that may be absent (an optional column without a value).</summary>
    public static CdbValue? CloneOrNull(CdbValue? value) => value is null ? null : Clone(value);
}
