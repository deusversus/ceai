using System.IO;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class PluginManagerViewModelTests
{
    private static PluginManagerViewModel CreateVm()
    {
        var host = new PluginHost(pluginDirectory: Path.Combine(Path.GetTempPath(), "ceai-test-plugins-" + Guid.NewGuid().ToString("N")));
        return new PluginManagerViewModel(host, new StubOutputLog());
    }

    [Fact]
    public void Constructor_InitializesEmptyPluginsList()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.Plugins);
        Assert.Empty(vm.Plugins);
    }

    [Fact]
    public void StatusText_WhenNoPlugins_ShowsHelpText()
    {
        var vm = CreateVm();
        Assert.Contains("No plugins loaded", vm.StatusText);
    }

    [Fact]
    public void Refresh_WithNoPlugins_KeepsEmptyList()
    {
        var vm = CreateVm();
        vm.RefreshCommand.Execute(null);
        Assert.Empty(vm.Plugins);
    }

    [Fact]
    public async Task Unload_NoSelection_DoesNotThrow()
    {
        var vm = CreateVm();
        vm.SelectedPlugin = null;
        await vm.UnloadCommand.ExecuteAsync(null);
    }

    [Fact]
    public void SelectedPlugin_DefaultsToNull()
    {
        var vm = CreateVm();
        Assert.Null(vm.SelectedPlugin);
    }
}
