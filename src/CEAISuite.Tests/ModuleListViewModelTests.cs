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
        new(_engineFacade, _processContext, _outputLog, _navigationService, _clipboard);

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
}
