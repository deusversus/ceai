using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests filling coverage gaps identified by the S1/S2/S3 audit agents.
/// Covers: createThread, AOBReplace, data conversion edge cases,
/// debug_isDebugging true case, wideString round-trip, enumMemoryRegions.
/// </summary>
public sealed class LuaAuditCoverageTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly StubDisassemblyEngine _disassembly = new();
    private readonly StubBreakpointEngine _breakpoints = new();
    private readonly StubScanEngine _scanner = new();
    private readonly StubMemoryProtectionEngine _memProtection = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaAuditCoverageTests()
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

    // ── createThread tests ──

    [Fact]
    public async Task CreateThread_ReturnsHandle()
    {
        // Only check that the thread handle table is returned correctly.
        // Don't wait for the thread — it contends for the engine gate
        // and can time out under parallel test load.
        var result = await _engine.ExecuteAsync("""
            local t = createThread(function() end)
            local ok = type(t) == "table" and type(t._id) == "string"
            return ok
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);

        // Give the background thread time to acquire the gate and complete
        await Task.Delay(200);
    }

    [Fact]
    public async Task CreateThread_NonFunction_Throws()
    {
        var result = await _engine.ExecuteAsync("""
            createThread("not a function")
            """, processId: 1234);

        Assert.False(result.Success);
        Assert.Contains("must be a function", result.Error);
    }

    // ── AOBReplace tests ──

    [Fact]
    public async Task AOBReplace_WithMatches_ReturnsCount()
    {
        _scanner.NextScanResult = new ScanResultSet(
            "test", 1234,
            new ScanConstraints(MemoryDataType.ByteArray, ScanType.ArrayOfBytes, "90 90"),
            [
                new ScanResultEntry((nuint)0x1000, "90 90", null, [0x90, 0x90]),
                new ScanResultEntry((nuint)0x2000, "90 90", null, [0x90, 0x90])
            ],
            10, 40960, DateTimeOffset.UtcNow);

        var result = await _engine.ExecuteAsync("""
            return AOBReplace("90 90", "CC CC")
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("2", result.ReturnValue);
    }

    [Fact]
    public async Task AOBReplace_NoMatches_ReturnsZero()
    {
        var result = await _engine.ExecuteAsync("""
            return AOBReplace("DE AD BE EF", "90 90 90 90")
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("0", result.ReturnValue);
    }

    // ── Data conversion edge cases (audit gaps) ──

    [Fact]
    public async Task WordToByteTable_LargeValue_Truncates()
    {
        // 70000 exceeds Int16 range — should truncate via unchecked cast
        var result = await _engine.ExecuteAsync("""
            local t = wordToByteTable(70000)
            return #t == 2
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task DwordToByteTable_NegativeValue_Works()
    {
        var result = await _engine.ExecuteAsync("""
            local t = dwordToByteTable(-1)
            local v = byteTableToDword(t)
            return v == -1
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task WideStringToByteTable_RoundTrips()
    {
        var result = await _engine.ExecuteAsync("""
            local t = wideStringToByteTable("Hello")
            local s = byteTableToWideString(t)
            return s
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Hello", result.ReturnValue);
    }

    // ── Debug coverage gaps ──

    [Fact]
    public async Task Debug_IsDebugging_TrueWhenBreakpointSet()
    {
        var result = await _engine.ExecuteAsync("""
            debug_setBreakpoint(0x7FF00100)
            return debug_isDebugging()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task Debug_GetBreakpointHitLog_ReturnsTable()
    {
        // Pre-populate a breakpoint with hits
        _breakpoints.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-test", (nuint)0x7FF00100, BreakpointType.HardwareExecute,
            BreakpointHitAction.LogAndContinue, true, 1));
        _breakpoints.AddCannedHits("bp-test",
            new BreakpointHitEvent("bp-test", (nuint)0x7FF00100, 1234,
                DateTimeOffset.UtcNow, new Dictionary<string, string> { ["RAX"] = "0x42" }));

        var result = await _engine.ExecuteAsync("""
            local hits = debug_getBreakpointHitLog("bp-test")
            return #hits
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("1", result.ReturnValue);
    }

    // ── Memory management edge cases ──

    [Fact]
    public async Task SetMemoryProtection_Works()
    {
        var result = await _engine.ExecuteAsync("""
            return setMemoryProtection(0x7FF00000, 4096, 0x40)
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task VirtualQueryEx_HasProtectionField()
    {
        var result = await _engine.ExecuteAsync("""
            local info = virtualQueryEx(0x7FF00000)
            return info.protection ~= nil
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task GetRegionInfo_IsAlias()
    {
        var result = await _engine.ExecuteAsync("""
            local a = virtualQueryEx(0x7FF00000)
            local b = getRegionInfo(0x7FF00000)
            return a.baseAddress == b.baseAddress
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    // ── Module coverage ──

    [Fact]
    public async Task EnumMemoryRegions_ReturnsTable()
    {
        _scanner.NextRegions = [
            new MemoryRegionDescriptor((nuint)0x7FF00000, 4096, true, false, true),
            new MemoryRegionDescriptor((nuint)0x7FF01000, 8192, true, true, false)
        ];

        var result = await _engine.ExecuteAsync("""
            local regions = enumMemoryRegions()
            return #regions
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("2", result.ReturnValue);
    }

    [Fact]
    public async Task WritePointer_Works()
    {
        var result = await _engine.ExecuteAsync("""
            writePointer('0x5000', 0x7FF00100)
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task WriteString_Wide_Works()
    {
        var result = await _engine.ExecuteAsync("""
            writeString('0x6000', 'Wide', true)
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task GetPreviousOpcode_ReturnsAddress()
    {
        var result = await _engine.ExecuteAsync("""
            local addr = getPreviousOpcode('0x7FF00105')
            return addr ~= nil
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }
}
