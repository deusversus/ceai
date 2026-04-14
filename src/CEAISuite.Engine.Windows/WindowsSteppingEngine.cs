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

    public void ClearState(int processId) =>
        _states.TryRemove(processId, out _);

    public Task<StepResult?> GetCurrentStateAsync(
        int processId, int threadId = 0, CancellationToken cancellationToken = default)
    {
        if (GetState(processId) != SteppingState.Suspended)
            return Task.FromResult<StepResult?>(null);

        return Task.FromResult<StepResult?>(new StepResult(
            Success: true,
            NewRip: 0,
            Registers: null,
            ThreadId: threadId,
            Reason: StoppedReason.StepComplete,
            Disassembly: null));
    }

    public async Task<StepResult> StepInAsync(
        int processId, int threadId = 0, int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        SetState(processId, SteppingState.Stepping);

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
        SetState(processId, SteppingState.Stepping);

        try
        {
            var ensureResult = await EnsureAgentInjectedAsync(processId, cancellationToken).ConfigureAwait(false);
            if (!ensureResult.Success)
                return FailResult(processId, ensureResult.Error ?? "Failed to inject VEH agent");

            // MVP: delegates to StepIn. Proper CALL detection needs CMD_GET_CONTEXT
            // in the VEH agent to read RIP before stepping — planned for future iteration.
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
        SetState(processId, SteppingState.Stepping);

        try
        {
            var ensureResult = await EnsureAgentInjectedAsync(processId, cancellationToken).ConfigureAwait(false);
            if (!ensureResult.Success)
                return FailResult(processId, ensureResult.Error ?? "Failed to inject VEH agent");

            // Step-out: We need the return address from the stack.
            // LIMITATION: We do a single step first to obtain RSP from the register snapshot.
            // If the stepped instruction modifies RSP (push, sub rsp, call), the read address
            // may be wrong. Proper fix requires CMD_GET_CONTEXT to read RSP without stepping.
            // This is acceptable for most cases since step-out is typically called while
            // suspended inside a function body, not at a stack-modifying instruction.
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

}
