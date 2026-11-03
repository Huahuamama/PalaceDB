using PalaceDB.Core.Cursor;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Commands;

/// <summary>The two derived-field features a sheet can switch on (S05 / S06).</summary>
public enum SheetFeature
{
    /// <summary>Writes a 0 based <c>index</c> on every row.</summary>
    Index,

    /// <summary>Writes a <c>group</c> that follows the titled separators.</summary>
    Group,
}

/// <summary>
/// Turns the derived <c>index</c> / <c>group</c> fields on or off for a sheet.
/// <para>
/// The flag lives in <c>props</c>, the fields live on every row. Both are written by this
/// command, so the whole switch is a single undo step; the fields are also recomputed on
/// every save, so they can never drift away from the row order.
/// </para>
/// </summary>
public sealed class SheetFeatureCommand : IEditorCommand
{
    private readonly SheetData _sheet;
    private readonly SheetFeature _feature;
    private readonly bool _enabled;
    private readonly bool _wasEnabled;
    private readonly string _key;
    private readonly List<CdbValue?> _before = new();
    private readonly List<CdbValue?> _after = new();

    public SheetFeatureCommand(SheetData sheet, SheetFeature feature, bool enabled)
    {
        _sheet = sheet;
        _feature = feature;
        _enabled = enabled;
        _wasEnabled = feature == SheetFeature.Index ? sheet.Props.HasIndex : sheet.Props.HasGroup;
        _key = feature == SheetFeature.Index ? DerivedFields.IndexKey : DerivedFields.GroupKey;

        if (enabled && sheet.GetColumn(_key) is not null)
            throw new InvalidOperationException(
                $"The table already has a column named '{_key}', so the {_key} field cannot be generated.");

        foreach (var row in sheet.Lines)
            _before.Add(row.Get(_key));

        // Build the new values once so undo and redo are exact, then put the sheet back
        // the way it was: the caller decides when the change really happens.
        ApplyFlag(enabled);
        ApplyDerived();

        foreach (var row in sheet.Lines)
            _after.Add(row.Get(_key));

        ApplyFlag(_wasEnabled);
        Write(_before);

        Label = enabled
            ? $"Add {FeatureName} to '{sheet.Name}'"
            : $"Remove {FeatureName} from '{sheet.Name}'";
    }

    public string Label { get; }

    /// <summary>Number of rows whose field value actually changes.</summary>
    public int ChangedRows => _after.Where((value, i) => !Equals(value, _before[i])).Count();

    /// <summary>True when the sheet was already in the requested state.</summary>
    public bool IsNoOp => _wasEnabled == _enabled;

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute()
    {
        ApplyFlag(_enabled);
        Write(_after);
    }

    public void Undo()
    {
        ApplyFlag(_wasEnabled);
        Write(_before);
    }

    private string FeatureName => _feature == SheetFeature.Index ? "Index" : "Group";

    private void ApplyFlag(bool value)
    {
        if (_feature == SheetFeature.Index)
            _sheet.Props.HasIndex = value;
        else
            _sheet.Props.HasGroup = value;
    }

    private void ApplyDerived()
    {
        if (!_enabled)
        {
            if (_feature == SheetFeature.Index)
                DerivedFields.RemoveIndex(_sheet);
            else
                DerivedFields.RemoveGroup(_sheet);
            return;
        }

        if (_feature == SheetFeature.Index)
            DerivedFields.ApplyIndex(_sheet);
        else
            DerivedFields.ApplyGroup(_sheet);
    }

    private void Write(List<CdbValue?> values)
    {
        for (var i = 0; i < _sheet.Lines.Count && i < values.Count; i++)
            _sheet.Lines[i].Set(_key, CdbValueCloner.CloneOrNull(values[i]));
    }
}

/// <summary>Reorders a table in the sidebar, together with its hidden sub-sheets (S04).</summary>
public sealed class SheetMoveCommand : IEditorCommand
{
    private readonly Database _database;
    private readonly int _from;
    private readonly int _count;
    private readonly int _to;

    /// <param name="direction">Negative moves the table up, positive moves it down.</param>
    public SheetMoveCommand(Database database, SheetData sheet, int direction)
    {
        _database = database;
        _from = database.Sheets.IndexOf(sheet);
        if (_from < 0)
            throw new ArgumentException("The table is not part of this database.", nameof(sheet));

        _count = BlockLength(database, _from);

        var target = Neighbour(database, _from, _count, direction)
            ?? throw new InvalidOperationException(
                direction < 0 ? "The table is already the first one." : "The table is already the last one.");

        // Neighbour reports a position in the current list; the move helper wants the
        // index measured after the run has been taken out.
        _to = target > _from ? target - _count : target;

        Label = direction < 0
            ? $"Move Table '{sheet.Name}' Up"
            : $"Move Table '{sheet.Name}' Down";
    }

    public string Label { get; }

    public CursorAnchor? CursorBefore { get; set; }

    public CursorAnchor? CursorAfter { get; set; }

    public void Execute() => _database.MoveSheetRange(_from, _count, _to);

    public void Undo() => _database.MoveSheetRange(_to, _count, _from);

    /// <summary>
    /// Length of the run of sheets that belong to the table at <paramref name="index"/>:
    /// the table itself plus the hidden sub-sheets named after it.
    /// </summary>
    public static int BlockLength(Database database, int index)
    {
        if (index < 0 || index >= database.Sheets.Count)
            return 0;

        var prefix = database.Sheets[index].Name + "@";
        var count = 1;

        while (index + count < database.Sheets.Count
               && database.Sheets[index + count].Name.StartsWith(prefix, StringComparison.Ordinal))
            count++;

        return count;
    }

    /// <summary>
    /// Position where the run starting at <paramref name="index"/> should be inserted so
    /// that it lands next to its neighbour, or null when it is already at the edge.
    /// </summary>
    public static int? Neighbour(Database database, int index, int count, int direction)
    {
        if (direction < 0)
        {
            for (var i = index - 1; i >= 0; i--)
                if (!database.Sheets[i].Props.Hide)
                    return i;
            return null;
        }

        for (var i = index + count; i < database.Sheets.Count; i++)
            if (!database.Sheets[i].Props.Hide)
                return i + BlockLength(database, i);

        return null;
    }
}
