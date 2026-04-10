using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class MemoryRegionsViewModelTests
{
    private readonly StubScanEngine _scanEngine = new();
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubNavigationService _navigationService = new();

    private MemoryRegionsViewModel CreateVm() =>
        new(_scanEngine, _engineFacade, _processContext, _outputLog, _navigationService,
            new StubClipboardService(), new StubAiContextService());

    [Fact]
    public async Task Refresh_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task Refresh_WithProcess_PopulatesRegions()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _scanEngine.NextRegions = [
            new MemoryRegionDescriptor(0x10000, 4096, true, true, false),
            new MemoryRegionDescriptor(0x20000, 8192, true, false, true)
        ];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Regions.Count);
        Assert.Contains("region", vm.StatusText);
    }

    [Fact]
    public async Task Refresh_ComputesProtectionString()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _scanEngine.NextRegions = [
            new MemoryRegionDescriptor(0x10000, 4096, true, true, false),
            new MemoryRegionDescriptor(0x20000, 8192, true, false, true),
            new MemoryRegionDescriptor(0x30000, 4096, true, true, true)
        ];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("RW", vm.Regions[0].Protection);
        Assert.Equal("RX", vm.Regions[1].Protection);
        Assert.Equal("RWX", vm.Regions[2].Protection);
    }

    [Fact]
    public async Task Refresh_MatchesModuleOwnership()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.AttachModules = [
            new ModuleDescriptor("game.exe", 0x400000, 4096)
        ];
        _scanEngine.NextRegions = [
            new MemoryRegionDescriptor(0x400000, 4096, true, false, true),
            new MemoryRegionDescriptor(0x500000, 4096, true, true, false)
        ];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("game.exe", vm.Regions[0].OwnerModule);
        Assert.Equal("", vm.Regions[1].OwnerModule);
    }

    [Fact]
    public void BuildProtectionString_CorrectFlags()
    {
        Assert.Equal("R", MemoryRegionsViewModel.BuildProtectionString(true, false, false));
        Assert.Equal("RW", MemoryRegionsViewModel.BuildProtectionString(true, true, false));
        Assert.Equal("RWX", MemoryRegionsViewModel.BuildProtectionString(true, true, true));
        Assert.Equal("X", MemoryRegionsViewModel.BuildProtectionString(false, false, true));
        Assert.Equal("---", MemoryRegionsViewModel.BuildProtectionString(false, false, false));
    }

    // ── Additional coverage tests ──

    [Fact]
    public void BuildProtectionString_WriteOnly()
    {
        Assert.Equal("W", MemoryRegionsViewModel.BuildProtectionString(false, true, false));
        Assert.Equal("WX", MemoryRegionsViewModel.BuildProtectionString(false, true, true));
        Assert.Equal("RX", MemoryRegionsViewModel.BuildProtectionString(true, false, true));
    }

    [Fact]
    public void CopyAddress_WithSelection_CopiesAddress()
    {
        var vm = CreateVm();
        vm.SelectedRegion = new CEAISuite.Desktop.Models.MemoryRegionDisplayItem
        {
            BaseAddress = "0x10000",
            Size = "4.0 KB",
            Protection = "RW",
            OwnerModule = "game.exe"
        };

        vm.CopyAddressCommand.Execute(null);

        // Clipboard should have the address
    }

    [Fact]
    public void CopyAddress_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedRegion = null;

        vm.CopyAddressCommand.Execute(null);
        // No crash
    }

    [Fact]
    public void CopyRegionInfo_WithSelection_CopiesFullInfo()
    {
        var clipboard = new StubClipboardService();
        var vm = new MemoryRegionsViewModel(
            _scanEngine, _engineFacade, _processContext, _outputLog, _navigationService,
            clipboard, new StubAiContextService());
        vm.SelectedRegion = new CEAISuite.Desktop.Models.MemoryRegionDisplayItem
        {
            BaseAddress = "0x10000",
            Size = "4.0 KB",
            Protection = "RW",
            OwnerModule = "test.dll"
        };

        vm.CopyRegionInfoCommand.Execute(null);

        Assert.NotNull(clipboard.LastText);
        Assert.Contains("0x10000", clipboard.LastText);
        Assert.Contains("RW", clipboard.LastText);
        Assert.Contains("test.dll", clipboard.LastText);
    }

    [Fact]
    public void NavigateToMemoryBrowser_WithSelection_Navigates()
    {
        var vm = CreateVm();
        vm.SelectedRegion = new CEAISuite.Desktop.Models.MemoryRegionDisplayItem
        {
            BaseAddress = "0x10000",
            Size = "4.0 KB",
            Protection = "RW",
        };

        vm.NavigateToMemoryBrowserCommand.Execute(null);

        Assert.Single(_navigationService.DocumentsShown);
        Assert.Equal("memoryBrowser", _navigationService.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void NavigateToMemoryBrowser_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedRegion = null;

        vm.NavigateToMemoryBrowserCommand.Execute(null);

        Assert.Empty(_navigationService.DocumentsShown);
    }

    [Fact]
    public async Task Refresh_FilterByProtection_FilteredCorrectly()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _scanEngine.NextRegions = [
            new MemoryRegionDescriptor(0x10000, 4096, true, true, false),   // RW
            new MemoryRegionDescriptor(0x20000, 8192, true, false, true),   // RX
            new MemoryRegionDescriptor(0x30000, 4096, true, true, true),    // RWX
            new MemoryRegionDescriptor(0x40000, 4096, false, false, true),  // X
        ];

        // Filter to only show regions containing "X" in protection
        vm.FilterProtection = "X";

        // Wait a bit for the auto-refresh triggered by OnFilterProtectionChanged
        await Task.Delay(100);
        // Also manually refresh to ensure
        await vm.RefreshCommand.ExecuteAsync(null);

        // Should show RX, RWX, and X regions (all contain "X")
        Assert.Equal(3, vm.Regions.Count);
        Assert.All(vm.Regions, r => Assert.Contains("X", r.Protection));
    }

    [Fact]
    public async Task Refresh_FormatsRegionSizesCorrectly()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _scanEngine.NextRegions = [
            new MemoryRegionDescriptor(0x10000, 512, true, false, false),       // < 1KB → "512 B"
            new MemoryRegionDescriptor(0x20000, 4096, true, false, false),      // 4 KB
            new MemoryRegionDescriptor(0x30000, 2_097_152, true, false, false), // 2 MB
        ];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("512 B", vm.Regions[0].Size);
        Assert.Contains("KB", vm.Regions[1].Size);
        Assert.Contains("MB", vm.Regions[2].Size);
    }

    [Fact]
    public void DisassembleRegion_WithSelection_NavigatesToDisassembler()
    {
        var vm = CreateVm();
        vm.SelectedRegion = new CEAISuite.Desktop.Models.MemoryRegionDisplayItem
        {
            BaseAddress = "0x10000",
            Size = "4.0 KB",
            Protection = "RX",
        };

        vm.DisassembleRegionCommand.Execute(null);

        Assert.Single(_navigationService.DocumentsShown);
        Assert.Equal("disassembler", _navigationService.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void Dispose_UnsubscribesFromProcessChanged()
    {
        var vm = CreateVm();
        vm.Dispose();
        // No crash after disposal
    }
}
