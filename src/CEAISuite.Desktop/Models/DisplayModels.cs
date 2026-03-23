namespace CEAISuite.Desktop.Models;

public sealed class OutputLogEntry
{
    public string Timestamp { get; init; } = "";
    public string Source { get; init; } = "";
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class BreakpointDisplayItem
{
    public string Id { get; init; } = "";
    public string Address { get; init; } = "";
    public string Type { get; init; } = "";
    public string Mode { get; init; } = "";
    public int HitCount { get; init; }
    public string Status { get; init; } = "";
}

public sealed class HitLogDisplayItem
{
    public string BreakpointId { get; init; } = "";
    public string Address { get; init; } = "";
    public int ThreadId { get; init; }
    public string Timestamp { get; init; } = "";
}

public sealed class CodeCaveHookDisplayItem
{
    public string Id { get; init; } = "";
    public string OriginalAddress { get; init; } = "";
    public string CaveAddress { get; init; } = "";
    public bool IsActive { get; init; }
    public int HitCount { get; init; }
}

public sealed class ScriptDisplayItem
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string StatusText { get; init; } = "";
    public bool IsEnabled { get; init; }
}

public sealed class SnapshotDisplayItem
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Address { get; init; } = "";
    public string Size { get; init; } = "";
    public string CapturedAt { get; init; } = "";
}

public sealed class SnapshotDiffDisplayItem
{
    public string Offset { get; init; } = "";
    public string OldValue { get; init; } = "";
    public string NewValue { get; init; } = "";
    public string Interpretation { get; init; } = "";
}

public sealed class HotkeyDisplayItem
{
    public int Id { get; init; }
    public string KeyCombo { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class FindResultDisplayItem
{
    public string Address { get; init; } = "";
    public string Instruction { get; init; } = "";
    public string Module { get; init; } = "";
    public string Context { get; init; } = "";
}

public sealed class PatchHistoryDisplayItem
{
    public string Timestamp { get; init; } = "";
    public string Address { get; init; } = "";
    public string DataType { get; init; } = "";
    public string NewValue { get; init; } = "";
}

public sealed class JournalEntryDisplayItem
{
    public string OperationId { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public string OperationType { get; init; } = "";
    public string Address { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Status { get; init; } = "";
}
