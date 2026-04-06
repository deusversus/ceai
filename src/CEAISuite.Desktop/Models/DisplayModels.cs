using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CEAISuite.Desktop.Models;

/// <summary>Display model for AI chat messages in the ItemsControl (used for history).</summary>
public sealed class AiChatDisplayItem
{
    public string RoleLabel { get; init; } = "";
    public string Content { get; set; } = "";
    public string Timestamp { get; init; } = "";
    public Brush Background { get; init; } = Brushes.Transparent;

    /// <summary>Optional image data for messages that included an image attachment.</summary>
    public byte[]? ImageData { get; init; }

    private BitmapImage? _imageSource;
    /// <summary>Lazily-created BitmapImage for XAML binding. Null if no image.</summary>
    public BitmapImage? ImageSource
    {
        get
        {
            if (_imageSource is not null || ImageData is null) return _imageSource;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(ImageData);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelHeight = 200; // thumbnail size
                bmp.EndInit();
                bmp.Freeze();
                _imageSource = bmp;
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[AiChatDisplayItem] Corrupt image data: {ex.Message}"); }
            return _imageSource;
        }
    }

    public bool HasImage => ImageData is not null;
}

// ── Structured content blocks for live streaming display ──

/// <summary>Base type for chat display content blocks (OpenCode-style part model).</summary>
public abstract partial class ChatContentBlock : ObservableObject
{
    public string Timestamp { get; init; } = "";
}

/// <summary>Text content from user or assistant.</summary>
public sealed partial class TextContentBlock : ChatContentBlock
{
    [ObservableProperty]
    private string _content = "";

    public string RoleLabel { get; init; } = "";
    public Brush Background { get; init; } = Brushes.Transparent;
}

/// <summary>Tool call with execution state and collapsible result.</summary>
public sealed partial class ToolCallBlock : ChatContentBlock
{
    public string ToolName { get; init; } = "";
    public string Arguments { get; init; } = "";

    [ObservableProperty]
    private string _status = "running";  // running, completed, error

    [ObservableProperty]
    private string? _result;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Icon based on tool type.</summary>
    public string Icon => ToolName switch
    {
        "ReadMemory" or "BrowseMemory" or "HexDump" => "📖",
        "WriteMemory" => "✏️",
        "StartScan" or "RefineScan" or "GetScanResults" => "🔍",
        "Disassemble" or "FindWritersToOffset" => "🔬",
        "SetBreakpoint" or "RemoveBreakpoint" => "🛑",
        "InstallCodeCaveHook" => "🪝",
        "ListProcesses" or "AttachProcess" or "InspectProcess" => "📋",
        _ => "🔧"
    };
}

/// <summary>Tool approval request with inline resolution buttons.</summary>
public sealed partial class ApprovalBlock : ChatContentBlock
{
    public string ToolName { get; init; } = "";
    public string Arguments { get; init; } = "";

    [ObservableProperty]
    private string _status = "pending";  // pending, approved, denied

    /// <summary>Callback to resolve the approval. Set by the ViewModel.</summary>
    public Action<bool>? Resolve { get; set; }
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

    /// <summary>Raw image bytes (PNG/JPG). Null for text attachments.</summary>
    public byte[]? ImageData { get; init; }
    /// <summary>MIME type, e.g. "image/png". Null for text attachments.</summary>
    public string? MediaType { get; init; }
    /// <summary>True when this attachment contains an image.</summary>
    public bool IsImage => ImageData is not null;

    private BitmapImage? _thumbnail;
    /// <summary>Lazily-created thumbnail for chip display.</summary>
    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is not null || ImageData is null) return _thumbnail;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(ImageData);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelHeight = 32; // small chip thumbnail
                bmp.EndInit();
                bmp.Freeze();
                _thumbnail = bmp;
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[AttachmentChip] Corrupt image data: {ex.Message}"); }
            return _thumbnail;
        }
    }
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
    public string Condition { get; init; } = "";
    public string ThreadFilter { get; init; } = "";
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

// ── Phase 3 display models ──

public sealed partial class StructureFieldDisplayItem : ObservableObject
{
    public int Offset { get; init; }
    public string OffsetHex => $"0x{Offset:X3}";
    public string ProbableType { get; init; } = "";
    public string DisplayValue { get; init; } = "";
    public double Confidence { get; init; }
    public string ConfidencePercent => $"{Confidence * 100:F0}%";

    [ObservableProperty]
    private string _name = "";
}

public sealed class StructureCompareDisplayItem
{
    public string OffsetHex { get; init; } = "";
    public string Type { get; init; } = "";
    public string ValueA { get; init; } = "";
    public string ValueB { get; init; } = "";
    public bool IsDifferent { get; init; }
}

public sealed partial class PointerPathDisplayItem : ObservableObject
{
    public string Chain { get; init; } = "";
    public string ResolvedAddress { get; init; } = "";
    public string ModuleName { get; init; } = "";
    public CEAISuite.Application.PointerPath? Source { get; init; }

    /// <summary>Validation status: Found, Stable, Drifted, Broken.</summary>
    [ObservableProperty]
    private string _status = "Found";
}

public sealed partial class DisassemblyLineDisplayItem : ObservableObject
{
    public string Address { get; init; } = "";
    public string HexBytes { get; init; } = "";
    public string Mnemonic { get; init; } = "";
    public string Operands { get; init; } = "";
    public bool IsFunctionStart { get; init; }
    public bool IsCallOrJump { get; init; }
    public string? XrefLabel { get; init; }
    public string? ModuleOffset { get; init; }
    public string? SymbolName { get; init; }

    [ObservableProperty]
    private string _comment = "";

    [ObservableProperty]
    private string _label = "";
}

public sealed partial class RegisterDisplayItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";

    /// <summary>True when this register's value differs from the previous breakpoint hit.</summary>
    [ObservableProperty]
    private bool _isChanged;
}

public sealed class CallStackFrameDisplayItem
{
    public int FrameIndex { get; init; }
    public string InstructionPointer { get; init; } = "";
    public string ModuleOffset { get; init; } = "";
    public string ReturnAddress { get; init; } = "";
}

public sealed class BreakpointHitDetailItem
{
    public string BreakpointId { get; init; } = "";
    public string Address { get; init; } = "";
    public int ThreadId { get; init; }
    public string Timestamp { get; init; } = "";
    public IReadOnlyList<RegisterDisplayItem> Registers { get; init; } = [];
}

// ── Phase 7B: Trace display model ──

public sealed class TraceEntryDisplayItem
{
    public string Address { get; init; } = "";
    public string Disassembly { get; init; } = "";
    public int ThreadId { get; init; }
    public bool IsCallInstruction { get; init; }
    public bool IsRetInstruction { get; init; }
}

// ── Phase 4 display models ──

public sealed class ModuleDisplayItem
{
    public string Name { get; init; } = "";
    public string BaseAddress { get; init; } = "";
    public string Size { get; init; } = "";
    public string Path { get; init; } = "";
}

public sealed class ThreadDisplayItem
{
    public int ThreadId { get; init; }
    public string State { get; init; } = "Running";
    public string CurrentInstruction { get; init; } = "";
    public string Module { get; init; } = "";
}

public sealed class MemoryRegionDisplayItem
{
    public string BaseAddress { get; init; } = "";
    public string Size { get; init; } = "";
    public string Protection { get; init; } = "";
    public string OwnerModule { get; init; } = "";
    public bool IsReadable { get; init; }
    public bool IsWritable { get; init; }
    public bool IsExecutable { get; init; }
}

public sealed class SessionDisplayItem
{
    public string Id { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public int? ProcessId { get; init; }
    public string CreatedAt { get; init; } = "";
    public int AddressCount { get; init; }
    public int ActionCount { get; init; }
}
