using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class DebuggerViewModelTests
{
    private readonly StubBreakpointEngine _breakpointEngine = new();
    private readonly StubCallStackEngine _callStackEngine = new();
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubNavigationService _navigationService = new();

    private DebuggerViewModel CreateVm()
    {
        var breakpointService = new BreakpointService(_breakpointEngine);
        return new DebuggerViewModel(
            breakpointService, _callStackEngine, _engineFacade,
            _processContext, _outputLog, _navigationService);
    }

    [Fact]
    public async Task RefreshBreakpoints_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task RefreshBreakpoints_WithProcess_PopulatesList()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-0", 0x7FF00100, BreakpointType.Software,
            BreakpointHitAction.LogAndContinue, true, 5, BreakpointMode.Hardware));

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);

        Assert.Single(vm.ActiveBreakpoints);
        Assert.Equal("bp-0", vm.ActiveBreakpoints[0].Id);
        Assert.Equal(5, vm.ActiveBreakpoints[0].HitCount);
    }

    [Fact]
    public async Task LoadHitDetails_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedBreakpoint = null;

        await vm.LoadHitDetailsCommand.ExecuteAsync(null);

        Assert.Empty(vm.HitDetails);
    }

    [Fact]
    public async Task LoadHitDetails_WithSelection_PopulatesHits()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _breakpointEngine.AddCannedHits("bp-0",
            new BreakpointHitEvent("bp-0", 0x7FF00100, 1, DateTimeOffset.UtcNow,
                new Dictionary<string, string> { ["RAX"] = "0x1234", ["RBX"] = "0x5678" }));
        vm.SelectedBreakpoint = new BreakpointDisplayItem { Id = "bp-0", Address = "0x7FF00100" };

        await vm.LoadHitDetailsCommand.ExecuteAsync(null);

        Assert.Single(vm.HitDetails);
        Assert.Equal("bp-0", vm.HitDetails[0].BreakpointId);
        Assert.Equal(2, vm.HitDetails[0].Registers.Count);
    }

    [Fact]
    public void SelectHit_PopulatesRegistersFromSnapshot()
    {
        var vm = CreateVm();
        var hit = new BreakpointHitDetailItem
        {
            BreakpointId = "bp-0",
            Address = "0x7FF00100",
            ThreadId = 1,
            Timestamp = "12:00:00",
            Registers = [
                new RegisterDisplayItem { Name = "RAX", Value = "0x1234" },
                new RegisterDisplayItem { Name = "RBX", Value = "0x5678" }
            ]
        };

        vm.SelectedHit = hit;

        Assert.Equal(2, vm.Registers.Count);
        Assert.Equal("RAX", vm.Registers[0].Name);
        Assert.Equal("0x1234", vm.Registers[0].Value);
    }

    [Fact]
    public async Task WalkCallStack_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.WalkCallStackCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task WalkCallStack_WithProcess_PopulatesFrames()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        await vm.WalkCallStackCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.CallStack.Count);
        Assert.Equal("game.dll+0x100", vm.CallStack[0].ModuleOffset);
    }
}
