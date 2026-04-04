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

    private readonly IClipboardService _clipboard;
    private readonly INavigationService _navigationService;
    private readonly IAiContextService _aiContext;

    public PointerScannerViewModel(
        PointerScannerService scannerService,
        AddressTableService addressTableService,
        IProcessContext processContext,
        IOutputLog outputLog,
        IClipboardService clipboard,
        INavigationService navigationService,
        IAiContextService aiContext)
    {
        _scannerService = scannerService;
        _addressTableService = addressTableService;
        _processContext = processContext;
        _outputLog = outputLog;
        _clipboard = clipboard;
        _navigationService = navigationService;
        _aiContext = aiContext;
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
    private async Task ValidatePathsAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        if (Results.Count == 0) { StatusText = "No results to validate."; return; }

        StatusText = "Validating...";

        // Validate in batches to avoid per-item UI thrash
        const int batchSize = 50;
        var items = Results.ToList();
        var statuses = new (string Status, int Index)[items.Count];

        for (int i = 0; i < items.Count; i += batchSize)
        {
            var batch = items.Skip(i).Take(batchSize).Select((item, offset) =>
            {
                var idx = i + offset;
                if (item.Source is null) return Task.FromResult(("Broken", idx));
                return ValidateOneAsync(pid.Value, item.Source, idx);
            });
            var results = await Task.WhenAll(batch);
            foreach (var (status, idx) in results)
                statuses[idx] = (status, idx);

            StatusText = $"Validating... {Math.Min(i + batchSize, items.Count)}/{items.Count}";
        }

        // Apply results to observable items in one pass
        int stable = 0, drifted = 0, broken = 0;
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Status = statuses[i].Status;
            switch (statuses[i].Status)
            {
                case "Stable": stable++; break;
                case "Drifted": drifted++; break;
                default: broken++; break;
            }
        }
        StatusText = $"Validated: {stable} stable, {drifted} drifted, {broken} broken";
    }

    private async Task<(string Status, int Index)> ValidateOneAsync(int pid, PointerPath path, int index)
    {
        var (status, _) = await _scannerService.ValidatePathAsync(pid, path);
        return (status, index);
    }

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

    // ── Cross-panel context menu commands ──

    [RelayCommand]
    private void CopyPath()
    {
        if (SelectedResult is null) return;
        _clipboard.SetText(SelectedResult.Chain);
    }

    [RelayCommand]
    private void CopyResolvedAddress()
    {
        if (SelectedResult is null) return;
        _clipboard.SetText(SelectedResult.ResolvedAddress);
    }

    [RelayCommand]
    private void BrowseResolved()
    {
        if (SelectedResult is null) return;
        _navigationService.ShowDocument("memoryBrowser", SelectedResult.ResolvedAddress);
    }

    [RelayCommand]
    private void DisassembleResolved()
    {
        if (SelectedResult is null) return;
        _navigationService.ShowDocument("disassembler", SelectedResult.ResolvedAddress);
    }

    [RelayCommand]
    private void AskAi()
    {
        if (SelectedResult is null) return;
        _aiContext.SendContext("Pointer Scanner",
            $"Pointer path: {SelectedResult.Chain} → {SelectedResult.ResolvedAddress} (Status: {SelectedResult.Status})");
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
