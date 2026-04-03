namespace CEAISuite.Desktop.Models;

/// <summary>
/// Records a single hex edit operation for undo/redo.
/// Stores both old and new bytes at the given address.
/// </summary>
public sealed record HexEditOperation(
    ulong Address,
    byte[] OldBytes,
    byte[] NewBytes);
