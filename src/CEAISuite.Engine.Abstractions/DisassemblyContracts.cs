namespace CEAISuite.Engine.Abstractions;

public sealed record DisassembledInstruction(
    nuint Address,
    string HexBytes,
    string Mnemonic,
    string Operands,
    int Length,
    string? SymbolName = null);

public sealed record DisassemblyResult(
    nuint StartAddress,
    IReadOnlyList<DisassembledInstruction> Instructions,
    int TotalBytesDisassembled);

public interface IDisassemblyEngine
{
    Task<DisassemblyResult> DisassembleAsync(
        int processId,
        nuint address,
        int maxInstructions = 20,
        CancellationToken cancellationToken = default);
}
