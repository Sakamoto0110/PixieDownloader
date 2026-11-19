namespace YtDlpCore;

/// <summary>
/// Undo/redo over snapshots of one value. The owner keeps the current value and asks this what to go to:
/// <see cref="Record"/> the value about to be replaced, <see cref="Undo"/>/<see cref="Redo"/> hand back the one
/// to show and keep the current one on the other side. A new <see cref="Record"/> drops what could be redone.
/// Grouping several changes into one step (a burst of typing) is the owner's call: it records once.
/// </summary>
public sealed class UndoHistory<T>
{
    private readonly List<T> _undo = [];
    private readonly Stack<T> _redo = new();
    private readonly int _limit;

    /// <param name="limit">Oldest steps are forgotten past this many.</param>
    public UndoHistory(int limit = 100) => _limit = Math.Max(1, limit);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Record(T previous)
    {
        _undo.Add(previous);
        if (_undo.Count > _limit)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>The value before the last step; <paramref name="current"/> becomes redoable. Throws when <see cref="CanUndo"/> is false.</summary>
    public T Undo(T current)
    {
        if (_undo.Count == 0)
            throw new InvalidOperationException("Nothing to undo.");
        var value = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(current);
        return value;
    }

    /// <summary>The value the last undo left; <paramref name="current"/> becomes undoable again. Throws when <see cref="CanRedo"/> is false.</summary>
    public T Redo(T current)
    {
        if (_redo.Count == 0)
            throw new InvalidOperationException("Nothing to redo.");
        var value = _redo.Pop();
        _undo.Add(current);
        return value;
    }
}
