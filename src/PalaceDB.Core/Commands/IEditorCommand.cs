using PalaceDB.Core.Cursor;

namespace PalaceDB.Core.Commands;

/// <summary>
/// A single reversible modification of the document.
/// <para>
/// Implementations must be idempotent: <see cref="Execute"/> is called once when the
/// command is first applied and again for every redo, so all state that has to be
/// restored must be captured by the constructor rather than by <see cref="Execute"/>.
/// </para>
/// <para>
/// Every command carries the cursor position before and after it ran. The editor uses
/// these anchors to restore a meaningful cursor when an operation is undone, which is
/// what keeps undo from throwing the selection back to the top-left cell.
/// </para>
/// </summary>
public interface IEditorCommand
{
    /// <summary>Short human readable description, shown in the Undo/Redo menu items.</summary>
    string Label { get; }

    /// <summary>Cursor before the command ran; set by the editor when the command is created.</summary>
    CursorAnchor? CursorBefore { get; set; }

    /// <summary>Cursor after the command ran; set by the editor when the command is created.</summary>
    CursorAnchor? CursorAfter { get; set; }

    void Execute();

    void Undo();
}
