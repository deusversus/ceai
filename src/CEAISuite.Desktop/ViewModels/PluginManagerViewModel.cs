using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CEAISuite.Desktop.ViewModels;

public sealed partial class PluginManagerViewModel : ObservableObject
{
    private readonly PluginHost _pluginHost;
    private readonly IOutputLog _outputLog;
    private readonly PluginCatalogService? _catalogService;
    private readonly IDispatcherService _dispatcher;

    // Cached catalog entries from last refresh (avoids re-fetching on install)
    private IReadOnlyList<PluginCatalogService.CatalogEntry> _cachedCatalogEntries = [];

    [ObservableProperty] private ObservableCollection<PluginDisplayItem> _plugins = [];
    [ObservableProperty] private PluginDisplayItem? _selectedPlugin;
    [ObservableProperty] private string _statusText = "No plugins loaded.";

    // Catalog tab
    [ObservableProperty] private ObservableCollection<CatalogPluginDisplayItem> _catalogPlugins = [];
    [ObservableProperty] private CatalogPluginDisplayItem? _selectedCatalogPlugin;
    [ObservableProperty] private bool _isLoadingCatalog;
    [ObservableProperty] private string _catalogStatus = "Click Refresh to browse community plugins.";
    [ObservableProperty] private int _selectedTabIndex;

    public PluginManagerViewModel(PluginHost pluginHost, IOutputLog outputLog,
        IDispatcherService dispatcher, PluginCatalogService? catalogService = null)
    {
        _pluginHost = pluginHost;
        _outputLog = outputLog;
        _dispatcher = dispatcher;
        _catalogService = catalogService;
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
    private async Task InstallAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Plugin DLL",
            Filter = "Plugin DLLs (*.dll)|*.dll",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        var sourcePath = dialog.FileName;
        var pluginDir = _pluginHost.PluginDirectory;

        try
        {
            if (!Directory.Exists(pluginDir))
                Directory.CreateDirectory(pluginDir);

            var destPath = Path.Combine(pluginDir, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, destPath, overwrite: true);
            _outputLog.Append("Plugins", "Info", $"Copied {Path.GetFileName(sourcePath)} to plugins folder");

            // Reload all plugins to pick up the new one
            Refresh();
            _outputLog.Append("Plugins", "Info", "Plugin installed. Restart to load new plugins, or use Refresh after manual load.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("Plugins", "Error", $"Install failed: {ex.Message}");
        }
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
        var dir = _pluginHost.PluginDirectory;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
        _outputLog.Append("Plugins", "Info", $"Opened: {dir}");
    }

    // ── Online Catalog ──

    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        if (_catalogService is null)
        {
            CatalogStatus = "Catalog service not available.";
            return;
        }

        IsLoadingCatalog = true;
        CatalogStatus = "Fetching catalog...";
        try
        {
            var entries = await _catalogService.FetchCatalogAsync().ConfigureAwait(false);
            _cachedCatalogEntries = entries;
            var installedNames = _pluginHost.Plugins.Select(p => p.Plugin.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            _dispatcher.Invoke(() =>
            {
                CatalogPlugins = new ObservableCollection<CatalogPluginDisplayItem>(
                    entries.Select(e => new CatalogPluginDisplayItem
                    {
                        Name = e.Name,
                        Version = e.Version,
                        Description = e.Description,
                        Author = e.Author,
                        Size = e.SizeBytes > 0 ? $"{e.SizeBytes / 1024.0:F0} KB" : "",
                        IsInstalled = installedNames.Contains(e.Name)
                    }));
                CatalogStatus = entries.Count > 0
                    ? $"{entries.Count} plugin(s) available"
                    : "No plugins in the community catalog.";
            });
        }
        catch (Exception ex)
        {
            CatalogStatus = $"Failed to fetch catalog: {ex.Message}";
            _outputLog.Append("Plugins", "Error", $"Catalog fetch failed: {ex.Message}");
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    [RelayCommand]
    private async Task InstallFromCatalogAsync()
    {
        if (_catalogService is null || SelectedCatalogPlugin is null) return;

        var name = SelectedCatalogPlugin.Name;
        _outputLog.Append("Plugins", "Info", $"Installing {name} from catalog...");

        try
        {
            // Use cached catalog entries instead of re-fetching
            var entry = _cachedCatalogEntries.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                _outputLog.Append("Plugins", "Error", $"Plugin '{name}' not found in cached catalog. Try refreshing.");
                return;
            }

            var destPath = await _catalogService.DownloadAndVerifyAsync(
                entry, _pluginHost.PluginDirectory).ConfigureAwait(false);

            _outputLog.Append("Plugins", "Info", $"Downloaded {name} to {destPath}. Restart to load, or use Refresh.");
            _dispatcher.Invoke(() => Refresh());
            await RefreshCatalogAsync().ConfigureAwait(false); // Update installed status
        }
        catch (Exception ex)
        {
            _outputLog.Append("Plugins", "Error", $"Catalog install failed for {name}: {ex.Message}");
        }
    }
}
