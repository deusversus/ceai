using System.Collections.ObjectModel;
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
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.AI;
using OpenAI;

namespace CEAISuite.Desktop;

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
    private readonly OperationJournal _operationJournal;
    private readonly ICodeCaveEngine _codeCaveEngine;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly IDialogService _dialogService;
    private readonly ObservableCollection<OutputLogEntry> _outputLogEntries;

    // ── Bottom-panel ViewModels ──
    private readonly OutputLogViewModel _outputLogVm;
    private readonly HotkeysViewModel _hotkeysVm;
    private readonly FindResultsViewModel _findResultsVm;
    private readonly SnapshotsViewModel _snapshotsVm;
    private readonly JournalViewModel _journalVm;
    private readonly BreakpointsViewModel _breakpointsVm;
    private readonly ScriptsViewModel _scriptsVm;

    // ── Phase 2.5 panel ViewModels ──
    private readonly ScannerViewModel _scannerVm;
    private readonly ProcessListViewModel _processListVm;
    private readonly InspectionViewModel _inspectionVm;
    private readonly AddressTableViewModel _addressTableVm;
    private readonly AiOperatorViewModel _aiOperatorVm;
    private readonly Dictionary<string, object> _closedPanelContent = new();
    private readonly Dictionary<string, object> _xamlPanelContent = new();

    // Bump this version whenever the default panel layout changes (e.g. new tabs added).
    // A mismatch auto-deletes the saved layout so XAML defaults apply cleanly.
    private const int LayoutVersion = 11; // v11 = */ 472px fixed bottom (matches serializer output)

    private static readonly string LayoutFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CEAISuite", "layout.xml");
    private static readonly string LayoutVersionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CEAISuite", "layout.version");

    public MainWindow(
        IEngineFacade engineFacade,
        WorkspaceDashboardService dashboardService,
        ScanService scanService,
        AddressTableService addressTableService,
        DisassemblyService disassemblyService,
        ScriptGenerationService scriptGenerationService,
        AddressTableExportService addressTableExportService,
        SessionService sessionService,
        BreakpointService breakpointService,
        IAutoAssemblerEngine autoAssemblerEngine,
        GlobalHotkeyService hotkeyService,
        PatchUndoService patchUndoService,
        IMemoryProtectionEngine memoryProtectionEngine,
        MemorySnapshotService snapshotService,
        PointerRescanService pointerRescanService,
        AppSettingsService appSettingsService,
        OperationJournal operationJournal,
        ICodeCaveEngine codeCaveEngine,
        AiToolFunctions toolFunctions,
        AiOperatorService aiOperatorService,
        IProcessContext processContext,
        IOutputLog outputLog,
        IDialogService dialogService,
        OutputLogViewModel outputLogVm,
        HotkeysViewModel hotkeysVm,
        FindResultsViewModel findResultsVm,
        SnapshotsViewModel snapshotsVm,
        JournalViewModel journalVm,
        BreakpointsViewModel breakpointsVm,
        ScriptsViewModel scriptsVm,
        ScannerViewModel scannerVm,
        ProcessListViewModel processListVm,
        InspectionViewModel inspectionVm,
        AddressTableViewModel addressTableVm,
        AiOperatorViewModel aiOperatorVm)
    {
        InitializeComponent();
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite",
            "workspace.db");

        // Assign injected services to fields
        _engineFacade = engineFacade;
        _dashboardService = dashboardService;
        _scanService = scanService;
        _addressTableService = addressTableService;
        _disassemblyService = disassemblyService;
        _scriptGenerationService = scriptGenerationService;
        _addressTableExportService = addressTableExportService;
        _sessionService = sessionService;
        _breakpointService = breakpointService;
        _autoAssemblerEngine = autoAssemblerEngine;
        _hotkeyService = hotkeyService;
        _patchUndoService = patchUndoService;
        _memoryProtectionEngine = memoryProtectionEngine;
        _snapshotService = snapshotService;
        _pointerRescanService = pointerRescanService;
        _appSettingsService = appSettingsService;
        _operationJournal = operationJournal;
        _codeCaveEngine = codeCaveEngine;
        _aiOperatorService = aiOperatorService;
        _processContext = processContext;
        _outputLog = outputLog;
        _dialogService = dialogService;
        _outputLogEntries = outputLog.Entries;

        // Assign bottom-panel ViewModels
        _outputLogVm = outputLogVm;
        _hotkeysVm = hotkeysVm;
        _findResultsVm = findResultsVm;
        _snapshotsVm = snapshotsVm;
        _journalVm = journalVm;
        _breakpointsVm = breakpointsVm;
        _scriptsVm = scriptsVm;

        // Assign Phase 2.5 panel ViewModels
        _scannerVm = scannerVm;
        _processListVm = processListVm;
        _inspectionVm = inspectionVm;
        _addressTableVm = addressTableVm;
        _aiOperatorVm = aiOperatorVm;

        // Apply saved theme
        var savedTheme = Enum.TryParse<AppTheme>(_appSettingsService.Settings.Theme, true, out var theme)
            ? theme : AppTheme.System;
        ThemeManager.ApplyTheme(savedTheme);
        ApplyDockTheme(ThemeManager.ResolvedTheme);
        UpdateSystemSelectionColors();
        ThemeManager.ThemeChanged += t => { ApplyDockTheme(t); UpdateSystemSelectionColors(); };

        // Restore saved panel layout (must happen after InitializeComponent + theme)
        RestoreLayout();

        // Stash panel content when user closes a panel so we can restore it from Windows menu
        DockManager.AnchorableClosing += (_, args) =>
        {
            if (args.Anchorable is { ContentId: not null, Content: not null })
                _closedPanelContent[args.Anchorable.ContentId] = args.Anchorable.Content;
        };

        // Apply saved density preset (after layout restore so it can override visibility)
        ApplyDensityPreset(_appSettingsService.Settings.DensityPreset ?? "Balanced");

        // Wire up AI operator with dynamic context injection
        _aiOperatorService.SetContextProvider(BuildAiContext);

        // Configure AI provider (may be null if no API key configured)
        try
        {
            var chatClient = CreateChatClient();
            _aiOperatorService.Reconfigure(chatClient);
        }
        catch (Exception ex)
        {
            // AI provider init failed — app still starts, just without AI
            System.Diagnostics.Debug.WriteLine($"AI provider init failed: {ex.Message}");
        }
        _aiOperatorService.RateLimitSeconds = _appSettingsService.Settings.RateLimitSeconds;
        _aiOperatorService.RateLimitWait = _appSettingsService.Settings.RateLimitWait;
        _aiOperatorService.Limits = TokenLimits.Resolve(_appSettingsService.Settings);

        // Live status updates from AI agent
        _aiOperatorService.StatusChanged += status =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _aiOperatorVm.StatusText = status;
                AppendOutputLog("Agent", "Info", status);
            });
        };
        OutputLogList.ItemsSource = _outputLogEntries;

        // Wire bottom-panel DataContexts via x:Name content roots
        OutputContent.DataContext = _outputLogVm;
        BreakpointsContent.DataContext = _breakpointsVm;
        ScriptsContent.DataContext = _scriptsVm;
        SnapshotsContent.DataContext = _snapshotsVm;
        FindResultsContent.DataContext = _findResultsVm;
        HotkeysContent.DataContext = _hotkeysVm;
        JournalContent.DataContext = _journalVm;

        // Wire Phase 2.5 panel DataContexts
        ScannerContent.DataContext = _scannerVm;
        ProcessesContent.DataContext = _processListVm;
        InspectionContent.DataContext = _inspectionVm;
        AddressTableContent.DataContext = _addressTableVm;

        // Wire AI Operator ViewModel
        AiOperatorContent.DataContext = _aiOperatorVm;
        _aiOperatorVm.ScrollToBottomRequested += () => Dispatcher.BeginInvoke(() => AiChatScrollViewer.ScrollToEnd());
        _aiOperatorVm.StreamingTextUpdated += () => Dispatcher.BeginInvoke(() => AiChatList.Items.Refresh());
        _aiOperatorVm.ChatDisplayRefreshed += () => Dispatcher.BeginInvoke(() =>
        {
            // Remove any approval cards that were injected during streaming
            for (int i = AiChatContainer.Children.Count - 1; i >= 0; i--)
            {
                if (AiChatContainer.Children[i] != AiChatList)
                    AiChatContainer.Children.RemoveAt(i);
            }
        });
        _aiOperatorVm.ApprovalCardRequested += approval => ShowInlineApprovalCard(approval);

        // Subscribe to AddressTableViewModel navigation events
        _addressTableVm.NavigateToMemoryBrowser += addr =>
        {
            if (DataContext is WorkspaceDashboard dashboard && dashboard.CurrentInspection is not null)
            {
                MemoryBrowserTab.AttachProcess(_engineFacade,
                    dashboard.CurrentInspection.ProcessId,
                    dashboard.CurrentInspection.ProcessName);
                ActivateDocument("memoryBrowser");
                if (addr != nuint.Zero)
                    _ = MemoryBrowserTab.NavigateToAddress(addr);
            }
        };
        _addressTableVm.NavigateToDisassembly += addrStr =>
        {
            _inspectionVm.DisassemblyAddress = addrStr;
            _inspectionVm.DisassembleAtAddressCommand.Execute(null);
        };
        _addressTableVm.PopulateFindResults += (items, desc) =>
        {
            PopulateFindResults(items, desc);
            ActivateAnchorable("findResults");
        };

        // Refresh chat switcher when chats change
        _aiOperatorService.ChatListChanged += () =>
        {
            Dispatcher.BeginInvoke(() => _aiOperatorVm.RefreshChatSwitcher());
        };

        // Wire up non-streaming approval handler (shows inline UI, waits for user decision)
        _aiOperatorService.ApprovalRequested += async (toolName, argsStr) =>
        {
            var approval = new AgentStreamEvent.ApprovalRequested(toolName, argsStr);
            await Dispatcher.InvokeAsync(() => _aiOperatorVm.HandleApprovalRequest(approval));
            return await approval.UserDecision;
        };

        _appSettingsService.SettingsChanged += () =>
        {
            Dispatcher.Invoke(() =>
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
        if (DataContext is WorkspaceDashboard dash)
            _processListVm.SetProcesses(dash.RunningProcesses);
        PopulateProcessCombo();
        _aiOperatorVm.RefreshChatSwitcher();

        // Wire up paste handler (thin wrapper — ViewModel handles attachment logic)
        DataObject.AddPastingHandler(AiChatInputTextBox, OnChatInputPaste);

        // Placeholder visibility for chat input
        AiChatInputTextBox.TextChanged += (_, _) =>
        {
            AiChatPlaceholder.Visibility = string.IsNullOrEmpty(AiChatInputTextBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        };

        // Attachment chips visibility
        _aiOperatorVm.Attachments.CollectionChanged += (_, _) =>
        {
            AttachmentChips.Visibility = _aiOperatorVm.Attachments.Count > 0
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
        _addressTableVm.StopAutoRefresh();
        _hotkeyService.Dispose();
        _aiOperatorService.SaveCurrentChat();
        base.OnClosed(e);
    }

    private async void InspectSelectedProcess(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard)
        {
            return;
        }

        var selectedProcess = _processListVm.SelectedProcess;
        if (selectedProcess is null)
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
        _addressTableVm.StartAutoRefresh(_appSettingsService.Settings.RefreshIntervalMs > 0
            ? _appSettingsService.Settings.RefreshIntervalMs
            : 500);
    }

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

    private void OnToolbarScanTypeLoaded(object sender, RoutedEventArgs e)
    {
        ToolbarScanType.ItemsSource = Enum.GetValues<MemoryDataType>();
        ToolbarScanType.SelectedItem = MemoryDataType.Int32;
    }

    // RefreshAddressTable, RemoveSelectedAddress, ToggleLockAddress → AddressTableViewModel commands

    // ─── AI Operator (thin wrappers — logic in AiOperatorViewModel) ──────

    private void SendAiMessage(object sender, RoutedEventArgs e) =>
        _aiOperatorVm.SendMessageCommand.Execute(null);

    private void OnAiChatPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            _aiOperatorVm.SendMessageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ClearAiChat(object sender, RoutedEventArgs e) =>
        _aiOperatorVm.ClearChatCommand.Execute(null);

    private void CopyAiMessage(object sender, RoutedEventArgs e)
    {
        // ContextMenu is in a separate visual tree — get DataContext from PlacementTarget
        if (sender is MenuItem mi
            && mi.Parent is ContextMenu ctx
            && ctx.PlacementTarget is FrameworkElement fe
            && fe.DataContext is AiChatDisplayItem item)
        {
            _aiOperatorVm.CopyMessageCommand.Execute(item);
        }
    }

    private void ExportAiChat(object sender, RoutedEventArgs e) =>
        _aiOperatorVm.ExportChatCommand.Execute(null);

    private void NewAiChat(object sender, RoutedEventArgs e) =>
        _aiOperatorVm.NewChatCommand.Execute(null);

    private void DeleteAiChat(object sender, RoutedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is ChatHistoryDisplayItem selected)
            _aiOperatorVm.DeleteChatCommand.Execute(selected);
    }

    private void ToggleChatHistory(object sender, RoutedEventArgs e) =>
        _aiOperatorVm.ToggleChatHistoryCommand.Execute(null);

    private void ChatHistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is ChatHistoryDisplayItem selected)
            _aiOperatorVm.SwitchChatCommand.Execute(selected);
    }

    // Context menu handlers for chat history
    private void ChatCtx_Open(object sender, RoutedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is ChatHistoryDisplayItem selected)
            _aiOperatorVm.OpenChat(selected);
    }

    private void ChatCtx_Rename(object sender, RoutedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is not ChatHistoryDisplayItem selected) return;

        // Simple rename dialog (WPF-specific, stays in code-behind)
        var dlg = new Window
        {
            Title = "Rename Chat",
            Width = 380, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = AiOperatorViewModel.FindThemeBrush("SidebarBackground"),
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
            _aiOperatorVm.RenameChat(selected, tb.Text.Trim());
        }
    }

    private void ChatCtx_Delete(object sender, RoutedEventArgs e) =>
        DeleteAiChat(sender, e);

    // Search box placeholder + filtering
    private void ChatSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (ChatSearchBox.Text == (string)ChatSearchBox.Tag)
        {
            ChatSearchBox.Text = "";
            ChatSearchBox.Foreground = AiOperatorViewModel.FindThemeBrush("PrimaryForeground");
        }
    }

    private void ChatSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ChatSearchBox.Text))
        {
            ChatSearchBox.Text = (string)ChatSearchBox.Tag;
            ChatSearchBox.Foreground = AiOperatorViewModel.FindThemeBrush("SecondaryForeground");
        }
    }

    private void ChatSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = ChatSearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query) || query == (string)ChatSearchBox.Tag)
        {
            _aiOperatorVm.FilterChatHistory(null);
            return;
        }
        _aiOperatorVm.FilterChatHistory(query);
    }

    private static Brush FindThemeBrush(string key) =>
        System.Windows.Application.Current.FindResource(key) as Brush ?? Brushes.Transparent;

    /// <summary>
    /// Shows an inline approval card in the chat with Allow/Deny/Allow All buttons.
    /// Remains in code-behind because it dynamically creates WPF elements.
    /// </summary>
    private void ShowInlineApprovalCard(AgentStreamEvent.ApprovalRequested approval)
    {
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
            Text = "\u26a0 Tool Approval Required",
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
            Content = "\u2713 Allow",
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
            Content = "\u2713 Allow All",
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
            Content = "\u2717 Deny",
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
            var label = approved
                ? $"\u2713 Approved: {approval.ToolName}"
                : $"\u2717 Denied: {approval.ToolName}";
            CollapseCard(card, label, approved);
            _aiOperatorVm.ResolveApproval(approval, approved);
        }

        void ResolveAllPending(bool approved)
        {
            // Collapse all approval cards in the UI
            var suffix = approved ? "Approved" : "Denied";
            foreach (var child in AiChatContainer.Children.OfType<Border>().ToList())
            {
                if (child.Tag as string != "approval-card") continue;
                CollapseCard(child, $"\u2713 {suffix} (Allow All)", approved);
            }
            _aiOperatorVm.ResolveAllPending(approved);
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

    // ─── Address Table Export / Import / Trainer → AddressTableViewModel commands ──

    // ─── Address Table thin wrappers (logic in AddressTableViewModel) ──

    private void SyncAddressTableSelection()
    {
        _addressTableVm.SelectedNode = AddressTableTree.SelectedItem as AddressTableNode;
    }

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
        await _addressTableVm.HandleActiveCheckBoxClickAsync(node);
    }

    private void AddressTableTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SyncAddressTableSelection();
        _addressTableVm.EditSelectedNode();
    }

    private void AddressTableTree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        SyncAddressTableSelection();
        if (_addressTableVm.SelectedNode is null) return;

        switch (e.Key)
        {
            case System.Windows.Input.Key.Delete:
                _addressTableVm.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Space:
                _addressTableVm.ToggleActivateCommand.Execute(null);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F2:
                _addressTableVm.ChangeDescriptionCommand.Execute(null);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter when !_addressTableVm.SelectedNode.IsGroup && !_addressTableVm.SelectedNode.IsScriptEntry:
                _addressTableVm.EditNodeValue(_addressTableVm.SelectedNode);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F5:
                _addressTableVm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.C:
                    _addressTableVm.CopyCommand.Execute(null);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.X:
                    _addressTableVm.CutCommand.Execute(null);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.V:
                    _addressTableVm.PasteCommand.Execute(null);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.F:
                    _addressTableVm.ToggleFreezeCommand.Execute(null);
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

    // Ctx* context menu handlers → delegated to AddressTableViewModel via XAML Command bindings

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

    private void OnLoadCheatTable(object sender, RoutedEventArgs e) => _addressTableVm.LoadCheatTableCommand.Execute(null);
    private void OnSaveCheatTable(object sender, RoutedEventArgs e) => _addressTableVm.SaveCheatTableCommand.Execute(null);

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
            var sessions = (await _sessionService.ListSessionsAsync(20)).ToList();
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

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            System.Windows.Controls.DockPanel.SetDock(buttonPanel, System.Windows.Controls.Dock.Bottom);

            var loadBtn = new System.Windows.Controls.Button { Content = "Load", Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
            loadBtn.Click += (_, _) => { dialogWindow.DialogResult = true; dialogWindow.Close(); };

            var deleteBtn = new System.Windows.Controls.Button
            {
                Content = "Delete", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0),
                Foreground = System.Windows.Media.Brushes.OrangeRed
            };
            deleteBtn.Click += async (_, _) =>
            {
                if (listBox.SelectedIndex < 0) return;
                var idx = listBox.SelectedIndex;
                var target = sessions[idx];
                var confirm = MessageBox.Show(
                    $"Delete session \"{target.Id}\"?\nThis cannot be undone.",
                    "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                await _sessionService.DeleteSessionAsync(target.Id);
                sessions = (await _sessionService.ListSessionsAsync(20)).ToList();
                listBox.Items.Clear();
                foreach (var s in sessions)
                    listBox.Items.Add($"{s.Id}  —  {s.ProcessName}  ({s.AddressEntryCount} addresses, {s.CreatedAtUtc:g})");
                if (sessions.Count > 0) listBox.SelectedIndex = 0;
            };

            var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            cancelBtn.Click += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };

            buttonPanel.Children.Add(loadBtn);
            buttonPanel.Children.Add(deleteBtn);
            buttonPanel.Children.Add(cancelBtn);

            panel.Children.Add(buttonPanel);
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
            _addressTableVm.StopAutoRefresh();

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
        SyncAddressTableSelection();
        _addressTableVm.ToggleSelectedScriptCommand.Execute(null);
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
            _addressTableVm.StopAutoRefresh();

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

    private async void ProcessComboBox_DropDownOpened(object sender, EventArgs e)
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
                StatusMessage = dashboard.StatusMessage
            };
            PopulateProcessCombo();
        }
        catch { /* swallow — non-critical refresh */ }
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
        combo.ItemsSource = Enum.GetValues<BreakpointType>();
        combo.SelectedItem = BreakpointType.Software;
    }

    // Breakpoint list/hit log/remove moved to dedicated Breakpoints bottom tab (Phase 2).
    // Set BP controls remain in Inspection panel for inline use.
    // SetBreakpoint moved to InspectionViewModel (Phase 2.5).

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

    // ── Drag and Drop: AI Panel Target (thin wrappers — needs DragEventArgs) ──

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

        _aiOperatorVm.AddAttachment("Context", context);
        AiChatInputTextBox.Focus();
        e.Handled = true;
    }

    // ── Paste Handler (thin wrapper — needs DataObjectPastingEventArgs) ──

    private void OnChatInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (text is not null && (text.Contains('\n') || text.Length > 300))
            {
                e.CancelCommand();
                _aiOperatorVm.AddAttachment(text.Contains('\n') ? "Pasted" : "Pasted text", text);
            }
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var chip = _aiOperatorVm.Attachments.FirstOrDefault(a => a.Id == id);
            if (chip is not null)
                _aiOperatorVm.RemoveAttachmentCommand.Execute(chip);
        }
    }

    private void AttachContext_Click(object sender, RoutedEventArgs e) =>
        _aiOperatorVm.AttachContextCommand.Execute(null);

    // ── Model Selector (popup is WPF-specific, stays in code-behind) ──

    private async void ModelSelector_Click(object sender, RoutedEventArgs e)
    {
        if (ModelSelectorPopup.IsOpen)
        {
            ModelSelectorPopup.IsOpen = false;
            return;
        }

        ModelSelectorList.Children.Clear();

        var currentModel = _appSettingsService.Settings.Model;
        var models = await _aiOperatorVm.GetAvailableModelsAsync();

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
        _aiOperatorVm.SelectModelCommand.Execute(model);
    }

    private void ApplyDockTheme(AppTheme resolved)
    {
        DockManager.Theme = resolved == AppTheme.Light
            ? new Vs2013LightTheme()
            : new Vs2013DarkTheme();
    }

    /// <summary>Update system selection colors and text colors from current theme resources.</summary>
    private void UpdateSystemSelectionColors()
    {
        if (TryFindResource("SelectionHighlight") is SolidColorBrush highlight)
        {
            Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(highlight.Color);
            Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(highlight.Color);
            Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(highlight.Color);
        }
        if (TryFindResource("SelectionHighlightText") is SolidColorBrush highlightText)
        {
            Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(highlightText.Color);
            Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = new SolidColorBrush(highlightText.Color);
        }

        // Override system text colors so native WPF controls (menus, tooltips, etc.)
        // use our theme foreground instead of OS defaults.
        if (TryFindResource("PrimaryForeground") is SolidColorBrush fg)
        {
            var fgBrush = new SolidColorBrush(fg.Color);
            Resources[SystemColors.ControlTextBrushKey] = fgBrush;
            Resources[SystemColors.WindowTextBrushKey] = fgBrush;
            Resources[SystemColors.MenuTextBrushKey] = fgBrush;
            Resources[SystemColors.InfoTextBrushKey] = fgBrush;
        }
        if (TryFindResource("WindowBackground") is SolidColorBrush bg)
        {
            Resources[SystemColors.WindowBrushKey] = new SolidColorBrush(bg.Color);
        }
        if (TryFindResource("MenuBackground") is SolidColorBrush menuBg)
        {
            Resources[SystemColors.MenuBrushKey] = new SolidColorBrush(menuBg.Color);
            Resources[SystemColors.MenuBarBrushKey] = new SolidColorBrush(menuBg.Color);
        }
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

    private void ActivateAnchorable(string contentId)
    {
        var anc = DockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);
        if (anc is not null)
        {
            anc.IsVisible = true;
            anc.IsActive = true;
        }
    }

    /// <summary>Show/restore a panel by ContentId (from Windows menu Tag).</summary>
    private void ShowPanel(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string contentId)
            return;

        // Try as document first (Address Table, Inspection, Memory Browser)
        var doc = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (doc is not null)
        {
            doc.IsActive = true;
            return;
        }

        // Try as anchorable (Processes, Scanner, Output, AI Operator)
        var anchorable = DockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);

        if (anchorable is not null)
        {
            // Panel exists in layout — show it if auto-hidden or hidden
            if (anchorable.IsAutoHidden)
                anchorable.ToggleAutoHide();
            anchorable.Show();
            anchorable.IsActive = true;
            return;
        }

        // Panel was fully closed — find its content and re-add it
        var content = FindContentByContentId(contentId);
        if (content is null)
        {
            // Try the user-closed stash first, then the XAML-defined content stash
            if (_closedPanelContent.TryGetValue(contentId, out var stashed))
                content = stashed;
            else if (_xamlPanelContent.TryGetValue(contentId, out var xamlStashed))
                content = xamlStashed;
        }

        if (content is not null)
        {
            var restored = new LayoutAnchorable
            {
                ContentId = contentId,
                Title = GetPanelTitle(contentId),
                Content = content
            };

            var targetPane = FindTargetPaneForContentId(contentId);
            if (targetPane is not null)
            {
                targetPane.Children.Add(restored);
            }
            else
            {
                var newPane = new LayoutAnchorablePane(restored);
                DockManager.Layout.RootPanel?.Children.Add(newPane);
            }

            restored.IsActive = true;
        }
    }

    private static string GetPanelTitle(string contentId) => contentId switch
    {
        "processes" => "Processes",
        "scanner" => "Scanner",
        "output" => "Output",
        "aiOperator" => "AI Operator",
        "addressTable" => "Address Table",
        "inspection" => "Inspection",
        "memoryBrowser" => "Memory Browser",
        "breakpoints" => "Breakpoints",
        "scripts" => "Scripts",
        "snapshots" => "Snapshots",
        "findResults" => "Find Results",
        "hotkeys" => "Hotkeys",
        "journal" => "Journal",
        _ => contentId
    };

    #region Layout Persistence

    private void SaveLayout()
    {
        try
        {
            var dir = Path.GetDirectoryName(LayoutFilePath)!;
            Directory.CreateDirectory(dir);
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.Serialize(LayoutFilePath);
            File.WriteAllText(LayoutVersionPath, LayoutVersion.ToString());
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

            // Auto-reset layout when the expected panel structure has changed
            var savedVersion = 0;
            try { if (File.Exists(LayoutVersionPath)) savedVersion = int.Parse(File.ReadAllText(LayoutVersionPath).Trim()); } catch { }
            if (savedVersion < LayoutVersion)
            {
                File.Delete(LayoutFilePath);
                return; // Use XAML defaults
            }

            // Stash all XAML-defined panel content BEFORE deserialization replaces the layout tree.
            // This lets us re-inject panels the saved layout doesn't know about (e.g. newly added tabs).
            // Also stored in _xamlPanelContent so ShowPanel can restore them later on demand.
            foreach (var item in DockManager.Layout.Descendents().OfType<LayoutContent>())
            {
                if (item.ContentId is not null && item.Content is not null)
                    _xamlPanelContent[item.ContentId] = item.Content;
            }
            var xamlContent = _xamlPanelContent;

            var restoredIds = new HashSet<string>();

            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                // Preserve existing content controls — match by ContentId
                if (args.Content != null)
                {
                    if (args.Model?.ContentId is not null)
                        restoredIds.Add(args.Model.ContentId);
                    return;
                }

                var contentId = args.Model?.ContentId;
                if (string.IsNullOrEmpty(contentId)) { args.Cancel = true; return; }

                if (xamlContent.TryGetValue(contentId, out var existing))
                {
                    args.Content = existing;
                    restoredIds.Add(contentId);
                }
                else
                    args.Cancel = true;
            };
            serializer.Deserialize(LayoutFilePath);

            // Re-add any XAML-defined panels that weren't in the saved layout
            // (i.e. newly added tabs since the layout was last saved).
            foreach (var (contentId, content) in xamlContent)
            {
                if (restoredIds.Contains(contentId)) continue;

                var anchorable = new LayoutAnchorable
                {
                    ContentId = contentId,
                    Title = GetPanelTitle(contentId),
                    Content = content
                };

                var targetPane = FindTargetPaneForContentId(contentId);
                if (targetPane is not null)
                    targetPane.Children.Add(anchorable);
            }
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
        try { File.Delete(LayoutVersionPath); } catch { }
        MessageBox.Show("Layout reset. Restart the application to apply the default layout.",
            "Reset Layout", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Lookup the original XAML-defined content control by ContentId.</summary>
    /// <summary>
    /// Find the correct LayoutAnchorablePane to place a panel based on its role:
    /// - "processes" → left sidebar (pane containing "processes")
    /// - "aiOperator" → right sidebar (pane containing "aiOperator")
    /// - Everything else → bottom pane (pane containing "scanner" or "output")
    /// Falls back to any available pane if the preferred one isn't found.
    /// </summary>
    private LayoutAnchorablePane? FindTargetPaneForContentId(string contentId)
    {
        var allPanes = DockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorablePane>()
            .ToList();

        // Determine which sibling ContentId to look for in existing panes
        var siblingId = contentId switch
        {
            "processes" => "processes",
            "aiOperator" => "aiOperator",
            _ => "scanner" // all Phase 2 tabs belong alongside Scanner/Output
        };

        // Find the pane that already contains the sibling
        var preferred = allPanes.FirstOrDefault(pane =>
            pane.Children.Any(c => c.ContentId == siblingId));

        return preferred ?? allPanes.FirstOrDefault();
    }

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
                // Processes auto-hidden (toolbar handles attach), everything else visible
                SetAnchorableVisibility(anchorables, "processes", autoHide: true);
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

    #region Phase 2 — Bottom Panel Handlers

    private void AppendOutputLog(string source, string level, string message)
    {
        _outputLog.Append(source, level, message);
    }

    /// <summary>Forwards to FindResultsViewModel (called from other MainWindow code).</summary>
    public void PopulateFindResults(IReadOnlyList<FindResultDisplayItem> items, string description)
    {
        _findResultsVm.Populate(items, description);
    }

    #endregion
}
