using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubDisassemblyEngine : IDisassemblyEngine
{
    public List<DisassembledInstruction> NextInstructions { get; set; } =
    [
        new(0x7FF00100, "48 89 5C 24 08", "mov", "[rsp+8],rbx", 5),
        new(0x7FF00105, "48 83 EC 20", "sub", "rsp,20h", 4),
        new(0x7FF00109, "E8 12 34 56 00", "call", "0x7FF56420", 5),
        new(0x7FF0010E, "48 8B D8", "mov", "rbx,rax", 3),
        new(0x7FF00111, "48 85 C0", "test", "rax,rax", 3),
        new(0x7FF00114, "74 0A", "je", "0x7FF00120", 2),
        new(0x7FF00116, "48 8B 48 10", "mov", "rcx,[rax+10h]", 4),
        new(0x7FF0011A, "FF 15 00 30 00 00", "call", "[rip+3000h]", 6),
        new(0x7FF00120, "48 8B 5C 24 30", "mov", "rbx,[rsp+30h]", 5),
        new(0x7FF00125, "48 83 C4 20", "add", "rsp,20h", 4),
        new(0x7FF00129, "C3", "ret", "", 1)
    ];

    public Task<DisassemblyResult> DisassembleAsync(
        int processId, nuint address, int maxInstructions = 20,
        CancellationToken cancellationToken = default)
    {
        var instructions = NextInstructions.Take(maxInstructions).ToList();
        var totalBytes = instructions.Sum(i => i.Length);
        return Task.FromResult(new DisassemblyResult(address, instructions, totalBytes));
    }
}
