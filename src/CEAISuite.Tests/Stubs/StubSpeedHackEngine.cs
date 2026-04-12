using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

/// <summary>
/// In-memory stub of <see cref="ISpeedHackEngine"/> for unit tests.
/// Tracks active speed hacks per process without any P/Invoke or process interaction.
/// </summary>
public sealed class StubSpeedHackEngine : ISpeedHackEngine
{
    private readonly ConcurrentDictionary<int, (double Multiplier, SpeedHackOptions Options)> _active = new();

    public Task<SpeedHackResult> ApplyAsync(
        int processId, double multiplier, SpeedHackOptions options,
        CancellationToken ct = default)
    {
        if (_active.ContainsKey(processId))
            return Task.FromResult(new SpeedHackResult(false, "Already active"));

        _active[processId] = (multiplier, options);
        var funcs = new List<string>();
        if (options.PatchTimeGetTime) funcs.Add("timeGetTime");
        if (options.PatchQueryPerformanceCounter) funcs.Add("QueryPerformanceCounter");
        if (options.PatchGetTickCount64) funcs.Add("GetTickCount64");
        return Task.FromResult(new SpeedHackResult(true, PatchedFunctions: funcs));
    }

    public Task<SpeedHackResult> RemoveAsync(int processId, CancellationToken ct = default)
    {
        if (!_active.TryRemove(processId, out _))
            return Task.FromResult(new SpeedHackResult(false, "Not active"));
        return Task.FromResult(new SpeedHackResult(true));
    }

    public SpeedHackState GetState(int processId)
    {
        if (_active.TryGetValue(processId, out var state))
        {
            var funcs = new List<string>();
            if (state.Options.PatchTimeGetTime) funcs.Add("timeGetTime");
            if (state.Options.PatchQueryPerformanceCounter) funcs.Add("QueryPerformanceCounter");
            if (state.Options.PatchGetTickCount64) funcs.Add("GetTickCount64");
            return new SpeedHackState(true, state.Multiplier, funcs);
        }
        return new SpeedHackState(false, 1.0, []);
    }

    public Task<SpeedHackResult> UpdateMultiplierAsync(
        int processId, double newMultiplier, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(processId, out var state))
            return Task.FromResult(new SpeedHackResult(false, "Not active"));
        _active[processId] = (newMultiplier, state.Options);
        return Task.FromResult(new SpeedHackResult(true));
    }
}
