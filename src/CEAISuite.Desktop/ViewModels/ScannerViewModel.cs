using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class ScannerViewModel : ObservableObject
{
    private readonly ScanService _scanService;
    private readonly AddressTableService _addressTableService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;

    private readonly INavigationService _navigationService;
    private readonly IClipboardService _clipboard;
    private readonly IAiContextService _aiContext;

    public ScannerViewModel(
        ScanService scanService,
        AddressTableService addressTableService,
        IProcessContext processContext,
        IOutputLog outputLog,
        INavigationService navigationService,
        IClipboardService clipboard,
        IAiContextService aiContext)
    {
        _scanService = scanService;
        _addressTableService = addressTableService;
        _processContext = processContext;
        _outputLog = outputLog;
        _navigationService = navigationService;
        _clipboard = clipboard;
        _aiContext = aiContext;
    }

    [ObservableProperty]
    private string _scanValue = "";

    [ObservableProperty]
    private ScanResultOverview? _selectedScanResult;

    [ObservableProperty]
    private ObservableCollection<ScanResultOverview> _scanResults = new();

    [ObservableProperty]
    private string? _scanStatus;

    [ObservableProperty]
    private string? _scanDetails;

    [ObservableProperty]
    private ScanType _selectedScanType = ScanType.ExactValue;

    [ObservableProperty]
    private MemoryDataType _selectedDataType = MemoryDataType.Int32;

    // ── Phase 7A Properties ──

    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _showAsHex;
    [ObservableProperty] private string? _floatEpsilon;
    [ObservableProperty] private bool _writableOnly = true;

    private ScanOptions BuildScanOptions() => new(
        WritableOnly: WritableOnly,
        FloatEpsilon: float.TryParse(FloatEpsilon, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var eps) ? eps : null,
        ShowAsHex: ShowAsHex);

    [RelayCommand]
    private async Task StartNewScanAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null)
        {
            ScanStatus = "No process attached";
            _outputLog.Append("Scanner", "Warn", "Select and inspect a process before scanning.");
            return;
        }

        try
        {
            _scanService.ResetScan();
            ScanStatus = "Scanning...";
            ScanDetails = "Initial scan in progress.";
            ScanResults.Clear();

            var options = BuildScanOptions();
            var overview = await _scanService.StartScanAsync(
                pid.Value,
                SelectedDataType,
                SelectedScanType,
                ScanValue,
                options);

            ScanResults = new ObservableCollection<ScanResultOverview>(overview.Results);
            ScanStatus = $"{overview.ResultCount:N0} results found";
            ScanDetails = $"Scanned {overview.TotalRegionsScanned} regions ({overview.TotalBytesScanned}), type={overview.DataType}, scan={overview.ScanType}";
            _outputLog.Append("Scanner", "Info", $"Scan complete: {overview.ResultCount:N0} results across {overview.TotalRegionsScanned} regions.");
        }
        catch (Exception ex)
        {
            ScanStatus = "Scan failed";
            _outputLog.Append("Scanner", "Error", $"Scan failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefineScanAsync()
    {
        if (_processContext.AttachedProcessId is null) return;

        if (_scanService.LastScanResults is null)
        {
            _outputLog.Append("Scanner", "Warn", "No active scan to refine. Start a new scan first.");
            return;
        }

        try
        {
            ScanStatus = "Refining...";
            ScanDetails = "Next scan in progress.";

            var options = BuildScanOptions();
            var overview = await _scanService.RefineScanAsync(
                SelectedScanType,
                ScanValue,
                options);

            ScanResults = new ObservableCollection<ScanResultOverview>(overview.Results);
            ScanStatus = $"{overview.ResultCount:N0} results remaining";
            ScanDetails = $"Refined with {overview.ScanType}, type={overview.DataType}";
            CanUndo = _scanService.CanUndo;
            _outputLog.Append("Scanner", "Info", $"Refinement complete: {overview.ResultCount:N0} results remaining.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("Scanner", "Error", $"Refinement failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ResetScan()
    {
        _scanService.ResetScan();
        ScanResults.Clear();
        ScanStatus = null;
        ScanDetails = null;
        CanUndo = false;
        _outputLog.Append("Scanner", "Info", "Scan reset. Ready for a new scan.");
    }

    [RelayCommand]
    private void UndoScan()
    {
        var overview = _scanService.UndoScan();
        if (overview is null)
        {
            _outputLog.Append("Scanner", "Warn", "No scan to undo.");
            return;
        }

        ScanResults = new System.Collections.ObjectModel.ObservableCollection<ScanResultOverview>(overview.Results);
        ScanStatus = $"{overview.ResultCount:N0} results (undo)";
        ScanDetails = $"Restored previous scan ({overview.ScanType})";
        CanUndo = _scanService.CanUndo;
        _outputLog.Append("Scanner", "Info", $"Undo scan: {overview.ResultCount:N0} results restored.");
    }

    [RelayCommand]
    private void ToggleHexDisplay()
    {
        ShowAsHex = !ShowAsHex;
        _outputLog.Append("Scanner", "Info", ShowAsHex ? "Hex display enabled." : "Hex display disabled.");
    }

    [RelayCommand]
    private void AddSelectedToTable()
    {
        if (SelectedScanResult is not { } selected)
        {
            _outputLog.Append("Scanner", "Warn", "Select a scan result to add to the address table.");
            return;
        }

        _addressTableService.AddFromScanResult(selected, SelectedDataType);
        _outputLog.Append("Scanner", "Info", $"Added {selected.Address} to address table.");
    }

    // ── Cross-panel context menu commands ──

    [RelayCommand]
    private void CopyAddress()
    {
        if (SelectedScanResult is null) return;
        _clipboard.SetText(SelectedScanResult.Address);
    }

    [RelayCommand]
    private void CopyValue()
    {
        if (SelectedScanResult is null) return;
        _clipboard.SetText(SelectedScanResult.CurrentValue);
    }

    [RelayCommand]
    private void BrowseMemory()
    {
        if (SelectedScanResult is null) return;
        _navigationService.ShowDocument("memoryBrowser", SelectedScanResult.Address);
    }

    [RelayCommand]
    private void DisassembleHere()
    {
        if (SelectedScanResult is null) return;
        _navigationService.ShowDocument("disassembler", SelectedScanResult.Address);
    }

    [RelayCommand]
    private void AskAi()
    {
        if (SelectedScanResult is null) return;
        _aiContext.SendContext("Scanner",
            $"Scan result: {SelectedScanResult.Address} = {SelectedScanResult.CurrentValue} ({SelectedDataType})");
    }
}
