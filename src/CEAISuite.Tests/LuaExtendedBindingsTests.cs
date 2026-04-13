using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for the Phase S1 extended Lua bindings: data conversion, disassembly,
/// memory management, scanning, debugger, and module functions.
/// </summary>
public sealed class LuaExtendedBindingsTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly StubDisassemblyEngine _disassembly = new();
    private readonly StubBreakpointEngine _breakpoints = new();
    private readonly StubScanEngine _scanner = new();
    private readonly StubMemoryProtectionEngine _memProtection = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaExtendedBindingsTests()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(
            _facade,
            _assembler,
            executionTimeout: TimeSpan.FromSeconds(5),
            disassemblyEngine: _disassembly,
            breakpointEngine: _breakpoints,
            scanEngine: _scanner,
            memoryProtectionEngine: _memProtection);
    }

    public void Dispose() => _engine.Dispose();

    // ── S1F: Data Conversion ──

    [Fact]
    public async Task DwordToByteTable_And_Back_RoundTrips()
    {
        var result = await _engine.ExecuteAsync("""
            local t = dwordToByteTable(0x12345678)
            local v = byteTableToDword(t)
            return v
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("305419896", result.ReturnValue); // 0x12345678
    }

    [Fact]
    public async Task FloatToByteTable_And_Back_RoundTrips()
    {
        var result = await _engine.ExecuteAsync("""
            local t = floatToByteTable(3.14)
            local v = byteTableToFloat(t)
            return math.abs(v - 3.14) < 0.001
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task DoubleToByteTable_And_Back_RoundTrips()
    {
        var result = await _engine.ExecuteAsync("""
            local t = doubleToByteTable(3.14159265)
            local v = byteTableToDouble(t)
            return math.abs(v - 3.14159265) < 0.0000001
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task WordToByteTable_And_Back_RoundTrips()
    {
        var result = await _engine.ExecuteAsync("""
            local t = wordToByteTable(1234)
            local v = byteTableToWord(t)
            return v
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("1234", result.ReturnValue);
    }

    [Fact]
    public async Task QwordToByteTable_And_Back_RoundTrips()
    {
        var result = await _engine.ExecuteAsync("""
            local t = qwordToByteTable(123456789)
            local v = byteTableToQword(t)
            return v
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("123456789", result.ReturnValue);
    }

    [Fact]
    public async Task StringToByteTable_And_Back_RoundTrips()
    {
        var result = await _engine.ExecuteAsync("""
            local t = stringToByteTable("ABC")
            return byteTableToString(t)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("ABC", result.ReturnValue);
    }

    [Fact]
    public async Task BitwiseOps_Work()
    {
        var result = await _engine.ExecuteAsync("""
            local a = bOr(0x0F, 0xF0)
            local b = bAnd(0xFF, 0x0F)
            local c = bXor(0xFF, 0x0F)
            local d = bShl(1, 4)
            local e = bShr(16, 4)
            local f = bNot(0)
            return a == 0xFF and b == 0x0F and c == 0xF0 and d == 16 and e == 1
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    // ── S1C: Disassembly ──

    [Fact]
    public async Task Disassemble_ReturnsInstructionTable()
    {
        var result = await _engine.ExecuteAsync("""
            local instr = disassemble('0x7FF00100')
            return instr.opcode
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("mov", result.ReturnValue);
    }

    [Fact]
    public async Task GetInstructionSize_ReturnsLength()
    {
        var result = await _engine.ExecuteAsync("""
            return getInstructionSize('0x7FF00100')
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("5", result.ReturnValue);
    }

    [Fact]
    public async Task SplitDisassembledString_ParsesCorrectly()
    {
        var result = await _engine.ExecuteAsync("""
            local parts = splitDisassembledString("7FF00100 - 48 89 5C 24 08 - mov [rsp+8],rbx")
            return parts.opcode .. "|" .. parts.extra
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("mov|[rsp+8],rbx", result.ReturnValue);
    }

    // ── S1D: Memory Management ──

    [Fact]
    public async Task AllocateMemory_ReturnsAddress()
    {
        var result = await _engine.ExecuteAsync("""
            local addr = allocateMemory(4096)
            return addr > 0
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task DeAllocateMemory_ReturnsTrue()
    {
        var result = await _engine.ExecuteAsync("""
            local addr = allocateMemory(4096)
            return deAllocateMemory(addr)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task VirtualQueryEx_ReturnsRegionInfo()
    {
        var result = await _engine.ExecuteAsync("""
            local info = virtualQueryEx(0x7FF00000)
            return info.isReadable
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task CopyMemory_CopiesBetweenAddresses()
    {
        _facade.WriteMemoryDirect((nuint)0x1000, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

        var result = await _engine.ExecuteAsync("""
            copyMemory(0x2000, 0x1000, 4)
            return readByte('0x2000')
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("170", result.ReturnValue); // 0xAA = 170
    }

    // ── S1A: Scanning ──

    [Fact]
    public async Task AOBScan_WithResults_ReturnsTable()
    {
        _scanner.NextScanResult = new ScanResultSet(
            "test", 1234,
            new ScanConstraints(MemoryDataType.ByteArray, ScanType.ArrayOfBytes, "48 89"),
            [new ScanResultEntry((nuint)0x1000, "48 89", null, [0x48, 0x89])],
            10, 40960, DateTimeOffset.UtcNow);

        var result = await _engine.ExecuteAsync("""
            local results = AOBScan("48 89")
            return results[1]
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Contains("0x", result.ReturnValue!);
    }

    [Fact]
    public async Task AOBScan_NoResults_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("""
            local results = AOBScan("DE AD BE EF")
            return results == nil
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task CreateMemScan_FirstScan_ReturnsResultCount()
    {
        _scanner.NextScanResult = new ScanResultSet(
            "test", 1234,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            [
                new ScanResultEntry((nuint)0x1000, "100", null, BitConverter.GetBytes(100)),
                new ScanResultEntry((nuint)0x2000, "100", null, BitConverter.GetBytes(100))
            ],
            10, 40960, DateTimeOffset.UtcNow);

        var result = await _engine.ExecuteAsync("""
            local scan = createMemScan()
            scan.firstScan(scan, "exact", "int32", "100")
            return scan.getResultCount(scan)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("2", result.ReturnValue);
    }

    // ── S1B: Debugger ──

    [Fact]
    public async Task Debug_SetBreakpoint_ReturnsId()
    {
        var result = await _engine.ExecuteAsync("""
            local id = debug_setBreakpoint(0x7FF00100)
            return type(id) == "string"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task Debug_RemoveBreakpoint_ReturnsTrue()
    {
        var result = await _engine.ExecuteAsync("""
            local id = debug_setBreakpoint(0x7FF00100)
            return debug_removeBreakpoint(id)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task Debug_GetBreakpointList_ReturnsTable()
    {
        var result = await _engine.ExecuteAsync("""
            debug_setBreakpoint(0x7FF00100)
            debug_setBreakpoint(0x7FF00200)
            local list = debug_getBreakpointList()
            return #list
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("2", result.ReturnValue);
    }

    [Fact]
    public async Task Debug_IsDebugging_FalseWhenNoBreakpoints()
    {
        var result = await _engine.ExecuteAsync("""
            return debug_isDebugging()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("false", result.ReturnValue);
    }

    // ── S1G: Module & Symbol Extended ──

    [Fact]
    public async Task EnumModules_ReturnsModuleList()
    {
        var result = await _engine.ExecuteAsync("""
            local mods = enumModules()
            return #mods > 0 and mods[1].name == "main.exe"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task GetModuleSize_ReturnsSize()
    {
        var result = await _engine.ExecuteAsync("""
            local size = getModuleSize("main.exe")
            return size
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("4096", result.ReturnValue);
    }

    [Fact]
    public async Task GetModuleSize_UnknownModule_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("""
            local size = getModuleSize("nonexistent.dll")
            return size == nil
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task GetNameFromAddress_InsideModule_ReturnsModuleOffset()
    {
        var result = await _engine.ExecuteAsync("""
            return getNameFromAddress(0x400100)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("main.exe+0x100", result.ReturnValue);
    }

    [Fact]
    public async Task GetNameFromAddress_OutsideModule_ReturnsHex()
    {
        var result = await _engine.ExecuteAsync("""
            return getNameFromAddress(0xDEADBEEF)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Contains("0x", result.ReturnValue!);
    }

    [Fact]
    public async Task ReadPointer_ReadsEightBytes()
    {
        _facade.WriteMemoryDirect((nuint)0x1000, BitConverter.GetBytes((long)0x7FF00100));

        var result = await _engine.ExecuteAsync("""
            local ptr = readPointer('0x1000')
            return ptr > 0
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task WriteString_WritesNullTerminated()
    {
        var result = await _engine.ExecuteAsync("""
            writeString('0x5000', 'hello')
            local s = readString('0x5000')
            return s
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("hello", result.ReturnValue);
    }

    // ── No Process Attached Errors ──

    [Fact]
    public async Task DisassembleWithoutProcess_ReturnsError()
    {
        using var bareEngine = new MoonSharpLuaEngine(
            _facade, _assembler,
            disassemblyEngine: _disassembly);
        // Don't set process ID
        var result = await bareEngine.ExecuteAsync("return disassemble('0x1000')");

        Assert.False(result.Success);
        Assert.Contains("No process attached", result.Error);
    }

    [Fact]
    public async Task AllocateMemoryWithoutProcess_ReturnsError()
    {
        using var bareEngine = new MoonSharpLuaEngine(
            _facade, _assembler,
            memoryProtectionEngine: _memProtection);
        var result = await bareEngine.ExecuteAsync("return allocateMemory(4096)");

        Assert.False(result.Success);
        Assert.Contains("No process attached", result.Error);
    }
}
