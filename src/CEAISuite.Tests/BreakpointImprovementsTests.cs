using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class BreakpointImprovementsTests
{
    // ── 7B.1: Conditional Breakpoints ──

    [Fact]
    public async Task ConditionalBreakpoint_StoresCondition()
    {
        var engine = new StubBreakpointEngine();
        var condition = new BreakpointCondition("RAX == 0x1000", BreakpointConditionType.RegisterCompare);

        var bp = await engine.SetConditionalBreakpointAsync(
            1234, (nuint)0x5000, BreakpointType.HardwareExecute, condition);

        Assert.NotNull(bp.Condition);
        Assert.Equal("RAX == 0x1000", bp.Condition.Expression);
        Assert.Equal(BreakpointConditionType.RegisterCompare, bp.Condition.Type);
    }

    [Fact]
    public void ConditionType_RegisterCompare_Exists()
    {
        var cond = new BreakpointCondition("RBX > 100", BreakpointConditionType.RegisterCompare);
        Assert.Equal(BreakpointConditionType.RegisterCompare, cond.Type);
    }

    [Fact]
    public void ConditionType_MemoryCompare_Exists()
    {
        var cond = new BreakpointCondition("[0x12345678] != 0", BreakpointConditionType.MemoryCompare);
        Assert.Equal(BreakpointConditionType.MemoryCompare, cond.Type);
    }

    [Fact]
    public void ConditionType_HitCount_Exists()
    {
        var cond = new BreakpointCondition("hitcount >= 5", BreakpointConditionType.HitCount);
        Assert.Equal(BreakpointConditionType.HitCount, cond.Type);
    }

    [Fact]
    public void Condition_InvalidExpression_HandledGracefully()
    {
        // A condition with garbage text should still create without throwing
        var cond = new BreakpointCondition("asdf!@#$", BreakpointConditionType.RegisterCompare);
        Assert.Equal("asdf!@#$", cond.Expression);
    }

    // ── 7B.3: Thread Filter ──

    [Fact]
    public async Task ThreadFilter_StoredOnDescriptor()
    {
        var engine = new StubBreakpointEngine();
        var condition = new BreakpointCondition("RAX > 0", BreakpointConditionType.RegisterCompare);

        var bp = await engine.SetConditionalBreakpointAsync(
            1234, (nuint)0x5000, BreakpointType.HardwareExecute, condition,
            threadFilter: 42);

        Assert.Equal(42, bp.ThreadFilter);
    }

    [Fact]
    public async Task ThreadFilter_NullByDefault()
    {
        var engine = new StubBreakpointEngine();
        var condition = new BreakpointCondition("RAX > 0", BreakpointConditionType.RegisterCompare);

        var bp = await engine.SetConditionalBreakpointAsync(
            1234, (nuint)0x5000, BreakpointType.HardwareExecute, condition);

        Assert.Null(bp.ThreadFilter);
    }

    // ── 7B.2: Break-and-Trace ──

    [Fact]
    public async Task TraceResult_ContainsEntries()
    {
        var engine = new StubBreakpointEngine();
        engine.NextTraceResult = new TraceResult("bp-1", new List<TraceEntry>
        {
            new((nuint)0x1000, "mov rax, rbx", 1, new Dictionary<string, string>(), DateTimeOffset.UtcNow),
            new((nuint)0x1003, "add rax, 1", 1, new Dictionary<string, string>(), DateTimeOffset.UtcNow),
            new((nuint)0x1006, "ret", 1, new Dictionary<string, string>(), DateTimeOffset.UtcNow)
        }, false, false);

        var result = await engine.TraceFromBreakpointAsync(1234, (nuint)0x1000);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal("mov rax, rbx", result.Entries[0].Disassembly);
        Assert.False(result.MaxDepthReached);
    }

    [Fact]
    public async Task TraceResult_Truncated_WhenMaxReached()
    {
        var engine = new StubBreakpointEngine();
        engine.NextTraceResult = new TraceResult("bp-1", new List<TraceEntry>
        {
            new((nuint)0x1000, "nop", 1, new Dictionary<string, string>(), DateTimeOffset.UtcNow)
        }, true, true);

        var result = await engine.TraceFromBreakpointAsync(1234, (nuint)0x1000, maxInstructions: 1);

        Assert.True(result.MaxDepthReached);
        Assert.True(result.WasTruncated);
    }

    // ── Service passthrough ──

    [Fact]
    public async Task ServicePassthrough_ConditionalBreakpoint()
    {
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine);
        var condition = new BreakpointCondition("RAX == 0xFF", BreakpointConditionType.RegisterCompare);

        var bp = await service.SetConditionalBreakpointAsync(
            1234, "0x5000", BreakpointType.HardwareWrite, condition, BreakpointMode.Hardware);

        Assert.Equal("0x5000", bp.Address);
        Assert.True(bp.IsEnabled);
    }

    [Fact]
    public async Task ServicePassthrough_TraceFromBreakpoint()
    {
        var engine = new StubBreakpointEngine();
        engine.NextTraceResult = new TraceResult("bp-t", new List<TraceEntry>
        {
            new((nuint)0x1000, "nop", 1, new Dictionary<string, string>(), DateTimeOffset.UtcNow)
        }, false, false);
        var service = new BreakpointService(engine);

        var result = await service.TraceFromBreakpointAsync(1234, "0x1000");

        Assert.Single(result.Entries);
        Assert.Equal("nop", result.Entries[0].Disassembly);
    }
}
