namespace CEAISuite.Engine.Abstractions;

// ─── Breakpoint Types ────────────────────────────────────────────────

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

/// <summary>
/// Intrusiveness modes for breakpoints, ordered from least to most intrusive.
/// </summary>
public enum BreakpointMode
{
    /// <summary>Engine picks the least intrusive mode that works for the request.</summary>
    Auto,

    /// <summary>Code cave detour — JMP hook into allocated memory. No debugger attachment.
    /// Best for execute monitoring. Cannot monitor data reads/writes.</summary>
    Stealth,

    /// <summary>PAGE_GUARD protection flag — catches memory access via guard page faults.
    /// Requires debugger but avoids hardware debug registers. Good for data access monitoring.</summary>
    PageGuard,

    /// <summary>Hardware debug registers DR0-DR3. Requires thread suspension to set context.
    /// Limited to 4 simultaneous breakpoints. Can monitor execute/read/write.</summary>
    Hardware,

    /// <summary>Software INT3 byte patch. Most intrusive, most compatible.
    /// Only supports execute breakpoints.</summary>
    Software
}

// ─── Capability Flags ────────────────────────────────────────────────

/// <summary>Capability flags for a breakpoint mode.</summary>
public sealed record BreakpointModeCapabilities(
    BreakpointMode Mode,
    bool SupportsExecuteHook,
    bool SupportsDataWriteWatch,
    bool RequiresDebugger,
    bool UsesPageProtection,
    bool UsesThreadSuspend,
    string StabilityTier,
    string Description);

// ─── Descriptors ─────────────────────────────────────────────────────

public sealed record BreakpointDescriptor(
    string Id,
    nuint Address,
    BreakpointType Type,
    BreakpointHitAction HitAction,
    bool IsEnabled,
    int HitCount,
    BreakpointMode Mode = BreakpointMode.Hardware);

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

// ─── Breakpoint Lifecycle ─────────────────────────────────────────────

public enum BreakpointLifecycleStatus
{
    Armed,
    Active,
    Downgraded,
    ThrottleDisabled,
    SingleHitRemoved,
    Faulted,
    ManuallyDisabled
}

// ─── Code Cave Types ─────────────────────────────────────────────────

/// <summary>
/// Describes an installed code cave (detour) hook — a JMP-based redirection
/// that requires no debugger attachment.
/// </summary>
public sealed record CodeCaveHook(
    string Id,
    nuint OriginalAddress,
    nuint CaveAddress,
    int OriginalBytesLength,
    bool IsActive,
    int HitCount);

/// <summary>Result of a code cave hook installation attempt.</summary>
public sealed record CodeCaveInstallResult(
    bool Success,
    CodeCaveHook? Hook,
    string? ErrorMessage);

// ─── Breakpoint Engine ───────────────────────────────────────────────

public interface IBreakpointEngine
{
    /// <summary>Set a breakpoint using the default (Hardware) mode.</summary>
    Task<BreakpointDescriptor> SetBreakpointAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        CancellationToken cancellationToken = default);

    /// <summary>Set a breakpoint with explicit mode selection.</summary>
    Task<BreakpointDescriptor> SetBreakpointAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointMode mode,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        bool singleHit = false,
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

// ─── Code Cave Engine ────────────────────────────────────────────────

/// <summary>
/// Installs JMP-detour hooks ("code caves") that redirect execution through
/// allocated trampolines. No debugger attachment required — the stealthiest
/// form of execution monitoring.
/// </summary>
public interface ICodeCaveEngine
{
    /// <summary>
    /// Install a code cave hook at the given address. Overwrites the first N bytes
    /// with a JMP to allocated executable memory containing: register snapshot →
    /// hit counter increment → original bytes → JMP back.
    /// </summary>
    Task<CodeCaveInstallResult> InstallHookAsync(
        int processId,
        nuint address,
        bool captureRegisters = true,
        CancellationToken cancellationToken = default);

    /// <summary>Remove a code cave hook, restoring original bytes.</summary>
    Task<bool> RemoveHookAsync(
        int processId,
        string hookId,
        CancellationToken cancellationToken = default);

    /// <summary>List all active code cave hooks for a process.</summary>
    Task<IReadOnlyList<CodeCaveHook>> ListHooksAsync(
        int processId,
        CancellationToken cancellationToken = default);

    /// <summary>Get the hit count and register snapshots from a code cave hook.</summary>
    Task<IReadOnlyList<BreakpointHitEvent>> GetHookHitsAsync(
        string hookId,
        int maxEntries = 50,
        CancellationToken cancellationToken = default);
}
