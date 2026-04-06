using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class PointerScannerViewModel : ObservableObject, IDisposable
{
    private readonly PointerScannerService _scannerService;
    private readonly AddressTableService _addressTableService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly IDialogService _dialogService;
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
        IAiContextService aiContext,
        IDialogService dialogService)
    {
        _scannerService = scannerService;
        _addressTableService = addressTableService;
        _processContext = processContext;
        _outputLog = outputLog;
        _clipboard = clipboard;
        _navigationService = navigationService;
        _aiContext = aiContext;
        _dialogService = dialogService;
    }

    [ObservableProperty] private string _targetAddress = "";
    [ObservableProperty] private int _maxDepth = 3;
    [ObservableProperty] private string _maxOffset = "0x2000";
    [ObservableProperty] private string? _moduleFilter;
    [ObservableProperty] private ObservableCollection<PointerPathDisplayItem> _results = new();
    [ObservableProperty] private PointerPathDisplayItem? _selectedResult;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _canResume;

    [RelayCommand]
    private async Task ScanAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        if (string.IsNullOrWhiteSpace(TargetAddress)) { StatusText = "Enter a target address."; return; }
        if (!TryParseAddress(TargetAddress, out var addr)) { StatusText = "Invalid address."; return; }

        var maxOff = 0x2000L;
        if (MaxOffset.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            _ = long.TryParse(MaxOffset[2..], System.Globalization.NumberStyles.HexNumber, null, out maxOff);
        else
            _ = long.TryParse(MaxOffset, out maxOff);

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        StatusText = "Scanning...";
        Results.Clear();

        try
        {
            CanResume = false;
            var modFilter = ParseModuleFilter(ModuleFilter);
            var paths = await _scannerService.ScanForPointersAsync(
                pid.Value, addr, MaxDepth, maxOff, modFilter, _scanCts.Token);

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
            CanResume = _scannerService.CanResume;
            StatusText = CanResume ? "Scan cancelled. Can resume." : "Scan cancelled.";
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
    private async Task ResumeScanAsync()
    {
        if (!CanResume) { StatusText = "Nothing to resume."; return; }
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        StatusText = "Resuming scan...";

        try
        {
            var paths = await _scannerService.ResumeScanAsync(pid.Value, _scanCts.Token);
            Results.Clear();
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
            StatusText = $"{Results.Count} pointer path(s) found (resumed).";
            CanResume = false;
        }
        catch (OperationCanceledException)
        {
            CanResume = _scannerService.CanResume;
            StatusText = CanResume ? "Scan cancelled. Can resume." : "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally { IsScanning = false; }
    }

    // ── Pointer Map Save / Load / Compare ──

    [RelayCommand]
    private async Task SavePointerMapAsync()
    {
        if (Results.Count == 0) { StatusText = "No results to save."; return; }

        var path = _dialogService.ShowSaveFileDialog("Pointer Maps (*.ptr)|*.ptr", "pointer_scan.ptr");
        if (path is null) return;

        var paths = Results.Where(r => r.Source is not null).Select(r => r.Source!).ToList();
        var processName = _processContext.AttachedProcessName ?? "Unknown";
        if (!TryParseAddress(TargetAddress, out var addr)) addr = 0;

        var map = new PointerMapFile(processName, addr, DateTimeOffset.UtcNow, MaxDepth, 0x2000, paths);
        await PointerScannerService.SavePointerMapAsync(path, map);
        StatusText = $"Saved {paths.Count} pointer paths to {System.IO.Path.GetFileName(path)}";
    }

    [RelayCommand]
    private async Task LoadPointerMapAsync()
    {
        var path = _dialogService.ShowOpenFileDialog("Pointer Maps (*.ptr)|*.ptr");
        if (path is null) return;

        try
        {
            var map = await PointerScannerService.LoadPointerMapAsync(path);
            Results.Clear();
            foreach (var p in map.Paths)
            {
                Results.Add(new PointerPathDisplayItem
                {
                    Chain = p.Display,
                    ResolvedAddress = $"0x{p.ResolvedAddress:X}",
                    ModuleName = p.ModuleName,
                    Status = "Loaded",
                    Source = p
                });
            }
            TargetAddress = $"0x{map.OriginalTargetAddress:X}";
            StatusText = $"Loaded {map.Paths.Count} paths from {System.IO.Path.GetFileName(path)} (scanned {map.ScanTimestamp:g})";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ComparePointerMapsAsync()
    {
        var pathA = _dialogService.ShowOpenFileDialog("Pointer Maps (*.ptr)|*.ptr");
        if (pathA is null) return;
        var pathB = _dialogService.ShowOpenFileDialog("Pointer Maps (*.ptr)|*.ptr");
        if (pathB is null) return;

        try
        {
            var mapA = await PointerScannerService.LoadPointerMapAsync(pathA);
            var mapB = await PointerScannerService.LoadPointerMapAsync(pathB);
            var comparison = PointerScannerService.CompareMaps(mapA, mapB);

            // Show common paths in results
            Results.Clear();
            foreach (var p in comparison.CommonPaths)
            {
                Results.Add(new PointerPathDisplayItem
                {
                    Chain = p.Display,
                    ResolvedAddress = $"0x{p.ResolvedAddress:X}",
                    ModuleName = p.ModuleName,
                    Status = "Common",
                    Source = p
                });
            }

            StatusText = $"Comparison: {comparison.CommonPaths.Count} common, " +
                $"{comparison.OnlyInFirst.Count} only in first, {comparison.OnlyInSecond.Count} only in second " +
                $"({comparison.OverlapRatio:P0} overlap)";
        }
        catch (Exception ex)
        {
            StatusText = $"Compare failed: {ex.Message}";
        }
    }

    private static List<string>? ParseModuleFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        return filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();
    }

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

    public void Dispose()
    {
        _scanCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
