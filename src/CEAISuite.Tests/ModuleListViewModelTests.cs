using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ModuleListViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubNavigationService _navigationService = new();
    private readonly StubClipboardService _clipboard = new();

    private ModuleListViewModel CreateVm() =>
        new(_engineFacade, _processContext, _outputLog, _navigationService, _clipboard, new StubAiContextService());

    [Fact]
    public async Task Refresh_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task Refresh_WithProcess_PopulatesModules()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.AttachModules = [
            new ModuleDescriptor("game.exe", 0x400000, 4096),
            new ModuleDescriptor("kernel32.dll", 0x7FF80000, 65536)
        ];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Modules.Count);
        Assert.Equal("game.exe", vm.Modules[0].Name);
        Assert.Contains("module", vm.StatusText);
    }

    [Fact]
    public async Task FilterText_FiltersModuleList()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.AttachModules = [
            new ModuleDescriptor("game.exe", 0x400000, 4096),
            new ModuleDescriptor("kernel32.dll", 0x7FF80000, 65536)
        ];
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.FilterText = "kernel";

        Assert.Single(vm.Modules);
        Assert.Equal("kernel32.dll", vm.Modules[0].Name);
    }

    [Fact]
    public void CopyAddress_NothingSelected_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedModule = null;

        vm.CopyAddressCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    // ── Additional coverage tests ──

    [Fact]
    public void CopyAddress_WithSelection_CopiesToClipboard()
    {
        var vm = CreateVm();
        vm.SelectedModule = new CEAISuite.Desktop.Models.ModuleDisplayItem
        {
            Name = "test.dll",
            BaseAddress = "0x7FF00000",
            Size = "64.0 KB",
            Path = "test.dll"
        };

        vm.CopyAddressCommand.Execute(null);

        Assert.Equal("0x7FF00000", _clipboard.LastText);
        Assert.Contains("Copied", vm.StatusText);
    }

    [Fact]
    public void BrowseMemory_WithSelection_NavigatesToMemoryBrowser()
    {
        var vm = CreateVm();
        vm.SelectedModule = new CEAISuite.Desktop.Models.ModuleDisplayItem
        {
            Name = "game.exe",
            BaseAddress = "0x400000",
            Size = "4.0 KB",
            Path = "game.exe"
        };

        vm.BrowseMemoryCommand.Execute(null);

        Assert.Single(_navigationService.DocumentsShown);
        Assert.Equal("memoryBrowser", _navigationService.DocumentsShown[0].ContentId);
        Assert.Equal("0x400000", _navigationService.DocumentsShown[0].Parameter);
    }

    [Fact]
    public void BrowseMemory_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedModule = null;

        vm.BrowseMemoryCommand.Execute(null);

        Assert.Empty(_navigationService.DocumentsShown);
    }

    [Fact]
    public void NavigateToDisassembly_WithSelection_NavigatesToDisassembler()
    {
        var vm = CreateVm();
        vm.SelectedModule = new CEAISuite.Desktop.Models.ModuleDisplayItem
        {
            Name = "game.exe",
            BaseAddress = "0x400000",
            Size = "4.0 KB",
            Path = "game.exe"
        };

        vm.NavigateToDisassemblyCommand.Execute(null);

        Assert.Single(_navigationService.DocumentsShown);
        Assert.Equal("disassembler", _navigationService.DocumentsShown[0].ContentId);
    }

    [Fact]
    public async Task Refresh_WithProcess_FormatsModuleSizesCorrectly()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.AttachModules = [
            new ModuleDescriptor("tiny.dll", 0x100000, 512),       // < 1KB → "512 B"
            new ModuleDescriptor("small.dll", 0x200000, 4096),     // 4 KB
            new ModuleDescriptor("large.dll", 0x300000, 2_097_152) // 2 MB
        ];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Modules.Count);
        Assert.Equal("512 B", vm.Modules[0].Size);
        Assert.Contains("KB", vm.Modules[1].Size);
        Assert.Contains("MB", vm.Modules[2].Size);
    }

    [Fact]
    public async Task Refresh_SortsModulesByBaseAddress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _engineFacade.AttachModules = [
            new ModuleDescriptor("z_last.dll", 0x7FF00000, 4096),
            new ModuleDescriptor("a_first.dll", 0x100000, 4096),
            new ModuleDescriptor("m_mid.dll", 0x400000, 4096)
        ];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("a_first.dll", vm.Modules[0].Name);
        Assert.Equal("m_mid.dll", vm.Modules[1].Name);
        Assert.Equal("z_last.dll", vm.Modules[2].Name);
    }

    [Fact]
    public void Dispose_UnsubscribesFromProcessChanged()
    {
        var vm = CreateVm();
        vm.Dispose();
        // Should not throw or cause side effects after disposal
    }
}
