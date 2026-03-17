namespace CEAISuite.Engine.Abstractions;

/// <summary>
/// A single stack frame from call stack unwinding.
/// </summary>
public sealed record CallStackFrame(
    int FrameIndex,
    nuint InstructionPointer,
    nuint ReturnAddress,
    nuint StackPointer,
    nuint FramePointer,
    string? ModuleName,
    long ModuleOffset);

/// <summary>
/// Engine for walking thread call stacks.
/// </summary>
public interface ICallStackEngine
{
    /// <summary>Walk the call stack of a specific thread.</summary>
    Task<IReadOnlyList<CallStackFrame>> WalkStackAsync(
        int processId,
        int threadId,
        IReadOnlyList<ModuleDescriptor> modules,
        int maxFrames = 64,
        CancellationToken cancellationToken = default);

    /// <summary>Walk the call stacks of all threads in a process.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<CallStackFrame>>> WalkAllThreadsAsync(
        int processId,
        IReadOnlyList<ModuleDescriptor> modules,
        int maxFrames = 32,
        CancellationToken cancellationToken = default);
}
