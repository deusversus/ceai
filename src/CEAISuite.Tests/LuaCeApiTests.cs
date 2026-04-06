using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public sealed class LuaCeApiTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaCeApiTests()
    {
        // Set up facade with test process attached
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(_facade, _assembler, TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _engine.Dispose();

    // ── readInteger / writeInteger ──

    [Fact]
    public async Task ReadInteger_ValidAddress_ReturnsValue()
    {
        _facade.WriteMemoryDirect((nuint)0x1000, BitConverter.GetBytes(42));

        var result = await _engine.ExecuteAsync("return readInteger('0x1000')", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("42", result.ReturnValue);
    }

    [Fact]
    public async Task WriteInteger_ValidAddress_WritesValue()
    {
        var result = await _engine.ExecuteAsync("writeInteger('0x2000', 99)", processId: 1234);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ReadFloat_ValidAddress_ReturnsValue()
    {
        _facade.WriteMemoryDirect((nuint)0x1000, BitConverter.GetBytes(3.14f));

        var result = await _engine.ExecuteAsync("return readFloat('0x1000')", processId: 1234);

        Assert.True(result.Success);
        Assert.NotNull(result.ReturnValue);
        Assert.StartsWith("3.14", result.ReturnValue);
    }

    [Fact]
    public async Task WriteFloat_ValidAddress_Succeeds()
    {
        var result = await _engine.ExecuteAsync("writeFloat('0x3000', 2.5)", processId: 1234);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ReadSmallInteger_ValidAddress_ReturnsValue()
    {
        _facade.WriteMemoryDirect((nuint)0x1000, BitConverter.GetBytes((short)256));

        var result = await _engine.ExecuteAsync("return readSmallInteger('0x1000')", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("256", result.ReturnValue);
    }

    [Fact]
    public async Task ReadBytes_ValidAddress_ReturnsTable()
    {
        _facade.WriteMemoryDirect((nuint)0x1000, new byte[] { 0xAA, 0xBB, 0xCC });

        var result = await _engine.ExecuteAsync("""
            local bytes = readBytes('0x1000', 3)
            return bytes[1]
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("170", result.ReturnValue); // 0xAA = 170
    }

    // ── readInteger without process ──

    [Fact]
    public async Task ReadInteger_NoProcess_ThrowsLuaError()
    {
        // Create engine WITHOUT attaching a process
        using var noProcessEngine = new MoonSharpLuaEngine(new StubEngineFacade(), null, TimeSpan.FromSeconds(5));

        var result = await noProcessEngine.ExecuteAsync("return readInteger('0x1000')");

        Assert.False(result.Success);
        Assert.Contains("No process attached", result.Error);
    }

    // ── getAddress ──

    [Fact]
    public async Task GetAddress_HexString_ReturnsAddress()
    {
        var result = await _engine.ExecuteAsync("return getAddress('0x400000')", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("4194304", result.ReturnValue); // 0x400000 = 4194304
    }

    [Fact]
    public async Task GetAddress_ModulePlusOffset_Resolves()
    {
        var result = await _engine.ExecuteAsync("return getAddress('main.exe+0x100')", processId: 1234);

        Assert.True(result.Success);
        // main.exe base = 0x400000, offset 0x100 => 0x400100 = 4194560
        Assert.Equal("4194560", result.ReturnValue);
    }

    [Fact]
    public async Task GetAddress_InvalidExpression_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("""
            local addr = getAddress('nonexistent_symbol')
            if addr == nil then return 'nil' end
            return tostring(addr)
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("nil", result.ReturnValue);
    }

    [Fact]
    public async Task GetModuleBaseAddress_KnownModule_ReturnsBase()
    {
        var result = await _engine.ExecuteAsync("return getModuleBaseAddress('main.exe')", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("4194304", result.ReturnValue); // 0x400000
    }

    [Fact]
    public async Task GetModuleBaseAddress_UnknownModule_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("""
            local addr = getModuleBaseAddress('nonexistent.dll')
            if addr == nil then return 'nil' end
            return tostring(addr)
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("nil", result.ReturnValue);
    }

    // ── Process Functions ──

    [Fact]
    public async Task GetProcessId_ReturnsCurrentPid()
    {
        var result = await _engine.ExecuteAsync("return getProcessId()", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("1234", result.ReturnValue);
    }

    [Fact]
    public async Task GetOpenedProcessID_Alias_Works()
    {
        var result = await _engine.ExecuteAsync("return getOpenedProcessID()", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("1234", result.ReturnValue);
    }

    // ── Auto Assembler ──

    [Fact]
    public async Task AutoAssemble_DelegatesToEngine()
    {
        _assembler.NextEnableResult = new ScriptExecutionResult(true, null, [], []);

        var result = await _engine.ExecuteAsync("""
            return autoAssemble('[ENABLE]\nnop\n[DISABLE]\nnop')
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task AutoAssembleCheck_Valid_ReturnsTrue()
    {
        _assembler.NextParseResult = new ScriptParseResult(true, [], [], "[ENABLE]", "[DISABLE]");

        var result = await _engine.ExecuteAsync("""
            return autoAssembleCheck('[ENABLE]\nnop\n[DISABLE]\nnop')
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task AutoAssembleCheck_Invalid_ReturnsFalse()
    {
        _assembler.NextParseResult = new ScriptParseResult(false, ["error"], [], null, null);

        var result = await _engine.ExecuteAsync("""
            return autoAssembleCheck('garbage script')
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("false", result.ReturnValue);
    }

    // ── Utility Functions ──

    [Fact]
    public async Task GetTickCount_ReturnsNumber()
    {
        var result = await _engine.ExecuteAsync("return getTickCount()");

        Assert.True(result.Success);
        Assert.NotNull(result.ReturnValue);
        Assert.True(double.Parse(result.ReturnValue, System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task Sleep_DoesNotHang()
    {
        var result = await _engine.ExecuteAsync("sleep(10)", processId: 1234);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ShowMessage_RoutesToPrint()
    {
        var result = await _engine.ExecuteAsync("showMessage('hello')", processId: 1234);

        Assert.True(result.Success);
        Assert.Contains("hello", result.OutputLines);
    }

    [Fact]
    public async Task StringToHex_Converts()
    {
        var result = await _engine.ExecuteAsync("return stringToHex('AB')", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("41 42", result.ReturnValue);
    }

    [Fact]
    public async Task HexToString_Converts()
    {
        var result = await _engine.ExecuteAsync("return hexToString('41 42')", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("AB", result.ReturnValue);
    }

    // ── Pointer Chain ──

    [Fact]
    public async Task GetAddress_PointerChain_Dereferences()
    {
        // Set up: [0x1000] = 0x2000, and we want [0x1000]+0x10 => 0x2010
        _facade.WriteMemoryDirect((nuint)0x1000, BitConverter.GetBytes((ulong)0x2000));

        var result = await _engine.ExecuteAsync("return getAddress('[0x1000]+0x10')", processId: 1234);

        Assert.True(result.Success);
        // 0x2000 + 0x10 = 0x2010 = 8208
        Assert.Equal("8208", result.ReturnValue);
    }

    // ── Read/Write round-trip ──

    [Fact]
    public async Task WriteInteger_ReadInteger_RoundTrip()
    {
        var result = await _engine.ExecuteAsync("""
            writeInteger('0x5000', 12345)
            return readInteger('0x5000')
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("12345", result.ReturnValue);
    }

    // ── Integration: Lua script using multiple CE APIs ──

    [Fact]
    public async Task Integration_ScriptUsesMultipleApis()
    {
        _facade.WriteMemoryDirect((nuint)0x1000, BitConverter.GetBytes(100));

        var result = await _engine.ExecuteAsync("""
            local addr = getAddress('0x1000')
            local val = readInteger('0x1000')
            if val == 100 then
                writeInteger('0x1000', val + 50)
            end
            return readInteger('0x1000')
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("150", result.ReturnValue);
    }

    [Fact]
    public async Task GetProcessList_ReturnsTable()
    {
        var result = await _engine.ExecuteAsync("""
            local list = getProcessList()
            return #list
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("3", result.ReturnValue); // StubEngineFacade returns 3 processes
    }
}
