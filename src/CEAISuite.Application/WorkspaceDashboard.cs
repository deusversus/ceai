using CEAISuite.Domain;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed record RunningProcessOverview(int Id, string Name, string Architecture);

public sealed record ModuleOverview(string Name, string BaseAddress, string Size);

public sealed record MemorySampleOverview(
    string Address,
    string HexBytes,
    string Int32Value,
    string PointerValue);

public sealed record ManualMemoryProbeOverview(
    string Address,
    string DataType,
    string DisplayValue,
    string HexBytes);

public sealed record ProcessInspectionOverview(
    int ProcessId,
    string ProcessName,
    string Architecture,
    IReadOnlyList<ModuleOverview> Modules,
    MemorySampleOverview? Sample,
    ManualMemoryProbeOverview? ManualProbe,
    string? LastWriteMessage,
    string StatusMessage);

public sealed record RecentSessionOverview(
    string Id,
    string ProcessName,
    int? ProcessId,
    DateTimeOffset CreatedAtUtc,
    int AddressEntryCount,
    int ScanSessionCount,
    int ActionLogCount);

public sealed record WorkspaceDashboard(
    WorkspaceOverview Overview,
    IReadOnlyList<RunningProcessOverview> RunningProcesses,
    IReadOnlyList<RecentSessionOverview> RecentSessions,
    ProcessInspectionOverview? CurrentInspection,
    IReadOnlyList<ScanResultOverview>? ScanResults,
    string? ScanStatus,
    string? ScanDetails,
    IReadOnlyList<AddressTableEntry>? AddressTableEntries,
    System.Collections.ObjectModel.ObservableCollection<AddressTableNode>? AddressTableNodes,
    string? AddressTableStatus,
    DisassemblyOverview? Disassembly,
    IReadOnlyList<AiChatMessage>? AiChatHistory,
    IReadOnlyList<AiActionLogEntry>? AiActionLog,
    bool AiConfigured,
    string? BreakpointStatus,
    string DataStorePath,
    string StatusMessage)
{
    public string ProductName => Overview.ProductName;

    public string Summary => Overview.Summary;

    public IReadOnlyList<LayerOverview> Layers => Overview.Layers;

    public IReadOnlyList<string> Milestones => Overview.Milestones;

    public IReadOnlyList<ToolOverview> Tooling => Overview.Tooling;

    public IReadOnlyList<EngineCapability> EngineCapabilities => Overview.EngineCapabilities;

    public ProjectProfile DefaultProfile => Overview.DefaultProfile;

    public static WorkspaceDashboard CreateLoading() =>
        new(
            WorkspaceBootstrap.CreateOverview(),
            Array.Empty<RunningProcessOverview>(),
            Array.Empty<RecentSessionOverview>(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            string.Empty,
            "Loading workspace services...");
}
