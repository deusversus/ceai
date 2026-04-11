using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Deep unit tests for AiToolFunctions tool methods.
/// Covers breakpoints, code caves, scripts, snapshots, Lua, disassembly, and analysis tools.
/// </summary>
public class AiToolFunctionsDeepTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubBreakpointEngine _bpEngine = new();
    private readonly StubDisassemblyEngine _disasmEngine = new();
    private readonly StubCodeCaveEngine _codeCaveEngine = new();
    private readonly StubMemoryProtectionEngine _memProtEngine = new();
    private readonly StubLuaScriptEngine _luaEngine = new();
    private readonly StubAutoAssemblerEngine _aaEngine = new();

    private AiToolFunctions CreateTools(
        BreakpointService? bpService = null,
        ICodeCaveEngine? codeCaveEngine = null,
        IMemoryProtectionEngine? memProtEngine = null,
        ILuaScriptEngine? luaEngine = null,
        IAutoAssemblerEngine? aaEngine = null,
        MemorySnapshotService? snapshotService = null,
        bool attach = true)
    {
        // Pre-attach so ValidateDestructiveProcessId passes for tests using Pid
        if (attach && !_engineFacade.IsAttached)
            _engineFacade.AttachAsync(Pid).GetAwaiter().GetResult();

        var dashboard = new WorkspaceDashboardService(_engineFacade, new StubSessionRepository());
        var scan = new ScanService(new StubScanEngine());
        var addressTable = new AddressTableService(_engineFacade);
        var disassembly = new DisassemblyService(_disasmEngine);
        var scriptGen = new ScriptGenerationService();

        return new AiToolFunctions(
            _engineFacade,
            dashboard,
            scan,
            addressTable,
            disassembly,
            scriptGen,
            breakpointService: bpService,
            codeCaveEngine: codeCaveEngine,
            memoryProtectionEngine: memProtEngine,
            luaEngine: luaEngine,
            autoAssemblerEngine: aaEngine);
    }

    private BreakpointService CreateBpService() => new BreakpointService(_bpEngine);
    private static int Pid => Environment.ProcessId;

    // ── Breakpoint Tools ──

    [Fact]
    public async Task SetBreakpoint_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.SetBreakpoint(Pid, "0x400000");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task SetBreakpoint_SoftwareType_ReturnsSuccess()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.SetBreakpoint(Pid, "0x400000", type: "Software", mode: "Auto");
        Assert.Contains("Breakpoint", result);
        Assert.Contains("set at", result);
    }

    [Fact]
    public async Task SetBreakpoint_SoftwareModeWithHardwareWrite_ReturnsError()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.SetBreakpoint(Pid, "0x400000", type: "HardwareWrite", mode: "Software");
        Assert.Contains("cannot monitor data writes", result);
    }

    [Fact]
    public async Task SetBreakpoint_StealthWithHardwareWrite_DowngradesToPageGuard()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.SetBreakpoint(Pid, "0x400000", type: "HardwareWrite", mode: "Stealth");
        // Should auto-downgrade from Stealth to PageGuard
        Assert.Contains("set at", result);
    }

    [Fact]
    public async Task SetBreakpoint_SingleHit_ReturnsMarker()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.SetBreakpoint(Pid, "0x400000", singleHit: true);
        Assert.Contains("SINGLE-HIT", result);
    }

    [Fact]
    public async Task RemoveBreakpoint_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.RemoveBreakpoint(Pid, "bp-0");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task RemoveBreakpoint_NotFound_ReturnsNotFound()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.RemoveBreakpoint(Pid, "bp-nonexistent");
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ListBreakpoints_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ListBreakpoints(Pid);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ListBreakpoints_Empty_ReturnsEmptyJson()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.ListBreakpoints(Pid);
        Assert.Contains("count", result);
        Assert.Contains("0", result);
    }

    [Fact]
    public async Task GetBreakpointHitLog_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.GetBreakpointHitLog("bp-0");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task GetBreakpointHitLog_NoHits_ReturnsNoHits()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.GetBreakpointHitLog("bp-0");
        Assert.Contains("No hits", result);
    }

    [Fact]
    public async Task EmergencyRestorePageProtection_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.EmergencyRestorePageProtection(Pid);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task EmergencyRestorePageProtection_NoActive_ReturnsNoActive()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.EmergencyRestorePageProtection(Pid);
        Assert.Contains("No active", result);
    }

    [Fact]
    public async Task ForceDetachAndCleanup_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ForceDetachAndCleanup(Pid);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ForceDetachAndCleanup_WithService_ReturnsSuccess()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.ForceDetachAndCleanup(Pid);
        Assert.Contains("Force detach complete", result);
    }

    [Fact]
    public static void GetBreakpointModeCapabilities_ReturnsCapabilities()
    {
        var result = AiToolFunctions.GetBreakpointModeCapabilities();
        Assert.Contains("Breakpoint Mode Capabilities", result);
        Assert.Contains("Stealth", result);
        Assert.Contains("PageGuard", result);
        Assert.Contains("Hardware", result);
        Assert.Contains("Software", result);
    }

    [Fact]
    public async Task SetConditionalBreakpoint_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.SetConditionalBreakpoint(Pid, "0x400000", "HardwareExecute", "RAX == 0");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task SetConditionalBreakpoint_Valid_ReturnsSuccess()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.SetConditionalBreakpoint(Pid, "0x400000", "HardwareExecute", "RAX == 0");
        Assert.Contains("Conditional breakpoint set", result);
        Assert.Contains("RAX == 0", result);
    }

    [Fact]
    public async Task TraceFromAddress_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.TraceFromAddress(Pid, "0x400000");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task TraceFromAddress_NoEntries_ReturnsNoInstructions()
    {
        var tools = CreateTools(bpService: CreateBpService());
        var result = await tools.TraceFromAddress(Pid, "0x400000");
        Assert.Contains("no instructions", result);
    }

    [Fact]
    public void RegisterBreakpointLuaCallback_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = tools.RegisterBreakpointLuaCallback("bp-0", "onHit");
        Assert.Contains("not available", result);
    }

    [Fact]
    public void UnregisterBreakpointLuaCallback_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = tools.UnregisterBreakpointLuaCallback("bp-0");
        Assert.Contains("not available", result);
    }

    // ── Code Cave Tools ──

    [Fact]
    public async Task InstallCodeCaveHook_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.InstallCodeCaveHook(Pid, "0x400000");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task InstallCodeCaveHook_NonExecutableRegion_ReturnsRejected()
    {
        _memProtEngine.NextRegion = new MemoryRegionDescriptor(
            (nuint)0x400000, 4096, IsReadable: true, IsWritable: true, IsExecutable: false);
        var tools = CreateTools(codeCaveEngine: _codeCaveEngine, memProtEngine: _memProtEngine);
        var result = await tools.InstallCodeCaveHook(Pid, "0x400000");
        Assert.Contains("REJECTED", result);
    }

    [Fact]
    public async Task InstallCodeCaveHook_ExecutableRegion_ReturnsSuccess()
    {
        _memProtEngine.NextRegion = new MemoryRegionDescriptor(
            (nuint)0x400000, 4096, IsReadable: true, IsWritable: false, IsExecutable: true);
        var tools = CreateTools(codeCaveEngine: _codeCaveEngine, memProtEngine: _memProtEngine);
        var result = await tools.InstallCodeCaveHook(Pid, "0x400000");
        Assert.Contains("Stealth hook installed", result);
    }

    [Fact]
    public async Task RemoveCodeCaveHook_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.RemoveCodeCaveHook(Pid, "hook-0");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ListCodeCaveHooks_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ListCodeCaveHooks(Pid);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ListCodeCaveHooks_Empty_ReturnsEmptyList()
    {
        var tools = CreateTools(codeCaveEngine: _codeCaveEngine);
        var result = await tools.ListCodeCaveHooks(Pid);
        Assert.Contains("count", result);
    }

    [Fact]
    public async Task GetCodeCaveHookHits_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.GetCodeCaveHookHits("hook-0");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task GetCodeCaveHookHits_NoHits_ReturnsNoHits()
    {
        var tools = CreateTools(codeCaveEngine: _codeCaveEngine);
        var result = await tools.GetCodeCaveHookHits("hook-0");
        Assert.Contains("No hits", result);
    }

    // ── Lua Tools ──

    [Fact]
    public async Task ExecuteLuaScript_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ExecuteLuaScript("print('hello')");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ExecuteLuaScript_Success_ReturnsOutput()
    {
        _luaEngine.NextExecuteResult = new LuaExecutionResult(true, null, null, ["Hello World"]);
        var tools = CreateTools(luaEngine: _luaEngine);
        var result = await tools.ExecuteLuaScript("print('Hello World')");
        Assert.Contains("Hello World", result);
    }

    [Fact]
    public async Task ExecuteLuaScript_Error_ReturnsError()
    {
        _luaEngine.NextExecuteResult = new LuaExecutionResult(false, null, "syntax error near 'end'", []);
        var tools = CreateTools(luaEngine: _luaEngine);
        var result = await tools.ExecuteLuaScript("bad code");
        Assert.Contains("Lua error", result);
        Assert.Contains("syntax error", result);
    }

    [Fact]
    public async Task ExecuteLuaScript_WithReturnValue_ReturnsIt()
    {
        _luaEngine.NextExecuteResult = new LuaExecutionResult(true, "42", null, []);
        var tools = CreateTools(luaEngine: _luaEngine);
        var result = await tools.ExecuteLuaScript("return 42");
        Assert.Contains("Return: 42", result);
    }

    [Fact]
    public async Task ExecuteLuaScript_NoOutput_ReturnsSuccessMessage()
    {
        _luaEngine.NextExecuteResult = new LuaExecutionResult(true, null, null, []);
        var tools = CreateTools(luaEngine: _luaEngine);
        var result = await tools.ExecuteLuaScript("-- comment");
        Assert.Contains("no output", result);
    }

    [Fact]
    public async Task ValidateLuaScript_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ValidateLuaScript("code");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ValidateLuaScript_ValidCode_ReturnsValid()
    {
        _luaEngine.NextValidateResult = new LuaValidationResult(true, []);
        var tools = CreateTools(luaEngine: _luaEngine);
        var result = await tools.ValidateLuaScript("print('hi')");
        Assert.Contains("Valid", result);
    }

    [Fact]
    public async Task ValidateLuaScript_InvalidCode_ReturnsInvalid()
    {
        _luaEngine.NextValidateResult = new LuaValidationResult(false, ["unexpected symbol"]);
        var tools = CreateTools(luaEngine: _luaEngine);
        var result = await tools.ValidateLuaScript("bad())(");
        Assert.Contains("Invalid", result);
    }

    [Fact]
    public async Task EvaluateLuaExpression_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.EvaluateLuaExpression("1+1");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task EvaluateLuaExpression_Success_ReturnsValue()
    {
        _luaEngine.NextEvaluateResult = new LuaExecutionResult(true, "2", null, []);
        var tools = CreateTools(luaEngine: _luaEngine);
        var result = await tools.EvaluateLuaExpression("1+1");
        Assert.Equal("2", result);
    }

    [Fact]
    public async Task EvaluateLuaExpression_NilResult_ReturnsNil()
    {
        _luaEngine.NextEvaluateResult = new LuaExecutionResult(true, null, null, []);
        var tools = CreateTools(luaEngine: _luaEngine);
        var result = await tools.EvaluateLuaExpression("nothing()");
        Assert.Equal("nil", result);
    }

    // ── Script Tools ──

    [Fact]
    public async Task ListScripts_NoScripts_ReturnsEmpty()
    {
        var tools = CreateTools();
        var result = await tools.ListScripts();
        Assert.Contains("No scripts", result);
    }

    [Fact]
    public async Task ViewScript_NotFound_ReturnsNotFound()
    {
        var tools = CreateTools();
        var result = await tools.ViewScript("nonexistent");
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ValidateScript_NullAAEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ValidateScript("nonexistent");
        Assert.Contains("not found", result);
    }

    // ── Auto Assembler Tools ──

    [Fact]
    public async Task ExecuteAutoAssemblerScript_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ExecuteAutoAssemblerScript(Pid, "code");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ExecuteAutoAssemblerScript_ParseFails_ReturnsErrors()
    {
        _aaEngine.NextParseResult = new ScriptParseResult(false, ["syntax error"], ["deprecated syntax"], null, null);
        var tools = CreateTools(aaEngine: _aaEngine);
        var result = await tools.ExecuteAutoAssemblerScript(Pid, "bad code");
        Assert.Contains("parse failed", result);
        Assert.Contains("syntax error", result);
    }

    [Fact]
    public async Task DisableAutoAssemblerScript_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.DisableAutoAssemblerScript(Pid, "code");
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ListRegisteredSymbols_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ListRegisteredSymbols();
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ResolveRegisteredSymbol_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.ResolveRegisteredSymbol("test");
        Assert.Contains("not available", result);
    }

    // ── Process Tools ──

    [Fact]
    public async Task ListProcesses_ReturnsProcessList()
    {
        var tools = CreateTools();
        var result = await tools.ListProcesses();
        Assert.Contains("TestGame.exe", result);
        Assert.Contains("notepad.exe", result);
    }

    [Fact]
    public async Task InspectProcess_ReturnsModules()
    {
        var tools = CreateTools();
        // Use a known fake PID that the stub returns
        var result = await tools.InspectProcess(1000);
        Assert.Contains("Modules", result);
    }

    // ── Disassembly Tools ──

    [Fact]
    public async Task Disassemble_ValidAddress_ReturnsInstructions()
    {
        var tools = CreateTools();
        var result = await tools.Disassemble(Pid, "0x7FF00100");
        Assert.Contains("instructions", result);
    }

    // ── Snapshot Tools ──

    [Fact]
    public void ListSnapshots_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = tools.ListSnapshots();
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteSnapshot_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = tools.DeleteSnapshot("snap-0");
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompareSnapshots_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = tools.CompareSnapshots("a", "b");
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureSnapshot_NullService_ReturnsNotAvailable()
    {
        var tools = CreateTools();
        var result = await tools.CaptureSnapshot(Pid, "0x400000", 64);
        Assert.Contains("not available", result);
    }

    // ── Artifact identification ──

    [Fact]
    public async Task IdentifyArtifact_HookPrefix_ReturnsHook()
    {
        var tools = CreateTools();
        var result = await tools.IdentifyArtifact("hook-123");
        Assert.Contains("hook", result);
        Assert.Contains("Code cave", result);
    }

    [Fact]
    public async Task IdentifyArtifact_BpPrefix_ReturnsBreakpoint()
    {
        var tools = CreateTools();
        var result = await tools.IdentifyArtifact("bp-123");
        Assert.Contains("breakpoint", result);
    }

    [Fact]
    public async Task IdentifyArtifact_ScriptPrefix_ReturnsScript()
    {
        var tools = CreateTools();
        var result = await tools.IdentifyArtifact("script-123");
        Assert.Contains("script", result);
    }

    [Fact]
    public async Task IdentifyArtifact_Unknown_ReturnsUnknown()
    {
        var tools = CreateTools();
        var result = await tools.IdentifyArtifact("xyz-123");
        Assert.Contains("unknown", result);
    }

    // ── ResolveSymbol ──

    [Fact]
    public async Task ResolveSymbol_RawHex_ReturnsResolved()
    {
        var tools = CreateTools();
        var result = await tools.ResolveSymbol(Pid, "0x400000");
        Assert.Contains("0x400000", result);
        Assert.Contains("isResolved", result);
    }

    [Fact]
    public async Task ResolveSymbol_ModulePlusOffset_ReturnsResolved()
    {
        var tools = CreateTools();
        var result = await tools.ResolveSymbol(Pid, "main.exe+0x100");
        Assert.Contains("main.exe", result);
        Assert.Contains("isResolved", result);
    }

    [Fact]
    public async Task ResolveSymbol_BareModuleName_ReturnsBase()
    {
        var tools = CreateTools();
        var result = await tools.ResolveSymbol(Pid, "main.exe");
        Assert.Contains("main.exe", result);
        Assert.Contains("isResolved", result);
    }

    [Fact]
    public async Task ResolveSymbol_UnknownModule_ReturnsNotFound()
    {
        var tools = CreateTools();
        var result = await tools.ResolveSymbol(Pid, "unknown.dll+0x100");
        Assert.Contains("not found", result);
    }
}
