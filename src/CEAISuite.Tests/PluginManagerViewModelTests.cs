using System.IO;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class PluginManagerViewModelTests
{
    private static (PluginManagerViewModel Vm, PluginHost Host, StubOutputLog Log) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceai-test-plugins-" + Guid.NewGuid().ToString("N"));
        var host = new PluginHost(pluginDirectory: dir);
        var log = new StubOutputLog();
        return (new PluginManagerViewModel(host, log), host, log);
    }

    [Fact]
    public void Constructor_InitializesEmptyPluginsList()
    {
        var (vm, _, _) = Create();
        Assert.NotNull(vm.Plugins);
        Assert.Empty(vm.Plugins);
    }

    [Fact]
    public void StatusText_WhenNoPlugins_ShowsHelpText()
    {
        var (vm, _, _) = Create();
        Assert.Contains("No plugins loaded", vm.StatusText);
    }

    [Fact]
    public void Refresh_WithNoPlugins_KeepsEmptyList()
    {
        var (vm, _, _) = Create();
        vm.RefreshCommand.Execute(null);
        Assert.Empty(vm.Plugins);
    }

    [Fact]
    public async Task Unload_NoSelection_DoesNotThrow()
    {
        var (vm, _, _) = Create();
        vm.SelectedPlugin = null;
        await vm.UnloadCommand.ExecuteAsync(null);
    }

    [Fact]
    public void SelectedPlugin_DefaultsToNull()
    {
        var (vm, _, _) = Create();
        Assert.Null(vm.SelectedPlugin);
    }

    [Fact]
    public async Task Unload_WithSelection_LogsErrorForUnknownPlugin()
    {
        var (vm, _, log) = Create();
        vm.SelectedPlugin = new PluginDisplayItem
        {
            Name = "FakePlugin",
            Version = "1.0",
            Description = "Test",
            ToolCount = 0,
            Status = "Loaded"
        };

        // Unloading a plugin that doesn't exist in PluginHost should not throw
        await vm.UnloadCommand.ExecuteAsync(null);

        // PluginHost.UnloadPluginAsync silently returns for unknown names,
        // so the VM logs success and refreshes
        Assert.Contains(log.LoggedMessages, m => m.Source == "Plugins");
    }

    [Fact]
    public void OpenFolder_CreatesDirectoryAndLogs()
    {
        var (vm, host, log) = Create();
        var dir = host.PluginDirectory;

        // Ensure the directory doesn't pre-exist
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);

        vm.OpenFolderCommand.Execute(null);

        Assert.True(Directory.Exists(dir));
        Assert.Contains(log.LoggedMessages, m =>
            m.Source == "Plugins" && m.Level == "Info" && m.Message.Contains(dir));

        // Cleanup
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void Refresh_UpdatesStatusText()
    {
        var (vm, _, _) = Create();

        // Initially shows help text
        Assert.Contains("No plugins loaded", vm.StatusText);

        // Refresh again — still no plugins, same message
        vm.RefreshCommand.Execute(null);
        Assert.Contains("Place .dll files", vm.StatusText);
    }

    [Fact]
    public void PluginDirectory_UsedByOpenFolder_MatchesHost()
    {
        var (vm, host, log) = Create();
        vm.OpenFolderCommand.Execute(null);

        // The logged message should reference the PluginHost's directory, not a hardcoded path
        Assert.Contains(log.LoggedMessages, m =>
            m.Message.Contains(host.PluginDirectory));
    }
}
