using System.ComponentModel;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Inject VEH agent into target process for hardware breakpoints without debugger attachment. Bypasses anti-debug checks.")]
    public async Task<string> InjectVehAgent([Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var result = await vehDebugService.InjectAsync(processId).ConfigureAwait(false);
        return result.Success ? "VEH agent injected. Use SetVehBreakpoint to set hardware breakpoints." : $"Injection failed: {result.Error}";
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Eject VEH agent from target process. Removes all hardware breakpoints.")]
    public async Task<string> EjectVehAgent([Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var ok = await vehDebugService.EjectAsync(processId).ConfigureAwait(false);
        return ok ? "VEH agent ejected. All hardware breakpoints removed." : "Ejection failed.";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Set a VEH hardware breakpoint (DR0-DR3, max 4). Types: Execute, Write, ReadWrite. DataSize: 1, 2, 4, or 8 bytes (default 8, ignored for Execute). Optional condition for filtered breaks.")]
    public async Task<string> SetVehBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex)")] string address,
        [Description("Breakpoint type: Execute, Write, ReadWrite")] string type = "Execute",
        [Description("Data watch size in bytes: 1, 2, 4, or 8 (default 8, ignored for Execute)")] int dataSize = 8,
        [Description("Condition expression (e.g., 'RAX == 0x100', '> 10' for hit count). Leave empty for unconditional.")] string? condition = null,
        [Description("Condition type: RegisterCompare, MemoryCompare, HitCount")] string conditionType = "RegisterCompare")
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        if (!Enum.TryParse<VehBreakpointType>(type, true, out var bpType))
            return $"Invalid type '{type}'. Use: Execute, Write, ReadWrite.";
        if (dataSize is not (1 or 2 or 4 or 8))
            return $"Invalid dataSize '{dataSize}'. Use: 1, 2, 4, or 8.";

        BreakpointCondition? bpCondition = null;
        if (!string.IsNullOrWhiteSpace(condition))
        {
            if (!Enum.TryParse<BreakpointConditionType>(conditionType, true, out var condType))
                return $"Invalid conditionType '{conditionType}'. Use: RegisterCompare, MemoryCompare, HitCount.";
            bpCondition = new BreakpointCondition(condition, condType);
        }

        var addr = ParseAddress(address);
        var result = await vehDebugService.SetBreakpointAsync(processId, addr, bpType, dataSize, bpCondition).ConfigureAwait(false);
        var condStr = bpCondition is not null ? $", condition={bpCondition.Type}:{bpCondition.Expression}" : "";
        return result.Success
            ? $"VEH breakpoint set at 0x{addr:X} (DR{result.DrSlot}, type={bpType}, size={dataSize}{condStr})"
            : $"Failed: {result.Error}";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove a VEH hardware breakpoint by DR slot (0-3).")]
    public async Task<string> RemoveVehBreakpoint(
        [Description("Process ID")] int processId,
        [Description("DR slot index (0-3)")] int drSlot)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var ok = await vehDebugService.RemoveBreakpointAsync(processId, drSlot).ConfigureAwait(false);
        return ok ? $"VEH breakpoint removed from DR{drSlot}." : $"Failed to remove DR{drSlot}.";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Re-enumerate threads and apply VEH breakpoints to any new threads missing them.")]
    public async Task<string> RefreshVehThreads([Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var ok = await vehDebugService.RefreshThreadsAsync(processId).ConfigureAwait(false);
        return ok ? "Thread refresh complete. All active breakpoints applied to new threads." : "Thread refresh failed.";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Start dynamic tracing from the next VEH hardware breakpoint hit. Single-steps instruction-by-instruction, collecting register state at each step. Returns trace entries with address + registers.")]
    public async Task<string> TraceVehBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Maximum instruction steps to trace (1-10000, default 500)")] int maxSteps = 500,
        [Description("Thread ID to trace (0 = all threads, default 0)")] int threadFilter = 0)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";

        var result = await vehDebugService.TraceFromBreakpointAsync(processId, maxSteps, threadFilter).ConfigureAwait(false);
        if (!result.Success)
            return $"Trace failed: {result.Error}";

        if (result.Entries.Count == 0)
            return "Trace started but no steps captured (no breakpoint hit during timeout, or thread filter didn't match).";

        var sb = new System.Text.StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"Trace: {result.Entries.Count} steps{(result.Truncated ? " (truncated)" : "")}").AppendLine();

        var maxStepsDisplay = _limits.MaxTraceSteps;
        // Compact JSON format for large traces, table format for small
        if (result.Entries.Count > 20)
        {
            var entries = result.Entries.Take(maxStepsDisplay).Select(e => new
            {
                addr = $"0x{(ulong)e.Address:X}",
                tid = e.ThreadId,
                rip = $"0x{e.Registers.Rip:X}",
                rax = $"0x{e.Registers.Rax:X}",
                rcx = $"0x{e.Registers.Rcx:X}",
                rsp = $"0x{e.Registers.Rsp:X}",
                eflags = $"0x{e.Registers.EFlags:X}"
            });
            sb.Append(ToJson(new { entries, shown = Math.Min(result.Entries.Count, maxStepsDisplay), total = result.Entries.Count }));
        }
        else
        {
            sb.AppendLine("Address          | TID  | RIP              | RAX              | RCX              | RSP              | EFlags");
            sb.AppendLine(new string('-', 120));

            foreach (var entry in result.Entries)
            {
                var r = entry.Registers;
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"0x{(ulong)entry.Address:X16} | {entry.ThreadId,4} | " +
                    $"0x{r.Rip:X16} | 0x{r.Rax:X16} | 0x{r.Rcx:X16} | 0x{r.Rsp:X16} | 0x{r.EFlags:X8}").AppendLine();
                // Extended registers on second line when non-zero
                if (r.R12 != 0 || r.R13 != 0 || r.R14 != 0 || r.R15 != 0)
                {
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                        $"                   {'|',4}   R12=0x{r.R12:X16} R13=0x{r.R13:X16} R14=0x{r.R14:X16} R15=0x{r.R15:X16}").AppendLine();
                }
            }
        }

        if (result.Entries.Count > maxStepsDisplay)
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"... and {result.Entries.Count - maxStepsDisplay} more steps (use PollVehBreakpointHits for streaming)").AppendLine();

        return sb.ToString();
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Stop an active VEH trace.")]
    public async Task<string> StopVehTrace([Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var ok = await vehDebugService.StopTraceAsync(processId).ConfigureAwait(false);
        return ok ? "Trace stopped." : "Failed to stop trace.";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Register a Lua callback function to be invoked on VEH breakpoint hits for a specific DR slot.")]
    public Task<string> RegisterVehLuaCallback(
        [Description("Process ID")] int processId,
        [Description("DR slot index (0-3)")] int drSlot,
        [Description("Lua function name to invoke on hit")] string luaFunctionName)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return Task.FromResult(pidError);
        if (vehDebugService is null) return Task.FromResult("VEH debugger not available.");
        if (drSlot is < 0 or > 3) return Task.FromResult("Invalid DR slot. Must be 0-3.");
        if (string.IsNullOrWhiteSpace(luaFunctionName)) return Task.FromResult("Lua function name is required.");
        var ok = vehDebugService.RegisterLuaCallback(processId, drSlot, luaFunctionName);
        return Task.FromResult(ok
            ? $"Lua callback '{luaFunctionName}' registered for DR{drSlot}."
            : "Failed to register Lua callback — VEH engine not available.");
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Unregister a Lua callback from a VEH breakpoint DR slot.")]
    public Task<string> UnregisterVehLuaCallback(
        [Description("Process ID")] int processId,
        [Description("DR slot index (0-3)")] int drSlot)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return Task.FromResult(pidError);
        if (vehDebugService is null) return Task.FromResult("VEH debugger not available.");
        if (drSlot is < 0 or > 3) return Task.FromResult("Invalid DR slot. Must be 0-3.");
        vehDebugService.UnregisterLuaCallback(processId, drSlot);
        return Task.FromResult($"Lua callback unregistered from DR{drSlot}.");
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Enable VEH stealth mode: cloak DR registers from GetThreadContext, hide agent DLL from module enumeration.")]
    public async Task<string> EnableVehStealth([Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var ok = await vehDebugService.EnableStealthAsync(processId).ConfigureAwait(false);
        return ok
            ? "Stealth enabled. DR registers cloaked from GetThreadContext, agent DLL hidden from module list."
            : "Failed to enable stealth. Ensure VEH agent is injected first.";
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Disable VEH stealth mode: restore NtGetThreadContext hook, re-link agent DLL in PEB.")]
    public async Task<string> DisableVehStealth([Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var ok = await vehDebugService.DisableStealthAsync(processId).ConfigureAwait(false);
        return ok ? "Stealth disabled. DR registers visible, agent DLL re-linked in module list." : "Failed to disable stealth.";
    }

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get VEH debugger status: injection state, active breakpoints, hit count, overflow count, agent health, stealth mode.")]
    public Task<string> GetVehStatus([Description("Process ID")] int processId)
    {
        if (vehDebugService is null) return Task.FromResult("VEH debugger not available.");
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return Task.FromResult(pidError);
        var status = vehDebugService.GetStatus(processId);
        if (!status.IsInjected) return Task.FromResult("VEH agent: not injected.");
        var stealthStr = status.StealthMode == VehStealthMode.Active ? ", STEALTH" : "";
        return Task.FromResult(
            $"VEH agent: ACTIVE ({status.AgentHealth}{stealthStr}). {status.ActiveBreakpoints}/4 breakpoints, " +
            $"{status.TotalHits} total hits, {status.OverflowCount} overflows.");
    }

    // ── PAGE_GUARD breakpoints (no DR slot limit) ──

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("page guard", "guard", "unlimited breakpoint", "page access")]
    [Description("Set a PAGE_GUARD breakpoint via VEH agent. Triggers on any access to the memory page containing the address. Does NOT consume a DR slot — unlimited count.")]
    public async Task<string> SetVehPageGuardBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex)")] string address)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var addr = ParseAddress(address);
        var result = await vehDebugService.SetPageGuardBreakpointAsync(processId, addr).ConfigureAwait(false);
        return result.Success
            ? $"PAGE_GUARD breakpoint set at 0x{addr:X}. No DR slot consumed."
            : $"Failed: {result.Error}";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove a PAGE_GUARD breakpoint set via VEH agent.")]
    public async Task<string> RemoveVehPageGuardBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex)")] string address)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var addr = ParseAddress(address);
        var ok = await vehDebugService.RemovePageGuardBreakpointAsync(processId, addr).ConfigureAwait(false);
        return ok ? $"PAGE_GUARD breakpoint removed at 0x{addr:X}." : $"Failed to remove PAGE_GUARD at 0x{addr:X}.";
    }

    // ── INT3 software breakpoints (no DR slot limit) ──

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("int3", "software breakpoint", "0xCC", "unlimited")]
    [Description("Set an INT3 (0xCC) software breakpoint via VEH agent. Triggers on execution. Does NOT consume a DR slot — unlimited count.")]
    public async Task<string> SetVehInt3Breakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex)")] string address)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var addr = ParseAddress(address);
        var result = await vehDebugService.SetInt3BreakpointAsync(processId, addr).ConfigureAwait(false);
        return result.Success
            ? $"INT3 breakpoint set at 0x{addr:X}. No DR slot consumed."
            : $"Failed: {result.Error}";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove an INT3 software breakpoint set via VEH agent.")]
    public async Task<string> RemoveVehInt3Breakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex)")] string address)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        var addr = ParseAddress(address);
        var ok = await vehDebugService.RemoveInt3BreakpointAsync(processId, addr).ConfigureAwait(false);
        return ok ? $"INT3 breakpoint removed at 0x{addr:X}." : $"Failed to remove INT3 at 0x{addr:X}.";
    }

    // ── Hit stream polling ──

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [SearchHint("hit stream", "poll", "monitor", "real-time", "breakpoint hits")]
    [Description("Poll real-time breakpoint hits from VEH agent. Collects up to maxEvents hits within timeoutMs, then returns. Use for monitoring active breakpoints.")]
    public async Task<string> PollVehBreakpointHits(
        [Description("Process ID")] int processId,
        [Description("Maximum events to collect (default 50)")] int maxEvents = 50,
        [Description("Timeout in milliseconds to wait for hits (default 2000)")] int timeoutMs = 2000)
    {
        if (vehDebugService is null) return "VEH debugger not available.";
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (maxEvents is < 1 or > 500) maxEvents = _limits.MaxVehPollHits;
        if (timeoutMs is < 100 or > 30000) timeoutMs = 2000;

        using var cts = new CancellationTokenSource(timeoutMs);
        var hits = new List<object>();
        try
        {
            await foreach (var hit in vehDebugService.GetHitStreamAsync(processId, cts.Token))
            {
                hits.Add(new
                {
                    address = $"0x{(ulong)hit.Address:X}",
                    threadId = hit.ThreadId,
                    type = hit.Type.ToString(),
                    rip = $"0x{hit.Registers.Rip:X}",
                    rax = $"0x{hit.Registers.Rax:X}",
                    rcx = $"0x{hit.Registers.Rcx:X}",
                    rsp = $"0x{hit.Registers.Rsp:X}"
                });
                if (hits.Count >= maxEvents) break;
            }
        }
        catch (OperationCanceledException) { /* timeout — return what we have */ }

        return ToJson(new { hits, count = hits.Count, timedOut = hits.Count < maxEvents });
    }

    // ── DR slot usage ──

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("slot", "DR", "debug register", "hardware slot", "capacity")]
    [Description("Get VEH hardware breakpoint DR slot usage (DR0-DR3) and counts of PAGE_GUARD and INT3 breakpoints (which don't consume DR slots).")]
    public Task<string> GetVehBreakpointSlotUsage([Description("Process ID")] int processId)
    {
        if (vehDebugService is null) return Task.FromResult("VEH debugger not available.");
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return Task.FromResult(pidError);
        var status = vehDebugService.GetStatus(processId);
        if (!status.IsInjected) return Task.FromResult("VEH agent not injected.");
        return Task.FromResult(
            $"DR slots: {status.ActiveBreakpoints}/4 in use. " +
            $"Total hits: {status.TotalHits}, overflows: {status.OverflowCount}. " +
            $"PAGE_GUARD and INT3 breakpoints do not consume DR slots.");
    }
}
