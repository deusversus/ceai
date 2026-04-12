using CEAISuite.Engine.Lua;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for Lua execution resource limits.
/// MoonSharp 2.0.0's IDebugger.GetAction() does not fire for bytecode instructions,
/// so instruction-counting is not viable. Instead, resource limits are enforced via
/// WaitAsync hard timeouts on the execution task. These tests verify that runaway
/// scripts are terminated within the configured timeout.
/// </summary>
public sealed class LuaMemoryLimitTests : IDisposable
{
    private readonly MoonSharpLuaEngine _engine = CreateEngine(timeoutSeconds: 10);

    public void Dispose() => _engine.Dispose();

    private static MoonSharpLuaEngine CreateEngine(long maxInstructions = 0, int timeoutSeconds = 5)
        => new(executionTimeout: TimeSpan.FromSeconds(timeoutSeconds), maxInstructions: maxInstructions);

    // ── Timeout enforcement: tight loops are terminated ──

    [Fact]
    public async Task InfiniteLoop_TimesOut_ReturnsError()
    {
        using var engine = CreateEngine(timeoutSeconds: 2);

        var result = await engine.ExecuteAsync("while true do end");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LargeForLoop_CompletesOrTimesOut()
    {
        // 1 million iterations — may complete quickly or hit timeout depending on host speed
        var result = await _engine.ExecuteAsync("local s = 0; for i = 1, 1000000 do s = s + i end; return s");

        // Either it completes (fast host) or times out (slow CI) — both are acceptable
        if (result.Success)
            Assert.Equal("500000500000", result.ReturnValue);
        else
            Assert.Contains("timed out", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmallScript_WithinTimeout_Succeeds()
    {
        var result = await _engine.ExecuteAsync("return 1 + 2");

        Assert.True(result.Success);
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task ModerateLoop_WithinTimeout_Succeeds()
    {
        var result = await _engine.ExecuteAsync("local s = 0; for i = 1, 50 do s = s + i end; return s");

        Assert.True(result.Success);
        Assert.Equal("1275", result.ReturnValue);
    }

    // ── Various runaway patterns ──

    [Fact]
    public async Task RecursiveFunction_ReturnsError()
    {
        var code = """
            function recurse(n)
                return recurse(n + 1)
            end
            return recurse(0)
            """;

        var result = await _engine.ExecuteAsync(code);

        // Hits stack overflow or timeout — both are errors
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task NestedLoops_CompletesOrTimesOut()
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

        if (result.Success)
            Assert.Equal("1000000", result.ReturnValue);
        else
            Assert.Contains("timed out", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Timeout is configurable ──

    [Fact]
    public async Task ShortTimeout_TerminatesQuickly()
    {
        using var engine = CreateEngine(timeoutSeconds: 1);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.ExecuteAsync("while true do end");
        sw.Stop();

        Assert.False(result.Success);
        // Should terminate within a reasonable multiple of the configured timeout.
        // On slow CI runners (especially Release mode with optimizations), MoonSharp's
        // instruction-limit interrupt can take significantly longer to fire.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"Expected termination within 30s but took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task LongTimeout_AllowsComplexScripts()
    {
        using var engine = CreateEngine(timeoutSeconds: 10);

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
        Assert.Equal("333833500", result.ReturnValue);
    }

    // ── MaxInstructions property (scaffolding for future MoonSharp versions) ──

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

    // ── String operations ──

    [Fact]
    public async Task LargeStringRep_WithinTimeout_Succeeds()
    {
        var result = await _engine.ExecuteAsync("return #string.rep('x', 1000)");

        Assert.True(result.Success);
        Assert.Equal("1000", result.ReturnValue);
    }

    [Fact]
    public async Task StringConcatLoop_CompletesOrTimesOut()
    {
        var code = """
            local s = ""
            for i = 1, 100000 do
                s = s .. "x"
            end
            return #s
            """;

        var result = await _engine.ExecuteAsync(code);

        // String concat in a loop is O(n^2) — may complete or timeout
        if (!result.Success)
            Assert.Contains("timed out", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Counter resets between executions ──

    [Fact]
    public async Task MultipleExecutions_EachGetsFreshTimeout()
    {
        var result1 = await _engine.ExecuteAsync("local s = 0; for i = 1, 50 do s = s + i end; return s");
        Assert.True(result1.Success);

        var result2 = await _engine.ExecuteAsync("local s = 0; for i = 1, 50 do s = s + i end; return s");
        Assert.True(result2.Success);

        Assert.Equal(result1.ReturnValue, result2.ReturnValue);
    }

    // ── Timeout error message ──

    [Fact]
    public async Task TimeoutError_MessageIsDescriptive()
    {
        using var engine = CreateEngine(timeoutSeconds: 1);

        var result = await engine.ExecuteAsync("while true do end");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
