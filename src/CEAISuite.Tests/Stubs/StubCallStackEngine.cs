using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubCallStackEngine : ICallStackEngine
{
    public List<CallStackFrame> NextFrames { get; set; } =
    [
        new(0, 0x7FF00100, 0x7FF00200, 0x0000FF00, 0x0000FE00, "game.dll", 0x100),
        new(1, 0x7FF00200, 0x7FF00300, 0x0000FE00, 0x0000FD00, "game.dll", 0x200),
        new(2, 0x7FF00300, 0x0, 0x0000FD00, 0x0000FC00, "kernel32.dll", 0x4000)
    ];

    public Task<IReadOnlyList<CallStackFrame>> WalkStackAsync(int processId, int threadId,
        IReadOnlyList<ModuleDescriptor> modules, int maxFrames = 64,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CallStackFrame>>(NextFrames.Take(maxFrames).ToList());

    public Task<IReadOnlyDictionary<int, IReadOnlyList<CallStackFrame>>> WalkAllThreadsAsync(
        int processId, IReadOnlyList<ModuleDescriptor> modules, int maxFrames = 32,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<CallStackFrame>>>(
            new Dictionary<int, IReadOnlyList<CallStackFrame>> { [0] = NextFrames.Take(maxFrames).ToList() });
}
