using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed class VehDebugService
{
    private readonly IVehDebugger? _engine;

    public VehDebugService(IVehDebugger? engine = null) => _engine = engine;

    public bool IsAvailable => _engine is not null;

    public async Task<VehInjectResult> InjectAsync(int processId, CancellationToken ct = default)
    {
        if (_engine is null) return new VehInjectResult(false, "VEH debugger not available.");
        return await _engine.InjectAsync(processId, ct).ConfigureAwait(false);
    }

    public async Task<bool> EjectAsync(int processId, CancellationToken ct = default)
    {
        if (_engine is null) return false;
        return await _engine.EjectAsync(processId, ct).ConfigureAwait(false);
    }

    public async Task<VehBreakpointResult> SetBreakpointAsync(int processId, nuint address, VehBreakpointType type, CancellationToken ct = default)
    {
        if (_engine is null) return new VehBreakpointResult(false, Error: "VEH debugger not available.");
        var status = _engine.GetStatus(processId);
        if (!status.IsInjected) return new VehBreakpointResult(false, Error: "VEH agent not injected. Call InjectVehAgent first.");
        if (status.ActiveBreakpoints >= 4) return new VehBreakpointResult(false, Error: "All 4 hardware breakpoint slots are in use.");
        return await _engine.SetBreakpointAsync(processId, address, type, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveBreakpointAsync(int processId, int drSlot, CancellationToken ct = default)
    {
        if (_engine is null) return false;
        return await _engine.RemoveBreakpointAsync(processId, drSlot, ct).ConfigureAwait(false);
    }

    public VehStatus GetStatus(int processId)
    {
        if (_engine is null) return new VehStatus(false, 0, 0);
        return _engine.GetStatus(processId);
    }
}
