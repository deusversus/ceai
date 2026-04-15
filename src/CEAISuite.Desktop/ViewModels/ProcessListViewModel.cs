using System.Collections.ObjectModel;
using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class ProcessListViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceDashboardService _dashboardService;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly EventSubscriptions _subs = new();

    private List<RunningProcessOverview> _allProcesses = [];

    public ProcessListViewModel(
        WorkspaceDashboardService dashboardService,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _dashboardService = dashboardService;
        _processContext = processContext;
        _outputLog = outputLog;

        _subs.Subscribe(h => _processContext.ProcessChanged += h, h => _processContext.ProcessChanged -= h, OnProcessChanged);
    }

    public void Dispose() => _subs.Dispose();

    [ObservableProperty]
    private ObservableCollection<RunningProcessOverview> _processes = new();

    [ObservableProperty]
    private RunningProcessOverview? _selectedProcess;

    [ObservableProperty]
    private string _filterText = "";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [ObservableProperty]
    private string? _attachedProcessName;

    [ObservableProperty]
    private bool _isAttached;

    [ObservableProperty]
    private string? _architecture;

    [ObservableProperty]
    private int _moduleCount;

    [ObservableProperty]
    private string? _processDetails;

    private void OnProcessChanged()
    {
        IsAttached = _processContext.AttachedProcessId is not null;
        AttachedProcessName = _processContext.AttachedProcessName;

        // Phase 4 + 12A: populate process details from inspection
        var inspection = _processContext.CurrentInspection;
        if (inspection is not null)
        {
            Architecture = inspection.Architecture;
            ModuleCount = inspection.Modules.Count;
            var details = $"{inspection.Architecture} | {inspection.Modules.Count} modules";
            if (inspection.IsElevated) details += " | Admin";
            if (inspection.ParentProcessName is not null) details += $" | Parent: {inspection.ParentProcessName}";
            ProcessDetails = details;
        }
        else
        {
            Architecture = null;
            ModuleCount = 0;
            ProcessDetails = null;
        }

        // Auto-select the attached process in the list if present
        if (_processContext.AttachedProcessId is { } pid)
        {
            var match = Processes.FirstOrDefault(p => p.Id == pid);
            if (match is not null)
                SelectedProcess = match;
        }
        else
        {
            SelectedProcess = null;
        }
    }

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
            _allProcesses = dashboard.RunningProcesses.ToList();
            ApplyFilter();
            _outputLog.Append("Processes", "Info", $"Refreshed: {_allProcesses.Count} processes found.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("Processes", "Error", $"Refresh failed: {ex.Message}");
        }
    }

    /// <summary>Replace the process list contents (called from MainWindow after initial load).</summary>
    public void SetProcesses(IReadOnlyList<RunningProcessOverview> processes)
    {
        _allProcesses = processes.ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(FilterText)
            ? _allProcesses
            : _allProcesses.Where(p =>
                p.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                p.Id.ToString(CultureInfo.InvariantCulture).Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                .ToList();
        Processes = new ObservableCollection<RunningProcessOverview>(filtered);
    }

}
