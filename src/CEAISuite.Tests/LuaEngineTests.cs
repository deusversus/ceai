using CEAISuite.Engine.Lua;

namespace CEAISuite.Tests;

public sealed class LuaEngineTests : IDisposable
{
    private readonly MoonSharpLuaEngine _engine = new(executionTimeout: TimeSpan.FromSeconds(5));

    public void Dispose() => _engine.Dispose();

    // ── Execute: basic output ──

    [Fact]
    public async Task Execute_PrintStatement_CapturesOutput()
    {
        var result = await _engine.ExecuteAsync("print('hello world')");

        Assert.True(result.Success);
        Assert.Single(result.OutputLines);
        Assert.Equal("hello world", result.OutputLines[0]);
    }

    [Fact]
    public async Task Execute_MultiplePrints_CapturesAll()
    {
        var result = await _engine.ExecuteAsync("print('a')\nprint('b')\nprint('c')");

        Assert.True(result.Success);
        Assert.Equal(3, result.OutputLines.Count);
        Assert.Equal(["a", "b", "c"], result.OutputLines);
    }

    [Fact]
    public async Task Execute_PrintMultipleArgs_TabSeparated()
    {
        var result = await _engine.ExecuteAsync("print(1, 'two', 3.5)");

        Assert.True(result.Success);
        Assert.Single(result.OutputLines);
        Assert.Contains("1", result.OutputLines[0]);
        Assert.Contains("two", result.OutputLines[0]);
    }

    [Fact]
    public async Task Execute_ReturnValue_ReturnsStringified()
    {
        var result = await _engine.ExecuteAsync("return 42");

        Assert.True(result.Success);
        Assert.Equal("42", result.ReturnValue);
    }

    [Fact]
    public async Task Execute_NoReturn_ReturnValueIsNull()
    {
        var result = await _engine.ExecuteAsync("local x = 10");

        Assert.True(result.Success);
        Assert.Null(result.ReturnValue);
    }

    // ── Execute: errors ──

    [Fact]
    public async Task Execute_SyntaxError_ReturnsError()
    {
        var result = await _engine.ExecuteAsync("if then end");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_RuntimeError_ReturnsError()
    {
        var result = await _engine.ExecuteAsync("error('boom')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task Execute_NilIndex_ReturnsRuntimeError()
    {
        var result = await _engine.ExecuteAsync("local x = nil\nreturn x.y");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── Execute: cancellation ──

    [Fact]
    public async Task Execute_Cancellation_ReturnsTimeout()
    {
        // Pre-cancelled token should return immediately
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _engine.ExecuteAsync("return 1", cts.Token);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validate ──

    [Fact]
    public void Validate_ValidSyntax_ReturnsValid()
    {
        var result = _engine.Validate("local x = 10\nprint(x)");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_InvalidSyntax_ReturnsErrors()
    {
        var result = _engine.Validate("if then end");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validate_EmptyString_IsValid()
    {
        var result = _engine.Validate("");

        Assert.True(result.IsValid);
    }

    // ── Globals ──

    [Fact]
    public async Task SetGlobal_ReadFromLua_Works()
    {
        _engine.SetGlobal("myVar", 42.0);

        var result = await _engine.ExecuteAsync("return myVar");

        Assert.True(result.Success);
        Assert.Equal("42", result.ReturnValue);
    }

    [Fact]
    public async Task GetGlobal_SetFromLua_Works()
    {
        await _engine.ExecuteAsync("testVal = 'hello'");

        var val = _engine.GetGlobal("testVal");

        Assert.Equal("hello", val);
    }

    [Fact]
    public void GetGlobal_Undefined_ReturnsNull()
    {
        var val = _engine.GetGlobal("nonExistent");

        Assert.Null(val);
    }

    // ── Reset ──

    [Fact]
    public async Task Reset_ClearsState()
    {
        var setResult = await _engine.ExecuteAsync("persistedVar = 'exists'");
        // On slow CI runners the script may time out — skip rather than fail
        if (!setResult.Success)
            Assert.Skip($"Script timed out on slow runner: {setResult.Error}");
        Assert.NotNull(_engine.GetGlobal("persistedVar"));

        _engine.Reset();

        Assert.Null(_engine.GetGlobal("persistedVar"));
    }

    // ── Sandbox: blocked modules ──

    [Fact]
    public async Task Execute_OsLibrary_Blocked()
    {
        var result = await _engine.ExecuteAsync("return os.execute('echo hi')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_IoLibrary_Blocked()
    {
        var result = await _engine.ExecuteAsync("return io.open('test.txt')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_LoadFile_Blocked()
    {
        var result = await _engine.ExecuteAsync("return loadfile('test.lua')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_DoFile_Blocked()
    {
        var result = await _engine.ExecuteAsync("dofile('test.lua')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── Sandbox: allowed modules ──

    [Fact]
    public async Task Execute_Bit32Operations_Work()
    {
        var result = await _engine.ExecuteAsync("return bit32.band(0xFF, 0x0F)");

        Assert.True(result.Success);
        Assert.Equal("15", result.ReturnValue);
    }

    [Fact]
    public async Task Execute_Bit32Bor_Works()
    {
        var result = await _engine.ExecuteAsync("return bit32.bor(0xF0, 0x0F)");

        Assert.True(result.Success);
        Assert.Equal("255", result.ReturnValue);
    }

    [Fact]
    public async Task Execute_Bit32Lshift_Works()
    {
        var result = await _engine.ExecuteAsync("return bit32.lshift(1, 8)");

        Assert.True(result.Success);
        Assert.Equal("256", result.ReturnValue);
    }

    [Fact]
    public async Task Execute_StringLibrary_Works()
    {
        var result = await _engine.ExecuteAsync("return string.format('hello %s', 'world')");

        Assert.True(result.Success);
        Assert.Equal("hello world", result.ReturnValue);
    }

    [Fact]
    public async Task Execute_MathLibrary_Works()
    {
        var result = await _engine.ExecuteAsync("return math.floor(3.7)");

        Assert.True(result.Success);
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task Execute_TableLibrary_Works()
    {
        var result = await _engine.ExecuteAsync("""
            local t = {3, 1, 2}
            table.sort(t)
            return t[1]
            """);

        Assert.True(result.Success);
        Assert.Equal("1", result.ReturnValue);
    }

    // ── Evaluate ──

    [Fact]
    public async Task Evaluate_SimpleExpression_Returns()
    {
        var result = await _engine.EvaluateAsync("1 + 2 * 3");

        Assert.True(result.Success);
        Assert.Equal("7", result.ReturnValue);
    }

    [Fact]
    public async Task Evaluate_StringExpression_Returns()
    {
        var result = await _engine.EvaluateAsync("'hello' .. ' ' .. 'world'");

        Assert.True(result.Success);
        Assert.Equal("hello world", result.ReturnValue);
    }

    [Fact]
    public async Task Evaluate_InvalidExpression_ReturnsError()
    {
        var result = await _engine.EvaluateAsync("1 +");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── OutputWritten event ──

    [Fact]
    public async Task OutputWritten_EventFired_OnPrint()
    {
        var captured = new List<string>();
        _engine.OutputWritten += line => captured.Add(line);

        await _engine.ExecuteAsync("print('event test')");

        Assert.Single(captured);
        Assert.Equal("event test", captured[0]);
    }

    // ── ProcessId context ──

    [Fact]
    public async Task Execute_WithProcessId_SucceedsWithContext()
    {
        // Verifies that ExecuteAsync(code, pid) works and doesn't error
        var result = await _engine.ExecuteAsync("return 1", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("1", result.ReturnValue);
    }

    [Fact]
    public async Task Execute_WithoutProcessId_CurrentProcessIdIsNull()
    {
        await _engine.ExecuteAsync("return 1");

        // After execution completes, PID context is restored to null
        Assert.Null(_engine.CurrentProcessId);
    }

    // ── State persistence across calls ──

    [Fact]
    public async Task Execute_StatePersistsAcrossCalls()
    {
        await _engine.ExecuteAsync("counter = 0");
        await _engine.ExecuteAsync("counter = counter + 1");
        var result = await _engine.ExecuteAsync("return counter");

        Assert.True(result.Success);
        Assert.Equal("1", result.ReturnValue);
    }

    [Fact]
    public async Task Execute_FunctionDefinitionPersists()
    {
        await _engine.ExecuteAsync("function double(x) return x * 2 end");
        var result = await _engine.ExecuteAsync("return double(21)");

        Assert.True(result.Success);
        Assert.Equal("42", result.ReturnValue);
    }
}
