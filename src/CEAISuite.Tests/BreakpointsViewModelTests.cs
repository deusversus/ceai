using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class BreakpointsViewModelTests
{
    private readonly StubBreakpointEngine _breakpointEngine = new();
    private readonly StubCodeCaveEngine _codeCaveEngine = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();

    private BreakpointsViewModel CreateVm()
    {
        var breakpointService = new BreakpointService(_breakpointEngine);
        return new BreakpointsViewModel(breakpointService, _codeCaveEngine, _processContext, _outputLog);
    }

    [Fact]
    public async Task RefreshBreakpoints_NoProcess_DoesNotThrow()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);

        // No process → early return, no crash
        Assert.Empty(vm.Breakpoints);
    }

    [Fact]
    public async Task RefreshBreakpoints_WithProcess_PopulatesList()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-0", 0x7FF00100, BreakpointType.Software,
            BreakpointHitAction.LogAndContinue, true, 3, BreakpointMode.Hardware));

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);

        Assert.Single(vm.Breakpoints);
    }

    [Fact]
    public async Task RemoveSelected_NoSelection_DoesNotThrow()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedBreakpoint = null;

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        // No selection → early return, no crash
        Assert.NotNull(vm);
    }

    [Fact]
    public async Task RefreshHitLog_NoSelection_DoesNotThrow()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedBreakpoint = null;

        await vm.RefreshHitLogCommand.ExecuteAsync(null);

        Assert.Empty(vm.HitLog);
    }
}
