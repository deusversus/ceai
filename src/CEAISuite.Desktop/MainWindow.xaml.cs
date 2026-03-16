using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    public string Content { get; init; } = "";
    public Brush Background { get; init; } = Brushes.Transparent;
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

    public MainWindow()
    {
        InitializeComponent();
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CEAISuite",
            "workspace.db");
        var engineFacade = new WindowsEngineFacade();
        _dashboardService = new WorkspaceDashboardService(
            engineFacade,
            new SqliteInvestigationSessionRepository(_databasePath));
        _scanService = new ScanService(new WindowsScanEngine());
        _addressTableService = new AddressTableService(engineFacade);
        _disassemblyService = new DisassemblyService(new WindowsDisassemblyEngine());
        _scriptGenerationService = new ScriptGenerationService();
        _addressTableExportService = new AddressTableExportService();
        _sessionService = new SessionService(new SqliteInvestigationSessionRepository(_databasePath));
        _breakpointService = new BreakpointService(null); // Engine injected when available

        // Wire up AI operator
        var toolFunctions = new AiToolFunctions(engineFacade, _dashboardService, _scanService, _addressTableService, _disassemblyService, _scriptGenerationService, _breakpointService);
        IChatClient? chatClient = CreateChatClient();
        _aiOperatorService = new AiOperatorService(chatClient, toolFunctions);

        DataContext = WorkspaceDashboard.CreateLoading();
        Loaded += OnLoaded;
    }

    private static IChatClient? CreateChatClient()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var model = Environment.GetEnvironmentVariable("CEAI_MODEL") ?? "gpt-5.4";
        return new OpenAIClient(apiKey)
            .GetChatClient(model)
            .AsIChatClient();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DataContext = await _dashboardService.BuildAsync(_databasePath);
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
        }
        catch (Exception exception)
        {
            DataContext = dashboard with { StatusMessage = exception.Message };
        }
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
            AddressTableEntries = _addressTableService.Entries.ToArray(),
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
            AddressTableEntries = _addressTableService.Entries.ToArray(),
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
                AddressTableEntries = _addressTableService.Entries.ToArray(),
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

        if (AddressTableList.SelectedItem is not AddressTableEntry selected)
        {
            DataContext = dashboard with { StatusMessage = "Select an address to remove." };
            return;
        }

        _addressTableService.RemoveEntry(selected.Id);
        DataContext = dashboard with
        {
            AddressTableEntries = _addressTableService.Entries.ToArray(),
            AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
            StatusMessage = $"Removed {selected.Address} from address table."
        };
    }

    private void ToggleLockAddress(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard)
        {
            return;
        }

        if (AddressTableList.SelectedItem is not AddressTableEntry selected)
        {
            DataContext = dashboard with { StatusMessage = "Select an address to toggle lock." };
            return;
        }

        _addressTableService.ToggleLock(selected.Id);
        DataContext = dashboard with
        {
            AddressTableEntries = _addressTableService.Entries.ToArray(),
            StatusMessage = $"Toggled lock on {selected.Address}."
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
        var message = AiChatInputTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(message)) return;

        AiChatInputTextBox.Text = "";
        AiStatusText.Text = "Thinking...";

        try
        {
            await _aiOperatorService.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            if (DataContext is WorkspaceDashboard db)
                DataContext = db with { StatusMessage = $"AI error: {ex.Message}" };
        }
        finally
        {
            AiStatusText.Text = _aiOperatorService.IsConfigured ? "Ready" : "Not configured (set OPENAI_API_KEY)";
            RefreshAiChatDisplay();
            // Also refresh address table in case AI modified it
            if (DataContext is WorkspaceDashboard dashboard)
            {
                DataContext = dashboard with
                {
                    AddressTableEntries = _addressTableService.Entries.ToArray(),
                    AddressTableStatus = $"{_addressTableService.Entries.Count} entries"
                };
            }
        }
    }

    private void OnAiChatKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
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

    private void RefreshAiChatDisplay()
    {
        var items = _aiOperatorService.DisplayHistory.Select(msg => new AiChatDisplayItem
        {
            RoleLabel = msg.Role == "user" ? "You" : "AI Operator",
            Content = msg.Content,
            Background = msg.Role == "user"
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE))
                : new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xF8))
        }).ToList();

        AiChatList.ItemsSource = items;
        AiChatScrollViewer.ScrollToEnd();
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
                    AddressTableEntries = _addressTableService.Entries.ToArray(),
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

    // ─── Session Save / Load ───────────────────────────────────────────

    private async void SaveSession(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceDashboard dashboard) return;

        try
        {
            var sessionId = await _sessionService.SaveSessionAsync(
                dashboard.CurrentInspection?.ProcessName,
                dashboard.CurrentInspection?.ProcessId,
                _addressTableService.Entries.ToArray(),
                _aiOperatorService.ActionLog.ToArray());

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

            foreach (var entry in loaded.Value.Entries)
            {
                _addressTableService.AddEntry(entry.Address, entry.DataType, entry.CurrentValue, entry.Label);
            }

            DataContext = dashboard with
            {
                AddressTableEntries = _addressTableService.Entries.ToArray(),
                AddressTableStatus = $"{_addressTableService.Entries.Count} entries",
                StatusMessage = $"Loaded session {selected.Id} with {loaded.Value.Entries.Count} address entries."
            };
        }
        catch (Exception ex)
        {
            DataContext = dashboard with { StatusMessage = $"Load failed: {ex.Message}" };
        }
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
