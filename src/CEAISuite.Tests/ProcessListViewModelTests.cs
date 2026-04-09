using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ProcessListViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();

    private ProcessListViewModel CreateVm()
    {
        var dashboardService = new WorkspaceDashboardService(_engineFacade, new StubSessionRepository());
        return new ProcessListViewModel(dashboardService, _processContext, _outputLog);
    }

    [Fact]
    public async Task Refresh_PopulatesProcessList()
    {
        var vm = CreateVm();

        await vm.RefreshCommand.ExecuteAsync(null);

        // StubEngineFacade returns 3 test processes
        Assert.NotEmpty(vm.Processes);
    }

    // ── RefreshAsync with populated process list ──

    [Fact]
    public async Task Refresh_LogsProcessCount()
    {
        var vm = CreateVm();

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m =>
            m.Level == "Info" && m.Message.Contains("processes found"));
    }

    // ── Filter by name ──

    [Fact]
    public void SetProcesses_ThenFilterByName_ShowsOnlyMatchingProcesses()
    {
        var vm = CreateVm();
        vm.SetProcesses(new[]
        {
            new RunningProcessOverview(1000, "TestGame.exe", "x64"),
            new RunningProcessOverview(2000, "notepad.exe", "x64"),
            new RunningProcessOverview(3000, "calc.exe", "x86"),
        });

        Assert.Equal(3, vm.Processes.Count);

        vm.FilterText = "notepad";

        Assert.Single(vm.Processes);
        Assert.Equal("notepad.exe", vm.Processes[0].Name);
    }

    [Fact]
    public void FilterByProcessId_ShowsOnlyMatchingProcesses()
    {
        var vm = CreateVm();
        vm.SetProcesses(new[]
        {
            new RunningProcessOverview(1000, "TestGame.exe", "x64"),
            new RunningProcessOverview(2000, "notepad.exe", "x64"),
            new RunningProcessOverview(3000, "calc.exe", "x86"),
        });

        vm.FilterText = "2000";

        Assert.Single(vm.Processes);
        Assert.Equal(2000, vm.Processes[0].Id);
    }

    [Fact]
    public void FilterText_CaseInsensitive()
    {
        var vm = CreateVm();
        vm.SetProcesses(new[]
        {
            new RunningProcessOverview(1000, "TestGame.exe", "x64"),
            new RunningProcessOverview(2000, "notepad.exe", "x64"),
        });

        vm.FilterText = "TESTGAME";

        Assert.Single(vm.Processes);
        Assert.Equal("TestGame.exe", vm.Processes[0].Name);
    }

    [Fact]
    public void FilterText_EmptyString_ShowsAllProcesses()
    {
        var vm = CreateVm();
        vm.SetProcesses(new[]
        {
            new RunningProcessOverview(1000, "TestGame.exe", "x64"),
            new RunningProcessOverview(2000, "notepad.exe", "x64"),
        });

        vm.FilterText = "notepad";
        Assert.Single(vm.Processes);

        vm.FilterText = "";
        Assert.Equal(2, vm.Processes.Count);
    }

    [Fact]
    public void FilterText_NoMatch_ReturnsEmpty()
    {
        var vm = CreateVm();
        vm.SetProcesses(new[]
        {
            new RunningProcessOverview(1000, "TestGame.exe", "x64"),
        });

        vm.FilterText = "nonexistent";

        Assert.Empty(vm.Processes);
    }

    // ── Selection changes ──

    [Fact]
    public void SelectedProcess_CanBeSetAndGet()
    {
        var vm = CreateVm();
        var proc = new RunningProcessOverview(1234, "game.exe", "x64");
        vm.SetProcesses(new[] { proc });

        vm.SelectedProcess = vm.Processes[0];

        Assert.Equal(1234, vm.SelectedProcess.Id);
    }

    // ── Process attach/detach via ProcessContext ──

    [Fact]
    public void OnProcessChanged_SetsIsAttachedAndProcessName()
    {
        var vm = CreateVm();
        var inspection = new ProcessInspectionOverview(
            1234, "game.exe", "x64",
            new[] { new ModuleOverview("game.exe", "0x400000", "4096") },
            null, null, null, "Ready");

        _processContext.Attach(inspection);

        Assert.True(vm.IsAttached);
        Assert.Equal("game.exe", vm.AttachedProcessName);
        Assert.Equal("x64", vm.Architecture);
        Assert.Equal(1, vm.ModuleCount);
        Assert.NotNull(vm.ProcessDetails);
    }

    [Fact]
    public void OnProcessDetached_ClearsState()
    {
        var vm = CreateVm();
        var inspection = new ProcessInspectionOverview(
            1234, "game.exe", "x64",
            new[] { new ModuleOverview("game.exe", "0x400000", "4096") },
            null, null, null, "Ready");

        _processContext.Attach(inspection);
        Assert.True(vm.IsAttached);

        _processContext.Detach();

        Assert.False(vm.IsAttached);
        Assert.Null(vm.AttachedProcessName);
        Assert.Null(vm.Architecture);
        Assert.Equal(0, vm.ModuleCount);
        Assert.Null(vm.ProcessDetails);
        Assert.Null(vm.SelectedProcess);
    }

    [Fact]
    public void OnProcessChanged_AutoSelectsAttachedProcessInList()
    {
        var vm = CreateVm();
        vm.SetProcesses(new[]
        {
            new RunningProcessOverview(1234, "game.exe", "x64"),
            new RunningProcessOverview(5678, "other.exe", "x64"),
        });

        var inspection = new ProcessInspectionOverview(
            1234, "game.exe", "x64",
            new[] { new ModuleOverview("game.exe", "0x400000", "4096") },
            null, null, null, "Ready");

        _processContext.Attach(inspection);

        Assert.NotNull(vm.SelectedProcess);
        Assert.Equal(1234, vm.SelectedProcess.Id);
    }

    // ── SetProcesses ──

    [Fact]
    public void SetProcesses_ReplacesExistingList()
    {
        var vm = CreateVm();
        vm.SetProcesses(new[] { new RunningProcessOverview(1, "a.exe", "x64") });
        Assert.Single(vm.Processes);

        vm.SetProcesses(new[]
        {
            new RunningProcessOverview(2, "b.exe", "x64"),
            new RunningProcessOverview(3, "c.exe", "x64"),
        });
        Assert.Equal(2, vm.Processes.Count);
    }

    // ── Dispose ──

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var vm = CreateVm();
        vm.Dispose();
        // No exception
    }
}
