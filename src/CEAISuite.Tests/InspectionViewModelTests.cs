using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class InspectionViewModelTests : IDisposable
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubDialogService _dialogService = new();
    private readonly StubOutputLog _outputLog = new();

    private InspectionViewModel CreateVm()
    {
        var dashboardService = new WorkspaceDashboardService(_engineFacade, new StubSessionRepository());
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var breakpointService = new BreakpointService(null);
        var addressTableService = new AddressTableService(_engineFacade);
        return new InspectionViewModel(
            dashboardService, _processContext, disassemblyService,
            breakpointService, addressTableService, _dialogService, _outputLog);
    }

    public void Dispose()
    {
        // InspectionViewModel is IDisposable
    }

    [Fact]
    public async Task ReadAddressAsync_NoProcess_LogsWarning()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.ReadAddressCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("process"));
    }

    [Fact]
    public async Task WriteAddressAsync_NoProcess_LogsWarning()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.WriteAddressCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("process"));
    }

    [Fact]
    public void AddManualToTable_NoProbe_LogsWarning()
    {
        var vm = CreateVm();
        // CurrentInspection is null, so ManualProbe is null

        vm.AddManualToTableCommand.Execute(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("Read an address"));
    }

    [Fact]
    public async Task DisassembleAtAddressAsync_NoProcess_LogsWarning()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.DisassembleAtAddressCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("process"));
    }

    [Fact]
    public void SetInspection_UpdatesCurrentInspection()
    {
        var vm = CreateVm();
        var inspection = new ProcessInspectionOverview(
            ProcessId: 1234,
            ProcessName: "TestGame.exe",
            Architecture: "x64",
            Modules: new List<ModuleOverview>(),
            Sample: null,
            ManualProbe: null,
            LastWriteMessage: null,
            StatusMessage: "Attached"
        );

        vm.SetInspection(inspection);

        Assert.NotNull(vm.CurrentInspection);
        Assert.Equal(1234, vm.CurrentInspection!.ProcessId);
        Assert.Equal("TestGame.exe", vm.CurrentInspection.ProcessName);
    }

    [Fact]
    public void Constructor_InitializesDefaults()
    {
        var vm = CreateVm();

        Assert.Equal("0x0", vm.Address);
        Assert.Equal("0", vm.Value);
        Assert.Equal(MemoryDataType.Int32, vm.SelectedDataType);
        Assert.Equal("0x0", vm.DisassemblyAddress);
        Assert.Equal("0x0", vm.BpAddress);
        Assert.Equal(BreakpointType.Software, vm.SelectedBpType);
        Assert.Null(vm.CurrentInspection);
    }

    [Fact]
    public async Task WriteAddressAsync_UserCancelsConfirmation_DoesNotWrite()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SetInspection(new ProcessInspectionOverview(1234, "Test.exe", "x64", new List<ModuleOverview>(), null, null, null, "OK"));
        _dialogService.NextConfirmResult = false;
        vm.Address = "0x10000";
        vm.Value = "100";

        await vm.WriteAddressCommand.ExecuteAsync(null);

        // Confirm dialog was shown, user said no. No Info log about writing.
        Assert.DoesNotContain(_outputLog.LoggedMessages, m => m.Level == "Info" && m.Message.Contains("Wrote"));
    }
}
