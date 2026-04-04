using System.Collections.ObjectModel;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class OutputLogViewModel : ObservableObject
{
    private readonly IOutputLog _outputLog;
    private readonly IClipboardService _clipboard;

    public OutputLogViewModel(IOutputLog outputLog, IClipboardService clipboard)
    {
        _outputLog = outputLog;
        _clipboard = clipboard;
    }

    public ObservableCollection<OutputLogEntry> Entries => _outputLog.Entries;

    [ObservableProperty]
    private OutputLogEntry? _selectedEntry;

    [RelayCommand]
    private void Clear() => _outputLog.Clear();

    [RelayCommand]
    private void CopyLine()
    {
        if (SelectedEntry is { } entry)
            _clipboard.SetText($"[{entry.Timestamp}] [{entry.Source}] {entry.Level}: {entry.Message}");
    }

    [RelayCommand]
    private void CopyAll()
    {
        var lines = Entries.Select(e => $"[{e.Timestamp}] [{e.Source}] {e.Level}: {e.Message}");
        _clipboard.SetText(string.Join(Environment.NewLine, lines));
    }
}
