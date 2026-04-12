using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed class SpeedHackService
{
    private readonly ISpeedHackEngine? _engine;

    public SpeedHackService(ISpeedHackEngine? engine = null) => _engine = engine;

    public bool IsAvailable => _engine is not null;

    public async Task<SpeedHackResult> ApplyAsync(int processId, double multiplier, SpeedHackOptions? options = null, CancellationToken ct = default)
    {
        if (_engine is null) return new SpeedHackResult(false, "Speed hack engine not available.");
        var state = _engine.GetState(processId);
        if (state.IsActive) return new SpeedHackResult(false, "Speed hack already active for this process. Use UpdateMultiplierAsync to change speed.");
        multiplier = Math.Clamp(multiplier, 0.1, 8.0);
        return await _engine.ApplyAsync(processId, multiplier, options ?? new SpeedHackOptions(), ct).ConfigureAwait(false);
    }

    public async Task<SpeedHackResult> RemoveAsync(int processId, CancellationToken ct = default)
    {
        if (_engine is null) return new SpeedHackResult(false, "Speed hack engine not available.");
        var state = _engine.GetState(processId);
        if (!state.IsActive) return new SpeedHackResult(false, "Speed hack not active for this process.");

        // Set multiplier to 1.0x before removing hooks so the game's internal clock
        // is in sync with real time at the moment of unhook — no timing discontinuity.
        if (Math.Abs(state.Multiplier - 1.0) > 0.01)
            await _engine.UpdateMultiplierAsync(processId, 1.0, ct).ConfigureAwait(false);

        return await _engine.RemoveAsync(processId, ct).ConfigureAwait(false);
    }

    public SpeedHackState GetState(int processId)
    {
        if (_engine is null) return new SpeedHackState(false, 1.0, []);
        return _engine.GetState(processId);
    }

    public async Task<SpeedHackResult> UpdateMultiplierAsync(int processId, double newMultiplier, CancellationToken ct = default)
    {
        if (_engine is null) return new SpeedHackResult(false, "Speed hack engine not available.");
        var state = _engine.GetState(processId);
        if (!state.IsActive) return new SpeedHackResult(false, "Speed hack not active. Apply first.");
        newMultiplier = Math.Clamp(newMultiplier, 0.1, 8.0);
        return await _engine.UpdateMultiplierAsync(processId, newMultiplier, ct).ConfigureAwait(false);
    }
}
