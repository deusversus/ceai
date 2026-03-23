using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class AddressTableViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubDialogService _dialogService = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubDispatcherService _dispatcher = new();
    private readonly StubNavigationService _navigationService = new();

    private AddressTableViewModel CreateVm()
    {
        var addressTableService = new AddressTableService(_engineFacade);
        var exportService = new AddressTableExportService();
        var breakpointService = new BreakpointService(null);
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptService = new ScriptGenerationService();

        return new AddressTableViewModel(
            addressTableService,
            exportService,
            new StubProcessContext(),
            autoAssemblerEngine: null,
            breakpointService,
            disassemblyService,
            scriptService,
            _dialogService,
            _outputLog,
            _dispatcher,
            _navigationService);
    }

    [Fact]
    public void CreateGroup_WithName_AddsGroupToRoots()
    {
        var vm = CreateVm();
        _dialogService.NextInputResult = "My Group";

        vm.CreateGroupCommand.Execute(null);

        Assert.NotNull(vm.Roots);
        Assert.Single(vm.Roots);
        Assert.Equal("My Group", vm.Roots[0].Label);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("My Group"));
    }

    [Fact]
    public void CreateGroup_CancelDialog_NoGroupAdded()
    {
        var vm = CreateVm();
        _dialogService.NextInputResult = null;

        vm.CreateGroupCommand.Execute(null);

        Assert.NotNull(vm.Roots);
        Assert.Empty(vm.Roots);
    }

    [Fact]
    public void RemoveSelected_NoSelection_LogsWarning()
    {
        var vm = CreateVm();
        vm.SelectedNode = null;

        vm.RemoveSelectedCommand.Execute(null);

        Assert.Single(_outputLog.LoggedMessages);
        Assert.Equal("Warn", _outputLog.LoggedMessages[0].Level);
        Assert.Contains("Select an address", _outputLog.LoggedMessages[0].Message);
    }

    [Fact]
    public void Export_EmptyTable_LogsWarning()
    {
        var vm = CreateVm();

        vm.ExportCommand.Execute(null);

        Assert.Single(_outputLog.LoggedMessages);
        Assert.Equal("Warn", _outputLog.LoggedMessages[0].Level);
        Assert.Contains("No entries to export", _outputLog.LoggedMessages[0].Message);
    }

    [Fact]
    public void ToggleLock_NoSelection_LogsWarning()
    {
        var vm = CreateVm();
        vm.SelectedNode = null;

        vm.ToggleLockCommand.Execute(null);

        Assert.Single(_outputLog.LoggedMessages);
        Assert.Equal("Warn", _outputLog.LoggedMessages[0].Level);
    }

    [Fact]
    public async Task Refresh_NoProcess_LogsWarning()
    {
        var vm = CreateVm();

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn");
    }
}
