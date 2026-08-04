namespace OmniRef.Core.Services;

public interface IUndoableCommand
{
    string Description { get; }
    IReadOnlySet<Guid> ProtectedAssetIds { get; }
    void Execute();
    void Undo();
}

public sealed class DelegateUndoableCommand(
    string description,
    Action execute,
    Action undo,
    IEnumerable<Guid>? protectedAssetIds = null) : IUndoableCommand
{
    public string Description { get; } = description;
    public IReadOnlySet<Guid> ProtectedAssetIds { get; } =
        protectedAssetIds?.ToHashSet() ?? new HashSet<Guid>();

    public void Execute() => execute();

    public void Undo() => undo();
}

public sealed class UndoHistory
{
    private readonly int _capacity;
    private readonly LinkedList<IUndoableCommand> _undo = [];
    private readonly Stack<IUndoableCommand> _redo = [];

    public UndoHistory(int capacity = 100)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Last?.Value.Description;
    public string? RedoDescription => _redo.TryPeek(out var command) ? command.Description : null;
    public IReadOnlySet<Guid> ProtectedAssetIds
    {
        get
        {
            var assetIds = new HashSet<Guid>();
            foreach (var command in _undo)
            {
                assetIds.UnionWith(command.ProtectedAssetIds);
            }
            foreach (var command in _redo)
            {
                assetIds.UnionWith(command.ProtectedAssetIds);
            }
            return assetIds;
        }
    }

    public void Execute(IUndoableCommand command)
    {
        command.Execute();
        _undo.AddLast(command);
        _redo.Clear();
        while (_undo.Count > _capacity)
        {
            _undo.RemoveFirst();
        }
    }

    public void PushExecuted(IUndoableCommand command)
    {
        _undo.AddLast(command);
        _redo.Clear();
        while (_undo.Count > _capacity)
        {
            _undo.RemoveFirst();
        }
    }

    public void Undo()
    {
        if (_undo.Last is not { } node)
        {
            return;
        }

        _undo.RemoveLast();
        node.Value.Undo();
        _redo.Push(node.Value);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var command))
        {
            return;
        }

        command.Execute();
        _undo.AddLast(command);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
