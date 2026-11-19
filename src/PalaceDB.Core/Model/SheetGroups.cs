namespace PalaceDB.Core.Model;

/// <summary>
/// One heading and the rows it owns.
/// <para>
/// A heading without a title is a divider inside the run above it rather than the start of
/// a new group, which is why only titled groups take part in the derived <c>group</c>
/// numbering (see <see cref="DerivedFields.ApplyGroup"/>).
/// </para>
/// </summary>
public sealed class SheetGroup
{
    internal SheetGroup(string? title, int rows)
    {
        Title = title;
        Rows = rows;
    }

    /// <summary>Name of the heading, or null when the divider was never named.</summary>
    public string? Title { get; internal set; }

    /// <summary>How many rows sit under this heading.</summary>
    public int Rows { get; internal set; }
}

/// <summary>
/// The headings of one sheet and the rows each of them owns.
/// <para>
/// The .cdb stores a heading as the index of the row it sits in front of. Editing through
/// those numbers means every insert, delete and move has to renumber the headings below the
/// change, and two headings that land on the same row cannot both survive. Keeping the row
/// counts here instead makes a change a local adjustment, and the indices are computed only
/// when the file is written (<see cref="Indexes"/>). The bytes on disk are the same either
/// way: a heading is still a row index, and the two arrays are still parallel.
/// </para>
/// <para>
/// Rows above the first heading belong to no group at all; they are counted by
/// <see cref="LeadingRows"/>. A heading may also own no rows: two headings in front of the
/// same row describe an empty group, which real files do contain (and which the on-disk
/// array expresses as the same index twice).
/// </para>
/// </summary>
public sealed class SheetGroups
{
    private readonly SheetData _sheet;
    private readonly List<SheetGroup> _groups = new();

    internal SheetGroups(SheetData sheet) => _sheet = sheet;

    /// <summary>Rows above the first heading, which therefore have no group.</summary>
    public int LeadingRows { get; private set; }

    /// <summary>The headings in row order.</summary>
    public IReadOnlyList<SheetGroup> Items => _groups;

    public int Count => _groups.Count;

    /// <summary>Rows described by this model, the leading rows included.</summary>
    public int TotalRows
    {
        get
        {
            var total = LeadingRows;
            foreach (var group in _groups)
                total += group.Rows;
            return total;
        }
    }

    /// <summary>Zero-based row index of the heading at <paramref name="position"/>.</summary>
    public int StartOf(int position)
    {
        var start = LeadingRows;
        for (var i = 0; i < position && i < _groups.Count; i++)
            start += _groups[i].Rows;
        return start;
    }

    /// <summary>The headings as the <c>separators</c> array of the .cdb.</summary>
    public List<int> Indexes()
    {
        var result = new List<int>(_groups.Count);
        var cursor = LeadingRows;
        foreach (var group in _groups)
        {
            result.Add(cursor);
            cursor += group.Rows;
        }
        return result;
    }

    /// <summary>The headings paired with their titles, in row order.</summary>
    public List<(int Start, string? Title)> Starts()
    {
        var result = new List<(int, string?)>(_groups.Count);
        var cursor = LeadingRows;
        foreach (var group in _groups)
        {
            result.Add((cursor, group.Title));
            cursor += group.Rows;
        }
        return result;
    }

    /// <summary>
    /// The headings that carry a title, each with every row it owns up to the next titled
    /// heading. An untitled divider is a divider *inside* the group above it, so it does not
    /// end that group: this is the grouping the grid folds and the <c>group</c> field
    /// numbers, and it is what the page is sent so it never has to work it out again.
    /// </summary>
    public List<(int Start, int Count, string Title)> TitledGroups()
    {
        var result = new List<(int, int, string)>();
        var cursor = LeadingRows;
        var start = -1;
        var title = string.Empty;

        foreach (var group in _groups)
        {
            if (!string.IsNullOrEmpty(group.Title))
            {
                if (start >= 0)
                    result.Add((start, cursor - start, title));
                start = cursor;
                title = group.Title!;
            }
            cursor += group.Rows;
        }

        if (start >= 0)
            result.Add((start, cursor - start, title));

        return result;
    }

    /// <summary>True when a heading sits in front of <paramref name="row"/>.</summary>
    public bool HasHeadingAt(int row)
    {
        var cursor = LeadingRows;
        foreach (var group in _groups)
        {
            if (cursor == row)
                return true;
            cursor += group.Rows;
        }
        return false;
    }

    /// <summary>
    /// Rebuilds the model from the index/title pairs of a file. An index that repeats is an
    /// empty group and is kept: the array can say the same row twice, and dropping it would
    /// rewrite the file. Only an index that goes backwards is dropped, which no writer here
    /// produces.
    /// </summary>
    internal void Load(IReadOnlyList<(int Index, string? Title)> pairs, int rowCount)
    {
        _groups.Clear();
        LeadingRows = rowCount;

        var previous = -1;
        foreach (var pair in pairs.OrderBy(p => p.Index))
        {
            var index = Math.Clamp(pair.Index, 0, rowCount);
            if (index < previous)
                continue;

            if (_groups.Count == 0)
                LeadingRows = index;
            else
                _groups[^1].Rows = index - previous;

            _groups.Add(new SheetGroup(pair.Title, rowCount - index));
            previous = index;
        }
    }

    /// <summary>
    /// Copies titles onto the headings, by position. Used when the on-disk array is written
    /// from outside the model, so that <c>props.separatorTitles</c> stays the way in.
    /// </summary>
    internal void ApplyTitles(IReadOnlyList<string?> titles)
    {
        for (var i = 0; i < _groups.Count; i++)
            _groups[i].Title = i < titles.Count ? titles[i] : null;
    }

    /// <summary>
    /// Rows inserted at the gap <paramref name="index"/>. Which group takes them depends on
    /// <paramref name="joinAbove"/>.
    /// <para>
    /// False means they start at the gap, so the heading that sits in front of
    /// <paramref name="index"/> owns them - a heading with no rows under it yet included,
    /// which is what makes an empty group fillable. True means they stay with the rows
    /// above the gap: "insert below this row" and "drop above that heading" are the same
    /// request, and both must not hand the row to the group below.
    /// </para>
    /// </summary>
    internal void InsertRows(int index, int count, bool joinAbove = false)
    {
        if (count <= 0)
            return;

        if (joinAbove)
        {
            var anchor = index - 1;
            if (anchor < LeadingRows)
            {
                LeadingRows += count;
                return;
            }

            var above = LeadingRows;
            foreach (var group in _groups)
            {
                if (anchor < above + group.Rows)
                {
                    group.Rows += count;
                    return;
                }
                above += group.Rows;
            }

            if (_groups.Count > 0)
                _groups[^1].Rows += count;
            else
                LeadingRows += count;
            return;
        }

        // The heading in front of the gap owns the new rows. When several headings share
        // that row - an empty group - the last of them is the one whose run the rows under
        // it belong to, so that is the one that grows.
        SheetGroup? owner = null;
        var start = LeadingRows;
        foreach (var group in _groups)
        {
            if (start == index)
                owner = group;
            else if (index > start && index < start + group.Rows)
            {
                owner = group;
                break;
            }
            start += group.Rows;
        }

        if (owner is not null)
        {
            owner.Rows += count;
            return;
        }

        // Past the last row: the last heading takes them, which stops it being empty. Above
        // the first heading there is no group at all, so the leading run grows instead.
        if (_groups.Count > 0 && index >= start)
            _groups[^1].Rows += count;
        else
            LeadingRows += count;
    }

    /// <summary>
    /// Rows removed from a range. A heading goes with its last row: leaving it behind would
    /// hand the rows that moved up to a title that no longer describes them, which is what
    /// the old index arithmetic did when two headings collided.
    /// </summary>
    internal void DeleteRows(int index, int count)
    {
        if (count <= 0)
            return;

        var start = index;
        var end = index + count;
        var leadEnd = LeadingRows;

        if (start < leadEnd)
            LeadingRows -= Math.Min(end, leadEnd) - start;

        var cursor = leadEnd;
        for (var i = 0; i < _groups.Count; i++)
        {
            var group = _groups[i];
            var groupStart = cursor;
            var groupEnd = cursor + group.Rows;
            cursor = groupEnd;

            var overlap = Math.Min(end, groupEnd) - Math.Max(start, groupStart);
            if (overlap <= 0)
                continue;

            group.Rows -= overlap;
            if (group.Rows == 0)
                _groups.RemoveAt(i--);
        }

        Changed();
    }

    /// <summary>
    /// A single row dragged from <paramref name="from"/> to the gap at
    /// <paramref name="to"/>. The row changes group; the headings stay where they are,
    /// which is the whole point of a group owning its rows.
    /// <para>
    /// A heading is drawn as a strip inside the gap in front of its first row, so that gap
    /// has two sides: a row dropped under the heading becomes the first row of its group,
    /// a row dropped above it stays with the rows before it. Without
    /// <paramref name="belowHeading"/> only one of the two could be expressed, and the top
    /// half of a heading had no way of saying "keep this row where it is".
    /// </para>
    /// </summary>
    internal void MoveRow(int from, int to, bool belowHeading = true)
    {
        RemoveRow(from);
        InsertRows(to, 1, joinAbove: !belowHeading);
        Changed();
    }

    /// <summary>
    /// Takes one row out of the group that owns it. A heading whose last row goes is
    /// dropped with it: left behind, it would name a run of rows that is no longer there.
    /// An empty heading on the way is skipped, because no row can belong to it.
    /// </summary>
    private void RemoveRow(int from)
    {
        if (from < LeadingRows)
        {
            LeadingRows -= 1;
            return;
        }

        var cursor = LeadingRows;
        for (var i = 0; i < _groups.Count; i++)
        {
            var group = _groups[i];
            if (from < cursor + group.Rows)
            {
                group.Rows -= 1;
                if (group.Rows == 0)
                    _groups.RemoveAt(i);
                return;
            }
            cursor += group.Rows;
        }
    }

    /// <summary>
    /// Adds a heading in front of <paramref name="row"/>, or removes the one already there.
    /// Returns true when a heading was added.
    /// </summary>
    internal bool Toggle(int row)
    {
        var adding = !TryRemoveHeading(row);
        if (adding)
            AddHeading(row);
        Changed();
        return adding;
    }

    /// <summary>Names the heading in front of <paramref name="row"/>; no-op without one.</summary>
    internal void SetTitle(int row, string? title)
    {
        var cursor = LeadingRows;
        foreach (var group in _groups)
        {
            if (cursor == row)
            {
                group.Title = title;
                Changed();
                return;
            }
            cursor += group.Rows;
        }
    }

    internal Snapshot Capture()
    {
        var groups = new SheetGroup[_groups.Count];
        for (var i = 0; i < groups.Length; i++)
            groups[i] = new SheetGroup(_groups[i].Title, _groups[i].Rows);
        return new Snapshot(LeadingRows, groups);
    }

    /// <summary>
    /// Puts a captured state back. The copy is deliberate: the snapshot has to stay valid
    /// for a second undo after the same command was executed again.
    /// </summary>
    internal void Restore(Snapshot snapshot)
    {
        LeadingRows = snapshot.LeadingRows;
        _groups.Clear();
        foreach (var group in snapshot.Groups)
            _groups.Add(new SheetGroup(group.Title, group.Rows));
        Changed();
    }

    private bool TryRemoveHeading(int row)
    {
        if (_groups.Count > 0 && row == LeadingRows)
        {
            LeadingRows += _groups[0].Rows;
            _groups.RemoveAt(0);
            return true;
        }

        var cursor = LeadingRows;
        for (var i = 0; i < _groups.Count; i++)
        {
            var group = _groups[i];
            if (cursor == row)
            {
                // The rows of a removed heading are handed to the run above it, so no row
                // changes its number and only the heading disappears.
                _groups.RemoveAt(i);
                _groups[i - 1].Rows += group.Rows;
                return true;
            }
            cursor += group.Rows;
        }

        return false;
    }

    private void AddHeading(int row)
    {
        if (row < LeadingRows)
        {
            var below = LeadingRows - row;
            LeadingRows = row;
            _groups.Insert(0, new SheetGroup(null, below));
            return;
        }

        var cursor = LeadingRows;
        for (var i = 0; i < _groups.Count; i++)
        {
            var group = _groups[i];
            if (row < cursor + group.Rows)
            {
                var below = cursor + group.Rows - row;
                group.Rows = row - cursor;
                _groups.Insert(i + 1, new SheetGroup(null, below));
                return;
            }
            cursor += group.Rows;
        }

        // Past the last row: a heading with nothing under it yet. Its index is the row
        // count, which the array can describe, and it stays until rows are added under it.
        _groups.Add(new SheetGroup(null, 0));
    }

    private void Changed() => _sheet.SyncSeparatorTitles();
}

/// <summary>A heading layout captured for undo.</summary>
internal readonly record struct Snapshot(int LeadingRows, SheetGroup[] Groups);
