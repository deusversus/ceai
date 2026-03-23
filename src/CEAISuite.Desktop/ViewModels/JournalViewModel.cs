using System.Collections.ObjectModel;
using System.Windows;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class JournalViewModel : ObservableObject
{
    private readonly PatchUndoService _patchUndoService;
    private readonly OperationJournal _operationJournal;
    private readonly IOutputLog _outputLog;
    private readonly IDialogService _dialogService;

    public JournalViewModel(
        PatchUndoService patchUndoService,
        OperationJournal operationJournal,
        IOutputLog outputLog,
        IDialogService dialogService)
    {
        _patchUndoService = patchUndoService;
        _operationJournal = operationJournal;
        _outputLog = outputLog;
        _dialogService = dialogService;
    }

    [ObservableProperty]
    private ObservableCollection<PatchHistoryDisplayItem> _patchHistory = new();

    [ObservableProperty]
    private ObservableCollection<JournalEntryDisplayItem> _journalEntries = new();

    [ObservableProperty]
    private JournalEntryDisplayItem? _selectedJournalEntry;

    [RelayCommand]
    private void RefreshPatchHistory()
    {
        PatchHistory = new ObservableCollection<PatchHistoryDisplayItem>(
            _patchUndoService.GetHistory(50).Select(p => new PatchHistoryDisplayItem
            {
                Timestamp = p.Timestamp.LocalDateTime.ToString("HH:mm:ss"),
                Address = $"0x{p.Address:X}",
                DataType = p.DataType.ToString(),
                NewValue = p.NewValue
            }));
    }

    [RelayCommand]
    private async Task UndoPatchAsync()
    {
        try
        {
            var msg = await _patchUndoService.UndoAsync();
            _outputLog.Append("System", "Info", msg ?? "Nothing to undo");
            RefreshPatchHistory();
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Undo: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task RollbackAllAsync()
    {
        if (!_dialogService.Confirm("Confirm", "Rollback ALL memory patches?")) return;
        try
        {
            await _patchUndoService.RollbackAllAsync();
            _outputLog.Append("System", "Info", "All patches rolled back");
            RefreshPatchHistory();
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Rollback all: {ex.Message}"); }
    }

    [RelayCommand]
    private void RefreshJournal()
    {
        JournalEntries = new ObservableCollection<JournalEntryDisplayItem>(
            _operationJournal.GetEntries().Select(j => new JournalEntryDisplayItem
            {
                OperationId = j.OperationId,
                Timestamp = j.Timestamp.LocalDateTime.ToString("HH:mm:ss"),
                OperationType = j.OperationType,
                Address = $"0x{j.Address:X}",
                Mode = j.Mode,
                Status = j.Status.ToString()
            }));
    }

    [RelayCommand]
    private async Task RollbackSelectedEntryAsync()
    {
        if (SelectedJournalEntry is not { } item) return;
        try
        {
            var result = await _operationJournal.RollbackOperationAsync(item.OperationId);
            _outputLog.Append("System", "Info", result.Message);
            RefreshJournal();
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Rollback: {ex.Message}"); }
    }
}
