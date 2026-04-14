using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Application-layer wrapper around <see cref="ISteppingEngine"/>. Adds PID validation,
/// state tracking, and logging.
/// </summary>
public sealed class SteppingService
{
    private readonly ISteppingEngine? _engine;
    private readonly IEngineFacade _engineFacade;
    private readonly ILogger<SteppingService> _logger;

    public SteppingService(
        IEngineFacade engineFacade,
        ISteppingEngine? engine = null,
        ILogger<SteppingService>? logger = null)
    {
        _engineFacade = engineFacade;
        _engine = engine;
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<SteppingService>();
    }

    public bool IsAvailable => _engine is not null;

    public SteppingState GetState(int processId)
    {
        if (_engine is null) return SteppingState.Idle;
        return _engine.GetState(processId);
    }

    public async Task<StepResult> StepInAsync(int processId, int threadId = 0, CancellationToken ct = default)
    {
        var error = ValidatePid(processId);
        if (error is not null) return ErrorResult(error);
        return await _engine!.StepInAsync(processId, threadId, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<StepResult> StepOverAsync(int processId, int threadId = 0, CancellationToken ct = default)
    {
        var error = ValidatePid(processId);
        if (error is not null) return ErrorResult(error);
        return await _engine!.StepOverAsync(processId, threadId, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<StepResult> StepOutAsync(int processId, int threadId = 0, CancellationToken ct = default)
    {
        var error = ValidatePid(processId);
        if (error is not null) return ErrorResult(error);
        return await _engine!.StepOutAsync(processId, threadId, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<StepResult> ContinueAsync(int processId, CancellationToken ct = default)
    {
        var error = ValidatePid(processId);
        if (error is not null) return ErrorResult(error);
        return await _engine!.ContinueAsync(processId, ct).ConfigureAwait(false);
    }

    /// <summary>Clear stepping state for a process. Call on detach.</summary>
    public void OnProcessDetached(int processId) => _engine?.ClearState(processId);

    private string? ValidatePid(int processId)
    {
        if (_engine is null) return "Stepping engine not available.";
        if (!_engineFacade.IsAttached) return "No process attached.";
        if (_engineFacade.AttachedProcessId != processId)
            return $"PID mismatch: requested {processId}, attached to {_engineFacade.AttachedProcessId}.";
        return null;
    }

    private static StepResult ErrorResult(string error) =>
        new(false, 0, null, 0, StoppedReason.Error, Error: error);

    /// <summary>Format a StepResult for AI tool output.</summary>
    public static string FormatStepResult(StepResult result)
    {
        if (!result.Success)
            return $"Step failed: {result.Error ?? result.Reason.ToString()}";

        var parts = new List<string>
        {
            $"RIP: 0x{result.NewRip:X}",
            $"Thread: {result.ThreadId}",
            $"Reason: {result.Reason}"
        };

        if (result.Disassembly is not null)
            parts.Add($"Instruction: {result.Disassembly}");

        if (result.Registers is not null)
        {
            var r = result.Registers;
            parts.Add($"RAX=0x{r.Rax:X} RBX=0x{r.Rbx:X} RCX=0x{r.Rcx:X} RDX=0x{r.Rdx:X}");
            parts.Add($"RSI=0x{r.Rsi:X} RDI=0x{r.Rdi:X} RSP=0x{r.Rsp:X} RBP=0x{r.Rbp:X}");
            parts.Add($"R8=0x{r.R8:X} R9=0x{r.R9:X} R10=0x{r.R10:X} R11=0x{r.R11:X}");
            parts.Add($"R12=0x{r.R12:X} R13=0x{r.R13:X} R14=0x{r.R14:X} R15=0x{r.R15:X}");
            parts.Add($"EFLAGS=0x{r.EFlags:X}");
        }

        return string.Join("\n", parts);
    }
}
