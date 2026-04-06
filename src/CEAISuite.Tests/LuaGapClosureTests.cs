using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Engine.Windows;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>Tests for Phase 8 gap closure: unclosed Lua blocks, readByte/writeByte, registerSymbol sync, LuaCall parsing.</summary>
public sealed class LuaGapClosureTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaGapClosureTests()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(_facade, null, executionTimeout: TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _engine.Dispose();

    // ── readByte / writeByte ──

    [Fact]
    public async Task ReadByte_ValidAddress_ReturnsValue()
    {
        _facade.WriteMemoryDirect((nuint)0x1000, new byte[] { 0xAB });

        var result = await _engine.ExecuteAsync("return readByte('0x1000')", processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("171", result.ReturnValue); // 0xAB = 171
    }

    [Fact]
    public async Task WriteByte_ReadByte_RoundTrip()
    {
        var result = await _engine.ExecuteAsync("""
            writeByte('0x6000', 255)
            return readByte('0x6000')
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("255", result.ReturnValue);
    }

    // ── registerSymbol sync with AA ──

    [Fact]
    public async Task RegisterSymbol_SyncsToLuaGlobal()
    {
        var result = await _engine.ExecuteAsync("""
            registerSymbol('myAddr', 0x1234)
            return myAddr
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("4660", result.ReturnValue); // 0x1234 = 4660
    }

    [Fact]
    public async Task UnregisterSymbol_ClearsGlobal()
    {
        var result = await _engine.ExecuteAsync("""
            registerSymbol('tempSym', 0xFF)
            unregisterSymbol('tempSym')
            if tempSym == nil then return 'nil' end
            return tostring(tempSym)
            """, processId: 1234);

        Assert.True(result.Success);
        Assert.Equal("nil", result.ReturnValue);
    }

    // ── AA engine: unclosed Lua block ──

    [Fact]
    public void Parse_UnclosedLuaBlock_StillValid()
    {
        var luaStub = new StubLuaScriptEngine();
        var aa = new WindowsAutoAssemblerEngine(() => luaStub);

        // Script ends with {$luacode} block without {$asm} closing
        var script = "[ENABLE]\n{$luacode}\nprint('trailing')\n[DISABLE]\nnop";

        var result = aa.Parse(script);
        Assert.True(result.IsValid);
    }

    // ── AA engine: LuaCall with nested parens ──

    [Fact]
    public void Parse_LuaCallNestedParens_Valid()
    {
        var luaStub = new StubLuaScriptEngine();
        var aa = new WindowsAutoAssemblerEngine(() => luaStub);

        var script = "[ENABLE]\nLuaCall(myFunc(\"arg\"))\n[DISABLE]\nnop";

        var result = aa.Parse(script);
        Assert.True(result.IsValid);
    }

    // ── AA engine: RegisterSymbol interface ──

    [Fact]
    public void RegisterSymbol_OnInterface_PersistsToResolve()
    {
        var luaStub = new StubLuaScriptEngine();
        var aa = new WindowsAutoAssemblerEngine(() => luaStub);

        aa.RegisterSymbol("testSym", (nuint)0xDEAD);

        Assert.Equal((nuint)0xDEAD, aa.ResolveSymbol("testSym"));
    }

    [Fact]
    public void UnregisterSymbol_OnInterface_RemovesFromResolve()
    {
        var luaStub = new StubLuaScriptEngine();
        var aa = new WindowsAutoAssemblerEngine(() => luaStub);

        aa.RegisterSymbol("tempSym", (nuint)0xBEEF);
        aa.UnregisterSymbol("tempSym");

        Assert.Null(aa.ResolveSymbol("tempSym"));
    }

    // ── Breakpoint service: Lua callback registration ──

    [Fact]
    public void RegisterLuaCallback_StoresCallback()
    {
        var luaStub = new StubLuaScriptEngine();
        var bpService = new CEAISuite.Application.BreakpointService(
            new StubBreakpointEngine(), luaStub);

        bpService.RegisterLuaCallback("bp-001", "onHit");

        Assert.True(luaStub.IsCallbackRegistered("onHit"));
    }

    [Fact]
    public void UnregisterLuaCallback_DoesNotThrow()
    {
        var bpService = new CEAISuite.Application.BreakpointService(new StubBreakpointEngine(), null);

        // Should not throw even with no Lua engine
        bpService.UnregisterLuaCallback("bp-nonexistent");
    }

    // ── REPL command history ──

    [Fact]
    public async Task Console_HistoryUp_RecallsPreviousCommand()
    {
        var luaStub = new StubLuaScriptEngine();
        luaStub.NextExecuteResult = new LuaExecutionResult(true, null, null, []);
        var vm = new CEAISuite.Desktop.ViewModels.LuaConsoleViewModel(
            luaStub, new StubProcessContext(), new StubOutputLog(), new StubDialogService());

        vm.InputText = "print('first')";
        await vm.ExecuteCommand.ExecuteAsync(null);
        vm.InputText = "print('second')";
        await vm.ExecuteCommand.ExecuteAsync(null);

        vm.HistoryUpCommand.Execute(null);
        Assert.Equal("print('second')", vm.InputText);

        vm.HistoryUpCommand.Execute(null);
        Assert.Equal("print('first')", vm.InputText);
    }

    [Fact]
    public async Task Console_HistoryDown_NavigatesForward()
    {
        var luaStub = new StubLuaScriptEngine();
        luaStub.NextExecuteResult = new LuaExecutionResult(true, null, null, []);
        var vm = new CEAISuite.Desktop.ViewModels.LuaConsoleViewModel(
            luaStub, new StubProcessContext(), new StubOutputLog(), new StubDialogService());

        vm.InputText = "a";
        await vm.ExecuteCommand.ExecuteAsync(null);
        vm.InputText = "b";
        await vm.ExecuteCommand.ExecuteAsync(null);

        vm.HistoryUpCommand.Execute(null); // → "b"
        vm.HistoryUpCommand.Execute(null); // → "a"
        vm.HistoryDownCommand.Execute(null); // → "b"
        Assert.Equal("b", vm.InputText);

        vm.HistoryDownCommand.Execute(null); // → ""
        Assert.Equal(string.Empty, vm.InputText);
    }

    // ── registerSymbol with AA engine sync ──

    [Fact]
    public async Task RegisterSymbol_WithAAEngine_SyncsToAASymbolTable()
    {
        var assembler = new StubAutoAssemblerEngine();
        using var engineWithAA = new MoonSharpLuaEngine(_facade, assembler, executionTimeout: TimeSpan.FromSeconds(5));

        await engineWithAA.ExecuteAsync("registerSymbol('myHealth', 0x12345678)", processId: 1234);

        // Verify AA engine received the symbol
        Assert.Equal((nuint)0x12345678, assembler.ResolveSymbol("myHealth"));
    }
}
