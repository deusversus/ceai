using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Engine.Windows;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public sealed class LuaSteppingBindingsTests : IDisposable
{
    private const int TestPid = 1234;

    private readonly StubEngineFacade _facade = new();
    private readonly StubBreakpointEngine _bp = new();
    private readonly StubVehDebugger _veh = new();
    private readonly MoonSharpLuaEngine _engine;

    private static readonly RegisterSnapshot TestRegisters = new(
        Rax: 0x100, Rbx: 0x200, Rcx: 0x300, Rdx: 0x400,
        Rsi: 0x500, Rdi: 0x600, Rsp: 0x7FFE0000, Rbp: 0x7FFE0010,
        R8: 0x800, R9: 0x900, R10: 0xA00, R11: 0xB00,
        Rip: 0x00401000);

    public LuaSteppingBindingsTests()
    {
        _facade.AttachAsync(TestPid).GetAwaiter().GetResult();
        _veh.InjectAsync(TestPid).GetAwaiter().GetResult();

        var disasm = new StubDisassemblyEngine();
        var eventBus = new BreakpointEventBus();
        var steppingEngine = new WindowsSteppingEngine(_veh, disasm, _facade, _bp, eventBus);

        _engine = new MoonSharpLuaEngine(
            _facade,
            breakpointEngine: _bp,
            steppingEngine: steppingEngine,
            executionTimeout: TimeSpan.FromSeconds(10));
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task DebugStepIn_IsRegistered()
    {
        var result = await _engine.ExecuteAsync(
            "return type(debug_stepIn)", processId: TestPid);
        Assert.True(result.Success);
        Assert.Equal("function", result.ReturnValue);
    }

    [Fact]
    public async Task DebugStepOver_IsRegistered()
    {
        var result = await _engine.ExecuteAsync(
            "return type(debug_stepOver)", processId: TestPid);
        Assert.True(result.Success);
        Assert.Equal("function", result.ReturnValue);
    }

    [Fact]
    public async Task DebugStepOut_IsRegistered()
    {
        var result = await _engine.ExecuteAsync(
            "return type(debug_stepOut)", processId: TestPid);
        Assert.True(result.Success);
        Assert.Equal("function", result.ReturnValue);
    }

    [Fact]
    public async Task DebugContinue_IsRegistered()
    {
        var result = await _engine.ExecuteAsync(
            "return type(debug_continue)", processId: TestPid);
        Assert.True(result.Success);
        Assert.Equal("function", result.ReturnValue);
    }

    [Fact]
    public async Task DebugContinueFromBreakpoint_IsRegistered()
    {
        var result = await _engine.ExecuteAsync(
            "return type(debug_continueFromBreakpoint)", processId: TestPid);
        Assert.True(result.Success);
        Assert.Equal("function", result.ReturnValue);
    }

    [Fact]
    public async Task DebugStepIn_WithTraceHit_ReturnsRegisterTable()
    {
        _veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401001, 42, VehBreakpointType.Trace, 0x4000, TestRegisters, 0));

        var result = await _engine.ExecuteAsync(
            "local regs = debug_stepIn(); return regs and regs.RAX or 'nil'",
            processId: TestPid);

        Assert.True(result.Success);
        Assert.Contains("100", result.ReturnValue ?? ""); // 0x100
    }

    [Fact]
    public async Task DebugStepIn_NoTraceHit_ReturnsNil()
    {
        // No canned hits — trace returns empty
        var result = await _engine.ExecuteAsync(
            "local regs = debug_stepIn(); return regs == nil",
            processId: TestPid);

        Assert.True(result.Success);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task DebugContinue_WithExecuteHit_ReturnsTrue()
    {
        _veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401050, 42, VehBreakpointType.Execute, 0x1, TestRegisters, 0));

        var result = await _engine.ExecuteAsync(
            "return debug_continue()", processId: TestPid);

        Assert.True(result.Success);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task SteppingGlobals_NotRegistered_WhenNoEngine()
    {
        // Create engine without stepping support
        using var engineNoStepping = new MoonSharpLuaEngine(
            _facade, breakpointEngine: _bp,
            executionTimeout: TimeSpan.FromSeconds(5));

        var result = await engineNoStepping.ExecuteAsync(
            "return type(debug_stepIn)", processId: TestPid);

        Assert.True(result.Success);
        Assert.Equal("nil", result.ReturnValue); // not registered
    }
}
