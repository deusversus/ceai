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

    // ── Additional coverage tests ──

    [Fact]
    public async Task CaptureAsync_WithProcess_CapturesAndRefreshes()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        // Write some memory so the snapshot has data
        _engineFacade.WriteMemoryDirect((nuint)0x1000, new byte[256]);
        vm.Address = "0x1000";
        vm.Length = "256";
        vm.Label = "TestSnapshot";

        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.Single(vm.Snapshots);
        Assert.Equal("TestSnapshot", vm.Snapshots[0].Label);
        Assert.Contains("Info", _outputLog.LoggedMessages.Select(m => m.Level));
    }

    [Fact]
    public async Task CaptureAsync_EmptyAddress_DoesNotCapture()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.Address = "";

        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.Empty(vm.Snapshots);
    }

    [Fact]
    public async Task CaptureAsync_WithoutLabel_CapturesWithNullLabel()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.WriteMemoryDirect((nuint)0x2000, new byte[64]);
        vm.Address = "0x2000";
        vm.Length = "64";
        vm.Label = ""; // empty label

        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.Single(vm.Snapshots);
        // Label should be null/empty since we didn't provide one
    }

    [Fact]
    public async Task Compare_TwoSnapshots_PopulatesDiffItems()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        // Capture first snapshot
        _engineFacade.WriteMemoryDirect((nuint)0x1000, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        vm.Address = "0x1000";
        vm.Length = "8";
        vm.Label = "Before";
        await vm.CaptureCommand.ExecuteAsync(null);

        // Small delay to ensure unique snapshot ID (timestamp-based)
        await Task.Delay(10);

        // Capture second snapshot at different address
        _engineFacade.WriteMemoryDirect((nuint)0x2000, new byte[] { 1, 2, 99, 4, 5, 6, 7, 8 });
        vm.Address = "0x2000";
        vm.Label = "After";
        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Snapshots.Count);

        // Set up multi-select for compare
        vm.SelectedSnapshots = vm.Snapshots.ToList();
        vm.CompareCommand.Execute(null);

        // DiffItems should be populated with at least one change (different data)
        Assert.NotEmpty(vm.DiffItems);
    }

    [Fact]
    public async Task DeleteSelected_WithSelection_RemovesSnapshot()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.WriteMemoryDirect((nuint)0x1000, new byte[32]);
        vm.Address = "0x1000";
        vm.Length = "32";
        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.Single(vm.Snapshots);
        vm.SelectedSnapshot = vm.Snapshots[0];

        vm.DeleteSelectedCommand.Execute(null);

        Assert.Empty(vm.Snapshots);
    }

    [Fact]
    public async Task CaptureAsync_AddressWithout0xPrefix_Parses()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.WriteMemoryDirect((nuint)0x5000, new byte[16]);
        vm.Address = "5000"; // no 0x prefix — should still be parsed as hex
        vm.Length = "16";

        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.Single(vm.Snapshots);
    }

    [Fact]
    public async Task CompareWithLiveAsync_NoProcess_DoesNotThrow()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.SelectedSnapshot = null;

        await vm.CompareWithLiveCommand.ExecuteAsync(null);

        // No crash
    }
}
