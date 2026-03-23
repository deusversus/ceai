using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class ProcessListViewModel : ObservableObject
{
    private readonly WorkspaceDashboardService _dashboardService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;

    public ProcessListViewModel(
        WorkspaceDashboardService dashboardService,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _dashboardService = dashboardService;
        _processContext = processContext;
        _outputLog = outputLog;
    }

    [ObservableProperty]
    private ObservableCollection<RunningProcessOverview> _processes = new();

    [ObservableProperty]
    private RunningProcessOverview? _selectedProcess;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var databasePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CEAISuite",
                "workspace.db");
            var dashboard = await _dashboardService.BuildAsync(databasePath);
            Processes = new ObservableCollection<RunningProcessOverview>(dashboard.RunningProcesses);
            _outputLog.Append("Processes", "Info", $"Refreshed: {dashboard.RunningProcesses.Count} processes found.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("Processes", "Error", $"Refresh failed: {ex.Message}");
        }
    }

    /// <summary>Replace the process list contents (called from MainWindow after initial load).</summary>
    public void SetProcesses(IReadOnlyList<RunningProcessOverview> processes)
    {
        Processes = new ObservableCollection<RunningProcessOverview>(processes);
    }
}
