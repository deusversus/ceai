using CEAISuite.Application;
using CEAISuite.Desktop.Models;
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

    // ── Remove breakpoint ──

    [Fact]
    public async Task RemoveSelected_WithSelection_RemovesBreakpoint()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-remove-me", 0x7FF00100, BreakpointType.Software,
            BreakpointHitAction.LogAndContinue, true, 0, BreakpointMode.Hardware));

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);
        Assert.Single(vm.Breakpoints);

        vm.SelectedBreakpoint = vm.Breakpoints[0];
        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        // After remove + auto-refresh, should be empty
        Assert.Empty(vm.Breakpoints);
    }

    [Fact]
    public async Task RemoveSelected_NoProcess_DoesNotThrow()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.SelectedBreakpoint = new BreakpointDisplayItem { Id = "bp-0", Address = "0x100" };

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        // No crash, no process → early return
        Assert.NotNull(vm);
    }

    // ── Clear all breakpoints ──

    [Fact]
    public async Task RemoveAll_ClearsAllBreakpoints()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-a", 0x7FF00100, BreakpointType.Software,
            BreakpointHitAction.LogAndContinue, true, 1, BreakpointMode.Hardware));
        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-b", 0x7FF00200, BreakpointType.HardwareWrite,
            BreakpointHitAction.Break, true, 5, BreakpointMode.Hardware));

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Breakpoints.Count);

        await vm.RemoveAllCommand.ExecuteAsync(null);

        Assert.Empty(vm.Breakpoints);
    }

    [Fact]
    public async Task RemoveAll_NoProcess_DoesNotThrow()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.RemoveAllCommand.ExecuteAsync(null);

        // No crash
        Assert.Empty(vm.Breakpoints);
    }

    // ── Refresh hit log ──

    [Fact]
    public async Task RefreshHitLog_NoSelection_SetsSelectMessage()
    {
        var vm = CreateVm();
        vm.SelectedBreakpoint = null;

        await vm.RefreshHitLogCommand.ExecuteAsync(null);

        Assert.Contains("Select a breakpoint", vm.HitLogStatus);
    }

    [Fact]
    public async Task RefreshHitLog_WithSelection_PopulatesHitLog()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-hits", 0x7FF00100, BreakpointType.Software,
            BreakpointHitAction.LogAndContinue, true, 2, BreakpointMode.Hardware));
        _breakpointEngine.AddCannedHits("bp-hits",
            new BreakpointHitEvent("bp-hits", 0x7FF00100, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>()),
            new BreakpointHitEvent("bp-hits", 0x7FF00100, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);
        vm.SelectedBreakpoint = vm.Breakpoints[0];

        await vm.RefreshHitLogCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.HitLog.Count);
        Assert.Contains("2 hits", vm.HitLogStatus);
    }

    // ── Hit count display ──

    [Fact]
    public async Task RefreshBreakpoints_DisplaysHitCount()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-count", 0x7FF00100, BreakpointType.Software,
            BreakpointHitAction.LogAndContinue, true, 42, BreakpointMode.Hardware));

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);

        Assert.Single(vm.Breakpoints);
        Assert.Equal(42, vm.Breakpoints[0].HitCount);
    }

    [Fact]
    public async Task RefreshBreakpoints_DisplaysCorrectStatus()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-active", 0x7FF00100, BreakpointType.Software,
            BreakpointHitAction.LogAndContinue, true, 0, BreakpointMode.Hardware));

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);

        Assert.Equal("Active", vm.Breakpoints[0].Status);
    }

    [Fact]
    public async Task RefreshBreakpoints_DisplaysCorrectType()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _breakpointEngine.AddCannedBreakpoint(new BreakpointDescriptor(
            "bp-type", 0x7FF00100, BreakpointType.HardwareWrite,
            BreakpointHitAction.LogAndContinue, true, 0, BreakpointMode.Hardware));

        await vm.RefreshBreakpointsCommand.ExecuteAsync(null);

        Assert.Equal("HardwareWrite", vm.Breakpoints[0].Type);
    }

    // ── Code cave hooks ──

    [Fact]
    public async Task InstallHook_ValidAddress_InstallsAndRefreshes()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.CodeCaveAddress = "0x7FF00100";

        await vm.InstallHookCommand.ExecuteAsync(null);

        Assert.Single(vm.CodeCaveHooks);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Info" && m.Message.Contains("Hook installed"));
    }

    [Fact]
    public async Task InstallHook_EmptyAddress_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.CodeCaveAddress = "";

        await vm.InstallHookCommand.ExecuteAsync(null);

        Assert.Empty(vm.CodeCaveHooks);
    }

    [Fact]
    public async Task InstallHook_NoProcess_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.CodeCaveAddress = "0x7FF00100";

        await vm.InstallHookCommand.ExecuteAsync(null);

        Assert.Empty(vm.CodeCaveHooks);
    }

    [Fact]
    public async Task RemoveSelectedHook_WithSelection_Removes()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.CodeCaveAddress = "0x7FF00100";
        await vm.InstallHookCommand.ExecuteAsync(null);
        Assert.Single(vm.CodeCaveHooks);

        vm.SelectedCodeCaveHook = vm.CodeCaveHooks[0];
        await vm.RemoveSelectedHookCommand.ExecuteAsync(null);

        Assert.Empty(vm.CodeCaveHooks);
    }

    [Fact]
    public async Task RemoveSelectedHook_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedCodeCaveHook = null;

        await vm.RemoveSelectedHookCommand.ExecuteAsync(null);

        // No crash
    }

    [Fact]
    public async Task RefreshHooks_PopulatesList()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.CodeCaveAddress = "0x7FF00100";
        await vm.InstallHookCommand.ExecuteAsync(null);

        await vm.RefreshHooksCommand.ExecuteAsync(null);

        Assert.Single(vm.CodeCaveHooks);
        Assert.Contains("0x", vm.CodeCaveHooks[0].OriginalAddress);
        Assert.Contains("0x", vm.CodeCaveHooks[0].CaveAddress);
    }
}
