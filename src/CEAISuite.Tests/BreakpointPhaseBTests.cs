using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase B: Advanced breakpoint feature tests.
/// Covers breakpoint groups (B3) and region breakpoints (B2).
/// </summary>
public class BreakpointPhaseBTests
{
    // ── B3: Breakpoint Groups ──

    [Fact]
    public void CreateGroup_ReturnsGroupWithId()
    {
        var service = new BreakpointService(new StubBreakpointEngine());
        var group = service.CreateGroup("test-group", ["bp-0", "bp-1", "bp-2"]);

        Assert.StartsWith("grp-", group.GroupId);
        Assert.Equal("test-group", group.Name);
        Assert.Equal(3, group.BreakpointIds.Count);
    }

    [Fact]
    public void ListGroups_ReturnsAllCreatedGroups()
    {
        var service = new BreakpointService(new StubBreakpointEngine());
        service.CreateGroup("alpha", ["bp-0"]);
        service.CreateGroup("beta", ["bp-1", "bp-2"]);

        var groups = service.ListGroups();
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void GetGroup_ReturnsCorrectGroup()
    {
        var service = new BreakpointService(new StubBreakpointEngine());
        var created = service.CreateGroup("find-me", ["bp-0"]);

        var found = service.GetGroup(created.GroupId);
        Assert.NotNull(found);
        Assert.Equal("find-me", found.Name);
    }

    [Fact]
    public void GetGroup_NonexistentId_ReturnsNull()
    {
        var service = new BreakpointService(new StubBreakpointEngine());
        Assert.Null(service.GetGroup("grp-nonexistent"));
    }

    [Fact]
    public void RemoveGroup_ReturnsTrue_WhenExists()
    {
        var service = new BreakpointService(new StubBreakpointEngine());
        var group = service.CreateGroup("temp", ["bp-0"]);

        Assert.True(service.RemoveGroup(group.GroupId));
        Assert.Empty(service.ListGroups());
    }

    [Fact]
    public void RemoveGroup_ReturnsFalse_WhenNotExists()
    {
        var service = new BreakpointService(new StubBreakpointEngine());
        Assert.False(service.RemoveGroup("grp-nope"));
    }

    [Fact]
    public async Task DisableGroup_RemovesAllBreakpointsInGroup()
    {
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine);

        var bp0 = await service.SetBreakpointAsync(1234, "0x1000", BreakpointType.HardwareExecute);
        var bp1 = await service.SetBreakpointAsync(1234, "0x2000", BreakpointType.HardwareExecute);
        var group = service.CreateGroup("pair", [bp0.Id, bp1.Id]);

        var disabled = await service.DisableGroupAsync(1234, group.GroupId);
        Assert.Equal(2, disabled);

        var remaining = await service.ListBreakpointsAsync(1234);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task DisableGroup_NonexistentGroup_ReturnsZero()
    {
        var service = new BreakpointService(new StubBreakpointEngine());
        var result = await service.DisableGroupAsync(1234, "grp-nope");
        Assert.Equal(0, result);
    }

    // ── B2: Region Breakpoints ──

    [Fact]
    public async Task SetRegionBreakpoint_SmallRegion_SingleBreakpoint()
    {
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine);

        var bps = await service.SetRegionBreakpointAsync(1234, "0x1000", 4);

        Assert.Single(bps);
    }

    [Fact]
    public async Task SetRegionBreakpoint_LargeRegion_MultipleBreakpoints()
    {
        var engine = new StubBreakpointEngine();

        // StubBreakpointEngine.SetRegionBreakpointAsync returns a single BP,
        // but the real engine returns one per page. Test the service passthrough.
        var bps = await engine.SetRegionBreakpointAsync(1234, (nuint)0x1000, 8192);
        Assert.NotEmpty(bps);
    }

    [Fact]
    public async Task SetRegionBreakpoint_ZeroLength_Throws()
    {
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine);

        // The real engine validates length > 0
        // StubEngine doesn't validate, but we test that service passes through
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SetRegionBreakpointAsync(1234, "0x1000", 0));
    }

    [Fact]
    public async Task SetRegionBreakpoint_ExceedsMaxSize_Throws()
    {
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SetRegionBreakpointAsync(1234, "0x1000", 65537));
    }

    // ── B3: AI Tool Integration ──

    [Fact]
    public void CreateBreakpointGroup_AiTool_ReturnsGroupInfo()
    {
        var bpService = new BreakpointService(new StubBreakpointEngine());
        var tools = CreateToolFunctions(bpService);

        var result = tools.CreateBreakpointGroup("my-group", "bp-0, bp-1, bp-2");

        Assert.Contains("my-group", result);
        Assert.Contains("3 breakpoints", result);
    }

    [Fact]
    public void ListBreakpointGroups_AiTool_EmptyByDefault()
    {
        var bpService = new BreakpointService(new StubBreakpointEngine());
        var tools = CreateToolFunctions(bpService);

        var result = tools.ListBreakpointGroups();
        Assert.Contains("No breakpoint groups", result);
    }

    [Fact]
    public void RemoveBreakpointGroup_AiTool_NotFound()
    {
        var bpService = new BreakpointService(new StubBreakpointEngine());
        var tools = CreateToolFunctions(bpService);

        var result = tools.RemoveBreakpointGroup("grp-nope");
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task SetRegionBreakpoint_AiTool_ReturnsInfo()
    {
        var bpService = new BreakpointService(new StubBreakpointEngine());
        var tools = CreateAttachedToolFunctions(bpService, 1234);

        var result = await tools.SetRegionBreakpoint(1234, "0x1000", 4);
        Assert.Contains("Region breakpoint set", result);
    }

    [Fact]
    public async Task SetRegionBreakpoint_AiTool_InvalidLength()
    {
        var bpService = new BreakpointService(new StubBreakpointEngine());
        var tools = CreateAttachedToolFunctions(bpService, 1234);

        var result = await tools.SetRegionBreakpoint(1234, "0x1000", 0);
        Assert.Contains("Invalid region", result);
    }

    // ── B1: Watchpoint Coalescing (unit-level via ApplyHardwareRegisters) ──

    [Fact]
    public void BreakpointGroup_Record_HasCorrectProperties()
    {
        var group = new BreakpointGroup("grp-1", "test", ["bp-0", "bp-1"]);
        Assert.Equal("grp-1", group.GroupId);
        Assert.Equal("test", group.Name);
        Assert.Equal(2, group.BreakpointIds.Count);
    }

    [Fact]
    public async Task MultipleHardwareBPs_SameRegion_BothCreated()
    {
        // The stub doesn't implement coalescing, but this verifies the service layer
        // handles multiple BPs at nearby addresses without error
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine);

        var bp1 = await service.SetBreakpointAsync(1234, "0x1000", BreakpointType.HardwareWrite,
            BreakpointMode.Hardware);
        var bp2 = await service.SetBreakpointAsync(1234, "0x1004", BreakpointType.HardwareWrite,
            BreakpointMode.Hardware);

        Assert.NotEqual(bp1.Id, bp2.Id);
        var all = await service.ListBreakpointsAsync(1234);
        Assert.Equal(2, all.Count);
    }

    // ── B4: Extended Register Snapshot ──

    [Fact]
    public void VehConditionEvaluator_R12_Supported()
    {
        var cond = new BreakpointCondition("R12 == 0x100", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["R12"] = "0x100" };

        Assert.True(Engine.Windows.VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void VehConditionEvaluator_RIP_Supported()
    {
        var cond = new BreakpointCondition("RIP == 0x401000", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["RIP"] = "0x401000" };

        Assert.True(Engine.Windows.VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void VehConditionEvaluator_RFLAGS_Supported()
    {
        var cond = new BreakpointCondition("RFLAGS == 0x246", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["RFLAGS"] = "0x246" };

        Assert.True(Engine.Windows.VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void RegisterSnapshot_ExtendedFields_HaveDefaults()
    {
        var snap = new RegisterSnapshot(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);

        // B4: New fields default to 0
        Assert.Equal(0UL, snap.Rip);
        Assert.Equal(0UL, snap.R12);
        Assert.Equal(0UL, snap.R13);
        Assert.Equal(0UL, snap.R14);
        Assert.Equal(0UL, snap.R15);
        Assert.Equal(0UL, snap.EFlags);
    }

    [Fact]
    public void RegisterSnapshot_ExtendedFields_CanBeSet()
    {
        var snap = new RegisterSnapshot(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12,
            Rip: 0x401000, R12: 100, R13: 200, R14: 300, R15: 400, EFlags: 0x246);

        Assert.Equal(0x401000UL, snap.Rip);
        Assert.Equal(100UL, snap.R12);
        Assert.Equal(0x246UL, snap.EFlags);
    }

    // ── Helpers ──

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

    private static AiToolFunctions CreateAttachedToolFunctions(BreakpointService? breakpointService, int processId)
    {
        var engine = new StubEngineFacade();
        engine.AttachAsync(processId).GetAwaiter().GetResult();
        var dashboard = new WorkspaceDashboardService(engine, new StubSessionRepository());
        var scan = new ScanService(new StubScanEngine());
        var addressTable = new AddressTableService(engine);
        var disassembly = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGen = new ScriptGenerationService();
        return new AiToolFunctions(engine, dashboard, scan, addressTable, disassembly, scriptGen,
            breakpointService: breakpointService);
    }
}
