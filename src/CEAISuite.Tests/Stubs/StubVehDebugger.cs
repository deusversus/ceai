using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

/// <summary>
/// In-memory stub of <see cref="IVehDebugger"/> for unit tests.
/// Tracks injected state and breakpoint slots per process without any P/Invoke.
/// </summary>
public sealed class StubVehDebugger : IVehDebugger
{
    private readonly ConcurrentDictionary<int, StubVehState> _states = new();

    private sealed class StubVehState
    {
        public bool IsInjected;
        public int[] DrSlots = new int[4]; // 0=free, 1=used
        public int TotalHits { get; set; }
        public List<VehHitEvent> PendingHits = new();
    }

    public void AddCannedHit(int processId, VehHitEvent hit)
    {
        if (_states.TryGetValue(processId, out var state))
            state.PendingHits.Add(hit);
    }

    public Task<VehInjectResult> InjectAsync(int processId, CancellationToken ct = default)
    {
        if (_states.ContainsKey(processId))
            return Task.FromResult(new VehInjectResult(false, "Already injected"));
        _states[processId] = new StubVehState { IsInjected = true };
        return Task.FromResult(new VehInjectResult(true));
    }

    public Task<bool> EjectAsync(int processId, CancellationToken ct = default)
    {
        return Task.FromResult(_states.TryRemove(processId, out _));
    }

    public Task<VehBreakpointResult> SetBreakpointAsync(int processId, nuint address, VehBreakpointType type, CancellationToken ct = default)
    {
        if (!_states.TryGetValue(processId, out var state) || !state.IsInjected)
            return Task.FromResult(new VehBreakpointResult(false, Error: "Not injected"));
        for (int i = 0; i < 4; i++)
        {
            if (state.DrSlots[i] == 0)
            {
                state.DrSlots[i] = 1;
                return Task.FromResult(new VehBreakpointResult(true, i));
            }
        }
        return Task.FromResult(new VehBreakpointResult(false, Error: "All 4 slots full"));
    }

    public Task<bool> RemoveBreakpointAsync(int processId, int drSlot, CancellationToken ct = default)
    {
        if (!_states.TryGetValue(processId, out var state)) return Task.FromResult(false);
        if (drSlot < 0 || drSlot > 3 || state.DrSlots[drSlot] == 0) return Task.FromResult(false);
        state.DrSlots[drSlot] = 0;
        return Task.FromResult(true);
    }

    public async IAsyncEnumerable<VehHitEvent> GetHitStreamAsync(int processId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_states.TryGetValue(processId, out var state)) yield break;
        foreach (var hit in state.PendingHits)
        {
            if (ct.IsCancellationRequested) yield break;
            yield return hit;
        }
        state.PendingHits.Clear();
        await Task.CompletedTask; // suppress CS1998
    }

    public VehStatus GetStatus(int processId)
    {
        if (!_states.TryGetValue(processId, out var state))
            return new VehStatus(false, 0, 0);
        return new VehStatus(state.IsInjected, state.DrSlots.Count(s => s != 0), state.TotalHits);
    }
}
