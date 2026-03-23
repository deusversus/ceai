using System.Collections.ObjectModel;
using System.IO;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CEAISuite.Domain;
using Microsoft.Extensions.AI;

namespace CEAISuite.Desktop.ViewModels;

/// <summary>
/// Top-level ViewModel for MainWindow. Owns orchestration logic, session management,
/// process attach/detach, undo/redo, AI configuration, and menu-bar commands.
/// UI-specific code (AvalonDock layout, theme, density, drag-drop, WndProc) stays in MainWindow.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly string _databasePath;
    private readonly WorkspaceDashboardService _dashboardService;
    private readonly AddressTableService _addressTableService;
    private readonly SessionService _sessionService;
    private readonly BreakpointService _breakpointService;
    private readonly AiOperatorService _aiOperatorService;
    private readonly AppSettingsService _appSettingsService;
    private readonly PatchUndoService _patchUndoService;
    private readonly IEngineFacade _engineFacade;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly IDialogService _dialogService;
    private readonly ScanService _scanService;

    // Child ViewModels (injected, shared with MainWindow for DataContext wiring)
    private readonly AddressTableViewModel _addressTableVm;
    private readonly InspectionViewModel _inspectionVm;
    private readonly ProcessListViewModel _processListVm;
    private readonly AiOperatorViewModel _aiOperatorVm;
    private readonly FindResultsViewModel _findResultsVm;

    public MainViewModel(
        WorkspaceDashboardService dashboardService,
        AddressTableService addressTableService,
        SessionService sessionService,
        BreakpointService breakpointService,
        AiOperatorService aiOperatorService,
        AppSettingsService appSettingsService,
        PatchUndoService patchUndoService,
        IEngineFacade engineFacade,
        IProcessContext processContext,
        IOutputLog outputLog,
        IDialogService dialogService,
        ScanService scanService,
        AddressTableViewModel addressTableVm,
        InspectionViewModel inspectionVm,
        ProcessListViewModel processListVm,
        AiOperatorViewModel aiOperatorVm,
        FindResultsViewModel findResultsVm)
    {
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite",
            "workspace.db");

        _dashboardService = dashboardService;
        _addressTableService = addressTableService;
        _sessionService = sessionService;
        _breakpointService = breakpointService;
        _aiOperatorService = aiOperatorService;
        _appSettingsService = appSettingsService;
        _patchUndoService = patchUndoService;
        _engineFacade = engineFacade;
        _processContext = processContext;
        _outputLog = outputLog;
        _dialogService = dialogService;
        _scanService = scanService;

        _addressTableVm = addressTableVm;
        _inspectionVm = inspectionVm;
        _processListVm = processListVm;
        _aiOperatorVm = aiOperatorVm;
        _findResultsVm = findResultsVm;

        // Wire AI operator with dynamic context injection
        _aiOperatorService.SetContextProvider(BuildAiContext);

        // Configure AI provider (may be null if no API key configured)
        try
        {
            var chatClient = CreateChatClient();
            _aiOperatorService.Reconfigure(chatClient);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI provider init failed: {ex.Message}");
        }
        _aiOperatorService.RateLimitSeconds = _appSettingsService.Settings.RateLimitSeconds;
        _aiOperatorService.RateLimitWait = _appSettingsService.Settings.RateLimitWait;
        _aiOperatorService.Limits = TokenLimits.Resolve(_appSettingsService.Settings);
    }

    // ── Observable state ──

    [ObservableProperty]
    private WorkspaceDashboard _dashboard = WorkspaceDashboard.CreateLoading();

    [ObservableProperty]
    private string _statusBarProcessText = "No process attached";

    [ObservableProperty]
    private string _statusBarCenterText = "";

    [ObservableProperty]
    private ObservableCollection<ProcessComboItem> _processComboItems = new();

    /// <summary>Raised when MainViewModel needs MainWindow to perform a UI-specific action.</summary>
    public event Action<string, object?>? UiActionRequested;

    // ── AI Configuration ──

    public IChatClient? CreateChatClient()
    {
        return ChatClientFactory.Create(_appSettingsService.Settings);
    }

    /// <summary>Provides dynamic context to the AI agent before each message.</summary>
    public string BuildAiContext()
    {
        var sb = new System.Text.StringBuilder();
        var dashboard = Dashboard;

        if (dashboard.CurrentInspection is { } p)
            sb.AppendLine($"Attached: {p.ProcessName} (PID {p.ProcessId}, {p.Architecture}, {p.Modules.Count} modules)");
        else
            sb.AppendLine("No process attached.");

        var roots = _addressTableService.Roots;
        int total = 0, frozen = 0, scripts = 0;
        CountNodesRecursive(roots, ref total, ref frozen, ref scripts);
        sb.AppendLine($"Address table: {total} entries, {frozen} frozen, {scripts} scripts");

        if (_scanService.LastScanResults is { } scan)
            sb.AppendLine($"Active scan: {scan.Results.Count:N0} results ({scan.Constraints.DataType})");

        return sb.ToString();
    }

    private static void CountNodesRecursive(
        ObservableCollection<AddressTableNode> nodes,
        ref int total, ref int frozen, ref int scripts)
    {
        foreach (var n in nodes)
        {
            total++;
            if (n.IsLocked) frozen++;
            if (n.IsScriptEntry) scripts++;
            CountNodesRecursive(n.Children, ref total, ref frozen, ref scripts);
        }
    }

    // ── Settings Changed Handler ──

    public void HandleSettingsChanged()
    {
        _addressTableVm.SetRefreshInterval(
            _appSettingsService.Settings.RefreshIntervalMs > 0
                ? _appSettingsService.Settings.RefreshIntervalMs
                : 500);

        // Hot-swap AI provider when settings change
        try
        {
            var newClient = CreateChatClient();
            _aiOperatorService.Reconfigure(newClient);
            _aiOperatorService.RateLimitSeconds = _appSettingsService.Settings.RateLimitSeconds;
            _aiOperatorService.RateLimitWait = _appSettingsService.Settings.RateLimitWait;
            _aiOperatorService.Limits = TokenLimits.Resolve(_appSettingsService.Settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI provider reconfigure failed: {ex.Message}");
            _aiOperatorService.Reconfigure(null);
        }
    }

    // ── Process Attach / Detach ──

    public async Task InspectSelectedProcessAsync()
    {
        var dashboard = Dashboard;
        var selectedProcess = _processListVm.SelectedProcess;
        if (selectedProcess is null)
        {
            Dashboard = dashboard with { StatusMessage = "Select a process to inspect." };
            return;
        }

        try
        {
            var inspection = await _dashboardService.InspectProcessAsync(selectedProcess.Id);
            Dashboard = dashboard with
            {
                CurrentInspection = inspection,
                StatusMessage = inspection.StatusMessage
            };

            // Update InspectionViewModel and process context
            _inspectionVm.SetInspection(inspection);
            _processContext.Attach(inspection);

            // Set process context for pointer chain resolution
            try
            {
                var attachment = await _engineFacade.AttachAsync(selectedProcess.Id);
                _addressTableService.SetProcessContext(
                    attachment.Modules,
                    selectedProcess.Architecture == "x86");
            }
            catch { /* non-fatal -- address resolution will try again during refresh */ }

            // Start auto-refresh timer for address table values
            StartAutoRefresh();

            // Auto-open Memory Browser tab if enabled
            if (_appSettingsService.Settings.AutoOpenMemoryBrowser)
            {
                UiActionRequested?.Invoke("OpenMemoryBrowser", selectedProcess);
            }
        }
        catch (Exception exception)
        {
            Dashboard = dashboard with { StatusMessage = exception.Message };
        }
    }

    public void StartAutoRefresh()
    {
        _addressTableVm.StartAutoRefresh(_appSettingsService.Settings.RefreshIntervalMs > 0
            ? _appSettingsService.Settings.RefreshIntervalMs
            : 500);
    }

    public async Task DetachProcessAsync()
    {
        var dashboard = Dashboard;
        if (dashboard.CurrentInspection is null)
        {
            StatusBarCenterText = "No process attached";
            return;
        }

        var pid = dashboard.CurrentInspection.ProcessId;
        var processName = dashboard.CurrentInspection.ProcessName;

        try
        {
            // 1. Stop auto-refresh so we don't read from a detached process
            _addressTableVm.StopAutoRefresh();

            // 2. Remove all breakpoints for this process
            try
            {
                var bps = await _breakpointService.ListBreakpointsAsync(pid);
                foreach (var bp in bps)
                    await _breakpointService.RemoveBreakpointAsync(pid, bp.Id);
            }
            catch { /* best-effort -- process may already be gone */ }

            // 3. Detach engine facade + clear dashboard state
            _dashboardService.DetachProcess();

            // 4. Update dashboard to reflect detach
            Dashboard = dashboard with
            {
                CurrentInspection = null,
                StatusMessage = $"Detached from {processName} ({pid})"
            };

            // 5. Request UI-side cleanup (MemoryBrowserTab.Clear, ProcessComboBox reset)
            UiActionRequested?.Invoke("DetachCleanup", null);

            // 6. Update status bar
            StatusBarProcessText = "No process attached";
            StatusBarCenterText = $"Detached from {processName}";
        }
        catch (Exception ex)
        {
            StatusBarCenterText = $"Detach error: {ex.Message}";
        }
    }

    public async Task EmergencyStopAsync()
    {
        var dashboard = Dashboard;
        var pid = dashboard.CurrentInspection?.ProcessId;
        var processName = dashboard.CurrentInspection?.ProcessName ?? "unknown";

        try
        {
            // 1. Stop everything immediately
            _addressTableVm.StopAutoRefresh();

            // 2. Rollback ALL patches (restores original bytes)
            var rolled = await _patchUndoService.RollbackAllAsync();

            // 3. Force-detach breakpoint engine (emergency path -- no locks)
            if (pid.HasValue)
            {
                try { await _breakpointService.ForceDetachAndCleanupAsync(pid.Value); }
                catch { /* best-effort */ }
            }

            // 4. Detach engine facade + clear dashboard
            _dashboardService.DetachProcess();

            // 5. Update dashboard
            Dashboard = dashboard with
            {
                CurrentInspection = null,
                StatusMessage = $"EMERGENCY STOP -- detached from {processName}"
            };

            // 6. Request UI-side cleanup
            UiActionRequested?.Invoke("DetachCleanup", null);

            StatusBarProcessText = "EMERGENCY STOP -- detached";
            StatusBarCenterText = $"Rolled back {rolled} patch(es), force-detached";
        }
        catch (Exception ex)
        {
            StatusBarCenterText = $"Emergency stop error: {ex.Message}";
        }
    }

    // ── Process Combo ──

    public void PopulateProcessCombo()
    {
        var dashboard = Dashboard;
        var items = (dashboard.RunningProcesses ?? [])
            .Select(p => new ProcessComboItem { Pid = p.Id, Name = p.Name })
            .OrderBy(p => p.Name)
            .ToList();
        ProcessComboItems = new ObservableCollection<ProcessComboItem>(items);
    }

    public async Task RefreshProcessComboAsync()
    {
        var dashboard = Dashboard;
        try
        {
            var updated = await _dashboardService.BuildAsync(_databasePath);
            Dashboard = updated with
            {
                CurrentInspection = dashboard.CurrentInspection,
                ScanResults = dashboard.ScanResults,
                ScanStatus = dashboard.ScanStatus,
                ScanDetails = dashboard.ScanDetails,
                AddressTableNodes = _addressTableService.Roots,
                AddressTableStatus = dashboard.AddressTableStatus,
                Disassembly = dashboard.Disassembly,
                BreakpointStatus = dashboard.BreakpointStatus,
                StatusMessage = dashboard.StatusMessage
            };
            PopulateProcessCombo();
        }
        catch { /* swallow -- non-critical refresh */ }
    }

    // ── Undo / Redo ──

    public async Task<string?> PerformUndoAsync()
    {
        var msg = await _patchUndoService.UndoAsync();
        RefreshAddressTableUI(msg);
        return msg;
    }

    public async Task<string?> PerformRedoAsync()
    {
        var msg = await _patchUndoService.RedoAsync();
        RefreshAddressTableUI(msg);
        return msg;
    }

    // ── Address Table UI Refresh ──

    public void RefreshAddressTableUI(string? statusMessage = null)
    {
        var dashboard = Dashboard;
        Dashboard = dashboard with
        {
            AddressTableNodes = _addressTableService.Roots,
            AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
            StatusMessage = statusMessage ?? dashboard.StatusMessage
        };
    }

    // ── Session Save / Load ──

    public async Task SaveSessionAsync()
    {
        var dashboard = Dashboard;

        try
        {
            // Save current chat first so it's persisted to disk
            _aiOperatorService.SaveCurrentChat();

            var sessionId = await _sessionService.SaveSessionAsync(
                dashboard.CurrentInspection?.ProcessName,
                dashboard.CurrentInspection?.ProcessId,
                _addressTableService.Entries.ToArray(),
                _aiOperatorService.ActionLog.ToArray(),
                _aiOperatorService.CurrentChatId);

            Dashboard = dashboard with { StatusMessage = $"Session saved: {sessionId}" };
        }
        catch (Exception ex)
        {
            Dashboard = dashboard with { StatusMessage = $"Save failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Loads a session. Returns (sessions, dialogNeeded) for the MainWindow to present a dialog.
    /// The actual session restoration is done via <see cref="RestoreSession"/>.
    /// </summary>
    public async Task<IReadOnlyList<SavedInvestigationSession>?> ListSessionsAsync()
    {
        try
        {
            var sessions = (await _sessionService.ListSessionsAsync(20)).ToList();
            if (sessions.Count == 0)
            {
                Dashboard = Dashboard with { StatusMessage = "No saved sessions found." };
                return null;
            }
            return sessions;
        }
        catch (Exception ex)
        {
            Dashboard = Dashboard with { StatusMessage = $"Load failed: {ex.Message}" };
            return null;
        }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        await _sessionService.DeleteSessionAsync(sessionId);
    }

    public async Task<IReadOnlyList<SavedInvestigationSession>> RefreshSessionListAsync()
    {
        return (await _sessionService.ListSessionsAsync(20)).ToList();
    }

    public async Task RestoreSession(string sessionId)
    {
        var dashboard = Dashboard;
        try
        {
            var loaded = await _sessionService.LoadSessionAsync(sessionId);
            if (loaded is null)
            {
                Dashboard = dashboard with { StatusMessage = "Session data not found." };
                return;
            }

            // Clear existing table before importing
            _addressTableService.ClearAll();

            // Restore entries with full metadata
            foreach (var entry in loaded.Value.Entries)
            {
                var node = new AddressTableNode(entry.Id, entry.Label, false)
                {
                    Address = entry.Address,
                    DataType = entry.DataType,
                    CurrentValue = entry.CurrentValue,
                    Notes = entry.Notes,
                    IsLocked = entry.IsLocked,
                    LockedValue = entry.IsLocked ? entry.CurrentValue : null
                };
                _addressTableService.ImportNodes(new[] { node });
            }

            // Restore chat history if a chat was linked
            if (!string.IsNullOrEmpty(loaded.Value.ChatId))
            {
                _aiOperatorService.SwitchChat(loaded.Value.ChatId);
            }

            Dashboard = dashboard with
            {
                AddressTableNodes = _addressTableService.Roots,
                AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
                StatusMessage = $"Loaded session {sessionId} with {loaded.Value.Entries.Count} address entries."
            };
        }
        catch (Exception ex)
        {
            Dashboard = dashboard with { StatusMessage = $"Load failed: {ex.Message}" };
        }
    }

    // ── Menu Handlers ──

    public void OpenSkillsFolder()
    {
        var userSkillsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite", "skills");
        Directory.CreateDirectory(userSkillsDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = userSkillsDir,
            UseShellExecute = true
        });
    }

    public (bool success, string? errorMessage) ReloadSkills()
    {
        try
        {
            var newClient = CreateChatClient();
            _aiOperatorService.Reconfigure(newClient);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void ReconfigureAiAfterSkillsChange()
    {
        try
        {
            var newClient = CreateChatClient();
            _aiOperatorService.Reconfigure(newClient);
        }
        catch { }
    }

    public void ShowAbout()
    {
        _dialogService.ShowInfo(
            "About CE AI Suite",
            "CE AI Suite\nA Cheat Engine-class desktop application with integrated AI operator.\n\nVersion 0.1.0-alpha");
    }

    // ── Output / Find Results Forwarding ──

    public void AppendOutputLog(string source, string level, string message)
    {
        _outputLog.Append(source, level, message);
    }

    public void PopulateFindResults(IReadOnlyList<FindResultDisplayItem> items, string description)
    {
        _findResultsVm.Populate(items, description);
    }

    // ── Lifecycle ──

    public async Task InitializeAsync()
    {
        Dashboard = await _dashboardService.BuildAsync(_databasePath);
        if (Dashboard is { RunningProcesses: not null })
            _processListVm.SetProcesses(Dashboard.RunningProcesses);
        PopulateProcessCombo();
        _aiOperatorVm.RefreshChatSwitcher();
    }

    public void OnClosing()
    {
        _addressTableVm.StopAutoRefresh();
        _aiOperatorService.SaveCurrentChat();
    }
}
