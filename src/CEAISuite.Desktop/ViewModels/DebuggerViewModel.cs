using System.Collections.ObjectModel;
using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class DebuggerViewModel : ObservableObject
{
    private readonly BreakpointService _breakpointService;
    private readonly ICallStackEngine _callStackEngine;
    private readonly IEngineFacade _engineFacade;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly INavigationService _navigationService;
    private readonly IDispatcherService _dispatcherService;
    private readonly SteppingService? _steppingService;

    public DebuggerViewModel(
        BreakpointService breakpointService,
        ICallStackEngine callStackEngine,
        IEngineFacade engineFacade,
        IProcessContext processContext,
        IOutputLog outputLog,
        INavigationService navigationService,
        IDispatcherService dispatcherService,
        SteppingService? steppingService = null)
    {
        _breakpointService = breakpointService;
        _callStackEngine = callStackEngine;
        _engineFacade = engineFacade;
        _processContext = processContext;
        _outputLog = outputLog;
        _navigationService = navigationService;
        _dispatcherService = dispatcherService;
        _steppingService = steppingService;
    }

    [ObservableProperty] private ObservableCollection<RegisterDisplayItem> _registers = new();
    [ObservableProperty] private ObservableCollection<CallStackFrameDisplayItem> _callStack = new();
    [ObservableProperty] private ObservableCollection<BreakpointDisplayItem> _activeBreakpoints = new();
    [ObservableProperty] private BreakpointDisplayItem? _selectedBreakpoint;
    [ObservableProperty] private ObservableCollection<BreakpointHitDetailItem> _hitDetails = new();
    [ObservableProperty] private BreakpointHitDetailItem? _selectedHit;
    [ObservableProperty] private string? _statusText;

    /// <summary>Register values from the previously selected hit, keyed by name.</summary>
    private Dictionary<string, string>? _previousRegisterSnapshot;

    partial void OnSelectedHitChanged(BreakpointHitDetailItem? value)
    {
        var prevSnapshot = _previousRegisterSnapshot;
        Registers.Clear();
        if (value?.Registers is null) return;

        foreach (var reg in value.Registers)
        {
            reg.IsChanged = prevSnapshot is not null
                && prevSnapshot.TryGetValue(reg.Name, out var prevVal)
                && !string.Equals(prevVal, reg.Value, StringComparison.OrdinalIgnoreCase);
            Registers.Add(reg);
        }

        // Store current as snapshot for next comparison
        _previousRegisterSnapshot = value.Registers.ToDictionary(r => r.Name, r => r.Value);
    }

    [RelayCommand]
    private async Task RefreshBreakpointsAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        try
        {
            var bps = await _breakpointService.ListBreakpointsAsync(pid.Value);
            ActiveBreakpoints.Clear();
            foreach (var bp in bps)
            {
                ActiveBreakpoints.Add(new BreakpointDisplayItem
                {
                    Id = bp.Id,
                    Address = bp.Address,
                    Type = bp.Type,
                    Mode = bp.Mode,
                    HitCount = bp.HitCount,
                    Status = bp.IsEnabled ? "Armed" : "Disabled",
                    Condition = bp.Condition ?? "",
                    ThreadFilter = bp.ThreadFilter?.ToString(CultureInfo.InvariantCulture) ?? ""
                });
            }
            StatusText = $"{ActiveBreakpoints.Count} breakpoint(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadHitDetailsAsync()
    {
        if (SelectedBreakpoint is null) return;
        try
        {
            var hits = await _breakpointService.GetHitLogAsync(SelectedBreakpoint.Id, 50);
            HitDetails.Clear();
            foreach (var hit in hits)
            {
                HitDetails.Add(new BreakpointHitDetailItem
                {
                    BreakpointId = hit.BreakpointId,
                    Address = hit.Address,
                    ThreadId = hit.ThreadId,
                    Timestamp = hit.Timestamp,
                    Registers = hit.Registers
                        .Select(kv => new RegisterDisplayItem { Name = kv.Key, Value = kv.Value })
                        .ToList()
                });
            }
            StatusText = $"{HitDetails.Count} hit(s) for {SelectedBreakpoint.Id}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task WalkCallStackAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }

        // Use thread ID from last selected hit, or default to 0 (main thread)
        var hit = SelectedHit;
        var threadId = hit?.ThreadId ?? 0;
        try
        {
            var attachment = await _engineFacade.AttachAsync(pid.Value);
            var frames = await _callStackEngine.WalkStackAsync(
                pid.Value, threadId, attachment.Modules, 64);

            CallStack.Clear();
            foreach (var frame in frames)
            {
                CallStack.Add(new CallStackFrameDisplayItem
                {
                    FrameIndex = frame.FrameIndex,
                    InstructionPointer = $"0x{frame.InstructionPointer:X}",
                    ModuleOffset = frame.ModuleName is not null
                        ? $"{frame.ModuleName}+0x{frame.ModuleOffset:X}"
                        : $"0x{frame.InstructionPointer:X}",
                    ReturnAddress = $"0x{frame.ReturnAddress:X}"
                });
            }
            StatusText = $"{CallStack.Count} frame(s) on thread {threadId}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    // ── Break-and-Trace ──

    [ObservableProperty] private ObservableCollection<TraceEntryDisplayItem> _traceEntries = new();
    [ObservableProperty] private string _traceAddress = "";
    [ObservableProperty] private int _traceMaxInstructions = 500;

    [RelayCommand]
    private async Task StartTraceAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        if (string.IsNullOrWhiteSpace(TraceAddress)) { StatusText = "Enter a trace address."; return; }

        try
        {
            StatusText = "Tracing...";
            var result = await _breakpointService.TraceFromBreakpointAsync(
                pid.Value, TraceAddress, TraceMaxInstructions);

            TraceEntries.Clear();
            foreach (var entry in result.Entries)
            {
                TraceEntries.Add(new TraceEntryDisplayItem
                {
                    Address = $"0x{entry.InstructionAddress:X}",
                    Disassembly = entry.Disassembly,
                    ThreadId = entry.ThreadId,
                    IsCallInstruction = entry.Disassembly.StartsWith("call", StringComparison.OrdinalIgnoreCase),
                    IsRetInstruction = entry.Disassembly.StartsWith("ret", StringComparison.OrdinalIgnoreCase)
                });
            }
            StatusText = $"Trace: {result.Entries.Count} instructions" +
                (result.MaxDepthReached ? " (max depth)" : "") +
                (result.WasTruncated ? " (truncated)" : "");
        }
        catch (Exception ex)
        {
            StatusText = $"Trace error: {ex.Message}";
            _outputLog.Append("Debugger", "Error", $"Trace failed: {ex.Message}");
        }
    }

    // ── Conditional Breakpoint UI ──

    [ObservableProperty] private string _conditionExpression = "";
    [ObservableProperty] private string _threadFilterText = "";
    [ObservableProperty] private string _conditionalBpAddress = "";

    [RelayCommand]
    private async Task SetConditionalBreakpointAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        if (string.IsNullOrWhiteSpace(ConditionalBpAddress)) { StatusText = "Enter an address."; return; }
        if (string.IsNullOrWhiteSpace(ConditionExpression)) { StatusText = "Enter a condition expression."; return; }

        try
        {
            var condType = ConditionExpression.TrimStart('[') switch
            {
                _ when ConditionExpression.StartsWith('[') => BreakpointConditionType.MemoryCompare,
                _ when ConditionExpression.StartsWith("hitcount", StringComparison.OrdinalIgnoreCase) => BreakpointConditionType.HitCount,
                _ => BreakpointConditionType.RegisterCompare
            };
            var condition = new BreakpointCondition(ConditionExpression, condType);
            int? threadFilter = int.TryParse(ThreadFilterText, out var tf) ? tf : null;

            var bp = await _breakpointService.SetConditionalBreakpointAsync(
                pid.Value, ConditionalBpAddress, BreakpointType.HardwareExecute, condition,
                threadFilter: threadFilter);

            StatusText = $"Conditional breakpoint set at {bp.Address}";
            _outputLog.Append("Debugger", "Info",
                $"Conditional BP at {bp.Address}: {ConditionExpression}" +
                (threadFilter.HasValue ? $" (thread {threadFilter})" : ""));
            await RefreshBreakpointsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _outputLog.Append("Debugger", "Error", $"SetConditionalBreakpoint failed: {ex.Message}");
        }
    }

    // ── Interactive Stepping ──

    [ObservableProperty] private bool _isStepping;
    [ObservableProperty] private string _steppingStatus = "";
    [ObservableProperty] private ObservableCollection<TraceEntryDisplayItem> _stepHistory = new();

    private bool CanStep() => !IsStepping && _processContext.AttachedProcessId.HasValue && _steppingService is not null;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepInAsync() => ExecuteStepAsync("Step In", (pid, tid) =>
        _steppingService!.StepInAsync(pid, tid));

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepOverAsync() => ExecuteStepAsync("Step Over", (pid, tid) =>
        _steppingService!.StepOverAsync(pid, tid));

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepOutAsync() => ExecuteStepAsync("Step Out", (pid, tid) =>
        _steppingService!.StepOutAsync(pid, tid));

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task ContinueAsync() => ExecuteStepAsync("Continue", (pid, _) =>
        _steppingService!.ContinueAsync(pid));

    private async Task ExecuteStepAsync(string operationName, Func<int, int, Task<StepResult>> stepAction)
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null || _steppingService is null) return;

        IsStepping = true;
        SteppingStatus = $"{operationName}...";
        StepInCommand.NotifyCanExecuteChanged();
        StepOverCommand.NotifyCanExecuteChanged();
        StepOutCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();

        try
        {
            var threadId = SelectedHit?.ThreadId ?? 0;
            var result = await stepAction(pid.Value, threadId).ConfigureAwait(false);

            _dispatcherService.Invoke(() =>
            {
                if (result.Success)
                {
                    SteppingStatus = $"Suspended at 0x{result.NewRip:X} ({result.Reason})";

                    // Update register display
                    UpdateRegistersFromStepResult(result);

                    // Add to step history
                    StepHistory.Add(new TraceEntryDisplayItem
                    {
                        Address = $"0x{result.NewRip:X}",
                        Disassembly = result.Disassembly ?? "(unknown)",
                        ThreadId = result.ThreadId,
                        IsCallInstruction = result.Disassembly?.StartsWith("call", StringComparison.OrdinalIgnoreCase) == true,
                        IsRetInstruction = result.Disassembly?.StartsWith("ret", StringComparison.OrdinalIgnoreCase) == true
                    });

                    // Refresh call stack (fire-and-forget with fault observation)
                    WalkCallStackCommand.ExecuteAsync(null).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            _outputLog.Append("Debugger", "Error", $"Call stack refresh failed: {t.Exception?.InnerException?.Message}");
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
                else
                {
                    SteppingStatus = $"{operationName} failed: {result.Error ?? result.Reason.ToString()}";
                    _outputLog.Append("Debugger", "Error", $"{operationName} failed: {result.Error}");
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcherService.Invoke(() =>
            {
                SteppingStatus = $"{operationName} error: {ex.Message}";
                _outputLog.Append("Debugger", "Error", $"{operationName} error: {ex.Message}");
            });
        }
        finally
        {
            _dispatcherService.Invoke(() =>
            {
                IsStepping = false;
                StepInCommand.NotifyCanExecuteChanged();
                StepOverCommand.NotifyCanExecuteChanged();
                StepOutCommand.NotifyCanExecuteChanged();
                ContinueCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void UpdateRegistersFromStepResult(StepResult result)
    {
        var prevSnapshot = _previousRegisterSnapshot;
        Registers.Clear();

        if (result.Registers is null) return;

        var r = result.Registers;
        var regs = new (string Name, ulong Value)[]
        {
            ("RAX", r.Rax), ("RBX", r.Rbx), ("RCX", r.Rcx), ("RDX", r.Rdx),
            ("RSI", r.Rsi), ("RDI", r.Rdi), ("RSP", r.Rsp), ("RBP", r.Rbp),
            ("R8", r.R8), ("R9", r.R9), ("R10", r.R10), ("R11", r.R11),
            ("R12", r.R12), ("R13", r.R13), ("R14", r.R14), ("R15", r.R15),
            ("RIP", r.Rip), ("EFLAGS", r.EFlags)
        };

        var newSnapshot = new Dictionary<string, string>();
        foreach (var (name, value) in regs)
        {
            var hexValue = $"0x{value:X}";
            var isChanged = prevSnapshot is not null
                && prevSnapshot.TryGetValue(name, out var prevVal)
                && !string.Equals(prevVal, hexValue, StringComparison.OrdinalIgnoreCase);
            Registers.Add(new RegisterDisplayItem { Name = name, Value = hexValue, IsChanged = isChanged });
            newSnapshot[name] = hexValue;
        }
        _previousRegisterSnapshot = newSnapshot;
    }

    [RelayCommand]
    private void ClearStepHistory() => StepHistory.Clear();
}
