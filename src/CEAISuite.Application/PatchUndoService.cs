using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Tracks memory patches with original bytes for undo/redo support.
/// Every write operation should go through this service to enable rollback.
/// </summary>
public sealed class PatchUndoService(IEngineFacade engineFacade)
{
    public sealed record Patch(
        int ProcessId,
        nuint Address,
        MemoryDataType DataType,
        byte[] OriginalBytes,
        string NewValue,
        DateTimeOffset Timestamp);

    private readonly List<Patch> _undoStack = new();
    private readonly List<Patch> _redoStack = new();
    private const int MaxHistory = 500;

    /// <summary>Number of patches available to undo.</summary>
    public int UndoCount => _undoStack.Count;

    /// <summary>Number of patches available to redo.</summary>
    public int RedoCount => _redoStack.Count;

    /// <summary>
    /// Write a value to memory, recording the original bytes for undo.
    /// </summary>
    public async Task<MemoryWriteResult> WriteWithUndoAsync(
        int processId, nuint address, MemoryDataType dataType, string value,
        CancellationToken ct = default)
    {
        // Read original bytes before writing
        var size = GetDataTypeSize(dataType);
        var originalRead = await engineFacade.ReadMemoryAsync(processId, address, size, ct);
        byte[] originalBytes = originalRead.Bytes.ToArray();

        var result = await engineFacade.WriteValueAsync(processId, address, dataType, value, ct);
        if (result.BytesWritten > 0)
        {
            _undoStack.Add(new Patch(processId, address, dataType, originalBytes, value, DateTimeOffset.UtcNow));
            _redoStack.Clear(); // new write invalidates redo history

            // Trim history
            if (_undoStack.Count > MaxHistory)
                _undoStack.RemoveAt(0);
        }
        return result;
    }

    /// <summary>Undo the last memory patch by restoring original bytes.</summary>
    public async Task<string> UndoAsync(CancellationToken ct = default)
    {
        if (_undoStack.Count == 0) return "Nothing to undo.";

        var patch = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        // Write original bytes back
        var writeResult = await engineFacade.ReadMemoryAsync(patch.ProcessId, patch.Address,
            patch.OriginalBytes.Length, ct);
        byte[] currentBytes = writeResult.Bytes.ToArray();

        // Write the original bytes directly using raw write
        await WriteRawBytesAsync(patch.ProcessId, patch.Address, patch.OriginalBytes, ct);

        _redoStack.Add(patch with { OriginalBytes = currentBytes });

        return $"Undone write at 0x{patch.Address:X}: restored {patch.OriginalBytes.Length} bytes.";
    }

    /// <summary>Redo the last undone patch.</summary>
    public async Task<string> RedoAsync(CancellationToken ct = default)
    {
        if (_redoStack.Count == 0) return "Nothing to redo.";

        var patch = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        var originalRead = await engineFacade.ReadMemoryAsync(patch.ProcessId, patch.Address,
            GetDataTypeSize(patch.DataType), ct);
        byte[] currentBytes = originalRead.Bytes.ToArray();

        var result = await engineFacade.WriteValueAsync(patch.ProcessId, patch.Address,
            patch.DataType, patch.NewValue, ct);
        if (result.BytesWritten > 0)
            _undoStack.Add(patch with { OriginalBytes = currentBytes });

        return result.BytesWritten > 0
            ? $"Redone write at 0x{patch.Address:X}: wrote '{patch.NewValue}'."
            : $"Redo failed at 0x{patch.Address:X}.";
    }

    /// <summary>Get recent patch history for display.</summary>
    public IReadOnlyList<Patch> GetHistory(int count = 20) =>
        _undoStack.Skip(Math.Max(0, _undoStack.Count - count)).ToList().AsReadOnly();

    private async Task WriteRawBytesAsync(int processId, nuint address, byte[] data, CancellationToken ct)
    {
        // Write raw bytes by converting to appropriate type writes
        // For simplicity, write as individual bytes
        for (int i = 0; i < data.Length; i++)
        {
            await engineFacade.WriteValueAsync(processId, address + (nuint)i,
                MemoryDataType.Byte, data[i].ToString(), ct);
        }
    }

    private static int GetDataTypeSize(MemoryDataType type) => type switch
    {
        MemoryDataType.Byte => 1,
        MemoryDataType.Int16 => 2,
        MemoryDataType.Int32 => 4,
        MemoryDataType.Int64 => 8,
        MemoryDataType.Float => 4,
        MemoryDataType.Double => 8,
        MemoryDataType.Pointer => 8,
        _ => 4
    };
}
