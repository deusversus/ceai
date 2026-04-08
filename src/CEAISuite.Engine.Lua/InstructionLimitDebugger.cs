using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Lightweight MoonSharp debugger that counts executed instructions and throws
/// a <see cref="ScriptRuntimeException"/> when the configured limit is exceeded.
/// Attach via <see cref="Script.AttachDebugger"/> before execution.
/// </summary>
internal sealed class InstructionLimitDebugger : IDebugger
{
    private readonly long _maxInstructions;
    private long _instructionCount;

    public InstructionLimitDebugger(long maxInstructions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInstructions);
        _maxInstructions = maxInstructions;
    }

    /// <summary>Number of instructions executed since the last <see cref="Reset"/>.</summary>
    public long InstructionCount => Interlocked.Read(ref _instructionCount);

    /// <summary>Reset the counter before a new script execution.</summary>
    public void Reset() => Interlocked.Exchange(ref _instructionCount, 0);

    // ── IDebugger implementation ──

    public DebuggerAction GetAction(int ip, SourceRef sourceref)
    {
        var count = Interlocked.Increment(ref _instructionCount);
        if (count > _maxInstructions)
        {
            throw new ScriptRuntimeException(
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Instruction limit exceeded ({_maxInstructions} max). Script terminated."));
        }

        // Continue normal execution
        return new DebuggerAction() { Action = DebuggerAction.ActionType.Run };
    }

    public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugByteCode;

    public void SetDebugService(DebugService debugService) { }

    public void SetSourceCode(SourceCode sourceCode) { }

    public void SetByteCode(string[] byteCode) { }

    public bool IsPauseRequested() => false;

    public bool SignalRuntimeException(ScriptRuntimeException ex) => false;

    public void SignalExecutionEnded() { }

    public void Update(WatchType watchType, IEnumerable<WatchItem> items) { }

    public List<DynamicExpression> GetWatchItems() => [];

    public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
}
