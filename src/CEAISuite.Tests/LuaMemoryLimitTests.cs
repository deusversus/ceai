using CEAISuite.Engine.Lua;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for Lua instruction-count-based execution limits.
/// MoonSharp 2.0.0 does not expose direct memory tracking, so we enforce
/// resource limits via an instruction counter attached through IDebugger.
/// </summary>
public sealed class LuaMemoryLimitTests : IDisposable
{
    private readonly MoonSharpLuaEngine _engine = CreateEngine(maxInstructions: 10_000);

    public void Dispose() => _engine.Dispose();

    private static MoonSharpLuaEngine CreateEngine(long maxInstructions = 0, int timeoutSeconds = 5)
        => new(executionTimeout: TimeSpan.FromSeconds(timeoutSeconds), maxInstructions: maxInstructions);

    // ── Instruction limit: tight loops are terminated ──

    [Fact]
    public async Task InfiniteLoop_ExceedsInstructionLimit_ReturnsError()
    {
        var result = await _engine.ExecuteAsync("while true do end");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Instruction limit exceeded", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeForLoop_ExceedsInstructionLimit_ReturnsError()
    {
        // 1 million iterations will far exceed the 10,000 instruction limit
        var result = await _engine.ExecuteAsync("local s = 0; for i = 1, 1000000 do s = s + i end; return s");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Instruction limit exceeded", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmallScript_WithinLimit_Succeeds()
    {
        // A simple script that uses very few instructions should succeed
        var result = await _engine.ExecuteAsync("return 1 + 2");

        Assert.True(result.Success);
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task ModerateLoop_WithinLimit_Succeeds()
    {
        // A small loop should stay within the 10,000 instruction limit
        var result = await _engine.ExecuteAsync("local s = 0; for i = 1, 50 do s = s + i end; return s");

        Assert.True(result.Success);
        Assert.Equal("1275", result.ReturnValue);
    }

    // ── Instruction limit: various runaway patterns ──

    [Fact]
    public async Task RecursiveFunction_ExceedsLimit_ReturnsError()
    {
        var code = """
            function recurse(n)
                return recurse(n + 1)
            end
            return recurse(0)
            """;

        var result = await _engine.ExecuteAsync(code);

        // Either hits instruction limit or stack overflow — both are errors
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task NestedLoops_ExceedsLimit_ReturnsError()
    {
        var code = """
            local s = 0
            for i = 1, 1000 do
                for j = 1, 1000 do
                    s = s + 1
                end
            end
            return s
            """;

        var result = await _engine.ExecuteAsync(code);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Instruction limit exceeded", result.Error, StringComparison.Ordinal);
    }

    // ── Configurable limit ──

    [Fact]
    public async Task VeryLowLimit_TerminatesQuickly()
    {
        using var engine = CreateEngine(maxInstructions: 10);

        // Even a trivial script may exceed 10 instructions due to setup overhead
        var result = await engine.ExecuteAsync("local x = 1; local y = 2; local z = x + y; return z");

        // With only 10 instructions allowed, this should fail
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Instruction limit exceeded", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HighLimit_AllowsComplexScripts()
    {
        using var engine = CreateEngine(maxInstructions: 1_000_000);

        var code = """
            local t = {}
            for i = 1, 1000 do
                t[i] = i * i
            end
            local sum = 0
            for i = 1, 1000 do
                sum = sum + t[i]
            end
            return sum
            """;

        var result = await engine.ExecuteAsync(code);

        Assert.True(result.Success);
        // Sum of squares from 1 to 1000 = 1000*1001*2001/6 = 333833500
        Assert.Equal("333833500", result.ReturnValue);
    }

    // ── No limit (zero) means unlimited ──

    [Fact]
    public async Task ZeroMaxInstructions_NoLimit_LoopSucceeds()
    {
        using var engine = CreateEngine(maxInstructions: 0, timeoutSeconds: 5);

        // With no instruction limit, a moderate loop should complete fine
        var result = await engine.ExecuteAsync("local s = 0; for i = 1, 10000 do s = s + i end; return s");

        Assert.True(result.Success);
        Assert.Equal("50005000", result.ReturnValue);
    }

    // ── MaxInstructions property ──

    [Fact]
    public void MaxInstructions_Property_ReflectsConstructorValue()
    {
        using var engine = CreateEngine(maxInstructions: 42_000);
        Assert.Equal(42_000, engine.MaxInstructions);
    }

    [Fact]
    public void MaxInstructions_Property_ZeroByDefault()
    {
        using var engine = new MoonSharpLuaEngine();
        Assert.Equal(0, engine.MaxInstructions);
    }

    // ── Instruction limit with string operations (memory-intensive patterns) ──

    [Fact]
    public async Task LargeStringRep_WithinLimit_Succeeds()
    {
        // string.rep creates a repeated string — tests memory-intensive but low-instruction-count code
        using var engine = CreateEngine(maxInstructions: 100_000);

        var result = await engine.ExecuteAsync("return #string.rep('x', 1000)");

        Assert.True(result.Success);
        Assert.Equal("1000", result.ReturnValue);
    }

    [Fact]
    public async Task StringConcatLoop_ExceedsInstructionLimit()
    {
        // Repeated string concatenation in a loop — both memory and instruction intensive
        var code = """
            local s = ""
            for i = 1, 100000 do
                s = s .. "x"
            end
            return #s
            """;

        var result = await _engine.ExecuteAsync(code);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Instruction limit exceeded", result.Error, StringComparison.Ordinal);
    }

    // ── Large table creation under instruction limit ──

    [Fact]
    public async Task LargeTableCreation_ExceedsInstructionLimit()
    {
        var code = """
            local t = {}
            for i = 1, 100000 do
                t[i] = { value = i, name = "item" .. tostring(i) }
            end
            return #t
            """;

        var result = await _engine.ExecuteAsync(code);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Instruction limit exceeded", result.Error, StringComparison.Ordinal);
    }

    // ── Counter resets between executions ──

    [Fact]
    public async Task InstructionCounter_ResetsPerExecution()
    {
        // Run a script that uses some instructions
        var result1 = await _engine.ExecuteAsync("local s = 0; for i = 1, 50 do s = s + i end; return s");
        Assert.True(result1.Success);

        // Run the same script again — should succeed because counter was reset
        var result2 = await _engine.ExecuteAsync("local s = 0; for i = 1, 50 do s = s + i end; return s");
        Assert.True(result2.Success);

        Assert.Equal(result1.ReturnValue, result2.ReturnValue);
    }

    // ── Error message is descriptive ──

    [Fact]
    public async Task InstructionLimitError_ContainsMaxCount()
    {
        using var engine = CreateEngine(maxInstructions: 500);

        var result = await engine.ExecuteAsync("while true do end");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("500", result.Error, StringComparison.Ordinal);
    }
}
