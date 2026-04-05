using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class SymbolEngineTests
{
    // ── SymbolInfo record ──

    [Fact]
    public void SymbolInfo_DisplayName_WithDisplacement()
    {
        var info = new SymbolInfo("CreateFileW", "kernel32", 0x1A);
        Assert.Equal("kernel32!CreateFileW+0x1A", info.DisplayName);
    }

    [Fact]
    public void SymbolInfo_DisplayName_ZeroDisplacement()
    {
        var info = new SymbolInfo("main", "game", 0);
        Assert.Equal("game!main", info.DisplayName);
    }

    // ── DisassembledInstruction backward compat ──

    [Fact]
    public void DisassembledInstruction_SymbolName_DefaultNull()
    {
        var instr = new DisassembledInstruction((nuint)0x1000, "90", "nop", "", 1);
        Assert.Null(instr.SymbolName);
    }

    [Fact]
    public void DisassembledInstruction_WithSymbol()
    {
        var instr = new DisassembledInstruction((nuint)0x1000, "90", "nop", "", 1, "kernel32!Sleep");
        Assert.Equal("kernel32!Sleep", instr.SymbolName);
    }

    // ── StubSymbolEngine ──

    [Fact]
    public void StubSymbolEngine_ResolvesKnownAddress()
    {
        var engine = new StubSymbolEngine();
        engine.AddSymbol((nuint)0x7FF00100, "CreateFileW", "kernel32", 0x1A);

        var info = engine.ResolveAddress((nuint)0x7FF00100);

        Assert.NotNull(info);
        Assert.Equal("CreateFileW", info!.FunctionName);
        Assert.Equal("kernel32", info.ModuleName);
        Assert.Equal(0x1AUL, info.Displacement);
    }

    [Fact]
    public void StubSymbolEngine_ReturnsNullForUnknown()
    {
        var engine = new StubSymbolEngine();
        Assert.Null(engine.ResolveAddress((nuint)0xDEAD));
    }

    // ── Disassembly with symbols end-to-end ──

    [Fact]
    public async Task DisassemblyService_PassesThroughSymbolName()
    {
        var stubDisasm = new StubDisassemblyEngine();
        // Replace first instruction with one that has a symbol
        stubDisasm.NextInstructions =
        [
            new((nuint)0x7FF00100, "48 89 5C 24 08", "mov", "[rsp+8],rbx", 5, "game!PlayerUpdate+0x10"),
            new((nuint)0x7FF00105, "C3", "ret", "", 1)
        ];

        var svc = new DisassemblyService(stubDisasm);
        var result = await svc.DisassembleAtAsync(1234, "0x7FF00100", 10);

        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("game!PlayerUpdate+0x10", result.Lines[0].SymbolName);
        Assert.Null(result.Lines[1].SymbolName);
    }

    [Fact]
    public async Task DisassemblyService_NullSymbol_BackwardCompat()
    {
        var stubDisasm = new StubDisassemblyEngine();
        var svc = new DisassemblyService(stubDisasm);

        var result = await svc.DisassembleAtAsync(1234, "0x7FF00100", 5);

        // All default stub instructions have null symbol
        Assert.All(result.Lines, l => Assert.Null(l.SymbolName));
    }

    // ── DisassemblyLineOverview ──

    [Fact]
    public void DisassemblyLineOverview_SymbolName_DefaultNull()
    {
        var overview = new DisassemblyLineOverview("0x1000", "90", "nop", "");
        Assert.Null(overview.SymbolName);
    }

    [Fact]
    public void DisassemblyLineOverview_WithSymbol()
    {
        var overview = new DisassemblyLineOverview("0x1000", "90", "nop", "", "ntdll!RtlUserThreadStart");
        Assert.Equal("ntdll!RtlUserThreadStart", overview.SymbolName);
    }
}
