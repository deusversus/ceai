using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ScriptsViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubAutoAssemblerEngine _autoAssembler = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();

    private ScriptsViewModel CreateVm()
    {
        var addressTableService = new AddressTableService(_engineFacade);
        return new ScriptsViewModel(addressTableService, _autoAssembler, _processContext, _outputLog);
    }

    [Fact]
    public void Refresh_NoScripts_ReturnsEmptyCollection()
    {
        var vm = CreateVm();

        vm.RefreshCommand.Execute(null);

        Assert.NotNull(vm.Scripts);
        Assert.Empty(vm.Scripts);
    }

    [Fact]
    public void Refresh_WithScriptEntries_PopulatesScripts()
    {
        var addressTableService = new AddressTableService(_engineFacade);
        var node = new AddressTableNode("test-1", "HP Script", isGroup: false)
        {
            AssemblerScript = "[ENABLE]\nnop\n[DISABLE]\nnop",
            IsScriptEnabled = false
        };
        addressTableService.ImportNodes(new[] { node });

        var vm = new ScriptsViewModel(addressTableService, _autoAssembler, _processContext, _outputLog);
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Scripts);
        Assert.Equal("HP Script", vm.Scripts[0].Label);
        Assert.Equal("DISABLED", vm.Scripts[0].StatusText);
        Assert.False(vm.Scripts[0].IsEnabled);
    }

    [Fact]
    public void SelectedScript_DefaultsToNull()
    {
        var vm = CreateVm();
        Assert.Null(vm.SelectedScript);
    }

    [Fact]
    public void Constructor_InitializesScriptsCollection()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.Scripts);
    }
}
