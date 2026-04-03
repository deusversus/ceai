using CEAISuite.Desktop.Models;

namespace CEAISuite.Desktop.Services;

/// <summary>
/// Manages an undo/redo stack for hex editor byte edits.
/// </summary>
public sealed class HexEditUndoService
{
    private readonly Stack<HexEditOperation> _undoStack = new();
    private readonly Stack<HexEditOperation> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Push(HexEditOperation operation)
    {
        _undoStack.Push(operation);
        _redoStack.Clear();
    }

    public HexEditOperation? Undo()
    {
        if (_undoStack.Count == 0) return null;
        var op = _undoStack.Pop();
        _redoStack.Push(op);
        return op;
    }

    public HexEditOperation? Redo()
    {
        if (_redoStack.Count == 0) return null;
        var op = _redoStack.Pop();
        _undoStack.Push(op);
        return op;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
