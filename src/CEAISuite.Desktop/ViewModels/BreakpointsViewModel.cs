using System.Collections.ObjectModel;
using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class BreakpointsViewModel : ObservableObject
{
    private readonly BreakpointService _breakpointService;
    private readonly ICodeCaveEngine _codeCaveEngine;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;

    public BreakpointsViewModel(
        BreakpointService breakpointService,
        ICodeCaveEngine codeCaveEngine,
        IProcessContext processContext,
        IOutputLog outputLog)
    {
        _breakpointService = breakpointService;
        _codeCaveEngine = codeCaveEngine;
        _processContext = processContext;
        _outputLog = outputLog;
    }

    [ObservableProperty]
    private ObservableCollection<BreakpointDisplayItem> _breakpoints = new();

    [ObservableProperty]
    private BreakpointDisplayItem? _selectedBreakpoint;

    [ObservableProperty]
    private ObservableCollection<HitLogDisplayItem> _hitLog = new();

    [ObservableProperty]
    private string _hitLogStatus = "Select a breakpoint, then refresh";

    [ObservableProperty]
    private ObservableCollection<CodeCaveHookDisplayItem> _codeCaveHooks = new();

    [ObservableProperty]
    private CodeCaveHookDisplayItem? _selectedCodeCaveHook;

    [ObservableProperty]
    private string _codeCaveAddress = "";

    // ── Conditional breakpoint properties (C5) ──

    [ObservableProperty]
    private string _conditionExpression = "";

    [ObservableProperty]
    private string _conditionType = "RegisterCompare";

    [ObservableProperty]
    private string _threadFilterInput = "";

    [ObservableProperty]
    private string _conditionalAddress = "";

    [RelayCommand]
    private async Task SetConditionalBreakpointAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        if (string.IsNullOrWhiteSpace(ConditionExpression)) return;
        if (string.IsNullOrWhiteSpace(ConditionalAddress)) return;
        try
        {
            var condType = Enum.TryParse<BreakpointConditionType>(ConditionType, out var ct)
                ? ct : BreakpointConditionType.RegisterCompare;
            var condition = new BreakpointCondition(ConditionExpression, condType);
            int? tf = int.TryParse(ThreadFilterInput, out var t) ? t : null;
            await _breakpointService.SetConditionalBreakpointAsync(
                pid, ConditionalAddress, BreakpointType.HardwareExecute, condition, threadFilter: tf);
            await RefreshBreakpointsAsync();
            _outputLog.Append("System", "Info", $"Conditional BP set: {ConditionExpression}");
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Set conditional BP: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task RefreshBreakpointsAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            var bps = await _breakpointService.ListBreakpointsAsync(pid);
            Breakpoints = new ObservableCollection<BreakpointDisplayItem>(
                bps.Select(b => new BreakpointDisplayItem
                {
                    Id = b.Id,
                    Address = b.Address,
                    Type = b.Type,
                    HitCount = b.HitCount,
                    Status = _breakpointService.GetLifecycleStatus(b.Id).ToString(),
                    Condition = b.Condition ?? "",
                    ThreadFilter = b.ThreadFilter?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Mode = b.Mode
                }));
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Refresh breakpoints: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        if (SelectedBreakpoint is not { } item) return;
        try
        {
            await _breakpointService.RemoveBreakpointAsync(pid, item.Id);
            await RefreshBreakpointsAsync();
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Remove breakpoint: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task RemoveAllAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            var bps = await _breakpointService.ListBreakpointsAsync(pid);
            foreach (var bp in bps)
                await _breakpointService.RemoveBreakpointAsync(pid, bp.Id);
            await RefreshBreakpointsAsync();
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Remove all breakpoints: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task RefreshHitLogAsync()
    {
        if (SelectedBreakpoint is not { } bp) { HitLogStatus = "Select a breakpoint first"; return; }
        try
        {
            var hits = await _breakpointService.GetHitLogAsync(bp.Id);
            HitLog = new ObservableCollection<HitLogDisplayItem>(
                hits.Select(h => new HitLogDisplayItem
                {
                    BreakpointId = h.BreakpointId,
                    Address = h.Address,
                    ThreadId = h.ThreadId,
                    Timestamp = h.Timestamp
                }));
            HitLogStatus = $"{hits.Count} hits for {bp.Address}";
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Refresh hit log: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task InstallHookAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        var text = CodeCaveAddress.Trim();
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var addr = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? nuint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : nuint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            // Pre-flight conflict check: warn if an existing hook is at the same address
            var existingHooks = await _codeCaveEngine.ListHooksAsync(pid).ConfigureAwait(false);
            var conflict = existingHooks.FirstOrDefault(h => h.OriginalAddress == addr);
            if (conflict is not null)
            {
                _outputLog.Append("System", "Warn",
                    $"Conflict: hook already exists at 0x{addr:X} (cave at 0x{conflict.CaveAddress:X}). Skipping.");
                return;
            }

            var result = await _codeCaveEngine.InstallHookAsync(pid, addr);
            _outputLog.Append("System", "Info", $"Hook installed at 0x{addr:X} → cave 0x{result.Hook?.CaveAddress:X}");
            await RefreshHooksAsync();
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Install hook: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task RemoveSelectedHookAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        if (SelectedCodeCaveHook is not { } item) return;
        try
        {
            await _codeCaveEngine.RemoveHookAsync(pid, item.Id);
            await RefreshHooksAsync();
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Remove hook: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task RefreshHooksAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            var hooks = await _codeCaveEngine.ListHooksAsync(pid);
            CodeCaveHooks = new ObservableCollection<CodeCaveHookDisplayItem>(
                hooks.Select(h => new CodeCaveHookDisplayItem
                {
                    Id = h.Id,
                    OriginalAddress = $"0x{h.OriginalAddress:X}",
                    CaveAddress = $"0x{h.CaveAddress:X}",
                    IsActive = h.IsActive,
                    HitCount = h.HitCount
                }));
        }
        catch (Exception ex) { _outputLog.Append("System", "Error", $"Refresh hooks: {ex.Message}"); }
    }
}
