using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// AI tool function tests for all Phase 10 tools.
/// Covers the "zero AI tool test coverage" gap found across 10A, 10C, 10D.
/// Each section tests: PID validation, null service fallback, success paths, error paths.
/// </summary>
public class AiToolFunctionsPhase10Tests
{
    private const int AttachedPid = 1000;
    private const int WrongPid = 9999;

    // ── Helpers ──

    private static AiToolFunctions CreateTools(
        int attachedPid,
        PluginHost? pluginHost = null,
        IUiCommandBus? uiCommandBus = null,
        SpeedHackService? speedHackService = null)
    {
        var facade = new StubEngineFacade();
        facade.AttachAsync(attachedPid).Wait();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(facade, sessionRepo);
        var scanService = new ScanService(new StubScanEngine());
        var addressTable = new AddressTableService(facade);
        var disasmService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGen = new ScriptGenerationService();

        return new AiToolFunctions(facade, dashboard, scanService, addressTable,
            disasmService, scriptGen,
            pluginHost: pluginHost,
            uiCommandBus: uiCommandBus,
            speedHackService: speedHackService);
    }

    // ══════════════════════════════════════════════════════════════════
    // 10A: Plugin AI Tools (ListPlugins, GetPluginTools)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListPlugins_NoHost_ReturnsNotAvailable()
    {
        var tools = CreateTools(AttachedPid, pluginHost: null);
        var result = await tools.ListPlugins();
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListPlugins_NoPluginsLoaded_ReturnsHelpText()
    {
        var host = new PluginHost(pluginDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var tools = CreateTools(AttachedPid, pluginHost: host);
        var result = await tools.ListPlugins();
        Assert.Contains("No plugins loaded", result);
    }

    [Fact]
    public async Task GetPluginTools_NoHost_ReturnsNotAvailable()
    {
        var tools = CreateTools(AttachedPid, pluginHost: null);
        var result = await tools.GetPluginTools("SomePlugin");
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPluginTools_UnknownPlugin_ReturnsNotFound()
    {
        var host = new PluginHost(pluginDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var tools = CreateTools(AttachedPid, pluginHost: host);
        var result = await tools.GetPluginTools("NonExistent");
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════
    // 10C: Co-Pilot AI Tools (GetUiCommandWhitelist, ExecuteUiCommand, GetCurrentUiState)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetUiCommandWhitelist_NoBus_ReturnsNotAvailable()
    {
        var tools = CreateTools(AttachedPid, uiCommandBus: null);
        var result = await tools.GetUiCommandWhitelist();
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUiCommandWhitelist_WithBus_ListsAllCommands()
    {
        var bus = new UiCommandBus();
        var tools = CreateTools(AttachedPid, uiCommandBus: bus);
        var result = await tools.GetUiCommandWhitelist();
        Assert.Contains("NavigatePanel", result);
        Assert.Contains("PopulateScanForm", result);
        Assert.Contains("AddEntryToTable", result);
        Assert.Contains("SetEntryValue", result);
        Assert.Contains("AttachProcess", result);
    }

    [Fact]
    public async Task ExecuteUiCommand_NoBus_ReturnsNotAvailable()
    {
        var tools = CreateTools(AttachedPid, uiCommandBus: null);
        var result = await tools.ExecuteUiCommand("NavigatePanel", "{\"panelId\":\"scanner\"}");
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteUiCommand_InvalidType_ReturnsUnknown()
    {
        var bus = new UiCommandBus();
        var tools = CreateTools(AttachedPid, uiCommandBus: bus);
        var result = await tools.ExecuteUiCommand("DeleteEverything", "{}");
        Assert.Contains("Unknown", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteUiCommand_ValidCommand_DispatchesThrough()
    {
        var bus = new UiCommandBus();
        UiCommand? received = null;
        bus.CommandReceived += cmd => received = cmd;

        var tools = CreateTools(AttachedPid, uiCommandBus: bus);
        var result = await tools.ExecuteUiCommand("NavigatePanel", "{\"panelId\":\"scanner\"}");

        Assert.Contains("OK", result);
        Assert.NotNull(received);
        Assert.IsType<NavigatePanelCommand>(received);
        Assert.Equal("scanner", ((NavigatePanelCommand)received).PanelId);
    }

    [Fact]
    public async Task GetCurrentUiState_NoBus_StillReturnsState()
    {
        // GetCurrentUiState works even without a bus — it reports table/scan state
        var tools = CreateTools(AttachedPid, uiCommandBus: null);
        var result = await tools.GetCurrentUiState();
        Assert.Contains("UI State", result);
        Assert.Contains("Address table", result);
    }

    [Fact]
    public async Task GetCurrentUiState_WithBus_ReportsCoPilotAvailable()
    {
        var bus = new UiCommandBus();
        var tools = CreateTools(AttachedPid, uiCommandBus: bus);
        var result = await tools.GetCurrentUiState();
        Assert.Contains("available", result, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════
    // 10D: Speed Hack AI Tools (GetSpeedHackState, SetSpeedMultiplier, RemoveSpeedHack)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSpeedHackState_NoService_ReturnsNotAvailable()
    {
        var tools = CreateTools(AttachedPid, speedHackService: null);
        var result = await tools.GetSpeedHackState(AttachedPid);
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSpeedHackState_NotActive_ReportsInactive()
    {
        var svc = new SpeedHackService(new StubSpeedHackEngine());
        var tools = CreateTools(AttachedPid, speedHackService: svc);
        var result = await tools.GetSpeedHackState(AttachedPid);
        Assert.Contains("not active", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetSpeedMultiplier_WrongPid_Rejected()
    {
        var svc = new SpeedHackService(new StubSpeedHackEngine());
        var tools = CreateTools(AttachedPid, speedHackService: svc);
        var result = await tools.SetSpeedMultiplier(WrongPid, 2.0);
        Assert.Contains("PID", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetSpeedMultiplier_NoService_ReturnsNotAvailable()
    {
        var tools = CreateTools(AttachedPid, speedHackService: null);
        var result = await tools.SetSpeedMultiplier(AttachedPid, 2.0);
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetSpeedMultiplier_Apply_ReportsSuccess()
    {
        var svc = new SpeedHackService(new StubSpeedHackEngine());
        var tools = CreateTools(AttachedPid, speedHackService: svc);
        var result = await tools.SetSpeedMultiplier(AttachedPid, 3.0);
        Assert.Contains("3.0x", result);
    }

    [Fact]
    public async Task SetSpeedMultiplier_Update_ReportsNewSpeed()
    {
        var svc = new SpeedHackService(new StubSpeedHackEngine());
        var tools = CreateTools(AttachedPid, speedHackService: svc);

        // First call applies
        await tools.SetSpeedMultiplier(AttachedPid, 2.0);
        // Second call updates
        var result = await tools.SetSpeedMultiplier(AttachedPid, 4.0);
        Assert.Contains("4.0x", result);
    }

    [Fact]
    public async Task RemoveSpeedHack_WrongPid_Rejected()
    {
        var svc = new SpeedHackService(new StubSpeedHackEngine());
        var tools = CreateTools(AttachedPid, speedHackService: svc);
        var result = await tools.RemoveSpeedHack(WrongPid);
        Assert.Contains("PID", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveSpeedHack_NoService_ReturnsNotAvailable()
    {
        var tools = CreateTools(AttachedPid, speedHackService: null);
        var result = await tools.RemoveSpeedHack(AttachedPid);
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveSpeedHack_AfterApply_Succeeds()
    {
        var svc = new SpeedHackService(new StubSpeedHackEngine());
        var tools = CreateTools(AttachedPid, speedHackService: svc);
        await tools.SetSpeedMultiplier(AttachedPid, 2.0);

        var result = await tools.RemoveSpeedHack(AttachedPid);
        Assert.Contains("removed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSpeedHackState_AfterApply_ReportsActive()
    {
        var svc = new SpeedHackService(new StubSpeedHackEngine());
        var tools = CreateTools(AttachedPid, speedHackService: svc);
        await tools.SetSpeedMultiplier(AttachedPid, 3.5);

        var result = await tools.GetSpeedHackState(AttachedPid);
        Assert.Contains("ACTIVE", result);
        Assert.Contains("3.5x", result);
    }

    // ══════════════════════════════════════════════════════════════════
    // 10K: PID validation on SpeedHack + VEH destructive tools
    // (extends SecurityValidationTests coverage)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SetSpeedMultiplier_NotAttached_Rejected()
    {
        // Create tools with no process attached
        var facade = new StubEngineFacade();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(facade, sessionRepo);
        var svc = new SpeedHackService(new StubSpeedHackEngine());
        var tools = new AiToolFunctions(facade, dashboard, new ScanService(new StubScanEngine()),
            new AddressTableService(facade), new DisassemblyService(new StubDisassemblyEngine()),
            new ScriptGenerationService(), speedHackService: svc);

        var result = await tools.SetSpeedMultiplier(1234, 2.0);
        Assert.Contains("No process attached", result, StringComparison.OrdinalIgnoreCase);
    }
}
