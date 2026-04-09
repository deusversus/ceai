using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class SnapshotsViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();

    private SnapshotsViewModel CreateVm()
    {
        var snapshotService = new MemorySnapshotService(_engineFacade);
        return new SnapshotsViewModel(snapshotService, _processContext, _outputLog);
    }

    [Fact]
    public void RefreshList_NoSnapshots_SetsEmptyCollection()
    {
        var vm = CreateVm();

        vm.RefreshList();

        Assert.NotNull(vm.Snapshots);
        Assert.Empty(vm.Snapshots);
    }

    [Fact]
    public void DeleteSelected_NoSelection_DoesNotThrow()
    {
        var vm = CreateVm();
        vm.SelectedSnapshot = null;

        vm.DeleteSelectedCommand.Execute(null);

        // No exception = success
    }

    [Fact]
    public void Compare_NoSelections_LogsWarning()
    {
        var vm = CreateVm();
        vm.SelectedSnapshots = null;

        vm.CompareCommand.Execute(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("two snapshots"));
    }

    [Fact]
    public async Task CaptureAsync_NoProcess_DoesNotThrow()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.Address = "0x1000";

        await vm.CaptureCommand.ExecuteAsync(null);

        // No snapshot captured when no process is attached
        Assert.Empty(vm.Snapshots);
    }

    [Fact]
    public void Constructor_InitializesProperties()
    {
        var vm = CreateVm();

        Assert.NotNull(vm.Snapshots);
        Assert.NotNull(vm.DiffItems);
        Assert.Equal("256", vm.Length);
        Assert.Equal("", vm.Address);
        Assert.Equal("", vm.Label);
    }
}
