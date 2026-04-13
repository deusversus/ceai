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

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get VEH debugger status: injection state, active breakpoints, hit count, overflow count, agent health.")]
    public Task<string> GetVehStatus([Description("Process ID")] int processId)
    {
        if (vehDebugService is null) return Task.FromResult("VEH debugger not available.");
        // Validate PID for consistency — prevents information leak about arbitrary processes
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return Task.FromResult(pidError);
        var status = vehDebugService.GetStatus(processId);
        if (!status.IsInjected) return Task.FromResult("VEH agent: not injected.");
        return Task.FromResult(
            $"VEH agent: ACTIVE ({status.AgentHealth}). {status.ActiveBreakpoints}/4 breakpoints, " +
            $"{status.TotalHits} total hits, {status.OverflowCount} overflows.");
    }
}
