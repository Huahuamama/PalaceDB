using PalaceDB.Core.Cursor;

namespace PalaceDB.Core.Commands;

/// <summary>
/// Per-document undo/redo history.
/// <para>
/// The original PalaceDB editor restored an entire JSON snapshot on undo, which is
/// what made the cursor jump back to the first cell. This stack keeps individual
/// commands instead, so undo only touches the cells that actually changed and the
/// cursor can be restored from the command's own anchor.
/// </para>
/// </summary>
public sealed class CommandStack
{
    private readonly List<IEditorCommand> _undo = new();
    private readonly List<IEditorCommand> _redo = new();
    private readonly List<CompositeCommand> _openTransactions = new();

    /// <summary>Maximum number of retained undo steps.</summary>
    public int Capacity { get; set; } = 200;

    /// <summary>Raised after any change to the stacks, so the UI can refresh its menus.</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public string? UndoLabel => CanUndo ? _undo[^1].Label : null;

    public string? RedoLabel => CanRedo ? _redo[^1].Label : null;

    public IReadOnlyList<IEditorCommand> UndoHistory => _undo;

    public IReadOnlyList<IEditorCommand> RedoHistory => _redo;

    /// <summary>True while a transaction is open; individual commands are collected.</summary>
    public bool IsInTransaction => _openTransactions.Count > 0;

    /// <summary>Applies a command and records it. Any redo history is discarded.</summary>
    public void Execute(IEditorCommand command)
    {
        command.Execute();

        if (_openTransactions.Count > 0)
        {
            _openTransactions[^1].Add(command);
            return;
        }

        Push(command);
    }

    /// <summary>
    /// Records a command that has already been applied by the caller. Used when the
    /// UI updates the model directly and only needs the change to become undoable.
    /// </summary>
    public void PushApplied(IEditorCommand command)
    {
        if (_openTransactions.Count > 0)
        {
            _openTransactions[^1].Add(command);
            return;
        }

        Push(command);
    }

    private void Push(IEditorCommand command)
    {
        _undo.Add(command);
        _redo.Clear();

        if (_undo.Count > Capacity)
            _undo.RemoveRange(0, _undo.Count - Capacity);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes the last command. Returns the command so the caller can restore its cursor.</summary>
    public IEditorCommand? Undo()
    {
        if (_undo.Count == 0)
            return null;

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo();
        _redo.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
        return command;
    }

    /// <summary>Redoes the last undone command.</summary>
    public IEditorCommand? Redo()
    {
        if (_redo.Count == 0)
            return null;

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Execute();
        _undo.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
        return command;
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0 && _openTransactions.Count == 0)
            return;

        _undo.Clear();
        _redo.Clear();
        _openTransactions.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens a transaction. Everything executed until the scope is disposed becomes a
    /// single undo step. Nesting is supported and folds into the outer transaction.
    /// </summary>
    public Transaction BeginTransaction(string label)
    {
        var composite = new CompositeCommand(label);
        _openTransactions.Add(composite);
        return new Transaction(this, composite);
    }

    private void Commit(CompositeCommand composite)
    {
        var index = _openTransactions.LastIndexOf(composite);
        if (index < 0)
            return;

        _openTransactions.RemoveAt(index);

        // Nothing happened: do not pollute the history with an empty step.
        if (composite.Count == 0)
            return;

        if (_openTransactions.Count > 0)
        {
            _openTransactions[^1].Add(composite);
            return;
        }

        Push(composite);
    }

    /// <summary>Scope returned by <see cref="BeginTransaction"/>.</summary>
    public sealed class Transaction : IDisposable
    {
        private readonly CommandStack _stack;
        private readonly CompositeCommand _composite;
        private bool _disposed;

        internal Transaction(CommandStack stack, CompositeCommand composite)
        {
            _stack = stack;
            _composite = composite;
        }

        public bool IsEmpty => _composite.Count == 0;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _stack.Commit(_composite);
        }
    }
}
