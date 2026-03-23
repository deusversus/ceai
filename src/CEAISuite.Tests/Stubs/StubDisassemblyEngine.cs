using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubDisassemblyEngine : IDisassemblyEngine
{
    public Task<DisassemblyResult> DisassembleAsync(
        int processId, nuint address, int maxInstructions = 20,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DisassemblyResult(
            address, Array.Empty<DisassembledInstruction>(), 0));
}
