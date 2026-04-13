using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for VehDebugViewModel — agent lifecycle, breakpoint management,
/// stealth toggle, hit stream lifecycle, and status refresh.
/// </summary>
public class VehDebugViewModelTests
{
    private const int TestPid = 1234;

    private static (VehDebugViewModel vm, StubVehDebugger engine, StubOutputLog log) CreateVm()
    {
        var engine = new StubVehDebugger();
        var service = new VehDebugService(engine);
        var facade = new StubProcessContext();
        facade.AttachedProcessId = TestPid;
        var log = new StubOutputLog();
        var dispatcher = new StubDispatcherService();
        var vm = new VehDebugViewModel(service, facade, log, dispatcher);
        return (vm, engine, log);
    }

    [Fact]
    public async Task InjectAgent_Succeeds_UpdatesStatus()
    {
        var (vm, _, log) = CreateVm();

        await vm.InjectAgentCommand.ExecuteAsync(null);
        vm.RefreshStatus();

        Assert.True(vm.IsAgentInjected);
        Assert.Contains("Active", vm.AgentStatus);
        Assert.Contains(log.LoggedMessages, m => m.Message.Contains("injected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EjectAgent_Succeeds_ClearsState()
    {
        var (vm, _, _) = CreateVm();
        await vm.InjectAgentCommand.ExecuteAsync(null);

        await vm.EjectAgentCommand.ExecuteAsync(null);
        vm.RefreshStatus();

        Assert.False(vm.IsAgentInjected);
        Assert.Empty(vm.Breakpoints);
    }

    [Fact]
    public async Task SetBreakpoint_Succeeds_AddsToList()
    {
        var (vm, _, _) = CreateVm();
        await vm.InjectAgentCommand.ExecuteAsync(null);
        vm.NewBpAddress = "0x400000";
        vm.SelectedBpType = VehBreakpointType.Execute;

        await vm.SetBreakpointCommand.ExecuteAsync(null);

        Assert.Single(vm.Breakpoints);
        Assert.Contains("0x400000", vm.Breakpoints[0].Address, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", vm.NewBpAddress); // cleared after success
    }

    [Fact]
    public async Task RemoveBreakpoint_Succeeds_RemovesFromList()
    {
        var (vm, _, _) = CreateVm();
        await vm.InjectAgentCommand.ExecuteAsync(null);
        vm.NewBpAddress = "0x400000";
        await vm.SetBreakpointCommand.ExecuteAsync(null);
        vm.SelectedBreakpoint = vm.Breakpoints[0];

        await vm.RemoveBreakpointCommand.ExecuteAsync(null);

        Assert.Empty(vm.Breakpoints);
    }

    [Fact]
    public async Task ToggleStealth_EnablesAndDisables()
    {
        var (vm, _, _) = CreateVm();
        await vm.InjectAgentCommand.ExecuteAsync(null);

        // Enable
        await vm.ToggleStealthCommand.ExecuteAsync(null);
        vm.RefreshStatus();
        Assert.True(vm.IsStealthActive);

        // Disable
        await vm.ToggleStealthCommand.ExecuteAsync(null);
        vm.RefreshStatus();
        Assert.False(vm.IsStealthActive);
    }

    [Fact]
    public void RefreshStatus_NoProcess_ReportsNotAttached()
    {
        var engine = new StubVehDebugger();
        var service = new VehDebugService(engine);
        var facade = new StubProcessContext(); // not attached — AttachedProcessId is null
        var vm = new VehDebugViewModel(service, facade, new StubOutputLog(), new StubDispatcherService());

        vm.RefreshStatus();

        Assert.False(vm.IsAgentInjected);
        Assert.Contains("No process", vm.AgentStatus);
    }

    [Fact]
    public void HitStream_StartStop_TogglesRunningState()
    {
        var (vm, _, _) = CreateVm();

        // Can't start without injection, but the flag should toggle
        vm.StartHitStreamCommand.Execute(null);
        // IsHitStreamRunning should be true briefly (Task.Run fires)
        // Stop immediately
        vm.StopHitStreamCommand.Execute(null);
        Assert.False(vm.IsHitStreamRunning);
    }

    [Fact]
    public void ClearHitStream_EmptiesCollection()
    {
        var (vm, _, _) = CreateVm();
        vm.HitStream.Add(new VehHitDisplayItem { Address = "0x1" });
        vm.HitStream.Add(new VehHitDisplayItem { Address = "0x2" });

        vm.ClearHitStreamCommand.Execute(null);

        Assert.Empty(vm.HitStream);
    }

    [Fact]
    public async Task SetBreakpoint_EmptyAddress_DoesNothing()
    {
        var (vm, _, _) = CreateVm();
        await vm.InjectAgentCommand.ExecuteAsync(null);
        vm.NewBpAddress = "";

        await vm.SetBreakpointCommand.ExecuteAsync(null);

        Assert.Empty(vm.Breakpoints);
    }

    [Fact]
    public async Task RefreshStatus_WithStealth_IncludesStealthInStatus()
    {
        var (vm, _, _) = CreateVm();
        await vm.InjectAgentCommand.ExecuteAsync(null);
        await vm.ToggleStealthCommand.ExecuteAsync(null);
        vm.RefreshStatus();

        Assert.Contains("STEALTH", vm.AgentStatus);
    }
}
