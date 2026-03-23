using System.Windows.Media;

namespace CEAISuite.Desktop.Models;

/// <summary>Display model for AI chat messages in the ItemsControl.</summary>
public sealed class AiChatDisplayItem
{
    public string RoleLabel { get; init; } = "";
    public string Content { get; set; } = "";
    public string Timestamp { get; init; } = "";
    public Brush Background { get; init; } = Brushes.Transparent;
}

/// <summary>Display model for process selection in the command bar ComboBox.</summary>
public sealed class ProcessComboItem
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Label => $"{Name} (PID {Pid})";
    public override string ToString() => Label;
}

/// <summary>Display model for chat history list items.</summary>
public sealed class ChatHistoryDisplayItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string TimeAgo { get; init; } = "";
    public string Preview { get; init; } = "";
    public bool IsCurrent { get; init; }
}

/// <summary>Display model for attachment chips in the chat input.</summary>
public sealed class AttachmentChip
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = "Pasted";
    public string Preview { get; init; } = "";
    public string FullText { get; init; } = "";
}

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
