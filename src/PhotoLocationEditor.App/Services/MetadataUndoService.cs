using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public sealed class MetadataUndoService
{
    private const int MaxEntries = 15;
    private readonly Stack<UndoOperation> _stack = new();

    public bool CanUndo => _stack.Count > 0;
    public int Count => _stack.Count;
    public string? PeekDescription => _stack.Count > 0 ? _stack.Peek().Description : null;

    public void Push(string description, IReadOnlyList<MetadataSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return;

        if (_stack.Count >= MaxEntries)
        {
            var trimmed = _stack.Take(MaxEntries - 1).Reverse().ToArray();
            _stack.Clear();
            foreach (var item in trimmed)
                _stack.Push(item);
        }

        _stack.Push(new UndoOperation(
            Guid.NewGuid().ToString("N")[..8],
            description,
            DateTime.UtcNow,
            snapshots));
    }

    public UndoOperation? Pop() => _stack.Count > 0 ? _stack.Pop() : null;

    public void Clear() => _stack.Clear();
}
