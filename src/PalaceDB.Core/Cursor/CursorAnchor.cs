namespace PalaceDB.Core.Cursor;

/// <summary>Rectangular selection expressed in row/column indices.</summary>
public readonly record struct SelectionRange(int AnchorRow, int AnchorColumn, int FocusRow, int FocusColumn)
{
    public int Top => Math.Min(AnchorRow, FocusRow);
    public int Bottom => Math.Max(AnchorRow, FocusRow);
    public int Left => Math.Min(AnchorColumn, FocusColumn);
    public int Right => Math.Max(AnchorColumn, FocusColumn);

    public int RowCount => Bottom - Top + 1;
    public int ColumnCount => Right - Left + 1;

    public static SelectionRange Single(int row, int column) => new(row, column, row, column);

    public bool Contains(int row, int column) =>
        row >= Top && row <= Bottom && column >= Left && column <= Right;
}

/// <summary>
/// A cursor position that survives structural edits.
/// The id based fields are preferred; the index fields are the fallback when the
/// row or column no longer exists.
/// </summary>
public sealed record CursorAnchor(
    string SheetName,
    string? RowId,
    int RowIndex,
    string? ColumnId,
    int ColumnIndex,
    SelectionRange? Selection = null);

/// <summary>Resolved cursor: concrete indices valid for the current document state.</summary>
public readonly record struct CursorPosition(
    string SheetName,
    int Row,
    int Column,
    SelectionRange? Selection)
{
    public static CursorPosition None => new(string.Empty, 0, 0, null);

    public bool IsNone => string.IsNullOrEmpty(SheetName);
}
