using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Themes;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;
using CEAISuite.Persistence.Sqlite;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CEAISuite.Desktop;

/// <summary>Display model for AI chat messages in the ItemsControl.</summary>
public sealed class AiChatDisplayItem
{
    public string RoleLabel { get; init; } = "";
    public string Content { get; set; } = "";
    public string Timestamp { get; init; } = "";
    public Brush Background { get; init; } = Brushes.Transparent;
}

/// <summary>Display model for process selection in the command bar ComboBox.</summary>
public sealed class ProcessComboItem
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Label => $"{Name} (PID {Pid})";
    public override string ToString() => Label;
}

/// <summary>Display model for chat history list items.</summary>
public sealed class ChatHistoryDisplayItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string TimeAgo { get; init; } = "";
    public string Preview { get; init; } = "";
    public bool IsCurrent { get; init; }
}

/// <summary>Display model for attachment chips in the chat input.</summary>
public sealed class AttachmentChip
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = "Pasted";
    public string Preview { get; init; } = "";
    public string FullText { get; init; } = "";
}

public partial class MainWindow : Window
{
    private readonly string _databasePath;
    private readonly WorkspaceDashboardService _dashboardService;
    private readonly ScanService _scanService;
    private readonly AddressTableService _addressTableService;
    private readonly DisassemblyService _disassemblyService;
    private readonly AiOperatorService _aiOperatorService;
    private readonly ScriptGenerationService _scriptGenerationService;
    private readonly AddressTableExportService _addressTableExportService;
    private readonly SessionService _sessionService;
    private readonly BreakpointService _breakpointService;
    private readonly IAutoAssemblerEngine? _autoAssemblerEngine;
    private readonly IEngineFacade _engineFacade;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly PatchUndoService _patchUndoService;
    private readonly IMemoryProtectionEngine _memoryProtectionEngine;
    private readonly MemorySnapshotService _snapshotService;
    private readonly PointerRescanService _pointerRescanService;
    private readonly AppSettingsService _appSettingsService;
    private readonly List<AttachmentChip> _attachments = new();
    private System.Windows.Threading.DispatcherTimer? _refreshTimer;

    private static readonly string LayoutFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CEAISuite", "layout.xml");

    public MainWindow()
    {
        InitializeComponent();
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite",
            "workspace.db");
        var engineFacade = new WindowsEngineFacade();
        _engineFacade = engineFacade;
        _dashboardService = new WorkspaceDashboardService(
            engineFacade,
            new SqliteInvestigationSessionRepository(_databasePath));
        _scanService = new ScanService(new WindowsScanEngine());
        _addressTableService = new AddressTableService(engineFacade);
        _disassemblyService = new DisassemblyService(new WindowsDisassemblyEngine());
        _scriptGenerationService = new ScriptGenerationService();
        _addressTableExportService = new AddressTableExportService();
        _sessionService = new SessionService(new SqliteInvestigationSessionRepository(_databasePath));
        _breakpointService = new BreakpointService(new WindowsBreakpointEngine());
        _autoAssemblerEngine = new WindowsAutoAssemblerEngine();
        _hotkeyService = new GlobalHotkeyService();
        _patchUndoService = new PatchUndoService(engineFacade);
        _memoryProtectionEngine = new WindowsMemoryProtectionEngine();
        _snapshotService = new MemorySnapshotService(engineFacade);
        _pointerRescanService = new PointerRescanService(engineFacade);

        _appSettingsService = new AppSettingsService();
        _appSettingsService.Load();

        // Apply saved theme
        var savedTheme = Enum.TryParse<AppTheme>(_appSettingsService.Settings.Theme, true, out var theme)
            ? theme : AppTheme.System;
        ThemeManager.ApplyTheme(savedTheme);
        ApplyDockTheme(ThemeManager.ResolvedTheme);
        ThemeManager.ThemeChanged += ApplyDockTheme;

        // Restore saved panel layout (must happen after InitializeComponent + theme)
        RestoreLayout();

        // Apply saved density preset (after layout restore so it can override visibility)
        ApplyDensityPreset(_appSettingsService.Settings.DensityPreset ?? "Balanced");

        // Wire up AI operator with dynamic context injection
        var signatureService = new SignatureGeneratorService(engineFacade);
        var processWatchdog = new ProcessWatchdogService();
        var operationJournal = new OperationJournal();
        var chatStore = new AiChatStore();
        var tokenLimits = TokenLimits.Resolve(_appSettingsService.Settings);
        var toolFunctions = new AiToolFunctions(engineFacade, _dashboardService, _scanService, _addressTableService, _disassemblyService, _scriptGenerationService, _breakpointService, _autoAssemblerEngine, new WindowsScreenCaptureEngine(), _hotkeyService, _patchUndoService, _sessionService, signatureService, _memoryProtectionEngine, _snapshotService, _pointerRescanService, new WindowsCallStackEngine(), new WindowsCodeCaveEngine(), processWatchdog, operationJournal, chatStore,
            currentChatProvider: () => _aiOperatorService.DisplayHistory,
            tokenLimits: tokenLimits);
        IChatClient? chatClient = null;
        try
        {
            chatClient = CreateChatClient();
        }
        catch (Exception ex)
        {
            // AI provider init failed — app still starts, just without AI
            System.Diagnostics.Debug.WriteLine($"AI provider init failed: {ex.Message}");
        }
        _aiOperatorService = new AiOperatorService(chatClient, toolFunctions, BuildAiContext, chatStore);
        _aiOperatorService.RateLimitSeconds = _appSettingsService.Settings.RateLimitSeconds;
        _aiOperatorService.RateLimitWait = _appSettingsService.Settings.RateLimitWait;
        _aiOperatorService.Limits = tokenLimits;

        // Live status updates from AI agent
        _aiOperatorService.StatusChanged += status =>
        {
            Dispatcher.BeginInvoke(() => AiStatusText.Text = status);
        };

        // Refresh chat switcher when chats change
        _aiOperatorService.ChatListChanged += () =>
        {
            Dispatcher.BeginInvoke(RefreshChatSwitcher);
        };

        // Wire up non-streaming approval handler (shows inline UI, waits for user decision)
        _aiOperatorService.ApprovalRequested += async (toolName, argsStr) =>
        {
            var approval = new AgentStreamEvent.ApprovalRequested(toolName, argsStr);
            await Dispatcher.InvokeAsync(() => ShowInlineApprovalCard(approval));
            return await approval.UserDecision;
        };

        _appSettingsService.SettingsChanged += () =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_refreshTimer is not null)
                    _refreshTimer.Interval = TimeSpan.FromMilliseconds(_appSettingsService.Settings.RefreshIntervalMs);

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
            });
        };

        DataContext = WorkspaceDashboard.CreateLoading();
        Loaded += OnLoaded;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    private IChatClient? CreateChatClient()
    {
        return ChatClientFactory.Create(_appSettingsService.Settings);
    }

    /// <summary>Provides dynamic context to the AI agent before each message.</summary>
    private string BuildAiContext()
    {
        var sb = new System.Text.StringBuilder();
        if (DataContext is WorkspaceDashboard dashboard)
        {
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
        }
        return sb.ToString();
    }

    private static void CountNodesRecursive(
        System.Collections.ObjectModel.ObservableCollection<AddressTableNode> nodes,
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Hook WndProc for global hotkeys
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _hotkeyService.SetWindowHandle(hwnd);
        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        DataContext = await _dashboardService.BuildAsync(_databasePath);
        PopulateProcessCombo();
        RefreshChatSwitcher();
        // Init search box placeholder
        ChatSearchBox.Text = (string)ChatSearchBox.Tag;
        ChatSearchBox.Foreground = FindThemeBrush("SecondaryForeground");

        // Wire up chat input placeholder and paste handler
        ModelSelectorText.Text = _appSettingsService.Settings.Model;
        DataObject.AddPastingHandler(AiChatInputTextBox, OnChatInputPaste);
        AiChatInputTextBox.TextChanged += (_, _) =>
        {
            AiChatPlaceholder.Visibility = string.IsNullOrEmpty(AiChatInputTextBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            handled = _hotkeyService.HandleHotkeyMessage(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveLayout();
        _refreshTimer?.Stop();
        _hotkeyService.Dispose();
        _aiOperatorService.SaveCurrentChat();
        base.OnClosed(e);
    }

    private async void RefreshProcessList(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;
        try
        {
            var updated = await _dashboardService.BuildAsync(_databasePath);
            DataContext = updated with
            {
                CurrentInspection = dashboard.CurrentInspection,
                ScanResults = dashboard.ScanResults,
                ScanStatus = dashboard.ScanStatus,
                ScanDetails = dashboard.ScanDetails,
                AddressTableNodes = _addressTableService.Roots,
                AddressTableStatus = dashboard.AddressTableStatus,
                Disassembly = dashboard.Disassembly,
                BreakpointStatus = dashboard.BreakpointStatus,
                StatusMessage = $"Refreshed: {updated.RunningProcesses.Count} processes found."
            };
            PopulateProcessCombo();
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"Refresh failed: {ex.Message}" };
        }
    }

    private async void InspectSelectedProcess(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard)
        {
            return;
        }

        if (RunningProcessesList.SelectedItem is not RunningProcessOverview selectedProcess)
        {
            DataContext = dashboard with { StatusMessage = "Select a process to inspect." };
            return;
        }

        try
        {
            var inspection = await _dashboardService.InspectProcessAsync(selectedProcess.Id);
            DataContext = dashboard with
            {
                CurrentInspection = inspection,
                StatusMessage = inspection.StatusMessage
            };

            // Set process context for pointer chain resolution
            try
            {
                var attachment = await _engineFacade.AttachAsync(selectedProcess.Id);
                _addressTableService.SetProcessContext(
                    attachment.Modules,
                    selectedProcess.Architecture == "x86");
            }
            catch { /* non-fatal — address resolution will try again during refresh */ }

            // Start auto-refresh timer for address table values
            StartAutoRefresh();

            // Auto-open Memory Browser tab if enabled
            if (_appSettingsService.Settings.AutoOpenMemoryBrowser)
            {
                MemoryBrowserTab.AttachProcess(_engineFacade, selectedProcess.Id, selectedProcess.Name);
                ActivateDocument("memoryBrowser");
            }
        }
        catch (Exception exception)
        {
            DataContext = dashboard with { StatusMessage = exception.Message };
        }
    }

    private void StartAutoRefresh()
    {
        _refreshTimer?.Stop();
        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null) return;
            if (_addressTableService.Roots.Count == 0) return;
            try
            {
                _refreshTimer!.Stop(); // pause during refresh
                await _addressTableService.RefreshAllAsync(dashboard.CurrentInspection.ProcessId);
                DataContext = dashboard with
                {
                    AddressTableNodes = _addressTableService.Roots,
                    AddressTableStatus = $"{_addressTableService.Entries.Count} entries (live)"
                };
            }
            catch { /* non-fatal */ }
            finally
            {
                _refreshTimer?.Start(); // resume
            }
        };
        _refreshTimer.Start();
    }

    private async void ReadAddress(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            return;
        }

        try
        {
            var dataType = GetSelectedDataType();
            var probe = await _dashboardService.ReadAddressAsync(
                dashboard.CurrentInspection.ProcessId,
                AddressTextBox.Text,
                dataType);

            DataContext = dashboard with
            {
                CurrentInspection = dashboard.CurrentInspection with
                {
                    ManualProbe = probe,
                    LastWriteMessage = null,
                    StatusMessage = $"Read {probe.DataType} from {probe.Address}."
                },
                StatusMessage = $"Read {probe.DataType} from {probe.Address}."
            };
        }
        catch (Exception exception)
        {
            DataContext = dashboard with { StatusMessage = exception.Message };
        }
    }

    private async void WriteAddress(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            $"Write {ValueTextBox.Text} as {GetSelectedDataType()} to {AddressTextBox.Text} in process {dashboard.CurrentInspection.ProcessName}?",
            "Confirm memory write",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var dataType = GetSelectedDataType();
            var message = await _dashboardService.WriteAddressAsync(
                dashboard.CurrentInspection.ProcessId,
                AddressTextBox.Text,
                dataType,
                ValueTextBox.Text);

            DataContext = dashboard with
            {
                CurrentInspection = dashboard.CurrentInspection with
                {
                    LastWriteMessage = message,
                    StatusMessage = message
                },
                StatusMessage = message
            };
        }
        catch (Exception exception)
        {
            DataContext = dashboard with { StatusMessage = exception.Message };
        }
    }

    private MemoryDataType GetSelectedDataType() =>
        TypeComboBox.SelectedItem is MemoryDataType dataType ? dataType : MemoryDataType.Int32;

    private MemoryDataType GetScanDataType() =>
        ScanDataTypeComboBox.SelectedItem is MemoryDataType dataType ? dataType : MemoryDataType.Int32;

    private ScanType GetScanType() =>
        ScanTypeComboBox.SelectedItem is ScanType scanType ? scanType : ScanType.ExactValue;

    private void OnTypeComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        TypeComboBox.ItemsSource = Enum.GetValues<MemoryDataType>();
        TypeComboBox.SelectedItem = MemoryDataType.Int32;
    }

    private void OnScanTypeComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        ScanTypeComboBox.ItemsSource = Enum.GetValues<ScanType>();
        ScanTypeComboBox.SelectedItem = ScanType.ExactValue;
    }

    private void OnScanDataTypeComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        ScanDataTypeComboBox.ItemsSource = Enum.GetValues<MemoryDataType>();
        ScanDataTypeComboBox.SelectedItem = MemoryDataType.Int32;
    }

    private async void StartNewScan(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            if (DataContext is WorkspaceDashboard db)
            {
                DataContext = db with { StatusMessage = "Select and inspect a process before scanning." };
            }

            return;
        }

        try
        {
            _scanService.ResetScan();
            DataContext = dashboard with { ScanStatus = "Scanning...", ScanDetails = "Initial scan in progress.", ScanResults = null };

            var overview = await _scanService.StartScanAsync(
                dashboard.CurrentInspection.ProcessId,
                GetScanDataType(),
                GetScanType(),
                ScanValueTextBox.Text);

            DataContext = (DataContext as WorkspaceDashboard ?? dashboard) with
            {
                ScanResults = overview.Results,
                ScanStatus = $"{overview.ResultCount:N0} results found",
                ScanDetails = $"Scanned {overview.TotalRegionsScanned} regions ({overview.TotalBytesScanned}), type={overview.DataType}, scan={overview.ScanType}",
                StatusMessage = $"Scan complete: {overview.ResultCount:N0} results across {overview.TotalRegionsScanned} regions."
            };
        }
        catch (Exception exception)
        {
            DataContext = dashboard with { StatusMessage = $"Scan failed: {exception.Message}", ScanStatus = "Scan failed" };
        }
    }

    private async void RefineScan(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            return;
        }

        if (_scanService.LastScanResults is null)
        {
            DataContext = dashboard with { StatusMessage = "No active scan to refine. Start a new scan first." };
            return;
        }

        try
        {
            DataContext = dashboard with { ScanStatus = "Refining...", ScanDetails = "Next scan in progress." };

            var overview = await _scanService.RefineScanAsync(
                GetScanType(),
                ScanValueTextBox.Text);

            DataContext = (DataContext as WorkspaceDashboard ?? dashboard) with
            {
                ScanResults = overview.Results,
                ScanStatus = $"{overview.ResultCount:N0} results remaining",
                ScanDetails = $"Refined with {overview.ScanType}, type={overview.DataType}",
                StatusMessage = $"Refinement complete: {overview.ResultCount:N0} results remaining."
            };
        }
        catch (Exception exception)
        {
            DataContext = dashboard with { StatusMessage = $"Refinement failed: {exception.Message}" };
        }
    }

    private void ResetScan(object sender, RoutedEventArgs e)
    {
        _scanService.ResetScan();
        if (DataContext is WorkspaceDashboard dashboard)
        {
            DataContext = dashboard with
            {
                ScanResults = null,
                ScanStatus = null,
                ScanDetails = null,
                StatusMessage = "Scan reset. Ready for a new scan."
            };
        }
    }

    private void AddSelectedScanResultToTable(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard)
        {
            return;
        }

        if (ScanResultsList.SelectedItem is not ScanResultOverview selected)
        {
            DataContext = dashboard with { StatusMessage = "Select a scan result to add to the address table." };
            return;
        }

        _addressTableService.AddFromScanResult(selected, GetScanDataType());
        DataContext = dashboard with
        {
            AddressTableNodes = _addressTableService.Roots,
            AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
            StatusMessage = $"Added {selected.Address} to address table."
        };
    }

    private void AddManualToTable(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection?.ManualProbe is null)
        {
            return;
        }

        var probe = dashboard.CurrentInspection.ManualProbe;
        _addressTableService.AddEntry(probe.Address, GetSelectedDataType(), probe.DisplayValue);
        DataContext = dashboard with
        {
            AddressTableNodes = _addressTableService.Roots,
            AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
            StatusMessage = $"Added {probe.Address} to address table."
        };
    }

    private async void RefreshAddressTable(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            return;
        }

        try
        {
            await _addressTableService.RefreshAllAsync(dashboard.CurrentInspection.ProcessId);
            DataContext = dashboard with
            {
                AddressTableNodes = _addressTableService.Roots,
                AddressTableStatus = $"{_addressTableService.Entries.Count} entries (refreshed)",
                StatusMessage = "Address table refreshed."
            };
        }
        catch (Exception exception)
        {
            DataContext = dashboard with { StatusMessage = $"Refresh failed: {exception.Message}" };
        }
    }

    private void RemoveSelectedAddress(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard)
        {
            return;
        }

        if (AddressTableTree.SelectedItem is not AddressTableNode selected)
        {
            DataContext = dashboard with { StatusMessage = "Select an address to remove." };
            return;
        }

        _addressTableService.RemoveEntry(selected.Id);
        DataContext = dashboard with
        {
            AddressTableNodes = _addressTableService.Roots,
            AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
            StatusMessage = $"Removed {selected.Label} from address table."
        };
    }

    private void ToggleLockAddress(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard)
        {
            return;
        }

        if (AddressTableTree.SelectedItem is not AddressTableNode selected)
        {
            DataContext = dashboard with { StatusMessage = "Select an address to toggle lock." };
            return;
        }

        _addressTableService.ToggleLock(selected.Id);
        DataContext = dashboard with
        {
            AddressTableNodes = _addressTableService.Roots,
            StatusMessage = $"Toggled lock on {selected.Label}."
        };
    }

    private async void DisassembleAtAddress(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            if (DataContext is WorkspaceDashboard db)
            {
                DataContext = db with { StatusMessage = "Inspect a process before disassembling." };
            }

            return;
        }

        try
        {
            var overview = await _disassemblyService.DisassembleAtAsync(
                dashboard.CurrentInspection.ProcessId,
                DisasmAddressTextBox.Text);

            DataContext = dashboard with
            {
                Disassembly = overview,
                StatusMessage = overview.Summary
            };
        }
        catch (Exception exception)
        {
            DataContext = dashboard with { StatusMessage = $"Disassembly failed: {exception.Message}" };
        }
    }

    // ─── AI Operator ───────────────────────────────────────────────────────

    private async void SendAiMessage(object sender, RoutedEventArgs e)
    {
        var inputText = AiChatInputTextBox.Text?.Trim();

        // Build message: attachments prepended as context blocks
        var parts = new List<string>();
        foreach (var att in _attachments)
        {
            parts.Add($"<context label=\"{att.Label}\">\n{att.FullText}\n</context>");
        }
        if (!string.IsNullOrEmpty(inputText))
            parts.Add(inputText);

        var message = string.Join("\n\n", parts);
        if (string.IsNullOrEmpty(message)) return;

        AiChatInputTextBox.Text = "";
        _attachments.Clear();
        RefreshAttachmentChips();

        // Show user message immediately before starting AI work
        _aiOperatorService.AddUserMessageToHistory(message);
        RefreshAiChatDisplay();

        try
        {
            if (_appSettingsService.Settings.UseStreaming)
            {
                var reader = _aiOperatorService.SendMessageStreamingAsync(message);

                // Add a placeholder for the streaming response
                var streamItem = new AiChatDisplayItem
                {
                    RoleLabel = "AI Operator",
                    Content = "",
                    Timestamp = DateTime.Now.ToString("h:mm tt"),
                    Background = FindThemeBrush("ChatAiBubble")
                };

                Dispatcher.Invoke(() =>
                {
                    if (AiChatList.ItemsSource is List<AiChatDisplayItem> items)
                    {
                        items.Add(streamItem);
                        AiChatList.Items.Refresh();
                        AiChatScrollViewer.ScrollToEnd();
                    }
                });

                await foreach (var evt in reader.ReadAllAsync())
                {
                    switch (evt)
                    {
                        case AgentStreamEvent.TextDelta delta:
                            streamItem.Content += delta.Text;
                            Dispatcher.Invoke(() =>
                            {
                                AiChatList.Items.Refresh();
                                AiChatScrollViewer.ScrollToEnd();
                            });
                            break;

                        case AgentStreamEvent.ToolCallStarted tool:
                            Dispatcher.Invoke(() => AiStatusText.Text = $"Tool: {tool.ToolName}");
                            break;

                        case AgentStreamEvent.ApprovalRequested approval:
                            await Dispatcher.InvokeAsync(() =>
                            {
                                ShowInlineApprovalCard(approval);
                            });
                            break;

                        case AgentStreamEvent.Error err:
                            streamItem.Content = err.Message;
                            Dispatcher.Invoke(() => AiChatList.Items.Refresh());
                            break;
                    }
                }
            }
            else
            {
                // Non-streaming: wait for full response
                Dispatcher.Invoke(() => AiStatusText.Text = "Thinking...");
                var response = await Task.Run(() => _aiOperatorService.SendMessageAsync(message));
                // RefreshAiChatDisplay in finally block will show the response
            }
        }
        catch (Exception ex)
        {
            if (DataContext is WorkspaceDashboard db)
                DataContext = db with { StatusMessage = $"AI error: {ex.Message}" };
        }
        finally
        {
            if (AiStatusText.Text.StartsWith("Thinking") || AiStatusText.Text.StartsWith("Tool:"))
                AiStatusText.Text = _aiOperatorService.IsConfigured ? "Ready" : "Not configured — open Settings to add API key";
            RefreshAiChatDisplay();
            RefreshChatSwitcher();
            if (DataContext is WorkspaceDashboard dashboard)
            {
                DataContext = dashboard with
                {
                    AddressTableNodes = _addressTableService.Roots,
                    AddressTableStatus = $"{_addressTableService.Entries.Count} entries"
                };
            }
        }
    }

    private void OnAiChatPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            SendAiMessage(sender, e);
            e.Handled = true;
        }
    }

    private void ClearAiChat(object sender, RoutedEventArgs e)
    {
        _aiOperatorService.ClearHistory();
        RefreshAiChatDisplay();
    }

    private void CopyAiMessage(object sender, RoutedEventArgs e)
    {
        // ContextMenu is in a separate visual tree — get DataContext from PlacementTarget
        if (sender is MenuItem mi
            && mi.Parent is ContextMenu ctx
            && ctx.PlacementTarget is FrameworkElement fe
            && fe.DataContext is AiChatDisplayItem item)
        {
            try { Clipboard.SetText(item.Content); }
            catch { /* clipboard locked */ }
        }
    }

    private void ExportAiChat(object sender, RoutedEventArgs e)
    {
        var markdown = _aiOperatorService.ExportChatToMarkdown();
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Split('\n').Length <= 5)
        {
            MessageBox.Show("No messages to export.", "Export Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Chat History",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            DefaultExt = ".md",
            FileName = $"{_aiOperatorService.CurrentChatTitle.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd}"
        };

        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, markdown);
            AiStatusText.Text = $"Exported to {Path.GetFileName(dlg.FileName)}";
        }
    }

    private bool _suppressChatSwitch;
    private List<ChatHistoryDisplayItem> _allChatItems = new();

    private static string FormatTimeAgo(DateTimeOffset dt)
    {
        var diff = DateTimeOffset.UtcNow - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return dt.ToLocalTime().ToString("MMM d");
    }

    private void RefreshChatSwitcher()
    {
        _suppressChatSwitch = true;
        var chats = _aiOperatorService.ListChats();
        var currentId = _aiOperatorService.CurrentChatId;

        _allChatItems = chats.Select(c => new ChatHistoryDisplayItem
        {
            Id = c.Id,
            Title = c.Title,
            TimeAgo = FormatTimeAgo(c.UpdatedAt),
            Preview = c.Messages.LastOrDefault(m => m.Role == "assistant")?.Content is string last
                ? (last.Length > 80 ? last[..80] + "…" : last)
                : c.Messages.Count > 0 ? $"{c.Messages.Count} messages" : "Empty",
            IsCurrent = c.Id == currentId
        }).ToList();

        // Insert current unsaved chat at top if not already listed
        if (!_allChatItems.Any(c => c.Id == currentId))
        {
            _allChatItems.Insert(0, new ChatHistoryDisplayItem
            {
                Id = currentId,
                Title = _aiOperatorService.CurrentChatTitle,
                TimeAgo = "now",
                Preview = _aiOperatorService.DisplayHistory.Count > 0
                    ? $"{_aiOperatorService.DisplayHistory.Count} messages"
                    : "Active chat",
                IsCurrent = true
            });
        }

        ChatHistoryList.ItemsSource = _allChatItems;

        // Select current
        var currentItem = _allChatItems.FirstOrDefault(c => c.Id == currentId);
        if (currentItem is not null)
            ChatHistoryList.SelectedItem = currentItem;

        _suppressChatSwitch = false;
    }

    private void ToggleChatHistory(object sender, RoutedEventArgs e)
    {
        bool show = ChatHistoryToggle.IsChecked == true;
        ChatHistoryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) RefreshChatSwitcher();
    }

    private void ChatHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only switch on explicit double-click or context menu, not on selection change
    }

    private void ChatHistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_suppressChatSwitch) return;
        if (ChatHistoryList.SelectedItem is ChatHistoryDisplayItem selected &&
            selected.Id != _aiOperatorService.CurrentChatId)
        {
            _aiOperatorService.SwitchChat(selected.Id);
            RefreshAiChatDisplay();
            RefreshChatSwitcher();
        }
    }

    private void NewAiChat(object sender, RoutedEventArgs e)
    {
        _aiOperatorService.NewChat();
        RefreshAiChatDisplay();
        RefreshChatSwitcher();
    }

    private void DeleteAiChat(object sender, RoutedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is ChatHistoryDisplayItem selected)
        {
            var result = MessageBox.Show(
                $"Delete chat \"{selected.Title}\"?",
                "Delete Chat", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _aiOperatorService.DeleteChat(selected.Id);
                RefreshAiChatDisplay();
                RefreshChatSwitcher();
            }
        }
    }

    // Context menu handlers for chat history
    private void ChatCtx_Open(object sender, RoutedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is ChatHistoryDisplayItem selected)
        {
            _aiOperatorService.SwitchChat(selected.Id);
            RefreshAiChatDisplay();
            RefreshChatSwitcher();
        }
    }

    private void ChatCtx_Rename(object sender, RoutedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is not ChatHistoryDisplayItem selected) return;

        // Simple rename dialog
        var dlg = new Window
        {
            Title = "Rename Chat",
            Width = 380, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = FindThemeBrush("SidebarBackground"),
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        var tb = new TextBox
        {
            Text = selected.Title,
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
        };
        tb.SelectAll();
        var btn = new Button { Content = "Rename", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        btn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        tb.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) { dlg.DialogResult = true; dlg.Close(); } };
        sp.Children.Add(tb);
        sp.Children.Add(btn);
        dlg.Content = sp;
        tb.Focus();

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text))
        {
            _aiOperatorService.RenameChat(selected.Id, tb.Text.Trim());
            RefreshAiChatDisplay();
            RefreshChatSwitcher();
        }
    }

    private void ChatCtx_Delete(object sender, RoutedEventArgs e)
    {
        DeleteAiChat(sender, e);
    }

    // Search box placeholder + filtering
    private void ChatSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (ChatSearchBox.Text == (string)ChatSearchBox.Tag)
        {
            ChatSearchBox.Text = "";
            ChatSearchBox.Foreground = FindThemeBrush("PrimaryForeground");
        }
    }

    private void ChatSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ChatSearchBox.Text))
        {
            ChatSearchBox.Text = (string)ChatSearchBox.Tag;
            ChatSearchBox.Foreground = FindThemeBrush("SecondaryForeground");
        }
    }

    private void ChatSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = ChatSearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query) || query == (string)ChatSearchBox.Tag)
        {
            ChatHistoryList.ItemsSource = _allChatItems;
            return;
        }

        var filtered = _allChatItems
            .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.Preview.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        ChatHistoryList.ItemsSource = filtered;
    }

    // Track pending approval events so "Allow All" / "Deny All" can resolve them
    private readonly List<AgentStreamEvent.ApprovalRequested> _pendingApprovals = new();
    // Tools the user has approved for this session (skip future prompts)
    private readonly HashSet<string> _sessionTrustedTools = new(StringComparer.OrdinalIgnoreCase);

    private static Brush FindThemeBrush(string key) =>
        System.Windows.Application.Current.FindResource(key) as Brush ?? Brushes.Transparent;

    /// <summary>
    /// Shows an inline approval card in the chat with Allow/Deny/Allow All buttons.
    /// When the user clicks a button, it resolves the approval event's TaskCompletionSource
    /// so the agent can continue or abort the tool call.
    /// If the tool was already trusted this session, auto-approves immediately.
    /// </summary>
    private void ShowInlineApprovalCard(AgentStreamEvent.ApprovalRequested approval)
    {
        // Auto-approve tools the user already trusted this session
        if (_sessionTrustedTools.Contains(approval.ToolName))
        {
            AiStatusText.Text = $"✓ Auto-approved: {approval.ToolName}";
            approval.Resolve(true);
            return;
        }

        _pendingApprovals.Add(approval);
        AiStatusText.Text = $"⚠ Awaiting approval: {approval.ToolName} ({_pendingApprovals.Count} pending)";

        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 4, 0, 4),
            Background = FindThemeBrush("WarningBackground"),
            BorderBrush = FindThemeBrush("WarningBorder"),
            BorderThickness = new Thickness(1),
            Tag = "approval-card",
        };

        var stack = new StackPanel();

        var header = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = FindThemeBrush("WarningForeground"),
            Text = "⚠ Tool Approval Required",
            Margin = new Thickness(0, 0, 0, 4)
        };
        stack.Children.Add(header);

        var toolInfo = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FindThemeBrush("PrimaryForeground"),
            Text = string.IsNullOrEmpty(approval.Arguments)
                ? approval.ToolName
                : $"{approval.ToolName}({approval.Arguments})"
        };
        stack.Children.Add(toolInfo);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var allowBtn = new Button
        {
            Content = "✓ Allow",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(34, 139, 34)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var allowAllBtn = new Button
        {
            Content = "✓ Allow All",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(26, 110, 26)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Approve this and all future tool calls this session",
        };

        var denyBtn = new Button
        {
            Content = "✗ Deny",
            Padding = new Thickness(12, 4, 12, 4),
            Background = new SolidColorBrush(Color.FromRgb(180, 50, 50)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        void CollapseCard(Border target, string label, bool approved)
        {
            var color = approved
                ? Color.FromRgb(34, 139, 34)
                : Color.FromRgb(180, 50, 50);
            target.Padding = new Thickness(8, 4, 8, 4);
            target.Background = Brushes.Transparent;
            target.BorderBrush = Brushes.Transparent;
            target.BorderThickness = new Thickness(0);
            target.Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(color),
            };
        }

        void ResolveThis(bool approved)
        {
            _pendingApprovals.Remove(approval);
            var label = approved
                ? $"✓ Approved: {approval.ToolName}"
                : $"✗ Denied: {approval.ToolName}";
            CollapseCard(card, label, approved);
            AiStatusText.Text = approved
                ? $"Executing {approval.ToolName}..."
                : $"Denied: {approval.ToolName}";
            approval.Resolve(approved);
        }

        void ResolveAllPending(bool approved)
        {
            foreach (var pending in _pendingApprovals.ToList())
            {
                if (approved) _sessionTrustedTools.Add(pending.ToolName);
                pending.Resolve(approved);
            }
            _pendingApprovals.Clear();

            // Collapse all approval cards
            var suffix = approved ? "Approved" : "Denied";
            foreach (var child in AiChatContainer.Children.OfType<Border>().ToList())
            {
                if (child.Tag as string != "approval-card") continue;
                CollapseCard(child, $"✓ {suffix} (Allow All)", approved);
            }

            AiStatusText.Text = approved
                ? "Executing approved tools..."
                : "Denied all pending tools";
        }

        allowBtn.Click += (_, _) => ResolveThis(true);
        allowAllBtn.Click += (_, _) => ResolveAllPending(true);
        denyBtn.Click += (_, _) => ResolveThis(false);

        buttonPanel.Children.Add(allowBtn);
        buttonPanel.Children.Add(allowAllBtn);
        buttonPanel.Children.Add(denyBtn);
        stack.Children.Add(buttonPanel);
        card.Child = stack;

        AiChatContainer.Children.Add(card);
        AiChatScrollViewer.ScrollToEnd();
    }

    private void RefreshAiChatDisplay()
    {
        var userBrush = FindThemeBrush("ChatUserBubble");
        var aiBrush = FindThemeBrush("ChatAiBubble");

        var items = _aiOperatorService.DisplayHistory.Select(msg => new AiChatDisplayItem
        {
            RoleLabel = msg.Role == "user" ? "You" : "AI Operator",
            Content = msg.Content,
            Timestamp = msg.Timestamp.ToLocalTime().ToString("h:mm tt"),
            Background = msg.Role == "user" ? userBrush : aiBrush
        }).ToList();

        AiChatList.ItemsSource = items;

        // Remove any approval cards that were injected during streaming
        for (int i = AiChatContainer.Children.Count - 1; i >= 0; i--)
        {
            if (AiChatContainer.Children[i] != AiChatList)
                AiChatContainer.Children.RemoveAt(i);
        }

        AiChatScrollViewer.ScrollToEnd();
        AiChatTitleText.Text = _aiOperatorService.CurrentChatTitle;
    }

    // ─── Address Table Export / Import / Trainer ────────────────────────

    private void ExportAddressTable(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;
        var entries = _addressTableService.Entries;
        if (entries.Count == 0)
        {
            DataContext = dashboard with { StatusMessage = "No entries to export." };
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON files|*.json",
            FileName = "address_table.json"
        };

        if (dialog.ShowDialog() == true)
        {
            var json = _addressTableExportService.ExportToJson(entries.ToArray());
            File.WriteAllText(dialog.FileName, json);
            DataContext = dashboard with { StatusMessage = $"Exported {entries.Count} entries to {dialog.FileName}" };
        }
    }

    private void ImportAddressTable(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON files|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var imported = _addressTableExportService.ImportFromJson(json);
                foreach (var entry in imported)
                {
                    _addressTableService.AddEntry(entry.Address, entry.DataType, entry.CurrentValue, entry.Label);
                }

                DataContext = dashboard with
                {
                    AddressTableNodes = _addressTableService.Roots,
                    AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
                    StatusMessage = $"Imported {imported.Count} entries from {Path.GetFileName(dialog.FileName)}"
                };
            }
            catch (Exception ex)
            {
                DataContext = dashboard with { StatusMessage = $"Import failed: {ex.Message}" };
            }
        }
    }

    private void GenerateTrainerScript(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;
        var entries = _addressTableService.Entries;
        var lockedEntries = entries.Where(x => x.IsLocked).ToArray();

        if (lockedEntries.Length == 0)
        {
            DataContext = dashboard with { StatusMessage = "No locked entries to generate trainer from. Lock some addresses first." };
            return;
        }

        var processName = dashboard.CurrentInspection?.ProcessName ?? "Unknown";
        var script = _scriptGenerationService.GenerateTrainerScript(lockedEntries, processName);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "C# files|*.cs",
            FileName = $"Trainer_{processName.Replace(".exe", "")}.cs"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, script);
            DataContext = dashboard with { StatusMessage = $"Trainer script saved to {dialog.FileName}" };
        }
    }

    // ─── Address Table Context Menu & Editing (CE 7.5 style) ─────────

    private AddressTableNode? GetSelectedNode()
        => AddressTableTree.SelectedItem as AddressTableNode;

    private void RefreshAddressTableUI(string? statusMessage = null)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;
        DataContext = dashboard with
        {
            AddressTableNodes = _addressTableService.Roots,
            AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
            StatusMessage = statusMessage ?? dashboard.StatusMessage
        };
    }

    private async void ActiveCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb ||
            cb.DataContext is not AddressTableNode node) return;

        if (node.IsScriptEntry)
        {
            // Actually execute/disable the script via the AA engine
            if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
            {
                node.IsScriptEnabled = false; // revert — can't run without attached process
                if (DataContext is WorkspaceDashboard d)
                    DataContext = d with { StatusMessage = "Attach to a process before toggling scripts." };
                return;
            }

            if (_autoAssemblerEngine is null)
            {
                node.IsScriptEnabled = false;
                return;
            }

            try
            {
                if (node.IsScriptEnabled)
                {
                    var result = await _autoAssemblerEngine.EnableAsync(
                        dashboard.CurrentInspection.ProcessId, node.AssemblerScript!);
                    if (!result.Success)
                    {
                        node.IsScriptEnabled = false;
                        node.ScriptStatus = $"FAILED: {result.Error}";
                    }
                    else
                    {
                        node.ScriptStatus = $"Enabled ({result.Allocations.Count} allocs, {result.Patches.Count} patches)";
                    }
                }
                else
                {
                    var result = await _autoAssemblerEngine.DisableAsync(
                        dashboard.CurrentInspection.ProcessId, node.AssemblerScript!);
                    node.ScriptStatus = result.Success ? "Disabled" : $"Disable failed: {result.Error}";
                }

                DataContext = dashboard with
                {
                    StatusMessage = $"Script '{node.Label}': {node.ScriptStatus}"
                };
            }
            catch (Exception ex)
            {
                node.IsScriptEnabled = false;
                node.ScriptStatus = $"Error: {ex.Message}";
                DataContext = dashboard with { StatusMessage = $"Script error: {ex.Message}" };
            }
        }
        else
        {
            // Value entry: toggle freeze and capture current value
            node.LockedValue = node.IsLocked ? node.CurrentValue : null;
        }
    }

    private void AddressTableTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null) return;

        // Determine which column was clicked via hit-test position
        // CE behavior: double-click description = edit desc, value = edit value, address = edit addr
        // For simplicity: non-group, non-script → edit value; script → view script
        if (node.IsScriptEntry)
        {
            ViewSelectedScript(sender, e);
            return;
        }
        if (node.IsGroup) return;

        // Edit value dialog
        EditNodeValue(node);
    }

    private void EditNodeValue(AddressTableNode node)
    {
        var result = ShowInputDialog("Change Value", "New value:", node.CurrentValue);
        if (result is null) return;

        node.PreviousValue = node.CurrentValue;
        node.CurrentValue = result;
        if (node.IsLocked) node.LockedValue = result;

        // Attempt write if attached
        if (DataContext is WorkspaceDashboard dashboard && dashboard.CurrentInspection is not null)
        {
            try
            {
                var addr = AddressTableService.ParseAddress(node.Address);
                _ = _addressTableService.WriteValueAsync(
                    dashboard.CurrentInspection.ProcessId, node);
            }
            catch { /* best effort */ }
        }

        RefreshAddressTableUI($"Value changed: {node.Label} = {result}");
    }

    private void AddressTableTree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null) return;

        switch (e.Key)
        {
            case System.Windows.Input.Key.Delete:
                CtxDelete(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Space:
                CtxToggleActivate(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F2:
                CtxChangeDescription(sender, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter when !node.IsGroup && !node.IsScriptEntry:
                EditNodeValue(node);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F5:
                RefreshAddressTable(sender, e);
                e.Handled = true;
                break;
        }

        // Ctrl shortcuts
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.C:
                    CtxCopy(sender, e);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.X:
                    CtxCut(sender, e);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.V:
                    CtxPaste(sender, e);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.F:
                    CtxToggleFreeze(sender, e);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Z:
                    _ = PerformUndoAsync();
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Y:
                    _ = PerformRedoAsync();
                    e.Handled = true;
                    break;
            }
        }
    }

    private void CtxToggleActivate(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null) return;
        node.IsActive = !node.IsActive;
        if (!node.IsScriptEntry)
            node.LockedValue = node.IsLocked ? node.CurrentValue : null;
        RefreshAddressTableUI($"{node.Label}: {(node.IsActive ? "Activated" : "Deactivated")}");
    }

    private void CtxChangeDescription(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null) return;
        var result = ShowInputDialog("Change Description", "New description:", node.Label);
        if (result is not null)
        {
            _addressTableService.UpdateLabel(node.Id, result);
            RefreshAddressTableUI($"Renamed to '{result}'");
        }
    }

    private async Task PerformUndoAsync()
    {
        var msg = await _patchUndoService.UndoAsync();
        RefreshAddressTableUI(msg);
    }

    private async Task PerformRedoAsync()
    {
        var msg = await _patchUndoService.RedoAsync();
        RefreshAddressTableUI(msg);
    }

    private void CtxChangeAddress(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null || node.IsGroup || node.IsScriptEntry) return;
        var result = ShowInputDialog("Change Address", "New address (hex):", node.Address);
        if (result is not null)
        {
            node.Address = result;
            RefreshAddressTableUI($"Address changed: {node.Label} → {result}");
        }
    }

    private void CtxChangeValue(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null || node.IsGroup || node.IsScriptEntry) return;
        EditNodeValue(node);
    }

    private void CtxChangeType(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null || node.IsGroup || node.IsScriptEntry) return;

        var typeDialog = new System.Windows.Window
        {
            Title = "Change Type",
            Width = 260,
            Height = 180,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = System.Windows.ResizeMode.NoResize
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Select data type:" });
        var combo = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 6, 0, 8) };
        foreach (var t in Enum.GetValues<MemoryDataType>())
            combo.Items.Add(t.ToString());
        combo.SelectedItem = node.DataType.ToString();
        panel.Children.Add(combo);
        var okBtn = new System.Windows.Controls.Button
        {
            Content = "OK",
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        okBtn.Click += (_, _) => { typeDialog.DialogResult = true; };
        panel.Children.Add(okBtn);
        typeDialog.Content = panel;

        if (typeDialog.ShowDialog() == true && combo.SelectedItem is string selected)
        {
            node.DataType = Enum.Parse<MemoryDataType>(selected);
            RefreshAddressTableUI($"Type changed: {node.Label} → {selected}");
        }
    }

    private void CtxToggleFreeze(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null || node.IsGroup || node.IsScriptEntry) return;
        _addressTableService.ToggleLock(node.Id);
        RefreshAddressTableUI($"{node.Label}: {(node.IsLocked ? "Frozen" : "Unfrozen")}");
    }

    private void CtxShowAsHex(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null || node.IsGroup || node.IsScriptEntry) return;
        // Toggle hex display in notes
        if (node.Notes?.Contains("[HEX]") == true)
        {
            node.Notes = node.Notes.Replace("[HEX] ", "");
        }
        else
        {
            node.Notes = $"[HEX] {node.Notes ?? ""}".Trim();
        }
        RefreshAddressTableUI();
    }

    private async void CtxBrowseMemory(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null || node.IsGroup || node.IsScriptEntry) return;
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null) return;

        var addr = node.ResolvedAddress ?? nuint.Zero;
        if (addr == nuint.Zero)
        {
            try { addr = AddressTableService.ParseAddress(node.Address); } catch { }
        }

        MemoryBrowserTab.AttachProcess(_engineFacade,
            dashboard.CurrentInspection.ProcessId,
            dashboard.CurrentInspection.ProcessName);
        ActivateDocument("memoryBrowser");
        if (addr != nuint.Zero)
            await MemoryBrowserTab.NavigateToAddress(addr);
    }

    private void CtxDisassemble(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null || node.IsGroup || node.IsScriptEntry) return;

        var addr = node.ResolvedAddress ?? nuint.Zero;
        if (addr == nuint.Zero)
        {
            try { addr = AddressTableService.ParseAddress(node.Address); } catch { }
        }

        DisasmAddressTextBox.Text = $"0x{addr:X}";
        DisassembleAtAddress(sender, e);
    }

    private async void CtxFindWhatWrites(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null || node.IsGroup || node.IsScriptEntry) return;
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null) return;

        var addr = node.ResolvedAddress ?? nuint.Zero;
        if (addr == nuint.Zero)
        {
            try { addr = AddressTableService.ParseAddress(node.Address); } catch { }
        }

        if (addr == nuint.Zero) return;

        var pid = dashboard.CurrentInspection.ProcessId;

        try
        {
            var bp = await _breakpointService.SetBreakpointAsync(
                pid,
                $"0x{addr:X}",
                BreakpointType.HardwareWrite,
                BreakpointHitAction.LogAndContinue);

            _lastWriteBreakpointId = bp.Id;

            DataContext = dashboard with
            {
                StatusMessage = $"Breakpoint set on {node.Label} (0x{addr:X}). Trigger a write in-game, then right-click → 'View Write Log'."
            };
        }
        catch (Exception ex)
        {
            DataContext = dashboard with
            {
                StatusMessage = $"Failed to set write breakpoint: {ex.Message}"
            };
        }
    }

    private async void CtxViewWriteLog(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null) return;

        if (string.IsNullOrEmpty(_lastWriteBreakpointId))
        {
            DataContext = dashboard with
            {
                StatusMessage = "No write breakpoint active. Use 'Find What Writes to This Address' first."
            };
            return;
        }

        try
        {
            var hits = await _breakpointService.GetHitLogAsync(_lastWriteBreakpointId);

            if (hits.Count == 0)
            {
                DataContext = dashboard with
                {
                    StatusMessage = "No writes detected yet. Trigger a write in-game and try again."
                };
                return;
            }

            var pid = dashboard.CurrentInspection.ProcessId;
            var lines = new List<string>();

            foreach (var hit in hits)
            {
                lines.Add($"[{hit.Timestamp}] Thread {hit.ThreadId} — Instruction at {hit.Address}");

                // Try to disassemble the instruction that caused the write
                try
                {
                    var disasm = await _disassemblyService.DisassembleAtAsync(pid, hit.Address, 1);
                    if (disasm.Lines.Count > 0)
                    {
                        var instr = disasm.Lines[0];
                        lines.Add($"  {instr.Address}: {instr.Mnemonic} {instr.Operands}");
                    }
                }
                catch
                {
                    lines.Add("  (disassembly unavailable)");
                }

                // Show register snapshot
                if (hit.Registers.Count > 0)
                {
                    var regs = string.Join("  ", hit.Registers.Select(r => $"{r.Key}={r.Value}"));
                    lines.Add($"  Registers: {regs}");
                }

                lines.Add("");
            }

            var logWindow = new System.Windows.Window
            {
                Title = $"Write Log — Breakpoint {_lastWriteBreakpointId}",
                Width = 700,
                Height = 450,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var textBox = new System.Windows.Controls.TextBox
            {
                Text = string.Join(Environment.NewLine, lines),
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                TextWrapping = System.Windows.TextWrapping.NoWrap
            };

            logWindow.Content = textBox;
            logWindow.Show();

            DataContext = dashboard with
            {
                StatusMessage = $"Showing {hits.Count} write hit(s) for breakpoint {_lastWriteBreakpointId}."
            };
        }
        catch (Exception ex)
        {
            DataContext = dashboard with
            {
                StatusMessage = $"Failed to retrieve write log: {ex.Message}"
            };
        }
    }

    private void CtxMoveToGroup(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null) return;

        var groups = new List<AddressTableNode>();
        CollectGroups(_addressTableService.Roots, groups);

        if (groups.Count == 0)
        {
            if (DataContext is WorkspaceDashboard d)
                DataContext = d with { StatusMessage = "No groups exist. Create a group first." };
            return;
        }

        var dialog = new System.Windows.Window
        {
            Title = "Move to Group",
            Width = 300,
            Height = 160,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = System.Windows.ResizeMode.NoResize
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Select target group:" });
        var combo = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 6, 0, 8) };
        combo.Items.Add("(Root level)");
        foreach (var g in groups) combo.Items.Add(g.Label);
        combo.SelectedIndex = 0;
        panel.Children.Add(combo);
        var okBtn = new System.Windows.Controls.Button
        {
            Content = "Move",
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; };
        panel.Children.Add(okBtn);
        dialog.Content = panel;

        if (dialog.ShowDialog() != true) return;

        var targetGroupId = combo.SelectedIndex == 0 ? null : groups[combo.SelectedIndex - 1].Id;
        _addressTableService.MoveToGroup(node.Id, targetGroupId);
        RefreshAddressTableUI($"Moved '{node.Label}' to {(targetGroupId is null ? "root" : combo.SelectedItem)}");
    }

    private static void CollectGroups(
        System.Collections.ObjectModel.ObservableCollection<AddressTableNode> nodes,
        List<AddressTableNode> results)
    {
        foreach (var n in nodes)
        {
            if (n.IsGroup) results.Add(n);
            CollectGroups(n.Children, results);
        }
    }

    private void CtxDelete(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null) return;
        _addressTableService.RemoveEntry(node.Id);
        RefreshAddressTableUI($"Deleted '{node.Label}'");
    }

    private AddressTableNode? _clipboard;
    private string? _lastWriteBreakpointId;

    private void CtxCut(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null) return;
        _clipboard = node;
        _addressTableService.RemoveEntry(node.Id);
        RefreshAddressTableUI($"Cut '{node.Label}'");
    }

    private void CtxCopy(object sender, RoutedEventArgs e)
    {
        var node = GetSelectedNode();
        if (node is null) return;
        _clipboard = node;
        if (DataContext is WorkspaceDashboard d)
            DataContext = d with { StatusMessage = $"Copied '{node.Label}'" };
    }

    private void CtxPaste(object sender, RoutedEventArgs e)
    {
        if (_clipboard is null)
        {
            if (DataContext is WorkspaceDashboard d)
                DataContext = d with { StatusMessage = "Nothing to paste." };
            return;
        }

        // Clone the node
        var prefix = _clipboard.IsGroup ? "group" : (_clipboard.AssemblerScript != null ? "script" : "addr");
        var clone = new AddressTableNode(
            $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}",
            _clipboard.Label + " (copy)",
            _clipboard.IsGroup)
        {
            Address = _clipboard.Address,
            DataType = _clipboard.DataType,
            CurrentValue = _clipboard.CurrentValue,
            Notes = _clipboard.Notes,
            AssemblerScript = _clipboard.AssemblerScript
        };

        var selected = GetSelectedNode();
        if (selected?.IsGroup == true)
        {
            selected.Children.Add(clone);
        }
        else
        {
            _addressTableService.Roots.Add(clone);
        }

        RefreshAddressTableUI($"Pasted '{clone.Label}'");
    }

    private string? ShowInputDialog(string title, string prompt, string defaultValue)
    {
        var dialog = new System.Windows.Window
        {
            Title = title,
            Width = 340,
            Height = 150,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = System.Windows.ResizeMode.NoResize
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt });
        var textBox = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(0, 6, 0, 8),
            SelectionStart = 0,
            SelectionLength = defaultValue.Length
        };
        panel.Children.Add(textBox);
        var okBtn = new System.Windows.Controls.Button
        {
            Content = "OK",
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; };
        panel.Children.Add(okBtn);
        dialog.Content = panel;

        textBox.Focus();
        return dialog.ShowDialog() == true ? textBox.Text : null;
    }

    // ─── Address Table Grouping ──────────────────────────────────────

    private void CreateAddressGroup(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;

        var inputDialog = new System.Windows.Window
        {
            Title = "New Group",
            Width = 320,
            Height = 140,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = System.Windows.ResizeMode.NoResize
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Group name:" });
        var textBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 6, 0, 8) };
        panel.Children.Add(textBox);
        var okBtn = new System.Windows.Controls.Button { Content = "Create", Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        okBtn.Click += (_, _) => { inputDialog.DialogResult = true; };
        panel.Children.Add(okBtn);
        inputDialog.Content = panel;

        if (inputDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(textBox.Text)) return;

        _addressTableService.CreateGroup(textBox.Text.Trim());
        DataContext = dashboard with
        {
            AddressTableNodes = _addressTableService.Roots,
            AddressTableStatus = $"{_addressTableService.Entries.Count} entries in {_addressTableService.Roots.Count} nodes",
            StatusMessage = $"Created group '{textBox.Text.Trim()}'."
        };
    }

    // ─── Script Viewing & Toggling ──────────────────────────────────

    private void ViewSelectedScript(object sender, RoutedEventArgs e)
    {
        var selected = AddressTableTree.SelectedItem as AddressTableNode;
        if (selected?.AssemblerScript is null)
        {
            if (DataContext is WorkspaceDashboard d)
                DataContext = d with { StatusMessage = "Select a script entry first (marked with 📜)." };
            return;
        }

        var viewer = new System.Windows.Window
        {
            Title = $"Script: {selected.Label}",
            Width = 720,
            Height = 520,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this
        };

        var panel = new System.Windows.Controls.DockPanel();

        var toolbar = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(8)
        };
        System.Windows.Controls.DockPanel.SetDock(toolbar, System.Windows.Controls.Dock.Top);

        var statusText = new System.Windows.Controls.TextBlock
        {
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Text = selected.IsScriptEnabled ? "Status: ✅ Enabled" : "Status: ❌ Disabled",
            Foreground = selected.IsScriptEnabled
                ? FindThemeBrush("SuccessForeground")
                : FindThemeBrush("SecondaryForeground")
        };
        toolbar.Children.Add(statusText);

        if (selected.ScriptStatus is not null)
        {
            toolbar.Children.Add(new System.Windows.Controls.TextBlock
            {
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Text = selected.ScriptStatus,
                Foreground = FindThemeBrush("ErrorForeground")
            });
        }

        panel.Children.Add(toolbar);

        var scriptBox = new System.Windows.Controls.TextBox
        {
            Text = selected.AssemblerScript,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            IsReadOnly = true,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Margin = new Thickness(8),
            Background = FindThemeBrush("InputBackground"),
            Foreground = FindThemeBrush("SuccessForeground"),
            Padding = new Thickness(8)
        };
        panel.Children.Add(scriptBox);

        viewer.Content = panel;
        viewer.ShowDialog();
    }

    private async void ToggleSelectedScript(object sender, RoutedEventArgs e)
    {
        var selected = AddressTableTree.SelectedItem as AddressTableNode;
        if (selected?.AssemblerScript is null)
        {
            if (DataContext is WorkspaceDashboard d)
                DataContext = d with { StatusMessage = "Select a script entry first (marked with 📜)." };
            return;
        }

        if (DataContext is not WorkspaceDashboard dashboard) return;

        // Check if we have an attached process
        var processId = dashboard.CurrentInspection?.ProcessId ?? 0;
        if (processId == 0)
        {
            DataContext = dashboard with { StatusMessage = "Attach to a process before toggling scripts." };
            return;
        }

        if (_autoAssemblerEngine is null)
        {
            DataContext = dashboard with { StatusMessage = "Auto Assembler engine not available." };
            return;
        }

        try
        {
            if (selected.IsScriptEnabled)
            {
                // Disable
                var result = await _autoAssemblerEngine.DisableAsync(processId, selected.AssemblerScript);
                selected.IsScriptEnabled = false;
                selected.ScriptStatus = result.Success ? "Disabled successfully" : $"Disable failed: {result.Error}";
            }
            else
            {
                // Enable
                var result = await _autoAssemblerEngine.EnableAsync(processId, selected.AssemblerScript);
                selected.IsScriptEnabled = result.Success;
                selected.ScriptStatus = result.Success
                    ? $"Enabled ({result.Allocations.Count} allocs, {result.Patches.Count} patches)"
                    : $"Enable failed: {result.Error}";
            }

            DataContext = dashboard with
            {
                AddressTableNodes = _addressTableService.Roots,
                StatusMessage = $"Script '{selected.Label}': {selected.ScriptStatus}"
            };
        }
        catch (Exception ex)
        {
            selected.ScriptStatus = $"Error: {ex.Message}";
            DataContext = dashboard with { StatusMessage = $"Script error: {ex.Message}" };
        }
    }

    // ─── Cheat Table Import ──────────────────────────────────────────

    private void SaveCheatTable(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;
        try
        {
            if (_addressTableService.Roots.Count == 0)
            {
                DataContext = dashboard with { StatusMessage = "Address table is empty. Nothing to save." };
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Cheat Engine Table",
                Filter = "Cheat Tables (*.ct)|*.ct|All Files (*.*)|*.*",
                DefaultExt = ".ct"
            };

            if (dialog.ShowDialog(this) != true) return;

            var exporter = new CheatTableExporter();
            exporter.SaveToFile(_addressTableService.Roots, dialog.FileName);

            DataContext = dashboard with
            {
                StatusMessage = $"Saved {_addressTableService.Roots.Count} top-level entries to {System.IO.Path.GetFileName(dialog.FileName)}"
            };
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"CT save failed: {ex.Message}" };
        }
    }

    private void LoadCheatTable(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load Cheat Engine Table",
                Filter = "Cheat Tables (*.ct;*.CT)|*.ct;*.CT|XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
                DefaultExt = ".ct"
            };

            if (dialog.ShowDialog(this) != true) return;

            var parser = new CheatTableParser();
            var ctFile = parser.ParseFile(dialog.FileName);
            var nodes = parser.ToAddressTableNodes(ctFile);
            _addressTableService.ImportNodes(nodes);

            var scriptCount = CountScripts(ctFile.Entries);
            var leafCount = _addressTableService.Entries.Count;

            DataContext = dashboard with
            {
                AddressTableNodes = _addressTableService.Roots,
                AddressTableStatus = $"{leafCount} entries in {_addressTableService.Roots.Count} nodes",
                StatusMessage = $"Loaded {ctFile.FileName}: {ctFile.TotalEntryCount} CT entries imported with hierarchy, {scriptCount} scripts" +
                                (ctFile.LuaScript is not null ? " (has Lua script)" : "")
            };
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"CT load failed: {ex.Message}" };
        }
    }

    private static int CountScripts(IReadOnlyList<CheatTableEntry> entries)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            if (entry.AssemblerScript is not null) count++;
            count += CountScripts(entry.Children);
        }
        return count;
    }

    // ─── Session Save / Load ───────────────────────────────────────────

    private async void SaveSession(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;

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

            DataContext = dashboard with { StatusMessage = $"Session saved: {sessionId}" };
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"Save failed: {ex.Message}" };
        }
    }

    private async void LoadSession(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;

        try
        {
            var sessions = await _sessionService.ListSessionsAsync(20);
            if (sessions.Count == 0)
            {
                DataContext = dashboard with { StatusMessage = "No saved sessions found." };
                return;
            }

            // Build a selection dialog using a simple Window
            var dialogWindow = new Window
            {
                Title = "Load Session",
                Width = 480,
                Height = 340,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var panel = new System.Windows.Controls.DockPanel { Margin = new Thickness(12) };
            var listBox = new System.Windows.Controls.ListBox { Margin = new Thickness(0, 0, 0, 8) };
            System.Windows.Controls.DockPanel.SetDock(listBox, System.Windows.Controls.Dock.Top);

            foreach (var s in sessions)
                listBox.Items.Add($"{s.Id}  —  {s.ProcessName}  ({s.AddressEntryCount} addresses, {s.CreatedAtUtc:g})");

            listBox.SelectedIndex = 0;

            var loadBtn = new System.Windows.Controls.Button { Content = "Load", Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
            loadBtn.Click += (_, _) => { dialogWindow.DialogResult = true; dialogWindow.Close(); };
            System.Windows.Controls.DockPanel.SetDock(loadBtn, System.Windows.Controls.Dock.Bottom);

            panel.Children.Add(loadBtn);
            panel.Children.Add(listBox);
            dialogWindow.Content = panel;

            if (dialogWindow.ShowDialog() != true || listBox.SelectedIndex < 0) return;

            var selected = sessions[listBox.SelectedIndex];
            var loaded = await _sessionService.LoadSessionAsync(selected.Id);
            if (loaded is null)
            {
                DataContext = dashboard with { StatusMessage = "Session data not found." };
                return;
            }

            // Clear existing table before importing
            _addressTableService.ClearAll();

            // Restore entries with full metadata (label, notes, locked status, values)
            foreach (var entry in loaded.Value.Entries)
            {
                var node = new CEAISuite.Application.AddressTableNode(entry.Id, entry.Label, false)
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

            DataContext = dashboard with
            {
                AddressTableNodes = _addressTableService.Roots,
                AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
                StatusMessage = $"Loaded session {selected.Id} with {loaded.Value.Entries.Count} address entries."
            };
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"Load failed: {ex.Message}" };
        }
    }

    // ── Memory Browser ──

    private void OpenMemoryBrowser(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            System.Windows.MessageBox.Show("Attach to a process first.", "Memory Browser");
            return;
        }
        MemoryBrowserTab.AttachProcess(_engineFacade,
            dashboard.CurrentInspection.ProcessId,
            dashboard.CurrentInspection.ProcessName);
        ActivateDocument("memoryBrowser");
    }

    // ── Menu bar handlers ──

    private void OpenSkillsManager(object sender, RoutedEventArgs e)
    {
        var window = new SkillsManagerWindow();
        window.Owner = this;
        window.ShowDialog();

        if (window.SkillsChanged)
        {
            try
            {
                var newClient = CreateChatClient();
                _aiOperatorService.Reconfigure(newClient);
            }
            catch { }
        }
    }

    private void OpenSkillsFolder(object sender, RoutedEventArgs e)
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

    private void ReloadSkills(object sender, RoutedEventArgs e)
    {
        try
        {
            var newClient = CreateChatClient();
            _aiOperatorService.Reconfigure(newClient);
            MessageBox.Show("Skills reloaded successfully.", "Skills", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reload: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_appSettingsService);
        settingsWindow.Owner = this;
        settingsWindow.ShowDialog();
    }

    private void ExitApp(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // AutoHideMenu_Changed removed — replaced by hamburger menu in command bar

    private void OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+P → focus process combo
        if (e.Key == System.Windows.Input.Key.P
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ProcessComboBox.Focus();
            ProcessComboBox.IsDropDownOpen = true;
            e.Handled = true;
        }
        // Ctrl+F → focus scanner
        else if (e.Key == System.Windows.Input.Key.F
                 && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CmdFocusScanner(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    #region Command Bar Handlers

    private void CmdAttachProcess(object sender, RoutedEventArgs e)
    {
        if (ProcessComboBox.SelectedItem is ProcessComboItem item)
        {
            // Select the matching process in RunningProcessesList and trigger inspection
            if (DataContext is WorkspaceDashboard dashboard)
            {
                var process = dashboard.RunningProcesses?.FirstOrDefault(p => p.Id == item.Pid);
                if (process != null)
                {
                    RunningProcessesList.SelectedItem = process;
                    InspectSelectedProcess(sender, e);
                    return;
                }
            }
        }
        StatusBarCenter.Text = "Select a process from the dropdown first";
    }

    private async void CmdDetachProcess(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            StatusBarCenter.Text = "No process attached";
            return;
        }

        var pid = dashboard.CurrentInspection.ProcessId;
        var processName = dashboard.CurrentInspection.ProcessName;

        try
        {
            // 1. Stop auto-refresh so we don't read from a detached process
            _refreshTimer?.Stop();

            // 2. Remove all breakpoints for this process
            try
            {
                var bps = await _breakpointService.ListBreakpointsAsync(pid);
                foreach (var bp in bps)
                    await _breakpointService.RemoveBreakpointAsync(pid, bp.Id);
            }
            catch { /* best-effort — process may already be gone */ }

            // 3. Detach engine facade + clear dashboard state
            _dashboardService.DetachProcess();

            // 4. Update UI DataContext to reflect detach
            DataContext = dashboard with
            {
                CurrentInspection = null,
                StatusMessage = $"Detached from {processName} ({pid})"
            };

            // 5. Clear memory browser
            MemoryBrowserTab.Clear();

            // 6. Update status bar
            StatusBarProcess.Text = "No process attached";
            StatusBarCenter.Text = $"Detached from {processName}";

            // 7. Reset process combo selection
            ProcessComboBox.SelectedItem = null;
        }
        catch (Exception ex)
        {
            StatusBarCenter.Text = $"Detach error: {ex.Message}";
        }
    }

    private void CmdFocusScanner(object sender, RoutedEventArgs e)
    {
        // Find and activate the scanner anchorable
        var scanner = DockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == "scanner");
        if (scanner != null)
        {
            if (scanner.IsAutoHidden) scanner.ToggleAutoHide();
            scanner.IsActive = true;
        }
        ScanValueTextBox?.Focus();
    }

    private void CmdRunScript(object sender, RoutedEventArgs e)
    {
        // Toggle the currently selected script in the address table
        ToggleSelectedScript(sender, e);
    }

    private async void CmdEmergencyStop(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard)
            return;

        var pid = dashboard.CurrentInspection?.ProcessId;
        var processName = dashboard.CurrentInspection?.ProcessName ?? "unknown";

        try
        {
            // 1. Stop everything immediately
            _refreshTimer?.Stop();

            // 2. Rollback ALL patches (restores original bytes)
            var rolled = await _patchUndoService.RollbackAllAsync();

            // 3. Force-detach breakpoint engine (emergency path — no locks)
            if (pid.HasValue)
            {
                try { await _breakpointService.ForceDetachAndCleanupAsync(pid.Value); }
                catch { /* best-effort */ }
            }

            // 4. Detach engine facade + clear dashboard
            _dashboardService.DetachProcess();

            // 5. Update UI
            DataContext = dashboard with
            {
                CurrentInspection = null,
                StatusMessage = $"EMERGENCY STOP — detached from {processName}"
            };
            MemoryBrowserTab.Clear();
            ProcessComboBox.SelectedItem = null;

            StatusBarProcess.Text = "EMERGENCY STOP — detached";
            StatusBarCenter.Text = $"Rolled back {rolled} patch(es), force-detached";
        }
        catch (Exception ex)
        {
            StatusBarCenter.Text = $"Emergency stop error: {ex.Message}";
        }
    }

    #endregion

    private void PopulateProcessCombo()
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;
        var items = (dashboard.RunningProcesses ?? [])
            .Select(p => new ProcessComboItem { Pid = p.Id, Name = p.Name })
            .OrderBy(p => p.Name)
            .ToList();
        ProcessComboBox.ItemsSource = items;
    }

    private async void MenuUndo(object sender, RoutedEventArgs e)
    {
        await PerformUndoAsync();
    }

    private async void MenuRedo(object sender, RoutedEventArgs e)
    {
        await PerformRedoAsync();
    }

    private void ShowAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "CE AI Suite\nA Cheat Engine-class desktop application with integrated AI operator.\n\nVersion 0.1.0-alpha",
            "About CE AI Suite",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ── Breakpoint handlers ──

    private void OnBpTypeComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        var combo = (System.Windows.Controls.ComboBox)sender;
        combo.ItemsSource = Enum.GetNames<BreakpointType>();
        combo.SelectedIndex = 0;
    }

    private async void SetBreakpoint(object sender, RoutedEventArgs e)
    {
        var dashboard = DataContext as WorkspaceDashboard;
        if (dashboard?.CurrentInspection is null) return;
        try
        {
            var type = Enum.Parse<BreakpointType>(BpTypeComboBox.SelectedItem?.ToString() ?? "Software");
            var bp = await _breakpointService.SetBreakpointAsync(
                dashboard.CurrentInspection.ProcessId,
                BpAddressTextBox.Text,
                type);
            await RefreshBreakpointList(dashboard);
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"Breakpoint error: {ex.Message}" };
        }
    }

    private async void RemoveBreakpoint(object sender, RoutedEventArgs e)
    {
        var dashboard = DataContext as WorkspaceDashboard;
        if (dashboard?.CurrentInspection is null) return;
        if (BreakpointListView.SelectedItem is not BreakpointDisplayItem selected) return;
        try
        {
            await _breakpointService.RemoveBreakpointAsync(dashboard.CurrentInspection.ProcessId, selected.Id);
            await RefreshBreakpointList(dashboard);
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"Remove BP error: {ex.Message}" };
        }
    }

    private async void RefreshBreakpoints(object sender, RoutedEventArgs e)
    {
        var dashboard = DataContext as WorkspaceDashboard;
        if (dashboard?.CurrentInspection is null) return;
        try
        {
            await RefreshBreakpointList(dashboard);
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"Refresh BP error: {ex.Message}" };
        }
    }

    private async void ViewHitLog(object sender, RoutedEventArgs e)
    {
        var dashboard = DataContext as WorkspaceDashboard;
        if (BreakpointListView.SelectedItem is not BreakpointDisplayItem selected) return;
        try
        {
            var hits = await _breakpointService.GetHitLogAsync(selected.Id);
            var lines = hits.Select(h =>
            {
                var regs = string.Join(", ", h.Registers.Take(6).Select(r => $"{r.Key}={r.Value}"));
                return $"[{h.Timestamp}] TID={h.ThreadId} @ {h.Address}  {regs}";
            });
            System.Windows.MessageBox.Show(
                hits.Count == 0
                    ? "No hits recorded for this breakpoint."
                    : string.Join("\n", lines),
                $"Hit Log — {selected.Id}",
                MessageBoxButton.OK);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error: {ex.Message}", "Hit Log", MessageBoxButton.OK);
        }
    }

    private async Task RefreshBreakpointList(WorkspaceDashboard dashboard)
    {
        var bps = await _breakpointService.ListBreakpointsAsync(dashboard.CurrentInspection!.ProcessId);
        BreakpointListView.ItemsSource = bps.Select(b => new BreakpointDisplayItem
        {
            Id = b.Id,
            Address = b.Address,
            Type = b.Type,
            HitCount = b.HitCount,
            Status = b.IsEnabled ? "Active" : "Disabled"
        }).ToArray();
        DataContext = dashboard with { BreakpointStatus = $"{bps.Count} breakpoint(s) active" };
    }

    // ── Drag and Drop: Sources ──

    private Point _dragStartPoint;

    private void ProcessList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStartPoint = e.GetPosition(null);

    private void ProcessList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (RunningProcessesList.SelectedItem is RunningProcessOverview proc)
        {
            var data = new DataObject("CEAIContext",
                $"[Process] {proc.Name} (PID {proc.Id}, {proc.Architecture})");
            DragDrop.DoDragDrop(RunningProcessesList, data, DragDropEffects.Copy);
        }
    }

    private void ModuleList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStartPoint = e.GetPosition(null);

    private void ModuleList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (sender is ListView lv && lv.SelectedItem is ModuleOverview mod)
        {
            var data = new DataObject("CEAIContext",
                $"[Module] {mod.Name} @ {mod.BaseAddress} (size: {mod.Size})");
            DragDrop.DoDragDrop(lv, data, DragDropEffects.Copy);
        }
    }

    private void AddressTable_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStartPoint = e.GetPosition(null);

    private void AddressTable_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (AddressTableTree.SelectedItem is AddressTableNode node)
        {
            string context;
            if (node.IsGroup)
            {
                var childCount = node.Children.Count;
                context = $"[Group] \"{node.Label}\" ({childCount} entries)";
            }
            else if (node.IsScriptEntry)
            {
                var status = node.IsScriptEnabled ? "ENABLED" : "disabled";
                context = $"[Script] \"{node.Label}\" — {status} (ID: {node.Id})";
            }
            else
            {
                var frozen = node.IsLocked ? " [FROZEN]" : "";
                context = $"[Address] \"{node.Label}\" @ {node.DisplayAddress} = {node.DisplayValue} ({node.DisplayType}){frozen} (ID: {node.Id})";
            }

            var data = new DataObject("CEAIContext", context);
            DragDrop.DoDragDrop(AddressTableTree, data, DragDropEffects.Copy);
        }
    }

    // ── Drag and Drop: AI Panel Target ──

    private void AiPanel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("CEAIContext") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void AiPanel_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("CEAIContext")) return;
        var context = e.Data.GetData("CEAIContext") as string;
        if (string.IsNullOrWhiteSpace(context)) return;

        // Add as attachment chip instead of raw text
        AddAttachment("Context", context);
        AiChatInputTextBox.Focus();
        e.Handled = true;
    }

    // ── Attachment Management ──

    private void OnChatInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (text is not null && (text.Contains('\n') || text.Length > 300))
            {
                e.CancelCommand();
                AddAttachment(text.Contains('\n') ? "Pasted" : "Pasted text", text);
            }
        }
    }

    private void AddAttachment(string label, string fullText)
    {
        var lines = fullText.Split('\n');
        var preview = lines[0].Trim();
        if (preview.Length > 60) preview = preview[..57] + "…";
        if (lines.Length > 1) preview += $" (+{lines.Length - 1} lines)";

        _attachments.Add(new AttachmentChip
        {
            Label = label,
            Preview = preview,
            FullText = fullText
        });
        RefreshAttachmentChips();
    }

    private void RefreshAttachmentChips()
    {
        AttachmentChips.ItemsSource = null;
        AttachmentChips.ItemsSource = _attachments.ToList();
        AttachmentChips.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _attachments.RemoveAll(a => a.Id == id);
            RefreshAttachmentChips();
        }
    }

    private void AttachContext_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach Context File",
            Filter = "Text files|*.txt;*.md;*.cs;*.json;*.xml;*.log;*.csv|All files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 100_000)
                    {
                        MessageBox.Show($"{info.Name} is too large ({info.Length / 1024}KB). Max 100KB.",
                            "File Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }
                    var content = File.ReadAllText(file);
                    AddAttachment(Path.GetFileName(file), content);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to read {file}: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    // ── Model Selector ──

    private async void ModelSelector_Click(object sender, RoutedEventArgs e)
    {
        if (ModelSelectorPopup.IsOpen)
        {
            ModelSelectorPopup.IsOpen = false;
            return;
        }

        ModelSelectorList.Children.Clear();

        var currentModel = _appSettingsService.Settings.Model;

        List<string> models = new();
        if (_appSettingsService.Settings.Provider.Equals("copilot", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_appSettingsService.Settings.GitHubToken))
        {
            try
            {
                var copilotModels = await ChatClientFactory.CopilotService.FetchModelsAsync(_appSettingsService.Settings.GitHubToken);
                models.AddRange(copilotModels.Select(m => m.Id));
            }
            catch { }
        }

        if (models.Count == 0)
        {
            models.AddRange(new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-5.4", "claude-sonnet-4", "claude-sonnet-4.5", "o4-mini" });
        }

        if (!models.Contains(currentModel))
            models.Insert(0, currentModel);

        foreach (var model in models)
        {
            var btn = new Button
            {
                Content = model,
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource(model == currentModel ? "AccentForeground" : "PrimaryForeground"),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontWeight = model == currentModel ? FontWeights.SemiBold : FontWeights.Normal,
                Tag = model
            };
            btn.Click += ModelSelectorItem_Click;
            ModelSelectorList.Children.Add(btn);
        }

        ModelSelectorPopup.IsOpen = true;
    }

    private void ModelSelectorItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string model) return;
        ModelSelectorPopup.IsOpen = false;

        _appSettingsService.Settings.Model = model;
        _appSettingsService.Save();
        ModelSelectorText.Text = model;

        try
        {
            var newClient = CreateChatClient();
            _aiOperatorService.Reconfigure(newClient);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Model switch failed: {ex.Message}");
        }
    }

    private void ApplyDockTheme(AppTheme resolved)
    {
        DockManager.Theme = resolved == AppTheme.Light
            ? new Vs2013LightTheme()
            : new Vs2013DarkTheme();
    }

    /// <summary>Activate a LayoutDocument tab by its ContentId.</summary>
    private void ActivateDocument(string contentId)
    {
        var doc = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (doc is not null)
            doc.IsActive = true;
    }

    #region Layout Persistence

    private void SaveLayout()
    {
        try
        {
            var dir = Path.GetDirectoryName(LayoutFilePath)!;
            Directory.CreateDirectory(dir);
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.Serialize(LayoutFilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveLayout failed: {ex.Message}");
        }
    }

    private void RestoreLayout()
    {
        try
        {
            if (!File.Exists(LayoutFilePath)) return;
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                // Preserve existing content controls — match by ContentId
                if (args.Content != null) return;

                // Find the original content from the XAML-defined layout
                var contentId = args.Model?.ContentId;
                if (string.IsNullOrEmpty(contentId)) { args.Cancel = true; return; }

                var existing = FindContentByContentId(contentId);
                if (existing != null)
                    args.Content = existing;
                else
                    args.Cancel = true; // Unknown panel — skip it
            };
            serializer.Deserialize(LayoutFilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RestoreLayout failed: {ex.Message}");
            // Corrupted layout file — delete it so next launch uses defaults
            try { File.Delete(LayoutFilePath); } catch { }
        }
    }

    private void ResetLayout(object sender, RoutedEventArgs e)
    {
        try { File.Delete(LayoutFilePath); } catch { }
        MessageBox.Show("Layout reset. Restart the application to apply the default layout.",
            "Reset Layout", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Lookup the original XAML-defined content control by ContentId.</summary>
    private object? FindContentByContentId(string contentId)
    {
        // Walk all current layout elements to find one with matching ContentId that has Content
        foreach (var item in DockManager.Layout.Descendents().OfType<LayoutContent>())
        {
            if (item.ContentId == contentId && item.Content != null)
                return item.Content;
        }
        return null;
    }

    #endregion

    #region Density Presets

    private void ApplyDensityPreset(string preset)
    {
        var anchorables = DockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .ToDictionary(a => a.ContentId ?? "", a => a);

        switch (preset.ToLowerInvariant())
        {
            case "clean":
                // Sidebar hidden, bottom panel auto-hide, AI chat visible
                SetAnchorableVisibility(anchorables, "processes", autoHide: true);
                SetAnchorableVisibility(anchorables, "scanner", autoHide: true);
                SetAnchorableVisibility(anchorables, "output", autoHide: true);
                SetAnchorableVisibility(anchorables, "aiOperator", visible: true);
                StatusBarDensity.Text = "Clean";
                break;

            case "dense":
                // Everything expanded
                SetAnchorableVisibility(anchorables, "processes", visible: true);
                SetAnchorableVisibility(anchorables, "scanner", visible: true);
                SetAnchorableVisibility(anchorables, "output", visible: true);
                SetAnchorableVisibility(anchorables, "aiOperator", visible: true);
                StatusBarDensity.Text = "Dense";
                break;

            default: // Balanced
                // Sidebar visible, bottom panel visible with output, AI chat visible
                SetAnchorableVisibility(anchorables, "processes", visible: true);
                SetAnchorableVisibility(anchorables, "scanner", visible: true);
                SetAnchorableVisibility(anchorables, "output", visible: true);
                SetAnchorableVisibility(anchorables, "aiOperator", visible: true);
                StatusBarDensity.Text = "Balanced";
                break;
        }
    }

    private static void SetAnchorableVisibility(
        Dictionary<string, LayoutAnchorable> anchorables, string contentId,
        bool visible = false, bool autoHide = false)
    {
        if (!anchorables.TryGetValue(contentId, out var panel)) return;

        if (autoHide && !panel.IsAutoHidden)
            panel.ToggleAutoHide();
        else if (visible && panel.IsAutoHidden)
            panel.ToggleAutoHide();
    }

    private void CycleDensityPreset(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var current = (_appSettingsService.Settings.DensityPreset ?? "Balanced").ToLowerInvariant();
        var next = current switch
        {
            "clean" => "Balanced",
            "balanced" => "Dense",
            "dense" => "Clean",
            _ => "Balanced"
        };
        _appSettingsService.Settings.DensityPreset = next;
        _appSettingsService.Save();
        ApplyDensityPreset(next);
    }

    #endregion
}

/// <summary>Display model for breakpoint list items.</summary>
public sealed class BreakpointDisplayItem
{
    public string Id { get; init; } = "";
    public string Address { get; init; } = "";
    public string Type { get; init; } = "";
    public int HitCount { get; init; }
    public string Status { get; init; } = "";
}
