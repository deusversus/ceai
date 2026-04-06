using System.Collections.ObjectModel;
using System.IO;
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
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Extensions.Logging;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace CEAISuite.Desktop;

public partial class MainWindow : Window
{
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly AppSettingsService _appSettingsService;
    private readonly AiOperatorService _aiOperatorService;
    private readonly IEngineFacade _engineFacade;
    private readonly IOutputLog _outputLog;
    private readonly ILogger<MainWindow> _logger;
    private readonly ObservableCollection<OutputLogEntry> _outputLogEntries;

    // ── ViewModels ──
    private readonly MainViewModel _mainVm;
    private readonly AddressTableViewModel _addressTableVm;
    private readonly InspectionViewModel _inspectionVm;
    private readonly ProcessListViewModel _processListVm;
    private readonly AiOperatorViewModel _aiOperatorVm;
    private readonly DisassemblerViewModel _disassemblerVm;
    private readonly StructureDissectorViewModel _structureDissectorVm;
    private readonly MemoryBrowserViewModel _memoryBrowserVm;

    private readonly Dictionary<string, object> _closedPanelContent = new();
    private readonly Dictionary<string, object> _xamlPanelContent = new();

    // ── Stored event handlers (for unsubscription in OnClosed) ──
    private Action<string>? _statusChangedHandler;
    private Action? _chatListChangedHandler;
    private Action? _settingsChangedHandler;
    private Action<AppTheme>? _themeChangedHandler;
    private Action<string, string>? _contextRequestedHandler;
    private IAiContextService? _aiContextService;

    // Bump this version whenever the default panel layout changes (e.g. new tabs added).
    // A mismatch auto-deletes the saved layout so XAML defaults apply cleanly.
    private const int LayoutVersion = 18; // v18 = widen left sidebar from 250 → 420

    private static readonly string LayoutFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CEAISuite", "layout.xml");
    private static readonly string LayoutVersionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CEAISuite", "layout.version");

    public MainWindow(
        IEngineFacade engineFacade,
        GlobalHotkeyService hotkeyService,
        AppSettingsService appSettingsService,
        AiOperatorService aiOperatorService,
        IOutputLog outputLog,
        NavigationService navigationService,
        MainViewModel mainVm,
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
        AiOperatorViewModel aiOperatorVm,
        DisassemblerViewModel disassemblerVm,
        StructureDissectorViewModel structureDissectorVm,
        PointerScannerViewModel pointerScannerVm,
        ScriptEditorViewModel scriptEditorVm,
        DebuggerViewModel debuggerVm,
        ModuleListViewModel moduleListVm,
        ThreadListViewModel threadListVm,
        MemoryRegionsViewModel memoryRegionsVm,
        WorkspaceViewModel workspaceVm,
        MemoryBrowserViewModel memoryBrowserVm,
        IAiContextService aiContextService,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();

        _logger = logger;
        _engineFacade = engineFacade;
        _hotkeyService = hotkeyService;
        _appSettingsService = appSettingsService;
        _aiOperatorService = aiOperatorService;
        _outputLog = outputLog;
        _outputLogEntries = outputLog.Entries;

        // Assign ViewModels
        _mainVm = mainVm;
        _addressTableVm = addressTableVm;
        _inspectionVm = inspectionVm;
        _processListVm = processListVm;
        _aiOperatorVm = aiOperatorVm;
        _disassemblerVm = disassemblerVm;
        _structureDissectorVm = structureDissectorVm;
        _memoryBrowserVm = memoryBrowserVm;

        // Wire NavigationService to AvalonDock (with parameter routing)
        navigationService.Configure(
            (contentId, parameter) =>
            {
                ActivateDocument(contentId);
                if (parameter is string addrStr && !string.IsNullOrWhiteSpace(addrStr))
                    RouteNavigationParameter(contentId, addrStr);
            },
            contentId => ActivateAnchorable(contentId));

        // Apply saved theme
        var savedTheme = Enum.TryParse<AppTheme>(_appSettingsService.Settings.Theme, true, out var theme)
            ? theme : AppTheme.System;
        ThemeManager.ApplyTheme(savedTheme);
        ApplyDockTheme(ThemeManager.ResolvedTheme);
        UpdateSystemSelectionColors();
        _themeChangedHandler = t => { ApplyDockTheme(t); UpdateSystemSelectionColors(); };
        ThemeManager.ThemeChanged += _themeChangedHandler;

        // Restore saved panel layout (must happen after InitializeComponent + theme)
        RestoreLayout();
        EnforceLeftSidebarWidth();

        // Add scroll arrows + dropdown to tab strips when tabs overflow
        Controls.DocumentTabScrollHelper.Attach(DockManager);

        // Stash panel content when user closes a panel so we can restore it from Windows menu
        DockManager.AnchorableClosing += (_, args) =>
        {
            if (args.Anchorable is { ContentId: not null, Content: not null })
                _closedPanelContent[args.Anchorable.ContentId] = args.Anchorable.Content;
        };

        // Apply saved density preset (after layout restore so it can override visibility)
        ApplyDensityPreset(_appSettingsService.Settings.DensityPreset ?? "Balanced");

        // Live status updates from AI agent
        _statusChangedHandler = status =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _aiOperatorVm.StatusText = status;
                _mainVm.AppendOutputLog("Agent", "Info", status);
            });
        };
        _aiOperatorService.StatusChanged += _statusChangedHandler;
        OutputLogList.ItemsSource = _outputLogEntries;

        // Wire bottom-panel DataContexts via x:Name content roots
        OutputContent.DataContext = outputLogVm;
        BreakpointsContent.DataContext = breakpointsVm;
        ScriptsContent.DataContext = scriptsVm;
        SnapshotsContent.DataContext = snapshotsVm;
        FindResultsContent.DataContext = findResultsVm;
        HotkeysContent.DataContext = hotkeysVm;
        JournalContent.DataContext = journalVm;

        // Wire Phase 2.5 panel DataContexts
        ScannerContent.DataContext = scannerVm;
        ProcessesContent.DataContext = processListVm;
        InspectionContent.DataContext = inspectionVm;
        AddressTableContent.DataContext = addressTableVm;

        // Wire Phase 3 center-tab DataContexts
        DisassemblerContent.DataContext = disassemblerVm;
        StructureDissectorContent.DataContext = structureDissectorVm;
        PointerScannerContent.DataContext = pointerScannerVm;
        ScriptEditorContent.DataContext = scriptEditorVm;
        SetupScriptEditorHighlighting(scriptEditorVm);
        DebuggerContent.DataContext = debuggerVm;

        // Wire Phase 4 sidebar DataContexts
        ModulesContent.DataContext = moduleListVm;
        ThreadsContent.DataContext = threadListVm;
        MemoryRegionsContent.DataContext = memoryRegionsVm;
        WorkspaceContent.DataContext = workspaceVm;

        // Wire Phase 5 Memory Browser ViewModel
        MemoryBrowserTab.SetViewModel(memoryBrowserVm);

        // Wire Workspace events to MainViewModel session logic
        workspaceVm.LoadSessionRequested += sessionId => _ = _mainVm.RestoreSession(sessionId);
        workspaceVm.LoadCheatTableRequested += _ => _addressTableVm.LoadCheatTableCommand.Execute(null);

        // Auto-populate workspace session list on startup
        _ = workspaceVm.RefreshCommand.ExecuteAsync(null);

        // Wire AI Operator ViewModel
        AiOperatorContent.DataContext = aiOperatorVm;
        _aiOperatorVm.ScrollToBottomRequested += () => Dispatcher.BeginInvoke(() => AiChatScrollViewer.ScrollToEnd());
        _aiOperatorVm.StreamingBlocksUpdated += () => Dispatcher.BeginInvoke(() =>
        {
            StreamingBlocksList.Items.Refresh();
            AiChatScrollViewer.ScrollToEnd();
        });
        _aiOperatorVm.ChatDisplayRefreshed += () => Dispatcher.BeginInvoke(() =>
        {
            for (int i = AiChatContainer.Children.Count - 1; i >= 0; i--)
            {
                var child = AiChatContainer.Children[i];
                if (child != AiChatList && child != StreamingBlocksList)
                    AiChatContainer.Children.RemoveAt(i);
            }
        });
        _aiOperatorVm.ApprovalCardRequested += approval => ShowInlineApprovalCard(approval);

        // Subscribe to AddressTableViewModel navigation events
        _addressTableVm.NavigateToMemoryBrowser += addr =>
        {
            // Ensure Memory Browser is attached (idempotent if already attached)
            MemoryBrowserTab.AttachProcess();
            ActivateDocument("memoryBrowser");
            _ = MemoryBrowserTab.NavigateToAddress(addr);
        };
        _addressTableVm.NavigateToDisassembly += addrStr =>
        {
            disassemblerVm.NavigateToAddress(addrStr);
            ActivateDocument("disassembler");
        };
        _addressTableVm.PopulateFindResults += (items, desc) =>
        {
            _mainVm.PopulateFindResults(items, desc);
            ActivateAnchorable("findResults");
        };
        disassemblerVm.PopulateFindResults += (items, desc) =>
        {
            _mainVm.PopulateFindResults(items, desc);
            ActivateAnchorable("findResults");
        };

        // Refresh chat switcher when chats change
        _chatListChangedHandler = () =>
        {
            Dispatcher.BeginInvoke(() => _aiOperatorVm.RefreshChatSwitcher());
        };
        _aiOperatorService.ChatListChanged += _chatListChangedHandler;

        _settingsChangedHandler = () =>
        {
            Dispatcher.Invoke(() => _mainVm.HandleSettingsChanged());
        };
        _appSettingsService.SettingsChanged += _settingsChangedHandler;

        // Wire MainViewModel UI action requests
        _mainVm.UiActionRequested += OnMainVmUiAction;

        // Sync Dashboard → DataContext
        _mainVm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Dashboard))
                DataContext = _mainVm.Dashboard;
            else if (args.PropertyName == nameof(MainViewModel.StatusBarProcessText))
                StatusBarProcess.Text = _mainVm.StatusBarProcessText;
            else if (args.PropertyName == nameof(MainViewModel.StatusBarCenterText))
                StatusBarCenter.Text = _mainVm.StatusBarCenterText;
            else if (args.PropertyName == nameof(MainViewModel.StatusBarTokenText))
                StatusBarTokens.Text = _mainVm.StatusBarTokenText;
            else if (args.PropertyName == nameof(MainViewModel.StatusBarScanText))
                StatusBarScan.Text = _mainVm.StatusBarScanText;
            else if (args.PropertyName == nameof(MainViewModel.StatusBarWatchdogText))
                StatusBarWatchdog.Text = _mainVm.StatusBarWatchdogText;
            else if (args.PropertyName == nameof(MainViewModel.ProcessComboItems))
                ProcessComboBox.ItemsSource = _mainVm.ProcessComboItems;
            else if (args.PropertyName == nameof(MainViewModel.SelectedProcessComboItem))
                ProcessComboBox.SelectedItem = _mainVm.SelectedProcessComboItem;
        };

        // Wire universal "Ask AI" context service
        _aiContextService = aiContextService;
        _contextRequestedHandler = (label, context) =>
        {
            _aiOperatorVm.AddAttachment(label, context);
            ActivateAnchorable("aiOperator");
            AiChatInputTextBox.Focus();
        };
        aiContextService.ContextRequested += _contextRequestedHandler;

        DataContext = _mainVm.Dashboard;
        Loaded += OnLoaded;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    private void OnMainVmUiAction(string action, object? param)
    {
        switch (action)
        {
            case "OpenMemoryBrowser" when param is RunningProcessOverview proc:
                MemoryBrowserTab.AttachProcess();
                ActivateDocument("memoryBrowser");
                break;
            case "DetachCleanup":
                MemoryBrowserTab.Clear();
                break;
            case "SaveScreenshot" when param is CEAISuite.Engine.Abstractions.ScreenCaptureResult result:
                var screenshotDlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PNG Image|*.png",
                    DefaultExt = ".png",
                    FileName = $"capture_{result.WindowTitle.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd_HHmmss}.png"
                };
                if (screenshotDlg.ShowDialog() == true)
                {
                    System.IO.File.WriteAllBytes(screenshotDlg.FileName, result.PngData);
                    _mainVm.StatusBarCenterText = $"Screenshot saved: {screenshotDlg.FileName} ({result.Width}x{result.Height})";
                }
                break;
            case "SaveReport" when param is string markdown:
                var reportDlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Markdown|*.md|Text|*.txt",
                    DefaultExt = ".md",
                    FileName = $"report_{DateTime.Now:yyyyMMdd_HHmmss}.md"
                };
                if (reportDlg.ShowDialog() == true)
                {
                    System.IO.File.WriteAllText(reportDlg.FileName, markdown);
                    _mainVm.StatusBarCenterText = $"Report exported: {reportDlg.FileName}";
                }
                break;
        }
    }

    private void RouteNavigationParameter(string contentId, string addrStr)
    {
        switch (contentId)
        {
            case "memoryBrowser":
                MemoryBrowserTab.AttachProcess();
                var cleaned = addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? addrStr[2..] : addrStr;
                if (ulong.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var mbAddr))
                    _ = MemoryBrowserTab.NavigateToAddress((nuint)mbAddr);
                break;
            case "disassembler":
                _disassemblerVm.NavigateToAddress(addrStr);
                break;
            case "structureDissector":
                _structureDissectorVm.NavigateToAddress(addrStr);
                break;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Hook WndProc for global hotkeys
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _hotkeyService.SetWindowHandle(hwnd);
        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        await _mainVm.InitializeAsync();

        // Wire up paste handler (thin wrapper -- ViewModel handles attachment logic)
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
            handled = _hotkeyService.HandleHotkeyMessage(wParam.ToInt32());
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveLayout();
        _mainVm.OnClosing();
        _hotkeyService.Dispose();

        // Unsubscribe from all service/static events to allow clean GC
        if (_themeChangedHandler is not null)
            ThemeManager.ThemeChanged -= _themeChangedHandler;
        if (_statusChangedHandler is not null)
            _aiOperatorService.StatusChanged -= _statusChangedHandler;
        if (_chatListChangedHandler is not null)
            _aiOperatorService.ChatListChanged -= _chatListChangedHandler;
        if (_settingsChangedHandler is not null)
            _appSettingsService.SettingsChanged -= _settingsChangedHandler;
        if (_contextRequestedHandler is not null && _aiContextService is not null)
            _aiContextService.ContextRequested -= _contextRequestedHandler;
        _mainVm.UiActionRequested -= OnMainVmUiAction;

        // Clean up ViewModel event subscriptions
        _inspectionVm.Cleanup();
        _processListVm.Cleanup();
        _memoryBrowserVm.Cleanup();

        base.OnClosed(e);
    }

    // ─── Process Inspection (delegates to MainViewModel) ──────────────────

    private async void InspectSelectedProcess(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard) return;
        await _mainVm.InspectSelectedProcessAsync();
    }

    // ─── ComboBox Loaded Handlers (UI init only) ─────────────────────────

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

    // ─── AI Operator (thin wrappers -- logic in AiOperatorViewModel) ─────

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

    private void ChatHistoryPanel_Opened(object? sender, EventArgs e) =>
        _aiOperatorVm.RefreshChatSwitcher();

    private void ChatHistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is ChatHistoryDisplayItem selected)
            _aiOperatorVm.SwitchChatCommand.Execute(selected);
    }

    private void ChatCtx_Open(object sender, RoutedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is ChatHistoryDisplayItem selected)
            _aiOperatorVm.OpenChat(selected);
    }

    private void ChatCtx_Rename(object sender, RoutedEventArgs e)
    {
        if (ChatHistoryList.SelectedItem is not ChatHistoryDisplayItem selected) return;
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
        var btn = new Button { Content = "Rename", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        btn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        tb.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) { dlg.DialogResult = true; dlg.Close(); } };
        sp.Children.Add(tb);
        sp.Children.Add(btn);
        dlg.Content = sp;
        tb.Focus();
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text))
            _aiOperatorVm.RenameChat(selected, tb.Text.Trim());
    }

    private void ChatCtx_Delete(object sender, RoutedEventArgs e) =>
        DeleteAiChat(sender, e);

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
            _aiOperatorVm.FilterChatHistory(null);
            return;
        }
        _aiOperatorVm.FilterChatHistory(query);
    }

    private static Brush FindThemeBrush(string key) =>
        System.Windows.Application.Current.FindResource(key) as Brush ?? Brushes.Transparent;

    // ── Inline approval block handlers (streaming content blocks) ──

    private void ApprovalBlock_Allow(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Models.ApprovalBlock block })
            block.Resolve?.Invoke(true);
    }

    private void ApprovalBlock_Deny(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Models.ApprovalBlock block })
            block.Resolve?.Invoke(false);
    }

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
        stack.Children.Add(new TextBlock
        {
            FontWeight = FontWeights.SemiBold, FontSize = 12,
            Foreground = FindThemeBrush("WarningForeground"),
            Text = "\u26a0 Tool Approval Required",
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = FindThemeBrush("PrimaryForeground"),
            Text = string.IsNullOrEmpty(approval.Arguments)
                ? approval.ToolName
                : $"{approval.ToolName}({approval.Arguments})"
        });

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var allowBtn = new Button
        {
            Content = "\u2713 Allow", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(34, 139, 34)), Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, FontSize = 11, Cursor = Cursors.Hand,
        };
        var allowAllBtn = new Button
        {
            Content = "\u2713 Allow All", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(26, 110, 26)), Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, FontSize = 11, Cursor = Cursors.Hand,
            ToolTip = "Approve this and all future tool calls this session",
        };
        var denyBtn = new Button
        {
            Content = "\u2717 Deny", Padding = new Thickness(12, 4, 12, 4),
            Background = new SolidColorBrush(Color.FromRgb(180, 50, 50)), Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, FontSize = 11, Cursor = Cursors.Hand,
        };

        void CollapseCard(Border target, string label, bool approved)
        {
            var color = approved ? Color.FromRgb(34, 139, 34) : Color.FromRgb(180, 50, 50);
            target.Padding = new Thickness(8, 4, 8, 4);
            target.Background = Brushes.Transparent;
            target.BorderBrush = Brushes.Transparent;
            target.BorderThickness = new Thickness(0);
            target.Child = new TextBlock
            {
                Text = label, FontSize = 10, FontStyle = FontStyles.Italic,
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

    // ─── Address Table thin wrappers (logic in AddressTableViewModel) ────

    private void SyncAddressTableSelection()
    {
        _addressTableVm.SelectedNode = AddressTableTree.SelectedItem as AddressTableNode;
    }

    private async void ActiveCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not AddressTableNode node) return;
        await _addressTableVm.HandleActiveCheckBoxClickAsync(node);
    }

    private void AddressTableTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SyncAddressTableSelection();
        _addressTableVm.EditSelectedNode();
    }

    private void SortAddressTable(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string column)
            _addressTableVm.SortByCommand.Execute(column);
    }

    private void AddressTableTree_KeyDown(object sender, KeyEventArgs e)
    {
        SyncAddressTableSelection();
        if (_addressTableVm.SelectedNode is null) return;

        switch (e.Key)
        {
            case Key.Delete:
                _addressTableVm.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space:
                _addressTableVm.ToggleActivateCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                _addressTableVm.ChangeDescriptionCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when !_addressTableVm.SelectedNode.IsGroup && !_addressTableVm.SelectedNode.IsScriptEntry:
                _addressTableVm.EditNodeValue(_addressTableVm.SelectedNode);
                e.Handled = true;
                break;
            case Key.F5:
                _addressTableVm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C:
                    _addressTableVm.CopyCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.X:
                    _addressTableVm.CutCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.V:
                    _addressTableVm.PasteCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.F:
                    _addressTableVm.ToggleFreezeCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Z:
                    _ = _mainVm.PerformUndoAsync();
                    e.Handled = true;
                    break;
                case Key.Y:
                    _ = _mainVm.PerformRedoAsync();
                    e.Handled = true;
                    break;
            }
        }
    }

    private void OnLoadCheatTable(object sender, RoutedEventArgs e) => _addressTableVm.LoadCheatTableCommand.Execute(null);
    private void OnSaveCheatTable(object sender, RoutedEventArgs e) => _addressTableVm.SaveCheatTableCommand.Execute(null);

    // ─── Session Save / Load (UI dialog in code-behind, logic in MainViewModel) ──

    private async void SaveSession(object sender, RoutedEventArgs e) => await _mainVm.SaveSessionAsync();

    private async void LoadSession(object sender, RoutedEventArgs e)
    {
        var sessions = await _mainVm.ListSessionsAsync();
        if (sessions is null) return;

        var dialog = new LoadSessionWindow(
            sessions,
            _mainVm.DeleteSessionAsync,
            _mainVm.RefreshSessionListAsync)
        { Owner = this };

        if (dialog.ShowDialog() == true && dialog.SelectedSessionId is not null)
            await _mainVm.RestoreSession(dialog.SelectedSessionId);
    }

    // ── Memory Browser ──

    private void OpenMemoryBrowser(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard || dashboard.CurrentInspection is null)
        {
            MessageBox.Show("Attach to a process first.", "Memory Browser");
            return;
        }
        MemoryBrowserTab.AttachProcess();
        ActivateDocument("memoryBrowser");
    }

    // ── Menu bar handlers ──

    private void OpenSkillsManager(object sender, RoutedEventArgs e)
    {
        var window = new SkillsManagerWindow();
        window.Owner = this;
        window.ShowDialog();
        if (window.SkillsChanged)
            _mainVm.ReconfigureAiAfterSkillsChange();
    }

    private void OpenMcpManager(object sender, RoutedEventArgs e)
    {
        var window = new McpManagerWindow(_appSettingsService);
        window.Owner = this;
        window.ShowDialog();
    }

    private void OpenSkillsFolder(object sender, RoutedEventArgs e) => _mainVm.OpenSkillsFolder();

    private void ReloadSkills(object sender, RoutedEventArgs e)
    {
        var (success, errorMessage) = _mainVm.ReloadSkills();
        if (success)
            MessageBox.Show("Skills reloaded successfully.", "Skills", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show($"Failed to reload: {errorMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_appSettingsService);
        settingsWindow.Owner = this;
        settingsWindow.ShowDialog();
    }

    private void ExitApp(object sender, RoutedEventArgs e) => Close();

    private async void MenuUndo(object sender, RoutedEventArgs e) => await _mainVm.PerformUndoAsync();
    private async void MenuRedo(object sender, RoutedEventArgs e) => await _mainVm.PerformRedoAsync();

    private void ShowAbout(object sender, RoutedEventArgs e) => _mainVm.ShowAbout();

    private async void CaptureScreenshot(object sender, RoutedEventArgs e) => await _mainVm.CaptureScreenshotAsync();
    private async void ExportReport(object sender, RoutedEventArgs e) => await _mainVm.ExportReportAsync();

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ProcessComboBox.Focus();
            ProcessComboBox.IsDropDownOpen = true;
            e.Handled = true;
        }
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CmdFocusScanner(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Up && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _addressTableVm.IncreaseValueCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _addressTableVm.DecreaseValueCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Breakpoint handlers ──

    private void OnBpTypeComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        var combo = (ComboBox)sender;
        combo.ItemsSource = Enum.GetValues<BreakpointType>();
        combo.SelectedItem = BreakpointType.Software;
    }

    #region Command Bar Handlers

    private void CmdAttachProcess(object sender, RoutedEventArgs e)
    {
        if (ProcessComboBox.SelectedItem is ProcessComboItem item)
        {
            // Find the matching process in the Processes tab list and select it
            var match = _processListVm.Processes.FirstOrDefault(p => p.Id == item.Pid);
            if (match is not null)
            {
                _processListVm.SelectedProcess = match;
                InspectSelectedProcess(sender, e);
                return;
            }
        }
        StatusBarCenter.Text = "Select a process from the dropdown first";
    }

    private async void CmdDetachProcess(object sender, RoutedEventArgs e) => await _mainVm.DetachProcessAsync();

    private void CmdFocusScanner(object sender, RoutedEventArgs e)
    {
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
        SyncAddressTableSelection();
        _addressTableVm.ToggleSelectedScriptCommand.Execute(null);
    }

    private async void CmdEmergencyStop(object sender, RoutedEventArgs e) => await _mainVm.EmergencyStopAsync();

    #endregion

    private async void ProcessComboBox_DropDownOpened(object sender, EventArgs e) => await _mainVm.RefreshProcessComboAsync();

    // ─── Drag and Drop: Sources ──────────────────────────────────────────

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
            data.SetData("CEAIAddress", mod.BaseAddress);
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
                context = $"[Group] \"{node.Label}\" ({node.Children.Count} entries)";
            }
            else if (node.IsScriptEntry)
            {
                var status = node.IsScriptEnabled ? "ENABLED" : "disabled";
                context = $"[Script] \"{node.Label}\" \u2014 {status} (ID: {node.Id})";
            }
            else
            {
                var frozen = node.IsLocked ? " [FROZEN]" : "";
                context = $"[Address] \"{node.Label}\" @ {node.DisplayAddress} = {node.DisplayValue} ({node.DisplayType}){frozen} (ID: {node.Id})";
            }

            var data = new DataObject("CEAIContext", context);
            if (!node.IsGroup && !node.IsScriptEntry && !string.IsNullOrEmpty(node.DisplayAddress))
                data.SetData("CEAIAddress", node.DisplayAddress);
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
        _aiOperatorVm.AddAttachment("Context", context);
        AiChatInputTextBox.Focus();
        e.Handled = true;
    }

    // ── Drag and Drop: Scanner Source ──

    private void ScanResults_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStartPoint = e.GetPosition(null);

    private void ScanResults_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (ScanResultsList.SelectedItem is ScanResultOverview result)
        {
            var data = new DataObject("CEAIContext",
                $"[Scan Result] {result.Address} = {result.CurrentValue}");
            data.SetData("CEAIAddress", result.Address);
            DragDrop.DoDragDrop(ScanResultsList, data, DragDropEffects.Copy);
        }
    }

    // ── Drag and Drop: Cross-Panel Drop Targets ──

    private void AddressPanel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent("CEAIAddress") || e.Data.GetDataPresent("CEAIContext"))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void MemoryBrowser_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("CEAIAddress") is string addr)
        {
            MemoryBrowserTab.AttachProcess();
            RouteNavigationParameter("memoryBrowser", addr);
        }
        e.Handled = true;
    }

    private void Disassembler_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("CEAIAddress") is string addr)
            RouteNavigationParameter("disassembler", addr);
        e.Handled = true;
    }

    private void AddressTable_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("CEAIAddress") is string addr)
        {
            _addressTableVm.AddEntryFromDrop(addr);
        }
        e.Handled = true;
    }

    private void StructureDissector_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("CEAIAddress") is string addr)
            RouteNavigationParameter("structureDissector", addr);
        e.Handled = true;
    }

    // ── Paste Handler ──

    private void OnChatInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        // Check for clipboard image first (e.g., Ctrl+V after Print Screen)
        if (e.DataObject.GetDataPresent(DataFormats.Bitmap))
        {
            var bitmapSource = e.DataObject.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
            if (bitmapSource is not null)
            {
                e.CancelCommand();
                try
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));
                    using var ms = new System.IO.MemoryStream();
                    encoder.Save(ms);
                    _aiOperatorVm.AddImageFromBytes("Pasted image", ms.ToArray());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Paste image failed");
                }
                return;
            }
        }

        // Fall back to text paste handling
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

    // ── Model Selector (popup is WPF-specific) ──

    private async void ModelSelector_Click(object sender, RoutedEventArgs e)
    {
        if (ModelSelectorPopup.IsOpen) { ModelSelectorPopup.IsOpen = false; return; }
        await _aiOperatorVm.RefreshAvailableModelsCommand.ExecuteAsync(null);
        ModelSelectorList.ItemsSource = _aiOperatorVm.AvailableModels;
        ModelSelectorPopup.IsOpen = true;
    }

    private void ModelSelectorItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string model) return;
        var isCurrent = model == _appSettingsService.Settings.Model;
        btn.Foreground = (Brush)FindResource(isCurrent ? "AccentForeground" : "PrimaryForeground");
        btn.FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void ModelSelectorItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string model) return;
        ModelSelectorPopup.IsOpen = false;
        _aiOperatorVm.SelectModelCommand.Execute(model);
    }

    // ── Permission Mode Selector ──

    private void PermissionMode_Click(object sender, RoutedEventArgs e)
    {
        PermissionModePopup.IsOpen = !PermissionModePopup.IsOpen;
    }

    private void PermissionModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string mode)
        {
            _aiOperatorVm.SelectPermissionModeCommand.Execute(mode);
            PermissionModePopup.IsOpen = false;
        }
    }

    // ─── Theme ───────────────────────────────────────────────────────────

    private void ApplyDockTheme(AppTheme resolved)
    {
        DockManager.Theme = resolved == AppTheme.Light
            ? new Vs2013LightTheme()
            : new Vs2013DarkTheme();
    }

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
        if (TryFindResource("PrimaryForeground") is SolidColorBrush fg)
        {
            var fgBrush = new SolidColorBrush(fg.Color);
            Resources[SystemColors.ControlTextBrushKey] = fgBrush;
            Resources[SystemColors.WindowTextBrushKey] = fgBrush;
            Resources[SystemColors.MenuTextBrushKey] = fgBrush;
            Resources[SystemColors.InfoTextBrushKey] = fgBrush;
        }
        if (TryFindResource("WindowBackground") is SolidColorBrush bg)
            Resources[SystemColors.WindowBrushKey] = new SolidColorBrush(bg.Color);
        if (TryFindResource("MenuBackground") is SolidColorBrush menuBg)
        {
            Resources[SystemColors.MenuBrushKey] = new SolidColorBrush(menuBg.Color);
            Resources[SystemColors.MenuBarBrushKey] = new SolidColorBrush(menuBg.Color);
        }
    }

    // ─── Panel Navigation ────────────────────────────────────────────────

    private void ActivateDocument(string contentId)
    {
        var doc = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (doc is not null) doc.IsActive = true;
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

    private void ShowPanel(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string contentId)
            return;

        var doc = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == contentId);
        if (doc is not null) { doc.IsActive = true; return; }

        var anchorable = DockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);
        if (anchorable is not null)
        {
            if (anchorable.IsAutoHidden) anchorable.ToggleAutoHide();
            anchorable.Show();
            anchorable.IsActive = true;
            return;
        }

        var content = FindContentByContentId(contentId);
        if (content is null)
        {
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
                targetPane.Children.Add(restored);
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
        "disassembler" => "Disassembler",
        "structureDissector" => "Structure Dissector",
        "pointerScanner" => "Pointer Scanner",
        "scriptEditor" => "Script Editor",
        "debugger" => "Debugger",
        "breakpoints" => "Breakpoints",
        "scripts" => "Scripts",
        "snapshots" => "Snapshots",
        "findResults" => "Find Results",
        "hotkeys" => "Hotkeys",
        "journal" => "Journal",
        "modules" => "Modules",
        "threads" => "Threads",
        "memoryRegions" => "Memory Map",
        "workspace" => "Workspace",
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
            _logger.LogWarning(ex, "SaveLayout failed");
        }
    }

    /// <summary>Force the left sidebar pane to a usable width regardless of saved/default state.</summary>
    private void EnforceLeftSidebarWidth()
    {
        foreach (var pane in DockManager.Layout.Descendents().OfType<LayoutAnchorablePane>())
        {
            // Find the pane containing "processes" — that's our left sidebar
            if (pane.Children.Any(c => c.ContentId == "processes"))
            {
                pane.DockWidth = new System.Windows.GridLength(450);
                pane.DockMinWidth = 300;
                break;
            }
        }
    }

    private void RestoreLayout()
    {
        try
        {
            if (!File.Exists(LayoutFilePath)) return;

            var savedVersion = 0;
            try { if (File.Exists(LayoutVersionPath)) savedVersion = int.Parse(File.ReadAllText(LayoutVersionPath).Trim()); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not parse saved layout version from {Path}", LayoutVersionPath); }
            if (savedVersion < LayoutVersion)
            {
                File.Delete(LayoutFilePath);
                return;
            }

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
            _logger.LogWarning(ex, "RestoreLayout failed");
            try { File.Delete(LayoutFilePath); } catch (Exception deleteEx) { _logger.LogDebug(deleteEx, "Failed to delete layout file after restore failure"); }
        }
    }

    private void ResetLayout(object sender, RoutedEventArgs e)
    {
        try { File.Delete(LayoutFilePath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete layout file during reset"); }
        try { File.Delete(LayoutVersionPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete layout version file during reset"); }
        MessageBox.Show("Layout reset. Restart the application to apply the default layout.",
            "Reset Layout", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private LayoutAnchorablePane? FindTargetPaneForContentId(string contentId)
    {
        var allPanes = DockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorablePane>()
            .ToList();

        // Route closed panels back to their natural sibling group
        var siblingId = contentId switch
        {
            // Left sidebar
            "processes" or "modules" or "threads" or "memoryRegions" or "workspace" => "processes",
            // Right sidebar
            "aiOperator" => "aiOperator",
            // Bottom panels
            "scanner" or "breakpoints" or "scripts" or "snapshots"
                or "findResults" or "hotkeys" or "journal" or "output" => "scanner",
            _ => "scanner"
        };

        var preferred = allPanes.FirstOrDefault(pane =>
            pane.Children.Any(c => c.ContentId == siblingId));
        return preferred ?? allPanes.FirstOrDefault();
    }

    private object? FindContentByContentId(string contentId)
    {
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
                SetAnchorableVisibility(anchorables, "processes", autoHide: true);
                SetAnchorableVisibility(anchorables, "scanner", autoHide: true);
                SetAnchorableVisibility(anchorables, "output", autoHide: true);
                SetAnchorableVisibility(anchorables, "aiOperator", visible: true);
                StatusBarDensity.Text = "Clean";
                break;
            case "dense":
                SetAnchorableVisibility(anchorables, "processes", visible: true);
                SetAnchorableVisibility(anchorables, "scanner", visible: true);
                SetAnchorableVisibility(anchorables, "output", visible: true);
                SetAnchorableVisibility(anchorables, "aiOperator", visible: true);
                StatusBarDensity.Text = "Dense";
                break;
            default:
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

    private void CycleDensityPreset(object sender, MouseButtonEventArgs e)
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

    // ── AvalonEdit integration for Script Editor ──

    private void SetupScriptEditorHighlighting(ScriptEditorViewModel vm)
    {
        // Load Auto Assembler syntax highlighting from embedded resource
        var assembly = typeof(MainWindow).Assembly;
        using var stream = assembly.GetManifestResourceStream("CEAISuite.Desktop.Resources.AutoAssembler.xshd");
        if (stream is not null)
        {
            using var reader = new System.Xml.XmlTextReader(stream);
            ScriptTextEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }

        // Two-way binding: ViewModel.EditorText ↔ AvalonEdit.Text
        ScriptTextEditor.Text = vm.EditorText ?? "";
        ScriptTextEditor.TextChanged += (_, _) =>
        {
            if (vm.EditorText != ScriptTextEditor.Text)
                vm.EditorText = ScriptTextEditor.Text;
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.EditorText) && vm.EditorText != ScriptTextEditor.Text)
                Dispatcher.BeginInvoke(() => ScriptTextEditor.Text = vm.EditorText ?? "");
        };

        // Apply dark theme colors
        ScriptTextEditor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
    }
}
