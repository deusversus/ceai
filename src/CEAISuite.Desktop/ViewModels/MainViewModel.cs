using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CEAISuite.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Desktop.ViewModels;

/// <summary>
/// Top-level ViewModel for MainWindow. Owns orchestration logic, session management,
/// process attach/detach, undo/redo, AI configuration, and menu-bar commands.
/// UI-specific code (AvalonDock layout, theme, density, drag-drop, WndProc) stays in MainWindow.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly EventSubscriptions _subs = new();
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
    private readonly ScannerViewModel _scannerVm;
    private readonly ProcessWatchdogService _watchdog;
    private readonly IScreenCaptureEngine _screenCaptureEngine;
    private readonly ScriptGenerationService _scriptGenerationService;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<MainViewModel> _logger;

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
        FindResultsViewModel findResultsVm,
        ScannerViewModel scannerVm,
        ProcessWatchdogService watchdog,
        IScreenCaptureEngine screenCaptureEngine,
        ScriptGenerationService scriptGenerationService,
        IDispatcherService dispatcher,
        ILogger<MainViewModel> logger)
    {
        _logger = logger;
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite",
            "workspace.db");

        _dashboardService = dashboardService;
        _addressTableService = addressTableService;
        // Wire AddressTableService diagnostic log to Output panel
        addressTableService.DiagnosticLog = (source, level, msg) =>
            dispatcher.InvokeAsync(() => outputLog.Append(source, level, msg));
        _sessionService = sessionService;
        _breakpointService = breakpointService;
        _aiOperatorService = aiOperatorService;
        _appSettingsService = appSettingsService;
        _patchUndoService = patchUndoService;
        _engineFacade = engineFacade;
        // Wire engine diagnostic trace to Output panel (marshal to UI thread — Trace fires from Task.Run)
        if (engineFacade is CEAISuite.Engine.Windows.WindowsEngineFacade winFacade)
            winFacade.DiagnosticTrace = msg =>
                dispatcher.InvokeAsync(() => outputLog.Append("Engine", "Debug", msg));
        _processContext = processContext;
        _outputLog = outputLog;
        _dialogService = dialogService;
        _scanService = scanService;

        _addressTableVm = addressTableVm;
        _inspectionVm = inspectionVm;
        _processListVm = processListVm;
        _aiOperatorVm = aiOperatorVm;
        _findResultsVm = findResultsVm;
        _scannerVm = scannerVm;
        _watchdog = watchdog;
        // Wire watchdog diagnostic log to Output panel
        watchdog.DiagnosticLog = (source, level, msg) =>
            dispatcher.InvokeAsync(() => outputLog.Append(source, level, msg));
        _screenCaptureEngine = screenCaptureEngine;
        _scriptGenerationService = scriptGenerationService;
        _dispatcher = dispatcher;

        // Sync toolbar combo selection when process attach/detach state changes
        _subs.Subscribe(h => _processContext.ProcessChanged += h, h => _processContext.ProcessChanged -= h, OnProcessContextChanged);

        // Phase 6: Status bar — token usage updates when AI status changes
        _subs.Subscribe<string>(h => _aiOperatorService.StatusChanged += h, h => _aiOperatorService.StatusChanged -= h, OnAiStatusChanged);

        // Phase 6: Status bar — scan status
        System.ComponentModel.PropertyChangedEventHandler scanHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(ScannerViewModel.ScanStatus))
                StatusBarScanText = _scannerVm.ScanStatus ?? "";
        };
        _subs.Add(() => _scannerVm.PropertyChanged += scanHandler, () => _scannerVm.PropertyChanged -= scanHandler);

        // Phase 6: Status bar — watchdog rollback alerts
        _subs.Add(() => _watchdog.OnAutoRollback += OnWatchdogRollback, () => _watchdog.OnAutoRollback -= OnWatchdogRollback);

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
            _logger.LogWarning(ex, "AI provider init failed");
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
    private string _statusBarTokenText = "";

    [ObservableProperty]
    private string _statusBarScanText = "";

    [ObservableProperty]
    private string _statusBarWatchdogText = "";

    [ObservableProperty]
    private ObservableCollection<ProcessComboItem> _processComboItems = new();

    [ObservableProperty]
    private ProcessComboItem? _selectedProcessComboItem;

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
            sb.AppendLine(CultureInfo.InvariantCulture, $"Attached: {p.ProcessName} (PID {p.ProcessId}, {p.Architecture}, {p.Modules.Count} modules)");
        else
            sb.AppendLine("No process attached.");

        var roots = _addressTableService.Roots;
        int total = 0, frozen = 0, scripts = 0;
        CountNodesRecursive(roots, ref total, ref frozen, ref scripts);
        sb.AppendLine(CultureInfo.InvariantCulture, $"Address table: {total} entries, {frozen} frozen, {scripts} scripts");

        if (_scanService.LastScanResults is { } scan)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Active scan: {scan.Results.Count:N0} results ({scan.Constraints.DataType})");

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
            _logger.LogWarning(ex, "AI provider reconfigure failed");
            _aiOperatorService.Reconfigure(null);
        }
    }

    // ── Process Attach / Detach ──

    private int _isAttaching; // guard against concurrent attach/detach (atomic)

    public async Task InspectSelectedProcessAsync()
    {
        if (Interlocked.CompareExchange(ref _isAttaching, 1, 0) != 0)
        {
            _outputLog.Append("Attach", "Warning", "InspectSelectedProcessAsync: re-entrancy guard blocked call (already attaching/detaching).");
            return;
        }

        var dashboard = Dashboard;
        var selectedProcess = _processListVm.SelectedProcess;
        if (selectedProcess is null)
        {
            _outputLog.Append("Attach", "Warning", "InspectSelectedProcessAsync: no process selected.");
            Dashboard = dashboard with { StatusMessage = "Select a process to inspect." };
            Interlocked.Exchange(ref _isAttaching, 0);
            return;
        }

        _outputLog.Append("Attach", "Info", $"InspectSelectedProcessAsync: starting attach to {selectedProcess.Name} (PID {selectedProcess.Id})...");

        try
        {
            // Detach from current process first if switching to a different one
            if (dashboard.CurrentInspection is not null
                && dashboard.CurrentInspection.ProcessId != selectedProcess.Id)
            {
                _outputLog.Append("Attach", "Info",
                    $"Detaching from previous process PID {dashboard.CurrentInspection.ProcessId} before attaching to PID {selectedProcess.Id}.");
                _dashboardService.DetachProcess();
                _processContext.Detach();
                UiActionRequested?.Invoke("DetachCleanup", null);
                _outputLog.Append("Attach", "Info", "Previous process detached successfully.");
            }

            _outputLog.Append("Attach", "Info", $"Calling InspectProcessAsync(PID {selectedProcess.Id})...");
            var inspection = await _dashboardService.InspectProcessAsync(selectedProcess.Id);
            _outputLog.Append("Attach", "Info",
                $"InspectProcessAsync succeeded: {inspection.ProcessName} — {inspection.Modules.Count} modules, status=\"{inspection.StatusMessage}\"");
            Dashboard = dashboard with
            {
                CurrentInspection = inspection,
                StatusMessage = inspection.StatusMessage
            };

            // Update InspectionViewModel and process context
            _inspectionVm.SetInspection(inspection);
            _processContext.Attach(inspection);
            _outputLog.Append("Attach", "Info", "ProcessContext.Attach() done, ProcessChanged event fired.");

            // Set process context for pointer chain resolution
            // NOTE: InspectProcessAsync already called AttachAsync internally,
            // so we only call again here for the module list needed by AddressTableService.
            try
            {
                _outputLog.Append("Attach", "Info", $"Calling AttachAsync(PID {selectedProcess.Id}) for module context...");
                var attachment = await _engineFacade.AttachAsync(selectedProcess.Id);
                _outputLog.Append("Attach", "Info",
                    $"AttachAsync returned {attachment.Modules.Count} modules for {attachment.ProcessName}.");
                _addressTableService.SetProcessContext(
                    attachment.Modules,
                    selectedProcess.Architecture == "x86");
                _outputLog.Append("Attach", "Info", "AddressTableService.SetProcessContext() done.");
            }
            catch (Exception ex)
            {
                _outputLog.Append("Attach", "Warning",
                    $"Module enumeration failed for {selectedProcess.Name}: {ex.GetType().Name}: {ex.Message}. Address table resolution may be limited.");
            }

            // Start auto-refresh timer for address table values
            StartAutoRefresh();
            _outputLog.Append("Attach", "Info", "Auto-refresh started. Attach complete.");

            // Auto-open Memory Browser tab if enabled
            if (_appSettingsService.Settings.AutoOpenMemoryBrowser)
            {
                UiActionRequested?.Invoke("OpenMemoryBrowser", selectedProcess);
            }
        }
        catch (Exception exception)
        {
            _outputLog.Append("Attach", "Error",
                $"InspectSelectedProcessAsync FAILED: {exception.GetType().Name}: {exception.Message}");
            if (exception.InnerException is not null)
                _outputLog.Append("Attach", "Error", $"  Inner: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            _outputLog.Append("Attach", "Error", $"  Stack: {exception.StackTrace}");
            Dashboard = dashboard with { StatusMessage = exception.Message };
        }
        finally
        {
            Interlocked.Exchange(ref _isAttaching, 0);
            _outputLog.Append("Attach", "Info", "InspectSelectedProcessAsync: re-entrancy guard released.");
        }
    }

    private void OnProcessContextChanged()
    {
        if (_processContext.AttachedProcessId is { } pid)
        {
            // Select the matching item in the toolbar combo
            var match = ProcessComboItems.FirstOrDefault(c => c.Pid == pid);
            SelectedProcessComboItem = match;
            StatusBarProcessText = $"Attached: {_processContext.AttachedProcessName} ({pid})";
        }
        else
        {
            SelectedProcessComboItem = null;
            StatusBarProcessText = "No process attached";
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
        if (Interlocked.CompareExchange(ref _isAttaching, 1, 0) != 0)
        {
            _outputLog.Append("Detach", "Warning", "DetachProcessAsync: re-entrancy guard blocked call (already attaching/detaching).");
            return;
        }

        var dashboard = Dashboard;
        if (dashboard.CurrentInspection is null)
        {
            _outputLog.Append("Detach", "Warning", "DetachProcessAsync: no process attached (CurrentInspection is null).");
            StatusBarCenterText = "No process attached";
            Interlocked.Exchange(ref _isAttaching, 0);
            return;
        }

        var pid = dashboard.CurrentInspection.ProcessId;
        var processName = dashboard.CurrentInspection.ProcessName;
        _outputLog.Append("Detach", "Info", $"DetachProcessAsync: detaching from {processName} (PID {pid})...");

        try
        {
            // 1. Stop auto-refresh so we don't read from a detached process
            _addressTableVm.StopAutoRefresh();
            _outputLog.Append("Detach", "Info", "Auto-refresh stopped.");

            // 2. Remove all breakpoints for this process
            try
            {
                var bps = await _breakpointService.ListBreakpointsAsync(pid);
                _outputLog.Append("Detach", "Info", $"Removing {bps.Count} breakpoints...");
                foreach (var bp in bps)
                    await _breakpointService.RemoveBreakpointAsync(pid, bp.Id);
            }
            catch (Exception bpEx)
            {
                _outputLog.Append("Detach", "Warning", $"Breakpoint cleanup failed (best-effort): {bpEx.GetType().Name}: {bpEx.Message}");
            }

            // 3. Detach engine facade + clear dashboard state
            _dashboardService.DetachProcess();
            _processContext.Detach();
            _outputLog.Append("Detach", "Info", "Engine facade detached, ProcessContext cleared.");

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
            _outputLog.Append("Detach", "Info", $"Detach from {processName} (PID {pid}) complete.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("Detach", "Error", $"DetachProcessAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            StatusBarCenterText = $"Detach error: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _isAttaching, 0);
            _outputLog.Append("Detach", "Info", "DetachProcessAsync: re-entrancy guard released.");
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
                catch (Exception ex) { _outputLog.Append("EmergencyStop", "Warning", $"Breakpoint force-detach failed: {ex.Message}"); }
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
            _outputLog.Append("Processes", "Info", $"Process dropdown refreshed: {ProcessComboItems.Count} processes.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("Processes", "Error", $"Process dropdown refresh failed: {ex.Message}");
        }
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

    public static void OpenSkillsFolder()
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
        catch (Exception ex) { _logger.LogWarning(ex, "AI reconfigure after skills change failed"); }
    }

    public void ShowAbout()
    {
        var version = typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        _dialogService.ShowInfo(
            "About CE AI Suite",
            $"CE AI Suite\nA Cheat Engine-class desktop application with integrated AI operator.\n\nVersion {version}");
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

    // ── Phase 6: Status Bar Helpers ──

    private void OnAiStatusChanged(string _)
    {
        var budget = _aiOperatorService.TokenBudget;
        if (budget.TotalRequests > 0)
        {
            var text = $"${budget.EstimatedCostUsd:F4} | {FormatTokenCount(budget.TotalInputTokens)}↑ {FormatTokenCount(budget.TotalOutputTokens)}↓";
            _dispatcher.Invoke(() => StatusBarTokenText = text);
        }
    }

    private void OnWatchdogRollback(WatchdogRollbackEvent evt)
    {
        _dispatcher.Invoke(() => StatusBarWatchdogText = $"⚠ Rollback 0x{evt.Address:X}");
        // Auto-clear after 5 seconds
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            _dispatcher.Invoke(() => StatusBarWatchdogText = "");
        });
    }

    private static string FormatTokenCount(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M",
        >= 1_000 => $"{tokens / 1_000.0:F1}K",
        _ => tokens.ToString(CultureInfo.InvariantCulture)
    };

    // ── Phase 6: Screenshot Capture & Report Export ──

    public async Task CaptureScreenshotAsync()
    {
        if (Dashboard.CurrentInspection is null)
        {
            StatusBarCenterText = "Attach to a process first.";
            return;
        }

        var pid = Dashboard.CurrentInspection.ProcessId;
        var result = await _screenCaptureEngine.CaptureWindowAsync(pid);
        if (result is null)
        {
            StatusBarCenterText = "Screenshot failed — window may be minimized.";
            return;
        }

        UiActionRequested?.Invoke("SaveScreenshot", result);
    }

    public Task ExportReportAsync()
    {
        if (Dashboard.CurrentInspection is null)
        {
            StatusBarCenterText = "Attach to a process first.";
            return Task.CompletedTask;
        }

        var inspection = Dashboard.CurrentInspection;
        var markdown = ScriptGenerationService.SummarizeInvestigation(
            inspection.ProcessName,
            inspection.ProcessId,
            _addressTableService.Entries.ToList(),
            _scanService.LastScanResults is not null
                ? _scanService.LastScanResults.Results.Take(10).Select(r =>
                    new ScanResultOverview($"0x{r.Address:X}", r.CurrentValue, r.PreviousValue,
                        Convert.ToHexString(r.RawBytes.ToArray()))).ToArray()
                : null,
            Dashboard.Disassembly);

        UiActionRequested?.Invoke("SaveReport", markdown);
        return Task.CompletedTask;
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
        Dispose();
    }

    public void Dispose() => _subs.Dispose();
}
