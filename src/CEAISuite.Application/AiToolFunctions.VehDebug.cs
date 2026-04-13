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
    [Description("Set a VEH hardware breakpoint (DR0-DR3, max 4). Types: Execute, Write, ReadWrite. DataSize: 1, 2, 4, or 8 bytes (default 8, ignored for Execute).")]
    public async Task<string> SetVehBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex)")] string address,
        [Description("Breakpoint type: Execute, Write, ReadWrite")] string type = "Execute",
        [Description("Data watch size in bytes: 1, 2, 4, or 8 (default 8, ignored for Execute)")] int dataSize = 8)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (vehDebugService is null) return "VEH debugger not available.";
        if (!Enum.TryParse<VehBreakpointType>(type, true, out var bpType))
            return $"Invalid type '{type}'. Use: Execute, Write, ReadWrite.";
        if (dataSize is not (1 or 2 or 4 or 8))
            return $"Invalid dataSize '{dataSize}'. Use: 1, 2, 4, or 8.";
        var addr = ParseAddress(address);
        var result = await vehDebugService.SetBreakpointAsync(processId, addr, bpType, dataSize).ConfigureAwait(false);
        return result.Success
            ? $"VEH breakpoint set at 0x{addr:X} (DR{result.DrSlot}, type={bpType}, size={dataSize})"
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

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get VEH debugger status: injection state, active breakpoints, hit count, overflow count, agent health.")]
    public Task<string> GetVehStatus([Description("Process ID")] int processId)
    {
        if (vehDebugService is null) return Task.FromResult("VEH debugger not available.");
        var status = vehDebugService.GetStatus(processId);
        if (!status.IsInjected) return Task.FromResult("VEH agent: not injected.");
        return Task.FromResult(
            $"VEH agent: ACTIVE ({status.AgentHealth}). {status.ActiveBreakpoints}/4 breakpoints, " +
            $"{status.TotalHits} total hits, {status.OverflowCount} overflows.");
    }
}
