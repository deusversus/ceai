namespace CEAISuite.Engine.Abstractions;

// ─── Stepping State ────────────────────────────────────────────────

/// <summary>
/// Current state of the interactive stepping debugger.
/// </summary>
public enum SteppingState
{
    /// <summary>No active stepping session. Target is either running freely or not attached.</summary>
    Idle,

    /// <summary>A step operation is in progress (waiting for single-step completion).</summary>
    Stepping,

    /// <summary>Target is running freely after a Continue command. Waiting for next BP hit.</summary>
    Running,

    /// <summary>Target is suspended at a breakpoint or after a completed step. Ready for next command.</summary>
    Suspended
}

/// <summary>
/// Reason the target stopped after a stepping operation.
/// </summary>
public enum StoppedReason
{
    /// <summary>Single step completed normally.</summary>
    StepComplete,

    /// <summary>A breakpoint was hit (during step-over, step-out, or continue).</summary>
    BreakpointHit,

    /// <summary>Target process exited during the step.</summary>
    ProcessExited,

    /// <summary>Step operation timed out waiting for completion.</summary>
    Timeout,

    /// <summary>An error occurred during the step operation.</summary>
    Error
}

// ─── Step Result ───────────────────────────────────────────────────

/// <summary>
/// Result of a single stepping operation (step-in, step-over, step-out, or continue).
/// </summary>
public sealed record StepResult(
    bool Success,
    nuint NewRip,
    RegisterSnapshot? Registers,
    int ThreadId,
    StoppedReason Reason,
    string? Disassembly = null,
    string? Error = null);

// ─── Stepping Engine Interface ─────────────────────────────────────

/// <summary>
/// Interactive instruction-level stepping engine. Wraps VEH Trap Flag
/// single-stepping infrastructure for interactive debugger use.
/// <para>
/// Step-in: execute one instruction via TF single-step.
/// Step-over: if current instruction is CALL, set temp BP at next instruction and continue; else step-in.
/// Step-out: read return address from [RSP], set temp BP there, and continue.
/// Continue: resume target execution until next BP hit or user interrupt.
/// </para>
/// </summary>
public interface ISteppingEngine
{
    /// <summary>
    /// Execute a single instruction and return the new state.
    /// Uses VEH Trap Flag (CMD_START_TRACE with maxSteps=1).
    /// </summary>
    Task<StepResult> StepInAsync(
        int processId,
        int threadId = 0,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Step over the current instruction. If it's a CALL, sets a temporary
    /// breakpoint at the next instruction and continues execution until that
    /// BP is hit. Otherwise, performs a step-in.
    /// </summary>
    Task<StepResult> StepOverAsync(
        int processId,
        int threadId = 0,
        int timeoutMs = 10000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Step out of the current function. Reads the return address from [RSP],
    /// sets a temporary breakpoint there, and continues until it's hit.
    /// </summary>
    Task<StepResult> StepOutAsync(
        int processId,
        int threadId = 0,
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume target execution. Returns when the next breakpoint is hit,
    /// or when cancelled.
    /// </summary>
    Task<StepResult> ContinueAsync(
        int processId,
        CancellationToken cancellationToken = default);

    /// <summary>Get the current stepping state for a process.</summary>
    SteppingState GetState(int processId);

    /// <summary>
    /// Get the current instruction pointer and register state while suspended.
    /// Returns null if the target is not suspended.
    /// </summary>
    Task<StepResult?> GetCurrentStateAsync(
        int processId,
        int threadId = 0,
        CancellationToken cancellationToken = default);
}
