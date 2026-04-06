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
/// Each mode has a different detection surface for anti-cheat/anti-debug systems.
/// </summary>
public enum BreakpointMode
{
    /// <summary>Engine picks the least intrusive mode that works for the request.</summary>
    Auto,

    /// <summary>
    /// Code cave detour — JMP hook into allocated memory. No debugger attachment required.
    /// Best for execute monitoring. Cannot monitor data reads/writes.
    /// <para><b>Detection surface:</b> Code integrity scan (CRC checks on .text section will detect
    /// the overwritten bytes). VirtualQuery on the cave page shows PAGE_EXECUTE_READ allocation
    /// not backed by a module. Memory scanning for JMP/detour patterns.</para>
    /// <para><b>Mitigations:</b> Cave allocated within module range, page protection downgraded
    /// to PAGE_EXECUTE_READ after write (7A). No IsDebuggerPresent detection.</para>
    /// </summary>
    Stealth,

    /// <summary>
    /// PAGE_GUARD protection flag — catches memory access via guard page faults.
    /// Requires debugger but avoids hardware debug registers. Good for data access monitoring.
    /// <para><b>Detection surface:</b> IsDebuggerPresent / CheckRemoteDebuggerPresent returns true
    /// (debugger is attached). VirtualQuery on the guarded page shows PAGE_GUARD flag set.
    /// NtQueryInformationProcess(ProcessDebugPort) returns non-zero.</para>
    /// <para><b>Mitigations:</b> PAGE_GUARD is a normal OS mechanism and may not be flagged by
    /// heuristic detectors. Thread names removed (7B).</para>
    /// </summary>
    PageGuard,

    /// <summary>
    /// Hardware debug registers DR0-DR3. Requires thread suspension to set context.
    /// Limited to 4 simultaneous breakpoints. Can monitor execute/read/write.
    /// <para><b>Detection surface:</b> GetThreadContext reveals non-zero DR0-DR3/DR7 values.
    /// IsDebuggerPresent returns true (debugger attached). Some anti-cheats periodically
    /// read DR registers to detect hardware breakpoints.</para>
    /// <para><b>Mitigations:</b> Context is only modified during thread suspension windows.
    /// Thread names removed (7B). DR6 cleared after each hit to reduce fingerprint.</para>
    /// </summary>
    Hardware,

    /// <summary>
    /// Software INT3 (0xCC) byte patch. Most intrusive, most compatible.
    /// Only supports execute breakpoints.
    /// <para><b>Detection surface:</b> Code integrity scan detects the 0xCC byte modification.
    /// IsDebuggerPresent returns true. Memory CRC checks on code sections will fail.
    /// This is the most easily detected mode.</para>
    /// <para><b>Mitigations:</b> Instruction boundary validation (2C) prevents accidental
    /// corruption. Original byte is tracked for clean restoration.</para>
    /// </summary>
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
    BreakpointMode Mode = BreakpointMode.Hardware,
    BreakpointCondition? Condition = null,
    int? ThreadFilter = null);

public sealed record BreakpointHitEvent(
    string BreakpointId,
    nuint Address,
    int ThreadId,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> RegisterSnapshot);


/// <summary>Associates a Lua callback function with a breakpoint.</summary>
public sealed record BreakpointLuaCallback(string FunctionName);

// ─── Conditional Breakpoints ─────────────────────────────────────────

public enum BreakpointConditionType
{
    RegisterCompare,
    MemoryCompare,
    HitCount
}

public sealed record BreakpointCondition(
    string Expression,
    BreakpointConditionType Type);

// ─── Break-and-Trace ────────────────────────────────────────────────

public sealed record TraceEntry(
    nuint InstructionAddress,
    string Disassembly,
    int ThreadId,
    IReadOnlyDictionary<string, string> RegisterSnapshot,
    DateTimeOffset TimestampUtc);

public sealed record TraceResult(
    string BreakpointId,
    IReadOnlyList<TraceEntry> Entries,
    bool MaxDepthReached,
    bool WasTruncated);

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

    /// <summary>Set a breakpoint with a condition expression and optional thread filter.</summary>
    Task<BreakpointDescriptor> SetConditionalBreakpointAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointCondition condition,
        BreakpointMode mode = BreakpointMode.Auto,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        int? threadFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Trace execution from a breakpoint address, recording each instruction.</summary>
    Task<TraceResult> TraceFromBreakpointAsync(
        int processId,
        nuint address,
        int maxInstructions = 500,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default);

    /// <summary>Emergency: restore all page guard protections without locks. For crash recovery.</summary>
    Task<int> EmergencyRestorePageProtectionAsync(int processId);

    /// <summary>Force detach debugger and clean up. Nuclear option for hung processes.</summary>
    Task ForceDetachAndCleanupAsync(int processId);
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
