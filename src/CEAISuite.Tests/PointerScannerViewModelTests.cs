using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class PointerScannerViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();

    private PointerScannerViewModel CreateVm()
    {
        var scannerService = new PointerScannerService(_engineFacade);
        var addressTableService = new AddressTableService(_engineFacade);
        return new PointerScannerViewModel(scannerService, addressTableService, _processContext, _outputLog);
    }

    [Fact]
    public async Task Scan_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.TargetAddress = "0x10000";

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task Scan_InvalidAddress_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.TargetAddress = "garbage";

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains("Invalid", vm.StatusText);
    }

    [Fact]
    public async Task Scan_EmptyAddress_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.TargetAddress = "";

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains("Enter a target", vm.StatusText);
    }

    [Fact]
    public void AddSelectedToTable_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedResult = null;

        vm.AddSelectedToTableCommand.Execute(null);

        // No crash, no status update (method returns early)
        Assert.Null(vm.StatusText);
    }

    [Fact]
    public void AddSelectedToTable_WithSelection_AddsEntry()
    {
        var vm = CreateVm();
        var path = new PointerPath("game.dll", 0x7FF00000, 0x1000, [0x10], 0x50000);
        vm.Results.Add(new() { Chain = path.Display, ResolvedAddress = "0x50000", ModuleName = "game.dll", Source = path });
        vm.SelectedResult = vm.Results[0];

        vm.AddSelectedToTableCommand.Execute(null);

        Assert.Contains("Added to address table", vm.StatusText);
    }

    [Fact]
    public void CancelScan_DoesNotThrow()
    {
        var vm = CreateVm();
        // CancelScan with no active scan should not throw
        vm.CancelScanCommand.Execute(null);
    }
}
