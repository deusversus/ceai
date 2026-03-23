using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class ScriptsViewModel : ObservableObject
{
    private readonly AddressTableService _addressTableService;
    private readonly IAutoAssemblerEngine? _autoAssemblerEngine;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;

    public ScriptsViewModel(
        AddressTableService addressTableService,
        IAutoAssemblerEngine autoAssemblerEngine,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _addressTableService = addressTableService;
        _autoAssemblerEngine = autoAssemblerEngine;
        _processContext = processContext;
        _outputLog = outputLog;
    }

    [ObservableProperty]
    private ObservableCollection<ScriptDisplayItem> _scripts = new();

    [ObservableProperty]
    private ScriptDisplayItem? _selectedScript;

    [RelayCommand]
    private void Refresh()
    {
        Scripts = new ObservableCollection<ScriptDisplayItem>(
            FlattenNodes(_addressTableService.Roots)
                .Where(n => n.IsScriptEntry)
                .Select(n => new ScriptDisplayItem
                {
                    Id = n.Id,
                    Label = n.Label,
                    StatusText = n.IsScriptEnabled ? "ENABLED" : "DISABLED",
                    IsEnabled = n.IsScriptEnabled
                }));
    }

    [RelayCommand]
    private async Task EnableSelectedAsync() => await ToggleScriptAsync(true);

    [RelayCommand]
    private async Task DisableSelectedAsync() => await ToggleScriptAsync(false);

    [RelayCommand]
    private async Task ToggleSelectedAsync()
    {
        if (SelectedScript is { } item)
            await ToggleScriptAsync(!item.IsEnabled);
    }

    private async Task ToggleScriptAsync(bool enable)
    {
        if (SelectedScript is not { } item) return;
        if (_autoAssemblerEngine is null || _processContext.AttachedProcessId is not { } pid) return;
        var node = FlattenNodes(_addressTableService.Roots).FirstOrDefault(n => n.Id == item.Id);
        if (node?.AssemblerScript is null) return;
        try
        {
            if (enable)
                await _autoAssemblerEngine.EnableAsync(pid, node.AssemblerScript);
            else
                await _autoAssemblerEngine.DisableAsync(pid, node.AssemblerScript);
            node.IsScriptEnabled = enable;
            Refresh();
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Script toggle: {ex.Message}"); }
    }

    private static IEnumerable<AddressTableNode> FlattenNodes(IEnumerable<AddressTableNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var child in FlattenNodes(n.Children))
                yield return child;
        }
    }
}
