using System.Globalization;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed record DisassemblyLineOverview(
    string Address,
    string HexBytes,
    string Mnemonic,
    string Operands);

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
                    instr.Operands))
            .ToArray();

        return new DisassemblyOverview(
            $"0x{result.StartAddress:X}",
            lines,
            $"Disassembled {result.Instructions.Count} instructions ({result.TotalBytesDisassembled} bytes) starting at 0x{result.StartAddress:X}.");
    }

    private static nuint ParseAddress(string addressText)
    {
        var normalized = addressText.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return (nuint)ulong.Parse(normalized[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return (nuint)ulong.Parse(normalized, CultureInfo.InvariantCulture);
    }
}
