namespace CEAISuite.Engine.Abstractions;

/// <summary>
/// Engine for intercepting Windows timing APIs via IAT (Import Address Table) patching.
/// Scales timeGetTime, QueryPerformanceCounter, and GetTickCount64 return values
/// by a configurable multiplier to speed up or slow down game timers.
/// </summary>
public interface ISpeedHackEngine
{
    /// <summary>
    /// Apply speed hack to a process by patching timing API IAT entries.
    /// Allocates code caves with scaling trampolines and overwrites IAT slots.
    /// </summary>
    Task<SpeedHackResult> ApplyAsync(int processId, double multiplier, SpeedHackOptions options, CancellationToken ct = default);

    /// <summary>
    /// Remove speed hack from a process. Restores original IAT entries and frees code caves.
    /// </summary>
    Task<SpeedHackResult> RemoveAsync(int processId, CancellationToken ct = default);

    /// <summary>
    /// Get the current speed hack state for a process. Synchronous (reads from in-memory state).
    /// </summary>
    SpeedHackState GetState(int processId);

    /// <summary>
    /// Update the speed multiplier without re-patching. Writes the new fixed-point value
    /// directly to the trampoline data section (8-byte aligned = atomic on x64).
    /// </summary>
    Task<SpeedHackResult> UpdateMultiplierAsync(int processId, double newMultiplier, CancellationToken ct = default);
}

/// <summary>Per-function toggle options for speed hack.</summary>
public sealed record SpeedHackOptions(
    bool PatchTimeGetTime = true,
    bool PatchQueryPerformanceCounter = true,
    bool PatchGetTickCount64 = true);

/// <summary>Current speed hack state for a process.</summary>
public sealed record SpeedHackState(
    bool IsActive,
    double Multiplier,
    IReadOnlyList<string> PatchedFunctions);

/// <summary>Result of a speed hack operation.</summary>
public sealed record SpeedHackResult(
    bool Success,
    string? ErrorMessage = null,
    IReadOnlyList<string>? PatchedFunctions = null);
