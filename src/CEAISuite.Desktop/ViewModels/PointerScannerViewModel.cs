using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class PointerScannerViewModel : ObservableObject
{
    private readonly PointerScannerService _scannerService;
    private readonly AddressTableService _addressTableService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private CancellationTokenSource? _scanCts;

    public PointerScannerViewModel(
        PointerScannerService scannerService,
        AddressTableService addressTableService,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _scannerService = scannerService;
        _addressTableService = addressTableService;
        _processContext = processContext;
        _outputLog = outputLog;
    }

    [ObservableProperty] private string _targetAddress = "";
    [ObservableProperty] private int _maxDepth = 3;
    [ObservableProperty] private string _maxOffset = "0x2000";
    [ObservableProperty] private ObservableCollection<PointerPathDisplayItem> _results = new();
    [ObservableProperty] private PointerPathDisplayItem? _selectedResult;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isScanning;

    [RelayCommand]
    private async Task ScanAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        if (string.IsNullOrWhiteSpace(TargetAddress)) { StatusText = "Enter a target address."; return; }
        if (!TryParseAddress(TargetAddress, out var addr)) { StatusText = "Invalid address."; return; }

        var maxOff = 0x2000L;
        if (MaxOffset.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            long.TryParse(MaxOffset[2..], System.Globalization.NumberStyles.HexNumber, null, out maxOff);
        else
            long.TryParse(MaxOffset, out maxOff);

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        StatusText = "Scanning...";
        Results.Clear();

        try
        {
            var paths = await _scannerService.ScanForPointersAsync(
                pid.Value, addr, MaxDepth, maxOff, _scanCts.Token);

            foreach (var p in paths)
            {
                Results.Add(new PointerPathDisplayItem
                {
                    Chain = p.Display,
                    ResolvedAddress = $"0x{p.ResolvedAddress:X}",
                    ModuleName = p.ModuleName,
                    Status = "Found",
                    Source = p
                });
            }
            StatusText = $"{Results.Count} pointer path(s) found.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _outputLog.Append("PtrScan", "Error", ex.Message);
        }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private void AddSelectedToTable()
    {
        if (SelectedResult?.Source is not { } path) return;
        // Add as a pointer entry — use the chain display as label
        _addressTableService.AddEntry(
            $"0x{path.ResolvedAddress:X}",
            MemoryDataType.Int32,
            "0",
            path.Display);
        StatusText = $"Added to address table: {path.Display}";
    }

    private static bool TryParseAddress(string text, out nuint address)
    {
        address = 0;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return nuint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}
