namespace CEAISuite.Engine.Abstractions;

public enum BreakpointType
{
    Software,
    HardwareExecute,
    HardwareWrite,
    HardwareReadWrite
}

public enum BreakpointHitAction
{
    Break,
    Log,
    LogAndContinue
}

public sealed record BreakpointDescriptor(
    string Id,
    nuint Address,
    BreakpointType Type,
    BreakpointHitAction HitAction,
    bool IsEnabled,
    int HitCount);

public sealed record BreakpointHitEvent(
    string BreakpointId,
    nuint Address,
    int ThreadId,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> RegisterSnapshot);

public sealed record AccessTraceEntry(
    nuint InstructionAddress,
    nuint TargetAddress,
    string AccessType,
    int ThreadId,
    DateTimeOffset TimestampUtc);

public interface IBreakpointEngine
{
    Task<BreakpointDescriptor> SetBreakpointAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveBreakpointAsync(
        int processId,
        string breakpointId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BreakpointDescriptor>> ListBreakpointsAsync(
        int processId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BreakpointHitEvent>> GetHitLogAsync(
        string breakpointId,
        int maxEntries = 50,
        CancellationToken cancellationToken = default);
}
