using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for Phase 12B source line resolution:
/// SourceLineInfo record, ISymbolEngine.ResolveSourceLine, DisassembledInstruction
/// source fields, DisassemblyService pass-through, and AI tool output.
/// </summary>
public class SourceLineTests
{
    // ──────────────────────────────────────────────────────────
    // Record construction
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void SourceLineInfo_CreatesCorrectly()
    {
        var info = new SourceLineInfo(@"C:\src\game.cs", 42, (nuint)0x7FF00100);
        Assert.Equal(@"C:\src\game.cs", info.FileName);
        Assert.Equal(42, info.LineNumber);
        Assert.Equal((nuint)0x7FF00100, info.Address);
    }

    [Fact]
    public void DisassembledInstruction_SourceFields_DefaultNull()
    {
        var instr = new DisassembledInstruction(0x7FF00100, "90", "nop", "", 1);
        Assert.Null(instr.SourceFile);
        Assert.Null(instr.SourceLine);
    }

    [Fact]
    public void DisassembledInstruction_SourceFields_CanBeSet()
    {
        var instr = new DisassembledInstruction(0x7FF00100, "90", "nop", "", 1,
            SourceFile: @"C:\src\main.cs", SourceLine: 99);
        Assert.Equal(@"C:\src\main.cs", instr.SourceFile);
        Assert.Equal(99, instr.SourceLine);
    }

    // ──────────────────────────────────────���───────────────────
    // Stub symbol engine
    // ──────────────────��──────────────────────────────���────────

    [Fact]
    public void StubSymbolEngine_ResolveSourceLine_ReturnsNull_WhenNoData()
    {
        var engine = new StubSymbolEngine();
        Assert.Null(engine.ResolveSourceLine(0x7FF00100));
    }

    [Fact]
    public void StubSymbolEngine_ResolveSourceLine_ReturnsInfo_WhenAdded()
    {
        var engine = new StubSymbolEngine();
        engine.AddSourceLine(0x7FF00100, @"C:\src\game.cs", 42);

        var result = engine.ResolveSourceLine(0x7FF00100);
        Assert.NotNull(result);
        Assert.Equal(@"C:\src\game.cs", result.FileName);
        Assert.Equal(42, result.LineNumber);
    }

    // ──────��─────────────────────────��─────────────────────────
    // DisassemblyService pass-through
    // ──────��─────────────────────���─────────────────────────────

    [Fact]
    public async Task DisassemblyService_PassesThroughSourceLine()
    {
        var stubEngine = new StubDisassemblyEngine();
        stubEngine.NextInstructions =
        [
            new DisassembledInstruction(0x7FF00100, "90", "nop", "", 1,
                SourceFile: @"C:\src\main.cs", SourceLine: 10),
            new DisassembledInstruction(0x7FF00101, "C3", "ret", "", 1)
        ];

        var service = new DisassemblyService(stubEngine);
        var overview = await service.DisassembleAtAsync(1000, "0x7FF00100", 2);

        Assert.Equal(2, overview.Lines.Count);
        Assert.Equal(@"C:\src\main.cs", overview.Lines[0].SourceFile);
        Assert.Equal(10, overview.Lines[0].SourceLine);
        Assert.Null(overview.Lines[1].SourceFile);
        Assert.Null(overview.Lines[1].SourceLine);
    }

    [Fact]
    public void DisassemblyLineOverview_SourceFields_DefaultNull()
    {
        var line = new DisassemblyLineOverview("0x100", "90", "nop", "");
        Assert.Null(line.SourceFile);
        Assert.Null(line.SourceLine);
    }
}
