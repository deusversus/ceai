using System.Collections.ObjectModel;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class ModuleListViewModel : ObservableObject
{
    private readonly IEngineFacade _engineFacade;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly INavigationService _navigationService;
    private readonly IClipboardService _clipboard;

    private List<ModuleDisplayItem> _allModules = [];

    private readonly IAiContextService _aiContext;

    public ModuleListViewModel(
        IEngineFacade engineFacade,
        IProcessContext processContext,
        IOutputLog outputLog,
        INavigationService navigationService,
        IClipboardService clipboard,
        IAiContextService aiContext)
    {
        _engineFacade = engineFacade;
        _processContext = processContext;
        _outputLog = outputLog;
        _navigationService = navigationService;
        _clipboard = clipboard;
        _aiContext = aiContext;

        _processContext.ProcessChanged += () => _ = RefreshAsync();
    }

    [ObservableProperty] private ObservableCollection<ModuleDisplayItem> _modules = new();
    [ObservableProperty] private ModuleDisplayItem? _selectedModule;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string? _statusText;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        try
        {
            var attachment = await _engineFacade.AttachAsync(pid.Value);
            _allModules = attachment.Modules
                .OrderBy(m => m.BaseAddress)
                .Select(m => new ModuleDisplayItem
                {
                    Name = m.Name,
                    BaseAddress = $"0x{m.BaseAddress:X}",
                    Size = FormatSize(m.SizeBytes),
                    Path = m.Name
                })
                .ToList();
            ApplyFilter();
            StatusText = $"{_allModules.Count} module(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _outputLog.Append("Modules", "Error", ex.Message);
        }
    }

    [RelayCommand]
    private void CopyAddress()
    {
        if (SelectedModule is null) return;
        _clipboard.SetText(SelectedModule.BaseAddress);
        StatusText = $"Copied {SelectedModule.BaseAddress}";
    }

    [RelayCommand]
    private void NavigateToDisassembly()
    {
        if (SelectedModule is null) return;
        _navigationService.ShowDocument("disassembler", SelectedModule.BaseAddress);
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(FilterText)
            ? _allModules
            : _allModules.Where(m => m.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)).ToList();
        Modules = new ObservableCollection<ModuleDisplayItem>(filtered);
    }

    // ── Cross-panel context menu commands ──

    [RelayCommand]
    private void BrowseMemory()
    {
        if (SelectedModule is null) return;
        _navigationService.ShowDocument("memoryBrowser", SelectedModule.BaseAddress);
    }

    [RelayCommand]
    private void DissectModule()
    {
        if (SelectedModule is null) return;
        _navigationService.ShowDocument("structureDissector", SelectedModule.BaseAddress);
    }

    [RelayCommand]
    private void AskAi()
    {
        if (SelectedModule is null) return;
        _aiContext.SendContext("Module",
            $"Module: {SelectedModule.Name} @ {SelectedModule.BaseAddress} (Size: {SelectedModule.Size}, Path: {SelectedModule.Path})");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
