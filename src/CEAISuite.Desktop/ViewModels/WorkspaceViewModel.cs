using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class WorkspaceViewModel : ObservableObject
{
    private readonly SessionService _sessionService;
    private readonly IOutputLog _outputLog;
    private readonly IDialogService _dialogService;

    /// <summary>Raised when the user wants to load a saved session.</summary>
    public event Action<string>? LoadSessionRequested;

    /// <summary>Raised when the user wants to import a .CT file.</summary>
    public event Action<string>? LoadCheatTableRequested;

    public WorkspaceViewModel(
        SessionService sessionService,
        IOutputLog outputLog,
        IDialogService dialogService)
    {
        _sessionService = sessionService;
        _outputLog = outputLog;
        _dialogService = dialogService;
    }

    [ObservableProperty] private ObservableCollection<SessionDisplayItem> _sessions = new();
    [ObservableProperty] private SessionDisplayItem? _selectedSession;
    [ObservableProperty] private string? _statusText;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var saved = await _sessionService.ListSessionsAsync(20);
            Sessions.Clear();
            foreach (var s in saved)
            {
                Sessions.Add(new SessionDisplayItem
                {
                    Id = s.Id,
                    ProcessName = s.ProcessName,
                    ProcessId = s.ProcessId,
                    CreatedAt = s.CreatedAtUtc.LocalDateTime.ToString("g"),
                    AddressCount = s.AddressEntryCount,
                    ActionCount = s.ActionLogCount
                });
            }
            StatusText = $"{Sessions.Count} session(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _outputLog.Append("Workspace", "Error", ex.Message);
        }
    }

    [RelayCommand]
    private void LoadSelected()
    {
        if (SelectedSession is null) return;
        LoadSessionRequested?.Invoke(SelectedSession.Id);
        StatusText = $"Loading {SelectedSession.Id}...";
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedSession is null) return;
        var confirm = _dialogService.Confirm(
            $"Delete session '{SelectedSession.Id}'?", "Delete Session");
        if (!confirm) return;
        try
        {
            await _sessionService.DeleteSessionAsync(SelectedSession.Id);
            Sessions.Remove(SelectedSession);
            StatusText = "Session deleted.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ImportCheatTable()
    {
        // Delegate to AddressTableViewModel which handles its own file dialog + merge/replace logic
        LoadCheatTableRequested?.Invoke("");
    }
}
