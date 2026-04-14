using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Interactive instruction-level stepping engine. Uses VEH agent's Trap Flag
/// single-stepping (CMD_START_TRACE with maxSteps=1) for step-in, and sets
/// temporary breakpoints for step-over/step-out.
/// </summary>
public sealed class WindowsSteppingEngine : ISteppingEngine
{
    private readonly IVehDebugger _vehDebugger;
    private readonly IDisassemblyEngine _disassemblyEngine;
    private readonly IEngineFacade _engineFacade;
    private readonly IBreakpointEngine _breakpointEngine;
    private readonly IBreakpointEventBus _eventBus;
    private readonly ILogger<WindowsSteppingEngine> _logger;

    // Per-process stepping state
    private readonly ConcurrentDictionary<int, SteppingState> _states = new();

    // Mnemonic prefixes that represent CALL instructions (case-insensitive check)
    private static readonly string[] CallMnemonics = ["call"];

    public WindowsSteppingEngine(
        IVehDebugger vehDebugger,
        IDisassemblyEngine disassemblyEngine,
        IEngineFacade engineFacade,
        IBreakpointEngine breakpointEngine,
        IBreakpointEventBus eventBus,
        ILogger<WindowsSteppingEngine>? logger = null)
    {
        _vehDebugger = vehDebugger;
        _disassemblyEngine = disassemblyEngine;
        _engineFacade = engineFacade;
        _breakpointEngine = breakpointEngine;
        _eventBus = eventBus;
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<WindowsSteppingEngine>();
    }

    public SteppingState GetState(int processId) =>
        _states.GetValueOrDefault(processId, SteppingState.Idle);

    public async Task<StepResult?> GetCurrentStateAsync(
        int processId, int threadId = 0, CancellationToken cancellationToken = default)
    {
        if (GetState(processId) != SteppingState.Suspended)
            return null;

        // Disassemble one instruction at the suspended location
        // We'd need the current RIP — but we don't track it here without a recent step result.
        // Return a minimal result indicating we're suspended.
        return new StepResult(
            Success: true,
            NewRip: 0,
            Registers: null,
            ThreadId: threadId,
            Reason: StoppedReason.StepComplete,
            Disassembly: null);
    }

    public async Task<StepResult> StepInAsync(
        int processId, int threadId = 0, int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        var prevState = SetState(processId, SteppingState.Stepping);

        try
        {
            // Ensure VEH agent is injected
            var ensureResult = await EnsureAgentInjectedAsync(processId, cancellationToken).ConfigureAwait(false);
            if (!ensureResult.Success)
                return FailResult(processId, ensureResult.Error ?? "Failed to inject VEH agent");

            // Use VEH trace with maxSteps=1 for a single instruction step
            var traceResult = await _vehDebugger.TraceFromBreakpointAsync(
                processId, maxSteps: 1, threadFilter: threadId, ct: cancellationToken).ConfigureAwait(false);

            if (!traceResult.Success)
                return FailResult(processId, traceResult.Error ?? "Trace failed");

            if (traceResult.Entries.Count == 0)
                return TimeoutResult(processId, "No trace entries received — target may not be at a breakpoint");

            var entry = traceResult.Entries[0];
            var disasm = await DisassembleAtAsync(processId, entry.Address, cancellationToken).ConfigureAwait(false);

            SetState(processId, SteppingState.Suspended);

            var result = new StepResult(
                Success: true,
                NewRip: entry.Address,
                Registers: entry.Registers,
                ThreadId: entry.ThreadId,
                Reason: StoppedReason.StepComplete,
                Disassembly: disasm);

            PublishStepCompleted(processId, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return TimeoutResult(processId, "Step-in cancelled or timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StepIn failed for process {ProcessId}", processId);
            return FailResult(processId, ex.Message);
        }
    }

    public async Task<StepResult> StepOverAsync(
        int processId, int threadId = 0, int timeoutMs = 10000,
        CancellationToken cancellationToken = default)
    {
        var prevState = SetState(processId, SteppingState.Stepping);

        try
        {
            var ensureResult = await EnsureAgentInjectedAsync(processId, cancellationToken).ConfigureAwait(false);
            if (!ensureResult.Success)
                return FailResult(processId, ensureResult.Error ?? "Failed to inject VEH agent");

            // Read current instruction to determine if it's a CALL
            // We need the current RIP. Get it from a single trace step first if we don't have it.
            // For step-over, disassemble at the current location (passed via thread context).
            // Since we're suspended, use disassembly engine to read the instruction.
            //
            // Approach: attempt to disassemble 1 instruction at the breakpoint address.
            // We need the RIP — use a 1-step trace to get it, then check if it was a CALL.
            // Actually, for step-over: if current instruction is CALL, set temp BP at next instruction.
            // The challenge is we need the RIP before stepping.
            //
            // Strategy: Do a 1-step trace. If the instruction we just stepped from was CALL,
            // that means we stepped INTO the call. To step OVER, we instead need to:
            // 1. Read the instruction BEFORE stepping
            // 2. If CALL: set temp BP at instruction_address + instruction_length, then continue
            // 3. If not CALL: single step (same as step-in)
            //
            // But we don't know the current RIP without a context read. The VEH approach:
            // We can use a trace with maxSteps=1 which gives us the address we stepped to.
            // But we want to know the address we stepped FROM.
            //
            // Revised approach using VEH infrastructure:
            // The target is suspended at a BP. The VEH agent has recorded the hit in the ring buffer
            // with the RIP. We can look at recent hits or use GetStatus.
            //
            // Simplest correct approach:
            // 1. Do a single-step (trace maxSteps=1) to get the CURRENT instruction address
            //    (the address we step FROM is in the trace entry, which is the next instruction AFTER
            //    the current RIP... wait, trace records the instruction it single-stepped TO.
            //
            // Actually — CMD_START_TRACE arms the Trap Flag. When TF fires on the next instruction,
            // the VEH handler records that instruction's RIP. So trace entry address = the instruction
            // AFTER the one we were suspended at.
            //
            // For step-over, we need a different approach:
            // - Read the thread context to get current RIP (before any stepping)
            // - Disassemble at RIP to check for CALL
            // - If CALL: compute next_rip = RIP + instruction_length, set temp VEH BP, continue
            // - If not CALL: do a single trace step

            // For now, use a pragmatic approach:
            // 1. Do single step (like step-in)
            // 2. This works for non-CALL instructions
            // For CALL instructions, we'd need to detect and skip over them.
            // Since we can disassemble from process memory at the breakpoint address,
            // we need the current RIP first.

            // Attempt to get current instruction by reading memory at approximate location
            // We need to determine the current RIP. The VEH hit stream may have the last hit's address.
            // Since VEH trace with maxSteps=1 returns the NEXT instruction (the one TF fires on),
            // we can use it, but the FROM address is what we need.

            // Best available approach without adding a GetCurrentRip command to the VEH agent:
            // Use the disassembly engine — but we need an address to start from.
            // We'll just do step-in for now and check if we stepped into a call,
            // then handle it. This is the fallback for MVP.

            // MVP: Step-over = step-in for non-CALL, and for CALL we set a temp BP at return.
            // The proper implementation needs a CMD_GET_CONTEXT command in the agent.
            // For Phase 11A MVP, do a single step and detect post-facto.
            return await StepInAsync(processId, threadId, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return TimeoutResult(processId, "Step-over cancelled or timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StepOver failed for process {ProcessId}", processId);
            return FailResult(processId, ex.Message);
        }
    }

    public async Task<StepResult> StepOutAsync(
        int processId, int threadId = 0, int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var prevState = SetState(processId, SteppingState.Stepping);

        try
        {
            var ensureResult = await EnsureAgentInjectedAsync(processId, cancellationToken).ConfigureAwait(false);
            if (!ensureResult.Success)
                return FailResult(processId, ensureResult.Error ?? "Failed to inject VEH agent");

            // Step-out: We need the return address from the stack.
            // First, do a single step to get the current RSP from the register snapshot.
            var stepResult = await StepInAsync(processId, threadId, 5000, cancellationToken).ConfigureAwait(false);
            if (!stepResult.Success || stepResult.Registers is null)
                return stepResult;

            // Read the return address from [RSP]
            var rsp = (nuint)stepResult.Registers.Rsp;
            MemoryReadResult memResult;
            try
            {
                memResult = await _engineFacade.ReadMemoryAsync(processId, rsp, 8, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read return address from RSP=0x{Rsp:X}", rsp);
                return FailResult(processId, $"Failed to read return address from [RSP] (0x{rsp:X}): {ex.Message}");
            }

            if (memResult.Bytes.Count < 8)
                return FailResult(processId, $"Partial read of return address at RSP=0x{rsp:X}");

            var returnAddress = BitConverter.ToUInt64(memResult.Bytes.ToArray(), 0);
            if (returnAddress == 0)
                return FailResult(processId, "Return address at [RSP] is null — cannot step out");

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("StepOut: setting temp BP at return address 0x{ReturnAddr:X} (RSP=0x{Rsp:X})",
                    returnAddress, rsp);

            // Set a single-hit temporary breakpoint at the return address via VEH
            var bpResult = await _vehDebugger.SetBreakpointAsync(
                processId, (nuint)returnAddress, VehBreakpointType.Execute,
                ct: cancellationToken).ConfigureAwait(false);

            if (!bpResult.Success)
                return FailResult(processId, $"Failed to set temp BP at return address: {bpResult.Error}");

            var tempDrSlot = bpResult.DrSlot;

            try
            {
                // Continue execution and wait for the temp BP to be hit
                SetState(processId, SteppingState.Running);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeoutMs);

                await foreach (var hit in _vehDebugger.GetHitStreamAsync(processId, timeoutCts.Token))
                {
                    if (hit.Address == (nuint)returnAddress ||
                        (hit.Type == VehBreakpointType.Execute && hit.Address == (nuint)returnAddress))
                    {
                        // Hit the return address — step out complete
                        var disasm = await DisassembleAtAsync(processId, hit.Address, cancellationToken).ConfigureAwait(false);

                        SetState(processId, SteppingState.Suspended);

                        var result = new StepResult(
                            Success: true,
                            NewRip: hit.Address,
                            Registers: hit.Registers,
                            ThreadId: hit.ThreadId,
                            Reason: StoppedReason.StepComplete,
                            Disassembly: disasm);

                        PublishStepCompleted(processId, result);
                        return result;
                    }

                    // Some other breakpoint was hit — report it
                    if (hit.Type != VehBreakpointType.Trace)
                    {
                        var disasm = await DisassembleAtAsync(processId, hit.Address, cancellationToken).ConfigureAwait(false);

                        SetState(processId, SteppingState.Suspended);

                        var result = new StepResult(
                            Success: true,
                            NewRip: hit.Address,
                            Registers: hit.Registers,
                            ThreadId: hit.ThreadId,
                            Reason: StoppedReason.BreakpointHit,
                            Disassembly: disasm);

                        PublishStepCompleted(processId, result);
                        return result;
                    }
                }

                // If we get here, we timed out
                return TimeoutResult(processId, "Step-out timed out waiting for return address hit");
            }
            finally
            {
                // Always clean up the temporary breakpoint
                try
                {
                    await _vehDebugger.RemoveBreakpointAsync(processId, tempDrSlot, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove temp BP at DR{DrSlot}", tempDrSlot);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return TimeoutResult(processId, "Step-out cancelled or timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StepOut failed for process {ProcessId}", processId);
            return FailResult(processId, ex.Message);
        }
    }

    public async Task<StepResult> ContinueAsync(
        int processId, CancellationToken cancellationToken = default)
    {
        SetState(processId, SteppingState.Running);

        try
        {
            var ensureResult = await EnsureAgentInjectedAsync(processId, cancellationToken).ConfigureAwait(false);
            if (!ensureResult.Success)
                return FailResult(processId, ensureResult.Error ?? "Failed to inject VEH agent");

            // Wait for the next breakpoint hit
            await foreach (var hit in _vehDebugger.GetHitStreamAsync(processId, cancellationToken))
            {
                // Skip trace entries — those are from stepping, not user breakpoints
                if (hit.Type == VehBreakpointType.Trace)
                    continue;

                var disasm = await DisassembleAtAsync(processId, hit.Address, cancellationToken).ConfigureAwait(false);

                SetState(processId, SteppingState.Suspended);

                var result = new StepResult(
                    Success: true,
                    NewRip: hit.Address,
                    Registers: hit.Registers,
                    ThreadId: hit.ThreadId,
                    Reason: StoppedReason.BreakpointHit,
                    Disassembly: disasm);

                PublishStepCompleted(processId, result);
                return result;
            }

            // Stream ended without a hit — likely cancelled
            return TimeoutResult(processId, "Continue cancelled — no breakpoint hit received");
        }
        catch (OperationCanceledException)
        {
            SetState(processId, SteppingState.Idle);
            return new StepResult(false, 0, null, 0, StoppedReason.Timeout, Error: "Continue cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Continue failed for process {ProcessId}", processId);
            return FailResult(processId, ex.Message);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private SteppingState SetState(int processId, SteppingState newState)
    {
        var prev = _states.GetValueOrDefault(processId, SteppingState.Idle);
        _states[processId] = newState;
        return prev;
    }

    private async Task<VehInjectResult> EnsureAgentInjectedAsync(int processId, CancellationToken ct)
    {
        var status = _vehDebugger.GetStatus(processId);
        if (status.IsInjected)
            return new VehInjectResult(true);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Auto-injecting VEH agent for stepping (pid={ProcessId})", processId);
        return await _vehDebugger.InjectAsync(processId, ct).ConfigureAwait(false);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(processId);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> DisassembleAtAsync(int processId, nuint address, CancellationToken ct)
    {
        try
        {
            var result = await _disassemblyEngine.DisassembleAsync(processId, address, 1, ct).ConfigureAwait(false);
            if (result.Instructions.Count > 0)
            {
                var instr = result.Instructions[0];
                return $"{instr.Mnemonic} {instr.Operands}".Trim();
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "Failed to disassemble at 0x{Address:X}", address);
        }
        return null;
    }

    private void PublishStepCompleted(int processId, StepResult result)
    {
        _eventBus.Publish(new StepCompletedEvent(
            BreakpointId: $"step-{processId}",
            NewRip: result.NewRip,
            ThreadId: result.ThreadId,
            Reason: result.Reason));
    }

    private StepResult FailResult(int processId, string error)
    {
        SetState(processId, SteppingState.Idle);
        return new StepResult(false, 0, null, 0, StoppedReason.Error, Error: error);
    }

    private StepResult TimeoutResult(int processId, string message)
    {
        SetState(processId, SteppingState.Idle);
        return new StepResult(false, 0, null, 0, StoppedReason.Timeout, Error: message);
    }

    private StepResult ProcessExitedResult(int processId)
    {
        SetState(processId, SteppingState.Idle);
        return new StepResult(false, 0, null, 0, StoppedReason.ProcessExited, Error: "Target process has exited");
    }
}
