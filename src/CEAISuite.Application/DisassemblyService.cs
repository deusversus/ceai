using System.Globalization;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed record DisassemblyLineOverview(
    string Address,
    string HexBytes,
    string Mnemonic,
    string Operands,
    string? SymbolName = null);

public sealed record DisassemblyOverview(
    string StartAddress,
    IReadOnlyList<DisassemblyLineOverview> Lines,
    string Summary);

public sealed class DisassemblyService(IDisassemblyEngine disassemblyEngine)
{
    public async Task<DisassemblyOverview> DisassembleAtAsync(
        int processId,
        string addressText,
        int maxInstructions = 20,
        CancellationToken cancellationToken = default)
    {
        var address = ParseAddress(addressText);
        var result = await disassemblyEngine.DisassembleAsync(processId, address, maxInstructions, cancellationToken);

        var lines = result.Instructions
            .Select(
                instr => new DisassemblyLineOverview(
                    $"0x{instr.Address:X}",
                    instr.HexBytes,
                    instr.Mnemonic,
                    instr.Operands,
                    instr.SymbolName))
            .ToArray();

        return new DisassemblyOverview(
            $"0x{result.StartAddress:X}",
            lines,
            $"Disassembled {result.Instructions.Count} instructions ({result.TotalBytesDisassembled} bytes) starting at 0x{result.StartAddress:X}.");
    }

    private static nuint ParseAddress(string addressText) => AddressTableService.ParseAddress(addressText);
}
