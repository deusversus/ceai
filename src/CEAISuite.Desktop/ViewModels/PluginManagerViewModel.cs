using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public sealed partial class PluginManagerViewModel : ObservableObject
{
    private readonly PluginHost _pluginHost;
    private readonly IOutputLog _outputLog;

    [ObservableProperty] private ObservableCollection<PluginDisplayItem> _plugins = [];
    [ObservableProperty] private PluginDisplayItem? _selectedPlugin;
    [ObservableProperty] private string _statusText = "No plugins loaded.";

    public PluginManagerViewModel(PluginHost pluginHost, IOutputLog outputLog)
    {
        _pluginHost = pluginHost;
        _outputLog = outputLog;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var loaded = _pluginHost.Plugins;
        Plugins = new ObservableCollection<PluginDisplayItem>(
            loaded.Select(p => new PluginDisplayItem
            {
                Name = p.Plugin.Name,
                Version = p.Plugin.Version,
                Description = p.Plugin.Description,
                ToolCount = p.Tools.Count,
                Status = "Loaded"
            }));
        StatusText = loaded.Count > 0
            ? $"{loaded.Count} plugin(s) loaded, {loaded.Sum(p => p.Tools.Count)} tools"
            : "No plugins loaded. Place .dll files in the plugins folder.";
    }

    [RelayCommand]
    private async Task UnloadAsync()
    {
        if (SelectedPlugin is null) return;
        var name = SelectedPlugin.Name;
        try
        {
            await _pluginHost.UnloadPluginAsync(name).ConfigureAwait(false);
            _outputLog.Append("Plugins", "Info", $"Unloaded plugin: {name}");
            Refresh();
        }
        catch (Exception ex)
        {
            _outputLog.Append("Plugins", "Error", $"Failed to unload {name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite", "plugins");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
        _outputLog.Append("Plugins", "Info", $"Opened: {dir}");
    }
}
