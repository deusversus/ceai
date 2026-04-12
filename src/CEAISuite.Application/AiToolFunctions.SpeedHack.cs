using System.ComponentModel;
using System.Globalization;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get the current speed hack state for a process.")]
    public Task<string> GetSpeedHackState([Description("Process ID")] int processId)
    {
        if (speedHackService is null) return Task.FromResult("Speed hack not available.");
        var state = speedHackService.GetState(processId);
        if (!state.IsActive) return Task.FromResult("Speed hack: not active.");
        var ic = CultureInfo.InvariantCulture;
        return Task.FromResult($"Speed hack: ACTIVE at {state.Multiplier.ToString("F1", ic)}x. Patched: {string.Join(", ", state.PatchedFunctions)}");
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Apply or update speed hack multiplier. Values < 1.0 slow down, > 1.0 speed up. Range: 0.1 to 8.0.")]
    public async Task<string> SetSpeedMultiplier(
        [Description("Process ID")] int processId,
        [Description("Speed multiplier (0.1 = 10% speed, 1.0 = normal, 2.0 = double speed, 8.0 = max)")] double multiplier)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (speedHackService is null) return "Speed hack not available.";

        var state = speedHackService.GetState(processId);
        SpeedHackResult result;
        if (state.IsActive)
        {
            result = await speedHackService.UpdateMultiplierAsync(processId, multiplier).ConfigureAwait(false);
            return result.Success
                ? $"Speed updated to {Math.Clamp(multiplier, 0.1, 8.0).ToString("F1", CultureInfo.InvariantCulture)}x"
                : $"Failed to update speed: {result.ErrorMessage}";
        }

        result = await speedHackService.ApplyAsync(processId, multiplier).ConfigureAwait(false);
        return result.Success
            ? $"Speed hack applied at {Math.Clamp(multiplier, 0.1, 8.0).ToString("F1", CultureInfo.InvariantCulture)}x. Patched: {string.Join(", ", result.PatchedFunctions ?? [])}"
            : $"Failed to apply speed hack: {result.ErrorMessage}";
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove speed hack, restoring original timing functions.")]
    public async Task<string> RemoveSpeedHack([Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (speedHackService is null) return "Speed hack not available.";

        var result = await speedHackService.RemoveAsync(processId).ConfigureAwait(false);
        return result.Success
            ? "Speed hack removed. Original timing functions restored."
            : $"Failed to remove speed hack: {result.ErrorMessage}";
    }
}
