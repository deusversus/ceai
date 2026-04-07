using System.Collections.ObjectModel;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class MemoryRegionsViewModel : ObservableObject, IDisposable
{
    private readonly IScanEngine _scanEngine;
    private readonly IEngineFacade _engineFacade;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly INavigationService _navigationService;

    private readonly IClipboardService _clipboard;
    private readonly IAiContextService _aiContext;
    private readonly Action _processChangedHandler;

    public MemoryRegionsViewModel(
        IScanEngine scanEngine,
        IEngineFacade engineFacade,
        IProcessContext processContext,
        IOutputLog outputLog,
        INavigationService navigationService,
        IClipboardService clipboard,
        IAiContextService aiContext)
    {
        _scanEngine = scanEngine;
        _engineFacade = engineFacade;
        _processContext = processContext;
        _outputLog = outputLog;
        _navigationService = navigationService;
        _clipboard = clipboard;
        _aiContext = aiContext;

        _processChangedHandler = () => _ = RefreshAsync();
        _processContext.ProcessChanged += _processChangedHandler;
    }

    public void Dispose()
    {
        _processContext.ProcessChanged -= _processChangedHandler;
        GC.SuppressFinalize(this);
    }

    [ObservableProperty] private ObservableCollection<MemoryRegionDisplayItem> _regions = new();
    [ObservableProperty] private MemoryRegionDisplayItem? _selectedRegion;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string _filterProtection = "All";

    public IReadOnlyList<string> ProtectionFilters { get; } = ["All", "R", "RW", "RWX", "X"];

    partial void OnFilterProtectionChanged(string value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        try
        {
            var attachment = await _engineFacade.AttachAsync(pid.Value);
            var rawRegions = await _scanEngine.EnumerateRegionsAsync(pid.Value);

            Regions.Clear();
            long totalSize = 0;
            foreach (var r in rawRegions)
            {
                var prot = BuildProtectionString(r.IsReadable, r.IsWritable, r.IsExecutable);
                if (!MatchesFilter(prot)) continue;

                var owner = FindOwnerModule(r.BaseAddress, r.RegionSize, attachment.Modules);
                Regions.Add(new MemoryRegionDisplayItem
                {
                    BaseAddress = $"0x{r.BaseAddress:X}",
                    Size = FormatSize(r.RegionSize),
                    Protection = prot,
                    OwnerModule = owner,
                    IsReadable = r.IsReadable,
                    IsWritable = r.IsWritable,
                    IsExecutable = r.IsExecutable
                });
                totalSize += r.RegionSize;
            }
            StatusText = $"{Regions.Count} region(s), {FormatSize(totalSize)} total";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _outputLog.Append("MemRegions", "Error", ex.Message);
        }
    }

    [RelayCommand]
    private void NavigateToMemoryBrowser()
    {
        if (SelectedRegion is null) return;
        _navigationService.ShowDocument("memoryBrowser", SelectedRegion.BaseAddress);
    }

    public static string BuildProtectionString(bool r, bool w, bool x)
    {
        var s = "";
        if (r) s += "R";
        if (w) s += "W";
        if (x) s += "X";
        return s.Length > 0 ? s : "---";
    }

    private bool MatchesFilter(string prot) => FilterProtection switch
    {
        "All" => true,
        _ => prot.Contains(FilterProtection, StringComparison.Ordinal)
    };

    private static string FindOwnerModule(nuint regionBase, long regionSize, IReadOnlyList<ModuleDescriptor> modules)
    {
        foreach (var mod in modules)
        {
            var modEnd = (nuint)((long)mod.BaseAddress + mod.SizeBytes);
            if (regionBase >= mod.BaseAddress && regionBase < modEnd)
                return mod.Name;
        }
        return "";
    }

    // ── Cross-panel context menu commands ──

    [RelayCommand]
    private void CopyAddress()
    {
        if (SelectedRegion is null) return;
        _clipboard.SetText(SelectedRegion.BaseAddress);
    }

    [RelayCommand]
    private void CopyRegionInfo()
    {
        if (SelectedRegion is null) return;
        _clipboard.SetText($"{SelectedRegion.BaseAddress} | {SelectedRegion.Size} | {SelectedRegion.Protection} | {SelectedRegion.OwnerModule}");
    }

    [RelayCommand]
    private void DisassembleRegion()
    {
        if (SelectedRegion is null) return;
        _navigationService.ShowDocument("disassembler", SelectedRegion.BaseAddress);
    }

    [RelayCommand]
    private void AskAi()
    {
        if (SelectedRegion is null) return;
        _aiContext.SendContext("Memory Region",
            $"Region: {SelectedRegion.BaseAddress} | Size: {SelectedRegion.Size} | Protection: {SelectedRegion.Protection} | Module: {SelectedRegion.OwnerModule}");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };
}
