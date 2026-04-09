using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for CeApiBindings utility functions (stringToHex, hexToString, sleep, getTickCount)
/// and symbol registration. Memory read/write tests are covered in LuaCeApiTests.
/// </summary>
public sealed class CeApiBindingsTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly MoonSharpLuaEngine _engine;

    public CeApiBindingsTests()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(_facade, _assembler, executionTimeout: TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _engine.Dispose();

    // ── stringToHex ──

    [Fact]
    public async Task StringToHex_ConvertsAsciiToHex()
    {
        var result = await _engine.ExecuteAsync("return stringToHex('ABC')", processId: 1234);
        Assert.True(result.Success, result.Error);
        Assert.Equal("41 42 43", result.ReturnValue);
    }

    [Fact]
    public async Task StringToHex_EmptyString_ReturnsEmpty()
    {
        var result = await _engine.ExecuteAsync("return stringToHex('')", processId: 1234);
        Assert.True(result.Success, result.Error);
        Assert.Equal("", result.ReturnValue);
    }

    // ── hexToString ──

    [Fact]
    public async Task HexToString_ConvertsHexToAscii()
    {
        var result = await _engine.ExecuteAsync("return hexToString('48 65 6C 6C 6F')", processId: 1234);
        Assert.True(result.Success, result.Error);
        Assert.Equal("Hello", result.ReturnValue);
    }

    // ── getTickCount ──

    [Fact]
    public async Task GetTickCount_ReturnsPositiveNumber()
    {
        var result = await _engine.ExecuteAsync("return getTickCount()", processId: 1234);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ReturnValue);
        Assert.True(double.Parse(result.ReturnValue, System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    // ── sleep ──

    [Fact]
    public async Task Sleep_DoesNotThrow()
    {
        var result = await _engine.ExecuteAsync("sleep(1)", processId: 1234);
        Assert.True(result.Success, result.Error);
    }

    // ── registerSymbol / unregisterSymbol ──

    [Fact]
    public async Task RegisterSymbol_CreatesGlobalAndAASymbol()
    {
        var result = await _engine.ExecuteAsync("registerSymbol('myAddr', 0x1000)", processId: 1234);
        Assert.True(result.Success, result.Error);

        // Verify the AA engine got the symbol
        Assert.Equal((nuint)0x1000, _assembler.ResolveSymbol("myAddr"));
    }

    [Fact]
    public async Task UnregisterSymbol_RemovesSymbol()
    {
        await _engine.ExecuteAsync("registerSymbol('testSym', 0x2000)", processId: 1234);
        Assert.NotNull(_assembler.ResolveSymbol("testSym"));

        await _engine.ExecuteAsync("unregisterSymbol('testSym')", processId: 1234);
        Assert.Null(_assembler.ResolveSymbol("testSym"));
    }

    // ── getOpenedProcessID ──

    [Fact]
    public async Task GetOpenedProcessID_ReturnsAttachedPid()
    {
        var result = await _engine.ExecuteAsync("return getOpenedProcessID()", processId: 1234);
        Assert.True(result.Success, result.Error);
        Assert.Equal("1234", result.ReturnValue);
    }

    // ── autoAssembleCheck ──

    [Fact]
    public async Task AutoAssembleCheck_ValidScript_ReturnsTrue()
    {
        _assembler.NextParseResult = new ScriptParseResult(true, [], [], "[ENABLE]", "[DISABLE]");

        var result = await _engine.ExecuteAsync("return autoAssembleCheck('[ENABLE]\\nnop\\n[DISABLE]')", processId: 1234);
        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task AutoAssembleCheck_InvalidScript_ReturnsFalse()
    {
        _assembler.NextParseResult = new ScriptParseResult(false, ["syntax error"], [], "", "");

        var result = await _engine.ExecuteAsync("return autoAssembleCheck('garbage')", processId: 1234);
        Assert.True(result.Success, result.Error);
        Assert.Equal("false", result.ReturnValue);
    }

    // ── readBytes ──

    [Fact]
    public async Task ReadBytes_ReturnsLuaTable()
    {
        _facade.WriteMemoryDirect((nuint)0x3000, new byte[] { 0xAA, 0xBB, 0xCC });

        var result = await _engine.ExecuteAsync(@"
            local t = readBytes('0x3000', 3)
            return t[1] .. ',' .. t[2] .. ',' .. t[3]
        ", processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("170,187,204", result.ReturnValue);
    }

    // ── writeBytes ──

    [Fact]
    public async Task WriteBytes_WritesToMemory()
    {
        var result = await _engine.ExecuteAsync(@"
            local t = {0x11, 0x22, 0x33}
            writeBytes('0x4000', t)
        ", processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    // ── No process attached error ──

    [Fact]
    public async Task ReadInteger_NoProcess_Fails()
    {
        // Create engine without attaching process
        using var engine2 = new MoonSharpLuaEngine(_facade, _assembler, executionTimeout: TimeSpan.FromSeconds(5));
        var result = await engine2.ExecuteAsync("return readInteger('0x1000')");

        Assert.False(result.Success);
        Assert.Contains("No process attached", result.Error);
    }
}
