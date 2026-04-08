using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class AutoAssemblerToolTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubAutoAssemblerEngine _aaEngine = new();

    private AiToolFunctions CreateToolFunctions(IAutoAssemblerEngine? aaEngine = null)
    {
        var dashboardService = new WorkspaceDashboardService(_engineFacade, new StubSessionRepository());
        var scanService = new ScanService(new StubScanEngine());
        var addressTableService = new AddressTableService(_engineFacade);
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGenService = new ScriptGenerationService();

        return new AiToolFunctions(
            _engineFacade,
            dashboardService,
            scanService,
            addressTableService,
            disassemblyService,
            scriptGenService,
            autoAssemblerEngine: aaEngine);
    }

    [Fact]
    public async Task ExecuteAutoAssemblerScript_ValidScript_ReturnsSuccess()
    {
        _aaEngine.NextParseResult = new ScriptParseResult(true, [], [], "[ENABLE]", "[DISABLE]");
        _aaEngine.NextEnableResult = new ScriptExecutionResult(true, null,
            [new ScriptAllocation("cave1", (nuint)0x10000, 4096)],
            [new ScriptPatch((nuint)0x400000, [0x48], [0x90])]);

        var tools = CreateToolFunctions(_aaEngine);

        // Use current process ID so IsProcessAlive returns true
        var pid = Environment.ProcessId;
        var result = await tools.ExecuteAutoAssemblerScript(pid, "[ENABLE]\nnop\n[DISABLE]\ndb 48");

        Assert.Contains("ENABLED successfully", result);
        Assert.Contains("1 allocations", result);
        Assert.Contains("1 patches", result);
    }

    [Fact]
    public async Task ExecuteAutoAssemblerScript_InvalidScript_ReturnsParseErrors()
    {
        _aaEngine.NextParseResult = new ScriptParseResult(false,
            ["Unknown directive 'badcmd'"], ["Suspicious label name"], null, null);

        var tools = CreateToolFunctions(_aaEngine);
        var pid = Environment.ProcessId;
        var result = await tools.ExecuteAutoAssemblerScript(pid, "badcmd");

        Assert.Contains("Script parse failed", result);
        Assert.Contains("Unknown directive 'badcmd'", result);
        Assert.Contains("Suspicious label name", result);
    }

    [Fact]
    public async Task ExecuteAutoAssemblerScript_NullEngine_ReturnsNotAvailable()
    {
        var tools = CreateToolFunctions(aaEngine: null);
        var pid = Environment.ProcessId;
        var result = await tools.ExecuteAutoAssemblerScript(pid, "[ENABLE]\nnop");

        Assert.Equal("Auto Assembler engine not available.", result);
    }

    [Fact]
    public async Task DisableAutoAssemblerScript_ValidScript_ReturnsSuccess()
    {
        _aaEngine.NextParseResult = new ScriptParseResult(true, [], [], "[ENABLE]", "[DISABLE]");
        _aaEngine.NextDisableResult = new ScriptExecutionResult(true, null, [],
            [new ScriptPatch((nuint)0x400000, [0x90], [0x48])]);

        var tools = CreateToolFunctions(_aaEngine);
        var pid = Environment.ProcessId;
        var result = await tools.DisableAutoAssemblerScript(pid, "[ENABLE]\nnop\n[DISABLE]\ndb 48");

        Assert.Contains("DISABLED successfully", result);
        Assert.Contains("1 patches restored", result);
    }

    [Fact]
    public void DangerousTools_ContainsExecuteAutoAssemblerScript()
    {
        Assert.Contains("ExecuteAutoAssemblerScript", DangerousTools.Names);
    }

    [Fact]
    public void DangerousTools_ContainsDisableAutoAssemblerScript()
    {
        Assert.Contains("DisableAutoAssemblerScript", DangerousTools.Names);
    }
}
