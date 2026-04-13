using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Extended VEH debugging tests covering gaps from audit:
/// - AI tool layer (PID validation, null service, invalid inputs)
/// - Hit stream via StubVehDebugger.AddCannedHit
/// - DR slot reuse after removal
/// - Write/ReadWrite breakpoint types
/// - Concurrent breakpoint operations
/// </summary>
public class VehDebugExtendedTests
{
    private const int AttachedPid = 1000;
    private const int WrongPid = 9999;

    // ── Helper: create AiToolFunctions with VEH service wired ──

    private static AiToolFunctions CreateToolsWithVeh(int attachedPid, StubVehDebugger? engine = null)
    {
        var facade = new StubEngineFacade();
        facade.AttachAsync(attachedPid).Wait();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(facade, sessionRepo);
        var scanService = new ScanService(new StubScanEngine());
        var disasmService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGen = new ScriptGenerationService();
        var addressTable = new AddressTableService(facade);
        var vehEngine = engine ?? new StubVehDebugger();
        var vehService = new VehDebugService(vehEngine);

        return new AiToolFunctions(facade, dashboard, scanService, addressTable,
            disasmService, scriptGen, vehDebugService: vehService);
    }

    private static AiToolFunctions CreateToolsWithoutVeh(int attachedPid)
    {
        var facade = new StubEngineFacade();
        facade.AttachAsync(attachedPid).Wait();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(facade, sessionRepo);
        var scanService = new ScanService(new StubScanEngine());
        var disasmService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGen = new ScriptGenerationService();
        var addressTable = new AddressTableService(facade);

        return new AiToolFunctions(facade, dashboard, scanService, addressTable,
            disasmService, scriptGen);  // No VEH service
    }

    // ══════════════════════════════════════════════════════════════════
    // AI Tool Tests — PID Validation
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InjectVehAgent_WrongPid_Rejected()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        var result = await tools.InjectVehAgent(WrongPid);
        Assert.Contains("PID", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EjectVehAgent_WrongPid_Rejected()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        var result = await tools.EjectVehAgent(WrongPid);
        Assert.Contains("PID", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetVehBreakpoint_WrongPid_Rejected()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        var result = await tools.SetVehBreakpoint(WrongPid, "0x400000");
        Assert.Contains("PID", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveVehBreakpoint_WrongPid_Rejected()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        var result = await tools.RemoveVehBreakpoint(WrongPid, 0);
        Assert.Contains("PID", result, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════
    // AI Tool Tests — Null Service Fallback
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InjectVehAgent_NoService_ReturnsNotAvailable()
    {
        var tools = CreateToolsWithoutVeh(AttachedPid);
        var result = await tools.InjectVehAgent(AttachedPid);
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetVehBreakpoint_NoService_ReturnsNotAvailable()
    {
        var tools = CreateToolsWithoutVeh(AttachedPid);
        var result = await tools.SetVehBreakpoint(AttachedPid, "0x400000");
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetVehStatus_NoService_ReturnsNotAvailable()
    {
        var tools = CreateToolsWithoutVeh(AttachedPid);
        var result = await tools.GetVehStatus(AttachedPid);
        Assert.Contains("not available", result, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════
    // AI Tool Tests — Invalid Inputs
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SetVehBreakpoint_InvalidType_ReturnsError()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        await tools.InjectVehAgent(AttachedPid);
        var result = await tools.SetVehBreakpoint(AttachedPid, "0x400000", "Garbage");
        Assert.Contains("Invalid type", result, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════
    // AI Tool Tests — Success Paths
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InjectVehAgent_CorrectPid_Succeeds()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        var result = await tools.InjectVehAgent(AttachedPid);
        Assert.Contains("injected", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetVehBreakpoint_AfterInject_ReportsDrSlot()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        await tools.InjectVehAgent(AttachedPid);
        var result = await tools.SetVehBreakpoint(AttachedPid, "0x400000", "Execute");
        Assert.Contains("DR", result);
        Assert.Contains("0x400000", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetVehStatus_AfterInject_ReportsActive()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        await tools.InjectVehAgent(AttachedPid);
        var result = await tools.GetVehStatus(AttachedPid);
        Assert.Contains("ACTIVE", result);
    }

    [Fact]
    public async Task EjectVehAgent_AfterInject_Succeeds()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        await tools.InjectVehAgent(AttachedPid);
        var result = await tools.EjectVehAgent(AttachedPid);
        Assert.Contains("ejected", result, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════
    // Hit Stream Tests
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetHitStream_WithCannedHits_YieldsEvents()
    {
        var engine = new StubVehDebugger();
        var service = new VehDebugService(engine);

        await service.InjectAsync(AttachedPid);

        // Add canned hits
        engine.AddCannedHit(AttachedPid, new VehHitEvent(
            (nuint)0x400000, 1234, VehBreakpointType.Execute, (nuint)0x1,
            new RegisterSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            Environment.TickCount64));
        engine.AddCannedHit(AttachedPid, new VehHitEvent(
            (nuint)0x400004, 1234, VehBreakpointType.Execute, (nuint)0x2,
            new RegisterSnapshot(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            Environment.TickCount64));

        var hits = new List<VehHitEvent>();
        await foreach (var hit in engine.GetHitStreamAsync(AttachedPid))
            hits.Add(hit);

        Assert.Equal(2, hits.Count);
        Assert.Equal((nuint)0x400000, hits[0].Address);
        Assert.Equal((nuint)0x400004, hits[1].Address);
    }

    [Fact]
    public async Task GetHitStream_NotInjected_YieldsEmpty()
    {
        var engine = new StubVehDebugger();

        var hits = new List<VehHitEvent>();
        await foreach (var hit in engine.GetHitStreamAsync(AttachedPid))
            hits.Add(hit);

        Assert.Empty(hits);
    }

    // ══════════════════════════════════════════════════════════════════
    // DR Slot Reuse + Breakpoint Type Coverage
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DrSlotReuse_AfterRemoval_SlotBecomesAvailable()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(AttachedPid);

        // Fill all 4 slots
        var slots = new int[4];
        for (int i = 0; i < 4; i++)
        {
            var r = await svc.SetBreakpointAsync(AttachedPid, (nuint)(0x400000 + i * 0x100), VehBreakpointType.Execute);
            Assert.True(r.Success);
            slots[i] = r.DrSlot;
        }

        // Remove slot 2
        await svc.RemoveBreakpointAsync(AttachedPid, slots[2]);

        // Should be able to set a new breakpoint — gets the freed slot
        var reuse = await svc.SetBreakpointAsync(AttachedPid, (nuint)0x500000, VehBreakpointType.Execute);
        Assert.True(reuse.Success, "Should reuse freed DR slot");
        Assert.Equal(slots[2], reuse.DrSlot);
    }

    [Fact]
    public async Task SetBreakpoint_WriteType_Succeeds()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(AttachedPid);

        var result = await svc.SetBreakpointAsync(AttachedPid, (nuint)0x400000, VehBreakpointType.Write);

        Assert.True(result.Success);
        Assert.True(result.DrSlot >= 0 && result.DrSlot <= 3);
    }

    [Fact]
    public async Task SetBreakpoint_ReadWriteType_Succeeds()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(AttachedPid);

        var result = await svc.SetBreakpointAsync(AttachedPid, (nuint)0x400000, VehBreakpointType.ReadWrite);

        Assert.True(result.Success);
        Assert.True(result.DrSlot >= 0 && result.DrSlot <= 3);
    }

    [Fact]
    public async Task SetBreakpoint_MixedTypes_AllSucceed()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(AttachedPid);

        var r0 = await svc.SetBreakpointAsync(AttachedPid, (nuint)0x400000, VehBreakpointType.Execute);
        var r1 = await svc.SetBreakpointAsync(AttachedPid, (nuint)0x400100, VehBreakpointType.Write);
        var r2 = await svc.SetBreakpointAsync(AttachedPid, (nuint)0x400200, VehBreakpointType.ReadWrite);
        var r3 = await svc.SetBreakpointAsync(AttachedPid, (nuint)0x400300, VehBreakpointType.Execute);

        Assert.True(r0.Success); Assert.True(r1.Success);
        Assert.True(r2.Success); Assert.True(r3.Success);

        // All 4 slots should be different
        var usedSlots = new HashSet<int> { r0.DrSlot, r1.DrSlot, r2.DrSlot, r3.DrSlot };
        Assert.Equal(4, usedSlots.Count);
    }

    // ══════════════════════════════════════════════════════════════════
    // Concurrent Operations
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentSetBreakpoint_NoRaceCondition()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(AttachedPid);

        // Set 4 breakpoints concurrently — all should succeed, no duplicate slots
        var tasks = Enumerable.Range(0, 4).Select(i =>
            svc.SetBreakpointAsync(AttachedPid, (nuint)(0x400000 + i * 0x100), VehBreakpointType.Execute));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        var slots = results.Select(r => r.DrSlot).ToHashSet();
        Assert.Equal(4, slots.Count); // All unique slots
    }

    [Fact]
    public async Task ConcurrentInject_SamePid_OnlyOneSucceeds()
    {
        var engine = new StubVehDebugger();
        var svc = new VehDebugService(engine);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => svc.InjectAsync(AttachedPid));

        var results = await Task.WhenAll(tasks);

        int successCount = results.Count(r => r.Success);
        Assert.Equal(1, successCount); // Only one should win the race
    }

    // ══════════════════════════════════════════════════════════════════
    // Sub-phase A: Data Size Validation
    // ══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task SetBreakpoint_ValidDataSizes_Succeeds(int dataSize)
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(AttachedPid);

        var result = await svc.SetBreakpointAsync(AttachedPid, (nuint)0x400000, VehBreakpointType.Write, dataSize);

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    public async Task SetVehBreakpoint_InvalidDataSize_ReturnsError(int dataSize)
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        await tools.InjectVehAgent(AttachedPid);
        var result = await tools.SetVehBreakpoint(AttachedPid, "0x400000", "Write", dataSize);
        Assert.Contains("Invalid", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetVehBreakpoint_WithDataSize_ReportsSizeInOutput()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        await tools.InjectVehAgent(AttachedPid);
        var result = await tools.SetVehBreakpoint(AttachedPid, "0x400000", "Write", 2);
        Assert.Contains("size=2", result);
    }

    // ══════════════════════════════════════════════════════════════════
    // Sub-phase A: Overflow Detection
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetVehStatus_WithOverflows_ReportsOverflowCount()
    {
        var engine = new StubVehDebugger();
        var svc = new VehDebugService(engine);
        await svc.InjectAsync(AttachedPid);
        engine.SimulateOverflow(AttachedPid, 5);

        var status = svc.GetStatus(AttachedPid);

        Assert.Equal(5, status.OverflowCount);
    }

    [Fact]
    public async Task GetVehStatus_ReportsOverflowInToolOutput()
    {
        var engine = new StubVehDebugger();
        var tools = CreateToolsWithVeh(AttachedPid, engine);
        await tools.InjectVehAgent(AttachedPid);
        engine.SimulateOverflow(AttachedPid, 3);

        var result = await tools.GetVehStatus(AttachedPid);

        Assert.Contains("3 overflows", result);
    }

    // ══════════════════════════════════════════════════════════════════
    // Sub-phase A: Agent Health Monitoring
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetVehStatus_HealthyAgent_ReportsHealthy()
    {
        var engine = new StubVehDebugger { SimulatedHealth = VehAgentHealth.Healthy };
        var svc = new VehDebugService(engine);
        await svc.InjectAsync(AttachedPid);

        var status = svc.GetStatus(AttachedPid);

        Assert.Equal(VehAgentHealth.Healthy, status.AgentHealth);
    }

    [Fact]
    public async Task GetVehStatus_UnresponsiveAgent_ReportsUnresponsive()
    {
        var engine = new StubVehDebugger { SimulatedHealth = VehAgentHealth.Unresponsive };
        var svc = new VehDebugService(engine);
        await svc.InjectAsync(AttachedPid);

        var status = svc.GetStatus(AttachedPid);

        Assert.Equal(VehAgentHealth.Unresponsive, status.AgentHealth);
    }

    [Fact]
    public async Task GetVehStatus_ToolOutput_IncludesHealth()
    {
        var engine = new StubVehDebugger { SimulatedHealth = VehAgentHealth.Healthy };
        var tools = CreateToolsWithVeh(AttachedPid, engine);
        await tools.InjectVehAgent(AttachedPid);

        var result = await tools.GetVehStatus(AttachedPid);

        Assert.Contains("Healthy", result);
    }

    // ══════════════════════════════════════════════════════════════════
    // Sub-phase A: Thread Refresh
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshThreads_AfterInject_Succeeds()
    {
        var (svc, engine) = CreateService();
        await svc.InjectAsync(AttachedPid);

        var ok = await svc.RefreshThreadsAsync(AttachedPid);

        Assert.True(ok);
    }

    [Fact]
    public async Task RefreshThreads_NotInjected_ReturnsFalse()
    {
        var (svc, _) = CreateService();

        var ok = await svc.RefreshThreadsAsync(AttachedPid);

        Assert.False(ok);
    }

    [Fact]
    public async Task RefreshVehThreads_Tool_Succeeds()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        await tools.InjectVehAgent(AttachedPid);

        var result = await tools.RefreshVehThreads(AttachedPid);

        Assert.Contains("refresh complete", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshVehThreads_WrongPid_Rejected()
    {
        var tools = CreateToolsWithVeh(AttachedPid);
        var result = await tools.RefreshVehThreads(WrongPid);
        Assert.Contains("PID", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helper ──

    private static (VehDebugService service, StubVehDebugger engine) CreateService()
    {
        var engine = new StubVehDebugger();
        var service = new VehDebugService(engine);
        return (service, engine);
    }
}
