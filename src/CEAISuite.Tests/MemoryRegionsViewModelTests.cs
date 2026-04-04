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
}
