namespace CEAISuite.Engine.Abstractions;

/// <summary>
/// Vectored Exception Handler debugger — sets hardware breakpoints (DR0-DR3)
/// via an injected agent DLL that intercepts EXCEPTION_SINGLE_STEP without
/// DebugActiveProcess attachment, bypassing anti-debug checks.
/// </summary>
public interface IVehDebugger
{
    /// <summary>Inject the VEH agent DLL into the target process. Creates shared memory and events.</summary>
    Task<VehInjectResult> InjectAsync(int processId, CancellationToken ct = default);

    /// <summary>Eject the VEH agent. Sends shutdown command, waits for cleanup, frees resources.</summary>
    Task<bool> EjectAsync(int processId, CancellationToken ct = default);

    /// <summary>Set a hardware breakpoint via the injected agent (DR0-DR3, max 4 simultaneous).</summary>
    Task<VehBreakpointResult> SetBreakpointAsync(int processId, nuint address, VehBreakpointType type,
        int dataSize = 8, BreakpointCondition? condition = null, CancellationToken ct = default);

    /// <summary>Remove a hardware breakpoint by DR slot index (0-3).</summary>
    Task<bool> RemoveBreakpointAsync(int processId, int drSlot, CancellationToken ct = default);

    /// <summary>Re-enumerate threads and apply active breakpoints to any threads missing them.</summary>
    Task<bool> RefreshThreadsAsync(int processId, CancellationToken ct = default);

    /// <summary>Register a Lua callback function to be invoked on breakpoint hits for a specific DR slot.</summary>
    void RegisterLuaCallback(int processId, int drSlot, string luaFunctionName);

    /// <summary>Unregister a Lua callback from a DR slot.</summary>
    void UnregisterLuaCallback(int processId, int drSlot);

    /// <summary>Start dynamic tracing: single-step from the next hardware BP hit, collecting register state at each instruction.</summary>
    Task<VehTraceResult> TraceFromBreakpointAsync(int processId, int maxSteps = 500, int threadFilter = 0, CancellationToken ct = default);

    /// <summary>Stop an active trace.</summary>
    Task<bool> StopTraceAsync(int processId, CancellationToken ct = default);

    /// <summary>Enable stealth mode: hook NtGetThreadContext to cloak DR registers, hide agent DLL from PEB.</summary>
    Task<bool> EnableStealthAsync(int processId, CancellationToken ct = default);

    /// <summary>Disable stealth mode: restore NtGetThreadContext, re-link agent DLL in PEB.</summary>
    Task<bool> DisableStealthAsync(int processId, CancellationToken ct = default);

    /// <summary>Stream breakpoint hit events from the shared memory ring buffer.</summary>
    IAsyncEnumerable<VehHitEvent> GetHitStreamAsync(int processId, CancellationToken ct = default);

    /// <summary>Get the current VEH debugger status for a process.</summary>
    VehStatus GetStatus(int processId);
}

/// <summary>Hardware breakpoint type matching DR7 R/W encoding.</summary>
public enum VehBreakpointType
{
    /// <summary>Break on instruction execution (DR7 R/W = 00).</summary>
    Execute = 0,
    /// <summary>Break on data write (DR7 R/W = 01).</summary>
    Write = 1,
    /// <summary>Break on data read or write (DR7 R/W = 11).</summary>
    ReadWrite = 2,
    /// <summary>Dynamic trace step (Trap Flag single-step).</summary>
    Trace = 3
}

/// <summary>Stealth mode state of the VEH agent.</summary>
public enum VehStealthMode
{
    /// <summary>Stealth is not active — DR registers visible, agent DLL in module list.</summary>
    None,
    /// <summary>Stealth is active — DR registers cloaked, agent DLL hidden from PEB.</summary>
    Active
}

/// <summary>Health status of the VEH agent running inside the target process.</summary>
public enum VehAgentHealth
{
    /// <summary>Agent heartbeat is current (updated within last 2 seconds).</summary>
    Healthy,
    /// <summary>Agent heartbeat is stale (not updated for >2 seconds).</summary>
    Unresponsive,
    /// <summary>Agent is not injected or shared memory is invalid.</summary>
    Unknown
}

/// <summary>Result of VEH agent injection.</summary>
public sealed record VehInjectResult(bool Success, string? Error = null);

/// <summary>Result of setting a VEH hardware breakpoint.</summary>
public sealed record VehBreakpointResult(bool Success, int DrSlot = -1, string? Error = null);

/// <summary>A breakpoint hit event captured by the VEH agent.</summary>
public sealed record VehHitEvent(
    nuint Address,
    int ThreadId,
    VehBreakpointType Type,
    nuint Dr6,
    RegisterSnapshot Registers,
    long Timestamp);

/// <summary>Result of a dynamic trace operation.</summary>
public sealed record VehTraceResult(
    bool Success,
    IReadOnlyList<VehTraceEntry> Entries,
    bool Truncated = false,
    string? Error = null);

/// <summary>A single instruction in a dynamic trace.</summary>
public sealed record VehTraceEntry(
    nuint Address,
    int ThreadId,
    RegisterSnapshot Registers,
    string? Disassembly = null);

/// <summary>Register snapshot from the exception context.</summary>
public sealed record RegisterSnapshot(
    ulong Rax, ulong Rbx, ulong Rcx, ulong Rdx,
    ulong Rsi, ulong Rdi, ulong Rsp, ulong Rbp,
    ulong R8, ulong R9, ulong R10, ulong R11);

/// <summary>Current status of the VEH debugger for a process.</summary>
public sealed record VehStatus(
    bool IsInjected,
    int ActiveBreakpoints,
    int TotalHits,
    int OverflowCount = 0,
    VehAgentHealth AgentHealth = VehAgentHealth.Unknown,
    VehStealthMode StealthMode = VehStealthMode.None);
