using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class SnapshotsViewModel : ObservableObject
{
    private readonly MemorySnapshotService _snapshotService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;

    public SnapshotsViewModel(
        MemorySnapshotService snapshotService,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _snapshotService = snapshotService;
        _processContext = processContext;
        _outputLog = outputLog;
    }

    [ObservableProperty]
    private ObservableCollection<SnapshotDisplayItem> _snapshots = new();

    [ObservableProperty]
    private ObservableCollection<SnapshotDiffDisplayItem> _diffItems = new();

    [ObservableProperty]
    private SnapshotDisplayItem? _selectedSnapshot;

    [ObservableProperty]
    private string _address = "";

    [ObservableProperty]
    private string _length = "256";

    [ObservableProperty]
    private string _label = "";

    /// <summary>Selected items for multi-select compare. Set by code-behind.</summary>
    public IList<SnapshotDisplayItem>? SelectedSnapshots { get; set; }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        var addrText = Address.Trim();
        if (string.IsNullOrEmpty(addrText)) return;
        try
        {
            var addr = addrText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? nuint.Parse(addrText[2..], System.Globalization.NumberStyles.HexNumber)
                : nuint.Parse(addrText, System.Globalization.NumberStyles.HexNumber);
            var len = int.TryParse(Length, out var l) ? l : 256;
            var lbl = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim();
            await _snapshotService.CaptureAsync(pid, addr, len, lbl);
            RefreshList();
            _outputLog.Append("System", "Info", $"Snapshot captured: 0x{addr:X}, {len} bytes");
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Capture snapshot: {ex.Message}"); }
    }

    [RelayCommand]
    private void Compare()
    {
        var selected = SelectedSnapshots?.ToList() ?? new List<SnapshotDisplayItem>();
        if (selected.Count != 2) { _outputLog.Append("System", "Warn", "Select exactly two snapshots to compare"); return; }
        try
        {
            var diff = _snapshotService.Compare(selected[0].Id, selected[1].Id);
            PopulateDiff(diff);
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Compare snapshots: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task CompareWithLiveAsync()
    {
        if (_processContext.AttachedProcessId is null) return;
        if (SelectedSnapshot is not { } item) return;
        try
        {
            var diff = await _snapshotService.CompareWithLiveAsync(item.Id);
            PopulateDiff(diff);
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Compare with live: {ex.Message}"); }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedSnapshot is not { } item) return;
        _snapshotService.DeleteSnapshot(item.Id);
        RefreshList();
    }

    public void RefreshList()
    {
        var list = _snapshotService.ListSnapshots();
        Snapshots = new ObservableCollection<SnapshotDisplayItem>(
            list.Select(s => new SnapshotDisplayItem
            {
                Id = s.Id,
                Label = s.Label,
                Address = $"0x{s.BaseAddress:X}",
                Size = $"{s.Data.Length}",
                CapturedAt = s.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            }));
    }

    private void PopulateDiff(SnapshotDiff diff)
    {
        DiffItems = new ObservableCollection<SnapshotDiffDisplayItem>(
            diff.Changes.Select(c => new SnapshotDiffDisplayItem
            {
                Offset = $"+0x{c.Offset:X}",
                OldValue = BitConverter.ToString(c.OldBytes).Replace("-", " "),
                NewValue = BitConverter.ToString(c.NewBytes).Replace("-", " "),
                Interpretation = c.Interpretation ?? ""
            }));
    }
}
