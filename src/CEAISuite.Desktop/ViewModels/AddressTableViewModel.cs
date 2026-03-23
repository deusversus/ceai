using System.Collections.ObjectModel;
using System.IO;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class AddressTableViewModel : ObservableObject
{
    private readonly AddressTableService _addressTableService;
    private readonly AddressTableExportService _addressTableExportService;
    private readonly IProcessContext _processContext;
    private readonly IAutoAssemblerEngine? _autoAssemblerEngine;
    private readonly BreakpointService _breakpointService;
    private readonly DisassemblyService _disassemblyService;
    private readonly ScriptGenerationService _scriptGenerationService;
    private readonly IDialogService _dialogService;
    private readonly IOutputLog _outputLog;
    private readonly IDispatcherService _dispatcher;
    private readonly INavigationService _navigationService;

    private System.Threading.Timer? _refreshTimer;
    private int _refreshIntervalMs = 500;
    private AddressTableNode? _clipboard;
    private string? _lastWriteBreakpointId;

    // ── Navigation events (subscribed by MainWindow) ──
    public event Action<nuint>? NavigateToMemoryBrowser;
    public event Action<string>? NavigateToDisassembly;
    public event Action<IReadOnlyList<FindResultDisplayItem>, string>? PopulateFindResults;

    [ObservableProperty]
    private ObservableCollection<AddressTableNode>? _roots;

    [ObservableProperty]
    private AddressTableNode? _selectedNode;

    [ObservableProperty]
    private string? _addressTableStatus;

    public AddressTableViewModel(
        AddressTableService addressTableService,
        AddressTableExportService addressTableExportService,
        IProcessContext processContext,
        IAutoAssemblerEngine? autoAssemblerEngine,
        BreakpointService breakpointService,
        DisassemblyService disassemblyService,
        ScriptGenerationService scriptGenerationService,
        IDialogService dialogService,
        IOutputLog outputLog,
        IDispatcherService dispatcher,
        INavigationService navigationService)
    {
        _addressTableService = addressTableService;
        _addressTableExportService = addressTableExportService;
        _processContext = processContext;
        _autoAssemblerEngine = autoAssemblerEngine;
        _breakpointService = breakpointService;
        _disassemblyService = disassemblyService;
        _scriptGenerationService = scriptGenerationService;
        _dialogService = dialogService;
        _outputLog = outputLog;
        _dispatcher = dispatcher;
        _navigationService = navigationService;

        Roots = _addressTableService.Roots;
    }

    // ── Helpers ──

    private void RefreshUI(string? statusMessage = null)
    {
        // Force PropertyChanged even when the reference is the same ObservableCollection,
        // since CommunityToolkit.Mvvm skips notification on equal references.
        OnPropertyChanged(nameof(Roots));
        AddressTableStatus = $"{_addressTableService.Entries.Count} entries";
        if (statusMessage is not null)
            _outputLog.Append("AddressTable", "Info", statusMessage);
    }

    // ── Auto-refresh timer ──

    public void StartAutoRefresh(int intervalMs = 500)
    {
        StopAutoRefresh();
        _refreshIntervalMs = intervalMs;
        _refreshTimer = new System.Threading.Timer(OnRefreshTimerTick, null, intervalMs, Timeout.Infinite);
    }

    private async void OnRefreshTimerTick(object? state)
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null || _addressTableService.Roots.Count == 0)
        {
            _refreshTimer?.Change(_refreshIntervalMs, Timeout.Infinite);
            return;
        }
        try
        {
            await _addressTableService.RefreshAllAsync(pid.Value);
            _dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(Roots));
                AddressTableStatus = $"{_addressTableService.Entries.Count} entries (live)";
            });
        }
        catch { /* non-fatal */ }
        finally
        {
            _refreshTimer?.Change(_refreshIntervalMs, Timeout.Infinite);
        }
    }

    public void StopAutoRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    public void SetRefreshInterval(int intervalMs)
    {
        _refreshIntervalMs = intervalMs;
        _refreshTimer?.Change(intervalMs, Timeout.Infinite);
    }

    // ── Refresh / Remove / Lock (toolbar buttons) ──

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null)
        {
            _outputLog.Append("AddressTable", "Warn", "No process attached.");
            return;
        }

        try
        {
            await _addressTableService.RefreshAllAsync(pid.Value);
            OnPropertyChanged(nameof(Roots));
            AddressTableStatus = $"{_addressTableService.Entries.Count} entries (refreshed)";
            _outputLog.Append("AddressTable", "Info", "Address table refreshed.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("AddressTable", "Error", $"Refresh failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedNode is null)
        {
            _outputLog.Append("AddressTable", "Warn", "Select an address to remove.");
            return;
        }

        var label = SelectedNode.Label;
        _addressTableService.RemoveEntry(SelectedNode.Id);
        RefreshUI($"Removed {label} from address table.");
    }

    [RelayCommand]
    private void ToggleLock()
    {
        if (SelectedNode is null)
        {
            _outputLog.Append("AddressTable", "Warn", "Select an address to toggle lock.");
            return;
        }

        _addressTableService.ToggleLock(SelectedNode.Id);
        RefreshUI($"Toggled lock on {SelectedNode.Label}.");
    }

    // ── Group ──

    [RelayCommand]
    private void CreateGroup()
    {
        var name = _dialogService.ShowInput("New Group", "Group name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        _addressTableService.CreateGroup(name.Trim());
        RefreshUI($"Created group '{name.Trim()}'.");
    }

    // ── Export / Import ──

    [RelayCommand]
    private void Export()
    {
        var entries = _addressTableService.Entries;
        if (entries.Count == 0)
        {
            _outputLog.Append("AddressTable", "Warn", "No entries to export.");
            return;
        }

        var path = _dialogService.ShowSaveFileDialog("JSON files|*.json", "address_table.json");
        if (path is null) return;

        var json = _addressTableExportService.ExportToJson(entries.ToArray());
        File.WriteAllText(path, json);
        _outputLog.Append("AddressTable", "Info", $"Exported {entries.Count} entries to {path}");
    }

    [RelayCommand]
    private void Import()
    {
        var path = _dialogService.ShowOpenFileDialog("JSON files|*.json");
        if (path is null) return;

        try
        {
            var json = File.ReadAllText(path);
            var imported = _addressTableExportService.ImportFromJson(json);
            foreach (var entry in imported)
                _addressTableService.AddEntry(entry.Address, entry.DataType, entry.CurrentValue, entry.Label);

            RefreshUI($"Imported {imported.Count} entries from {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _outputLog.Append("AddressTable", "Error", $"Import failed: {ex.Message}");
        }
    }

    // ── Trainer ──

    [RelayCommand]
    private void GenerateTrainer()
    {
        var entries = _addressTableService.Entries;
        var locked = entries.Where(x => x.IsLocked).ToArray();

        if (locked.Length == 0)
        {
            _outputLog.Append("AddressTable", "Warn", "No locked entries. Lock some addresses first.");
            return;
        }

        var processName = _processContext.AttachedProcessName ?? "Unknown";
        var script = _scriptGenerationService.GenerateTrainerScript(locked, processName);

        var path = _dialogService.ShowSaveFileDialog(
            "C# files|*.cs",
            $"Trainer_{processName.Replace(".exe", "")}.cs");
        if (path is null) return;

        File.WriteAllText(path, script);
        _outputLog.Append("AddressTable", "Info", $"Trainer script saved to {path}");
    }

    // ── Cheat Table Save / Load ──

    [RelayCommand]
    private void SaveCheatTable()
    {
        try
        {
            if (_addressTableService.Roots.Count == 0)
            {
                _outputLog.Append("AddressTable", "Warn", "Address table is empty. Nothing to save.");
                return;
            }

            var path = _dialogService.ShowSaveFileDialog(
                "Cheat Tables (*.ct)|*.ct|All Files (*.*)|*.*", "");
            if (path is null) return;

            var exporter = new CheatTableExporter();
            exporter.SaveToFile(_addressTableService.Roots, path);
            _outputLog.Append("AddressTable", "Info",
                $"Saved {_addressTableService.Roots.Count} top-level entries to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _outputLog.Append("AddressTable", "Error", $"CT save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void LoadCheatTable()
    {
        try
        {
            var path = _dialogService.ShowOpenFileDialog(
                "Cheat Tables (*.ct;*.CT)|*.ct;*.CT|XML Files (*.xml)|*.xml|All Files (*.*)|*.*");
            if (path is null) return;

            // If the table already has entries, ask whether to merge or replace
            if (_addressTableService.Entries.Count > 0)
            {
                if (!_dialogService.Confirm("Load Cheat Table",
                    "The address table already has entries.\n\n" +
                    "Click Yes to replace the current table, or No to merge."))
                {
                    // User chose No → merge (just import on top)
                }
                else
                {
                    // User chose Yes → replace (clear first)
                    _addressTableService.ClearAll();
                }
            }

            var parser = new CheatTableParser();
            var ctFile = parser.ParseFile(path);
            var nodes = parser.ToAddressTableNodes(ctFile);
            _addressTableService.ImportNodes(nodes);

            var scriptCount = CountScripts(ctFile.Entries);
            RefreshUI(
                $"Loaded {ctFile.FileName}: {ctFile.TotalEntryCount} CT entries imported, {scriptCount} scripts" +
                (ctFile.LuaScript is not null ? " (has Lua script)" : ""));
        }
        catch (Exception ex)
        {
            _outputLog.Append("AddressTable", "Error", $"CT load failed: {ex.Message}");
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

    // ── Context Menu: Activate / Description / Address / Value / Type ──

    [RelayCommand]
    private void ToggleActivate()
    {
        if (SelectedNode is null) return;
        SelectedNode.IsActive = !SelectedNode.IsActive;
        if (!SelectedNode.IsScriptEntry)
            SelectedNode.LockedValue = SelectedNode.IsLocked ? SelectedNode.CurrentValue : null;
        RefreshUI($"{SelectedNode.Label}: {(SelectedNode.IsActive ? "Activated" : "Deactivated")}");
    }

    [RelayCommand]
    private void ChangeDescription()
    {
        if (SelectedNode is null) return;
        var result = _dialogService.ShowInput("Change Description", "New description:", SelectedNode.Label);
        if (result is not null)
        {
            _addressTableService.UpdateLabel(SelectedNode.Id, result);
            RefreshUI($"Renamed to '{result}'");
        }
    }

    [RelayCommand]
    private void ChangeAddress()
    {
        if (SelectedNode is null || SelectedNode.IsGroup || SelectedNode.IsScriptEntry) return;
        var result = _dialogService.ShowInput("Change Address", "New address (hex):", SelectedNode.Address);
        if (result is not null)
        {
            SelectedNode.Address = result;
            RefreshUI($"Address changed: {SelectedNode.Label} -> {result}");
        }
    }

    [RelayCommand]
    private void ChangeValue()
    {
        if (SelectedNode is null || SelectedNode.IsGroup || SelectedNode.IsScriptEntry) return;
        EditNodeValue(SelectedNode);
    }

    [RelayCommand]
    private void ChangeType()
    {
        if (SelectedNode is null || SelectedNode.IsGroup || SelectedNode.IsScriptEntry) return;

        // Present type choices via dialog
        var types = Enum.GetValues<MemoryDataType>().Select(t => t.ToString()).ToArray();
        var currentIdx = Array.IndexOf(types, SelectedNode.DataType.ToString());
        var result = _dialogService.ShowInput("Change Type",
            $"Enter data type ({string.Join(", ", types)}):",
            SelectedNode.DataType.ToString());
        if (result is not null && Enum.TryParse<MemoryDataType>(result, out var parsed))
        {
            SelectedNode.DataType = parsed;
            RefreshUI($"Type changed: {SelectedNode.Label} -> {result}");
        }
    }

    // ── Freeze / Hex ──

    [RelayCommand]
    private void ToggleFreeze()
    {
        if (SelectedNode is null || SelectedNode.IsGroup || SelectedNode.IsScriptEntry) return;
        _addressTableService.ToggleLock(SelectedNode.Id);
        RefreshUI($"{SelectedNode.Label}: {(SelectedNode.IsLocked ? "Frozen" : "Unfrozen")}");
    }

    [RelayCommand]
    private void ShowAsHex()
    {
        if (SelectedNode is null || SelectedNode.IsGroup || SelectedNode.IsScriptEntry) return;
        if (SelectedNode.Notes?.Contains("[HEX]") == true)
            SelectedNode.Notes = SelectedNode.Notes.Replace("[HEX] ", "");
        else
            SelectedNode.Notes = $"[HEX] {SelectedNode.Notes ?? ""}".Trim();
        RefreshUI();
    }

    // ── Navigation commands (raise events for MainWindow) ──

    [RelayCommand]
    private void BrowseMemory()
    {
        if (SelectedNode is null || SelectedNode.IsGroup || SelectedNode.IsScriptEntry) return;
        if (_processContext.AttachedProcessId is null) return;

        var addr = SelectedNode.ResolvedAddress ?? nuint.Zero;
        if (addr == nuint.Zero)
        {
            try { addr = AddressTableService.ParseAddress(SelectedNode.Address); } catch { }
        }

        NavigateToMemoryBrowser?.Invoke(addr);
    }

    [RelayCommand]
    private void Disassemble()
    {
        if (SelectedNode is null || SelectedNode.IsGroup || SelectedNode.IsScriptEntry) return;

        var addr = SelectedNode.ResolvedAddress ?? nuint.Zero;
        if (addr == nuint.Zero)
        {
            try { addr = AddressTableService.ParseAddress(SelectedNode.Address); } catch { }
        }

        NavigateToDisassembly?.Invoke($"0x{addr:X}");
    }

    [RelayCommand]
    private async Task FindWhatWritesAsync()
    {
        if (SelectedNode is null || SelectedNode.IsGroup || SelectedNode.IsScriptEntry) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) return;

        var addr = SelectedNode.ResolvedAddress ?? nuint.Zero;
        if (addr == nuint.Zero)
        {
            try { addr = AddressTableService.ParseAddress(SelectedNode.Address); } catch { }
        }
        if (addr == nuint.Zero) return;

        try
        {
            var bp = await _breakpointService.SetBreakpointAsync(
                pid.Value,
                $"0x{addr:X}",
                BreakpointType.HardwareWrite,
                BreakpointHitAction.LogAndContinue);

            _lastWriteBreakpointId = bp.Id;
            _outputLog.Append("AddressTable", "Info",
                $"Breakpoint set on {SelectedNode.Label} (0x{addr:X}). Trigger a write, then 'View Write Log'.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("AddressTable", "Error", $"Failed to set write breakpoint: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ViewWriteLogAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) return;

        if (string.IsNullOrEmpty(_lastWriteBreakpointId))
        {
            _outputLog.Append("AddressTable", "Warn",
                "No write breakpoint active. Use 'Find What Writes' first.");
            return;
        }

        try
        {
            var hits = await _breakpointService.GetHitLogAsync(_lastWriteBreakpointId);

            if (hits.Count == 0)
            {
                _outputLog.Append("AddressTable", "Info",
                    "No writes detected yet. Trigger a write in-game and try again.");
                return;
            }

            var results = new List<FindResultDisplayItem>();

            foreach (var hit in hits)
            {
                string instruction = "(disassembly unavailable)";
                try
                {
                    var disasm = await _disassemblyService.DisassembleAtAsync(pid.Value, hit.Address, 1);
                    if (disasm.Lines.Count > 0)
                    {
                        var instr = disasm.Lines[0];
                        instruction = $"{instr.Mnemonic} {instr.Operands}";
                    }
                }
                catch { /* disassembly unavailable */ }

                var context = hit.Registers.Count > 0
                    ? string.Join("  ", hit.Registers.Take(6).Select(r => $"{r.Key}={r.Value}"))
                    : "";

                results.Add(new FindResultDisplayItem
                {
                    Address = hit.Address,
                    Instruction = instruction,
                    Module = $"TID={hit.ThreadId}",
                    Context = context
                });
            }

            PopulateFindResults?.Invoke(results, $"Write log — BP {_lastWriteBreakpointId}");
            _outputLog.Append("AddressTable", "Info",
                $"Found {hits.Count} write hit(s) — see Find Results tab.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("AddressTable", "Error", $"Failed to retrieve write log: {ex.Message}");
        }
    }

    // ── Move to Group / Delete ──

    [RelayCommand]
    private void MoveToGroup()
    {
        if (SelectedNode is null) return;

        var groups = new List<AddressTableNode>();
        CollectGroups(_addressTableService.Roots, groups);

        if (groups.Count == 0)
        {
            _outputLog.Append("AddressTable", "Warn", "No groups exist. Create a group first.");
            return;
        }

        // Build choices: "(Root level)" + group labels
        var choices = new List<string> { "(Root level)" };
        choices.AddRange(groups.Select(g => g.Label));

        var result = _dialogService.ShowInput("Move to Group",
            $"Enter target group ({string.Join(", ", choices)}):",
            choices[0]);
        if (result is null) return;

        var idx = choices.IndexOf(result);
        var targetGroupId = idx <= 0 ? null : groups[idx - 1].Id;
        _addressTableService.MoveToGroup(SelectedNode.Id, targetGroupId);
        RefreshUI($"Moved '{SelectedNode.Label}' to {(targetGroupId is null ? "root" : result)}");
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedNode is null) return;
        var label = SelectedNode.Label;
        _addressTableService.RemoveEntry(SelectedNode.Id);
        RefreshUI($"Deleted '{label}'");
    }

    // ── Clipboard ──

    [RelayCommand]
    private void Cut()
    {
        if (SelectedNode is null) return;
        _clipboard = SelectedNode;
        _addressTableService.RemoveEntry(SelectedNode.Id);
        RefreshUI($"Cut '{SelectedNode.Label}'");
    }

    [RelayCommand]
    private void Copy()
    {
        if (SelectedNode is null) return;
        _clipboard = SelectedNode;
        _outputLog.Append("AddressTable", "Info", $"Copied '{SelectedNode.Label}'");
    }

    [RelayCommand]
    private void Paste()
    {
        if (_clipboard is null)
        {
            _outputLog.Append("AddressTable", "Warn", "Nothing to paste.");
            return;
        }

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

        if (SelectedNode?.IsGroup == true)
            SelectedNode.Children.Add(clone);
        else
            _addressTableService.Roots.Add(clone);

        RefreshUI($"Pasted '{clone.Label}'");
    }

    // ── Script View / Toggle ──

    [RelayCommand]
    private void ViewSelectedScript()
    {
        if (SelectedNode?.AssemblerScript is null)
        {
            _outputLog.Append("AddressTable", "Warn", "Select a script entry first.");
            return;
        }

        // Show script in a dialog (read-only view)
        _dialogService.ShowInfo($"Script: {SelectedNode.Label}", SelectedNode.AssemblerScript);
    }

    [RelayCommand]
    private async Task ToggleSelectedScriptAsync()
    {
        if (SelectedNode?.AssemblerScript is null)
        {
            _outputLog.Append("AddressTable", "Warn", "Select a script entry first.");
            return;
        }

        var pid = _processContext.AttachedProcessId;
        if (pid is null || pid == 0)
        {
            _outputLog.Append("AddressTable", "Warn", "Attach to a process before toggling scripts.");
            return;
        }

        if (_autoAssemblerEngine is null)
        {
            _outputLog.Append("AddressTable", "Warn", "Auto Assembler engine not available.");
            return;
        }

        try
        {
            if (SelectedNode.IsScriptEnabled)
            {
                var result = await _autoAssemblerEngine.DisableAsync(pid.Value, SelectedNode.AssemblerScript);
                SelectedNode.IsScriptEnabled = false;
                SelectedNode.ScriptStatus = result.Success
                    ? "Disabled successfully"
                    : $"Disable failed: {result.Error}";
            }
            else
            {
                var result = await _autoAssemblerEngine.EnableAsync(pid.Value, SelectedNode.AssemblerScript);
                SelectedNode.IsScriptEnabled = result.Success;
                SelectedNode.ScriptStatus = result.Success
                    ? $"Enabled ({result.Allocations.Count} allocs, {result.Patches.Count} patches)"
                    : $"Enable failed: {result.Error}";
            }

            OnPropertyChanged(nameof(Roots));
            _outputLog.Append("AddressTable", "Info",
                $"Script '{SelectedNode.Label}': {SelectedNode.ScriptStatus}");
        }
        catch (Exception ex)
        {
            SelectedNode.ScriptStatus = $"Error: {ex.Message}";
            _outputLog.Append("AddressTable", "Error", $"Script error: {ex.Message}");
        }
    }

    // ── ActiveCheckBox_Click support ──

    public async Task HandleActiveCheckBoxClickAsync(AddressTableNode node)
    {
        if (node.IsScriptEntry)
        {
            var pid = _processContext.AttachedProcessId;
            if (pid is null)
            {
                node.IsScriptEnabled = false;
                _outputLog.Append("AddressTable", "Warn", "Attach to a process before toggling scripts.");
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
                    var result = await _autoAssemblerEngine.EnableAsync(pid.Value, node.AssemblerScript!);
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
                    var result = await _autoAssemblerEngine.DisableAsync(pid.Value, node.AssemblerScript!);
                    node.ScriptStatus = result.Success ? "Disabled" : $"Disable failed: {result.Error}";
                }

                _outputLog.Append("AddressTable", "Info", $"Script '{node.Label}': {node.ScriptStatus}");
            }
            catch (Exception ex)
            {
                node.IsScriptEnabled = false;
                node.ScriptStatus = $"Error: {ex.Message}";
                _outputLog.Append("AddressTable", "Error", $"Script error: {ex.Message}");
            }
        }
        else
        {
            // Value entry: toggle freeze and capture current value
            node.LockedValue = node.IsLocked ? node.CurrentValue : null;
        }
    }

    // ── Editing support (called from MainWindow thin wrappers) ──

    public void EditNodeValue(AddressTableNode node)
    {
        var result = _dialogService.ShowInput("Change Value", "New value:", node.CurrentValue);
        if (result is null) return;

        node.PreviousValue = node.CurrentValue;
        node.CurrentValue = result;
        if (node.IsLocked) node.LockedValue = result;

        // Attempt write if attached
        var pid = _processContext.AttachedProcessId;
        if (pid is not null)
        {
            try
            {
                _ = _addressTableService.WriteValueAsync(pid.Value, node);
            }
            catch { /* best effort */ }
        }

        RefreshUI($"Value changed: {node.Label} = {result}");
    }

    public void EditSelectedNode()
    {
        if (SelectedNode is null) return;

        if (SelectedNode.IsScriptEntry)
        {
            ViewSelectedScriptCommand.Execute(null);
            return;
        }
        if (SelectedNode.IsGroup) return;

        EditNodeValue(SelectedNode);
    }

    public void DeleteSelectedNode()
    {
        if (SelectedNode is null) return;
        DeleteCommand.Execute(null);
    }

    // ── Helpers ──

    private static void CollectGroups(
        ObservableCollection<AddressTableNode> nodes,
        List<AddressTableNode> results)
    {
        foreach (var n in nodes)
        {
            if (n.IsGroup) results.Add(n);
            CollectGroups(n.Children, results);
        }
    }
}
