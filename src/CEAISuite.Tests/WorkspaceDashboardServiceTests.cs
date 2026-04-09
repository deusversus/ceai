using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class WorkspaceDashboardServiceTests
{
    private static (StubEngineFacade facade, StubSessionRepository repo, WorkspaceDashboardService svc) CreateService()
    {
        var facade = new StubEngineFacade();
        var repo = new StubSessionRepository();
        var svc = new WorkspaceDashboardService(facade, repo);
        return (facade, repo, svc);
    }

    [Fact]
    public async Task BuildAsync_LoadsProcessesAndSessions()
    {
        var (facade, repo, svc) = CreateService();
        repo.AddCannedSession("session-1", "TestGame.exe", 1000, 5, 3);

        var dashboard = await svc.BuildAsync("/tmp/store");

        Assert.NotNull(dashboard);
        Assert.Equal(3, dashboard.RunningProcesses.Count); // StubEngineFacade returns 3 processes
        Assert.Single(dashboard.RecentSessions);
        Assert.Equal("session-1", dashboard.RecentSessions[0].Id);
        Assert.Contains("3 processes", dashboard.StatusMessage);
        Assert.Contains("1 saved sessions", dashboard.StatusMessage);
    }

    [Fact]
    public async Task BuildAsync_NoSessions_CreatesInitialSession()
    {
        var (_, _, svc) = CreateService();

        var dashboard = await svc.BuildAsync("/tmp/store");

        // When no sessions exist, it creates an initial-workspace session
        Assert.NotNull(dashboard);
        Assert.Equal("/tmp/store", dashboard.DataStorePath);
    }

    [Fact]
    public async Task BuildAsync_ThrowsOnNullPath()
    {
        var (_, _, svc) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => svc.BuildAsync(""));
    }

    [Fact]
    public async Task BuildAsync_SetsCurrentDashboard()
    {
        var (_, repo, svc) = CreateService();
        repo.AddCannedSession("s1", "game.exe", 1, 1, 1);

        Assert.Null(svc.CurrentDashboard);
        await svc.BuildAsync("/tmp/store");
        Assert.NotNull(svc.CurrentDashboard);
    }

    [Fact]
    public async Task InspectProcessAsync_AttachesAndReturnsModules()
    {
        var (facade, _, svc) = CreateService();
        // Write some bytes at the module base for the memory sample
        facade.WriteMemoryDirect((nuint)0x400000, new byte[32]);

        var inspection = await svc.InspectProcessAsync(1000);

        Assert.Equal(1000, inspection.ProcessId);
        Assert.Equal("TestGame.exe", inspection.ProcessName);
        Assert.Single(inspection.Modules);
        Assert.Equal("main.exe", inspection.Modules[0].Name);
        Assert.Contains("Attached to TestGame.exe", inspection.StatusMessage);
    }

    [Fact]
    public async Task InspectProcessAsync_ProcessNotFound_Throws()
    {
        var (_, _, svc) = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.InspectProcessAsync(9999));
    }

    [Fact]
    public async Task InspectProcessAsync_UpdatesCurrentDashboard()
    {
        var (facade, repo, svc) = CreateService();
        repo.AddCannedSession("s1", "game.exe", 1, 1, 1);
        facade.WriteMemoryDirect((nuint)0x400000, new byte[32]);

        await svc.BuildAsync("/tmp/store");
        Assert.Null(svc.CurrentDashboard!.CurrentInspection);

        await svc.InspectProcessAsync(1000);
        Assert.NotNull(svc.CurrentDashboard!.CurrentInspection);
        Assert.Equal(1000, svc.CurrentDashboard.CurrentInspection!.ProcessId);
    }

    [Fact]
    public async Task DetachProcess_ClearsInspectionState()
    {
        var (facade, repo, svc) = CreateService();
        // Manually set up dashboard state
        repo.AddCannedSession("s1", "game.exe", 1, 1, 1);

        // Build first, then detach
        await svc.BuildAsync("/tmp/store");
        await svc.InspectProcessAsync(1000);

        Assert.NotNull(svc.CurrentDashboard!.CurrentInspection);

        svc.DetachProcess();

        Assert.Null(svc.CurrentDashboard!.CurrentInspection);
        Assert.Contains("Detached", svc.CurrentDashboard.StatusMessage);
        Assert.False(facade.IsAttached);
    }

    [Fact]
    public async Task ReadAddressAsync_ReturnsTypedValue()
    {
        var (facade, _, svc) = CreateService();
        facade.WriteMemoryDirect((nuint)0x1000, BitConverter.GetBytes(42));

        var probe = await svc.ReadAddressAsync(1000, "0x1000", MemoryDataType.Int32);

        Assert.Equal("0x1000", probe.Address);
        Assert.Equal("Int32", probe.DataType);
        Assert.Equal("42", probe.DisplayValue);
        Assert.NotNull(probe.HexBytes);
    }

    [Fact]
    public async Task WriteAddressAsync_WritesValue_ReturnsConfirmation()
    {
        var (facade, _, svc) = CreateService();

        var msg = await svc.WriteAddressAsync(1000, "0x2000", MemoryDataType.Int32, "999");

        Assert.Contains("Wrote", msg);
        Assert.Contains("999", msg);
        Assert.Contains("Int32", msg);
    }

    [Fact]
    public async Task ReadAddressAsync_HexAddress_ParsedCorrectly()
    {
        var (facade, _, svc) = CreateService();
        facade.WriteMemoryDirect((nuint)0xABCD, BitConverter.GetBytes(77));

        var probe = await svc.ReadAddressAsync(1000, "0xABCD", MemoryDataType.Int32);

        Assert.Equal("77", probe.DisplayValue);
    }
}
