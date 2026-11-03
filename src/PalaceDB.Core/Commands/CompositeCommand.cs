namespace PalaceDB.Core.Commands;

/// <summary>
/// Groups several commands into one undo step (multi-cell paste, bulk fill,
/// clear selection, ...). Undo runs in reverse order so nested changes unwind safely.
/// </summary>
public sealed class CompositeCommand : IEditorCommand
{
    private readonly List<IEditorCommand> _commands = new();

    public CompositeCommand(string label) => Label = label;

    public string Label { get; }

    public IReadOnlyList<IEditorCommand> Commands => _commands;

    public int Count => _commands.Count;

    public PalaceDB.Core.Cursor.CursorAnchor? CursorBefore { get; set; }

    public PalaceDB.Core.Cursor.CursorAnchor? CursorAfter { get; set; }

    /// <summary>Appends a command. The caller is responsible for executing it.</summary>
    public void Add(IEditorCommand command) => _commands.Add(command);

    public void Execute()
    {
        foreach (var command in _commands)
            command.Execute();
    }

    public void Undo()
    {
        for (var i = _commands.Count - 1; i >= 0; i--)
            _commands[i].Undo();
    }

    /// <summary>Creates a composite from commands that have already been applied.</summary>
    public static CompositeCommand FromApplied(string label, IEnumerable<IEditorCommand> commands)
    {
        var composite = new CompositeCommand(label);
        foreach (var command in commands)
            composite.Add(command);
        return composite;
    }
}
