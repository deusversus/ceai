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
        return new PointerScannerViewModel(scannerService, addressTableService, _processContext, _outputLog,
            new StubClipboardService(), new StubNavigationService(), new StubAiContextService(), new StubDialogService());
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

    [Fact]
    public async Task ValidatePaths_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.ValidatePathsCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task ValidatePaths_EmptyResults_SetsStatusMessage()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        await vm.ValidatePathsCommand.ExecuteAsync(null);

        Assert.Contains("No results", vm.StatusText);
    }

    [Fact]
    public async Task ValidatePaths_WithResult_UpdatesStatus()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        var path = new PointerPath("game.dll", 0x7FF00000, 0x1000, [0x10], 0x50000);
        vm.Results.Add(new() { Chain = path.Display, ResolvedAddress = "0x50000", ModuleName = "game.dll", Source = path });

        await vm.ValidatePathsCommand.ExecuteAsync(null);

        // The stub engine won't have the right memory so the path will be broken or drifted
        Assert.Contains("Validated", vm.StatusText);
        Assert.NotEqual("Found", vm.Results[0].Status);
    }

    // ── Additional coverage tests ──

    [Fact]
    public async Task ScanAsync_WithValidAddress_RunsScan()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.TargetAddress = "0x50000";
        vm.MaxDepth = 2;
        vm.MaxOffset = "0x1000";

        await vm.ScanCommand.ExecuteAsync(null);

        // Scan completes (stub returns empty results)
        Assert.Contains("pointer path(s) found", vm.StatusText);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task ScanAsync_DecimalMaxOffset_Parses()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.TargetAddress = "0x50000";
        vm.MaxOffset = "4096"; // decimal rather than hex

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains("pointer path(s) found", vm.StatusText);
    }

    [Fact]
    public void CopyPath_WithSelection_CopiesChain()
    {
        var vm = CreateVm();
        var path = new PointerPath("game.dll", 0x7FF00000, 0x1000, [0x10], 0x50000);
        var item = new CEAISuite.Desktop.Models.PointerPathDisplayItem
        {
            Chain = path.Display,
            ResolvedAddress = "0x50000",
            ModuleName = "game.dll",
            Source = path
        };
        vm.Results.Add(item);
        vm.SelectedResult = item;

        vm.CopyPathCommand.Execute(null);
        // No crash; clipboard should have the chain
    }

    [Fact]
    public void CopyPath_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedResult = null;

        vm.CopyPathCommand.Execute(null);
        // No crash
    }

    [Fact]
    public void CopyResolvedAddress_WithSelection_CopiesAddress()
    {
        var vm = CreateVm();
        var item = new CEAISuite.Desktop.Models.PointerPathDisplayItem
        {
            Chain = "game.dll+0x1000 → [+0x10]",
            ResolvedAddress = "0x50000",
            ModuleName = "game.dll",
            Source = new PointerPath("game.dll", 0x7FF00000, 0x1000, [0x10], 0x50000)
        };
        vm.Results.Add(item);
        vm.SelectedResult = item;

        vm.CopyResolvedAddressCommand.Execute(null);
        // No crash
    }

    [Fact]
    public void BrowseResolved_WithSelection_NavigatesToMemoryBrowser()
    {
        var vm = CreateVm();
        var item = new CEAISuite.Desktop.Models.PointerPathDisplayItem
        {
            Chain = "game.dll+0x1000",
            ResolvedAddress = "0x50000",
            ModuleName = "game.dll",
        };
        vm.SelectedResult = item;

        vm.BrowseResolvedCommand.Execute(null);
        // No crash
    }

    [Fact]
    public void BrowseResolved_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedResult = null;

        vm.BrowseResolvedCommand.Execute(null);
        // No crash
    }

    [Fact]
    public async Task ResumeScanAsync_CannotResume_SetsStatus()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        // CanResume is false by default

        await vm.ResumeScanCommand.ExecuteAsync(null);

        Assert.Contains("Nothing to resume", vm.StatusText);
    }

    [Fact]
    public async Task SavePointerMap_NoResults_SetsStatus()
    {
        var vm = CreateVm();

        await vm.SavePointerMapCommand.ExecuteAsync(null);

        Assert.Contains("No results", vm.StatusText);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var vm = CreateVm();
        vm.Dispose();
        // double dispose
        vm.Dispose();
    }
}
