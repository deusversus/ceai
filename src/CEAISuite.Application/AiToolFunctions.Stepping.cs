using System.ComponentModel;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get the current stepping/debugger state for the attached process.")]
    public Task<string> GetSteppingState([Description("Process ID")] int processId)
    {
        if (steppingService is null) return Task.FromResult("Stepping not available.");
        var state = steppingService.GetState(processId);
        return Task.FromResult($"Stepping state: {state}");
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Execute a single instruction (step into calls). Target must be suspended at a breakpoint.")]
    public async Task<string> StepIn(
        [Description("Process ID")] int processId,
        [Description("Thread ID to step (0 = any thread)")] int threadId = 0)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (steppingService is null) return "Stepping not available.";

        var result = await steppingService.StepInAsync(processId, threadId).ConfigureAwait(false);
        return SteppingService.FormatStepResult(result);
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Step over the current instruction. If it's a CALL, executes the entire called function and stops at the next instruction. Otherwise same as StepIn.")]
    public async Task<string> StepOver(
        [Description("Process ID")] int processId,
        [Description("Thread ID to step (0 = any thread)")] int threadId = 0)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (steppingService is null) return "Stepping not available.";

        var result = await steppingService.StepOverAsync(processId, threadId).ConfigureAwait(false);
        return SteppingService.FormatStepResult(result);
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Step out of the current function. Sets a temporary breakpoint at the return address and continues until it's hit.")]
    public async Task<string> StepOut(
        [Description("Process ID")] int processId,
        [Description("Thread ID to step (0 = any thread)")] int threadId = 0)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (steppingService is null) return "Stepping not available.";

        var result = await steppingService.StepOutAsync(processId, threadId).ConfigureAwait(false);
        return SteppingService.FormatStepResult(result);
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Resume target execution. Returns when the next breakpoint is hit.")]
    public async Task<string> ContinueExecution([Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (steppingService is null) return "Stepping not available.";

        var result = await steppingService.ContinueAsync(processId).ConfigureAwait(false);
        return SteppingService.FormatStepResult(result);
    }
}
