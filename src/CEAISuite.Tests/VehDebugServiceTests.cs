using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class VehDebugServiceTests
{
    private const int TestPid = 1234;

    private static (VehDebugService service, StubVehDebugger engine) CreateService()
    {
        var engine = new StubVehDebugger();
        var service = new VehDebugService(engine);
        return (service, engine);
    }

    [Fact]
    public async Task Inject_Succeeds()
    {
        var (svc, _) = CreateService();

        var result = await svc.InjectAsync(TestPid);

        Assert.True(result.Success);
        var status = svc.GetStatus(TestPid);
        Assert.True(status.IsInjected);
    }

    [Fact]
    public async Task Inject_AlreadyInjected_ReturnsError()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(TestPid);

        var result = await svc.InjectAsync(TestPid);

        Assert.False(result.Success);
        Assert.Contains("Already injected", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Eject_Succeeds()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(TestPid);

        var ok = await svc.EjectAsync(TestPid);

        Assert.True(ok);
        var status = svc.GetStatus(TestPid);
        Assert.False(status.IsInjected);
    }

    [Fact]
    public async Task Eject_NotInjected_ReturnsFalse()
    {
        var (svc, _) = CreateService();

        var ok = await svc.EjectAsync(TestPid);

        Assert.False(ok);
    }

    [Fact]
    public async Task SetBreakpoint_Succeeds_ReturnsDrSlot()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(TestPid);

        var result = await svc.SetBreakpointAsync(TestPid, (nuint)0x400000, VehBreakpointType.Execute);

        Assert.True(result.Success);
        Assert.True(result.DrSlot >= 0 && result.DrSlot <= 3);
    }

    [Fact]
    public async Task SetBreakpoint_NotInjected_ReturnsError()
    {
        var (svc, _) = CreateService();

        var result = await svc.SetBreakpointAsync(TestPid, (nuint)0x400000, VehBreakpointType.Execute);

        Assert.False(result.Success);
        Assert.Contains("not injected", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetBreakpoint_AllSlotsFull_ReturnsError()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(TestPid);

        // Fill all 4 DR slots
        for (int i = 0; i < 4; i++)
        {
            var r = await svc.SetBreakpointAsync(TestPid, (nuint)(0x400000 + i * 0x100), VehBreakpointType.Execute);
            Assert.True(r.Success, $"Slot {i} should succeed");
        }

        // 5th should fail
        var result = await svc.SetBreakpointAsync(TestPid, (nuint)0x500000, VehBreakpointType.Execute);

        Assert.False(result.Success);
        Assert.Contains("4 hardware breakpoint slots", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveBreakpoint_Succeeds()
    {
        var (svc, _) = CreateService();
        await svc.InjectAsync(TestPid);
        var bp = await svc.SetBreakpointAsync(TestPid, (nuint)0x400000, VehBreakpointType.Execute);

        var ok = await svc.RemoveBreakpointAsync(TestPid, bp.DrSlot);

        Assert.True(ok);
    }

    [Fact]
    public void GetStatus_NotInjected_ReturnsDefaults()
    {
        var (svc, _) = CreateService();

        var status = svc.GetStatus(TestPid);

        Assert.False(status.IsInjected);
        Assert.Equal(0, status.ActiveBreakpoints);
        Assert.Equal(0, status.TotalHits);
    }

    [Fact]
    public async Task EngineNotAvailable_ReturnsError()
    {
        var svc = new VehDebugService(engine: null);

        var injectResult = await svc.InjectAsync(TestPid);
        var ejectResult = await svc.EjectAsync(TestPid);
        var bpResult = await svc.SetBreakpointAsync(TestPid, (nuint)0x400000, VehBreakpointType.Execute);
        var removeResult = await svc.RemoveBreakpointAsync(TestPid, 0);
        var status = svc.GetStatus(TestPid);

        Assert.False(injectResult.Success);
        Assert.Contains("not available", injectResult.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(ejectResult);
        Assert.False(bpResult.Success);
        Assert.Contains("not available", bpResult.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(removeResult);
        Assert.False(status.IsInjected);
    }
}
