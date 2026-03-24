using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubBreakpointEngine : IBreakpointEngine
{
    private readonly List<BreakpointDescriptor> _breakpoints = new();
    private readonly Dictionary<string, List<BreakpointHitEvent>> _hitLogs = new();

    public void AddCannedBreakpoint(BreakpointDescriptor bp) => _breakpoints.Add(bp);
    public void AddCannedHits(string bpId, params BreakpointHitEvent[] hits) =>
        _hitLogs[bpId] = hits.ToList();

    public Task<BreakpointDescriptor> SetBreakpointAsync(int processId, nuint address,
        BreakpointType type, BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        CancellationToken cancellationToken = default)
        => SetBreakpointAsync(processId, address, type, BreakpointMode.Auto, action, false, cancellationToken);

    public Task<BreakpointDescriptor> SetBreakpointAsync(int processId, nuint address,
        BreakpointType type, BreakpointMode mode,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        bool singleHit = false, CancellationToken cancellationToken = default)
    {
        var bp = new BreakpointDescriptor($"bp-{_breakpoints.Count}", address, type, action, true, 0, mode);
        _breakpoints.Add(bp);
        return Task.FromResult(bp);
    }

    public Task<bool> RemoveBreakpointAsync(int processId, string breakpointId,
        CancellationToken cancellationToken = default)
    {
        var idx = _breakpoints.FindIndex(b => b.Id == breakpointId);
        if (idx >= 0) { _breakpoints.RemoveAt(idx); return Task.FromResult(true); }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<BreakpointDescriptor>> ListBreakpointsAsync(int processId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BreakpointDescriptor>>(_breakpoints.ToList());

    public Task<IReadOnlyList<BreakpointHitEvent>> GetHitLogAsync(string breakpointId,
        int maxEntries = 50, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BreakpointHitEvent>>(
            _hitLogs.TryGetValue(breakpointId, out var hits) ? hits.Take(maxEntries).ToList() : []);

    public Task<int> EmergencyRestorePageProtectionAsync(int processId) => Task.FromResult(0);
    public Task ForceDetachAndCleanupAsync(int processId) => Task.CompletedTask;
}
