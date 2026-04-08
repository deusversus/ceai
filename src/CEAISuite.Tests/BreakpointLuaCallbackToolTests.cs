using CEAISuite.Application;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class BreakpointLuaCallbackToolTests
{
    private static AiToolFunctions CreateToolFunctions(BreakpointService? breakpointService = null)
    {
        var engine = new StubEngineFacade();
        var dashboard = new WorkspaceDashboardService(engine, new StubSessionRepository());
        var scan = new ScanService(new StubScanEngine());
        var addressTable = new AddressTableService(engine);
        var disassembly = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGen = new ScriptGenerationService();
        return new AiToolFunctions(engine, dashboard, scan, addressTable, disassembly, scriptGen,
            breakpointService: breakpointService);
    }

    [Fact]
    public void RegisterBreakpointLuaCallback_ReturnsSuccessMessage()
    {
        var bpService = new BreakpointService(new StubBreakpointEngine());
        var tools = CreateToolFunctions(bpService);

        var result = tools.RegisterBreakpointLuaCallback("bp-1", "onHit");

        Assert.Contains("onHit", result);
        Assert.Contains("bp-1", result);
    }

    [Fact]
    public void UnregisterBreakpointLuaCallback_ReturnsSuccessMessage()
    {
        var bpService = new BreakpointService(new StubBreakpointEngine());
        var tools = CreateToolFunctions(bpService);

        var result = tools.UnregisterBreakpointLuaCallback("bp-1");

        Assert.Contains("bp-1", result);
        Assert.Contains("unregistered", result.ToLowerInvariant());
    }

    [Fact]
    public void RegisterBreakpointLuaCallback_NullService_ReturnsNotAvailable()
    {
        var tools = CreateToolFunctions(breakpointService: null);

        var result = tools.RegisterBreakpointLuaCallback("bp-1", "onHit");

        Assert.Equal("Breakpoint engine not available.", result);
    }

    [Fact]
    public void UnregisterBreakpointLuaCallback_NullService_ReturnsNotAvailable()
    {
        var tools = CreateToolFunctions(breakpointService: null);

        var result = tools.UnregisterBreakpointLuaCallback("bp-1");

        Assert.Equal("Breakpoint engine not available.", result);
    }
}
