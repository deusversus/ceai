using System.Collections.ObjectModel;
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

    public DebuggerViewModel(
        BreakpointService breakpointService,
        ICallStackEngine callStackEngine,
        IEngineFacade engineFacade,
        IProcessContext processContext,
        IOutputLog outputLog,
        INavigationService navigationService)
    {
        _breakpointService = breakpointService;
        _callStackEngine = callStackEngine;
        _engineFacade = engineFacade;
        _processContext = processContext;
        _outputLog = outputLog;
        _navigationService = navigationService;
    }

    [ObservableProperty] private ObservableCollection<RegisterDisplayItem> _registers = new();
    [ObservableProperty] private ObservableCollection<CallStackFrameDisplayItem> _callStack = new();
    [ObservableProperty] private ObservableCollection<BreakpointDisplayItem> _activeBreakpoints = new();
    [ObservableProperty] private BreakpointDisplayItem? _selectedBreakpoint;
    [ObservableProperty] private ObservableCollection<BreakpointHitDetailItem> _hitDetails = new();
    [ObservableProperty] private BreakpointHitDetailItem? _selectedHit;
    [ObservableProperty] private string? _statusText;

    partial void OnSelectedHitChanged(BreakpointHitDetailItem? value)
    {
        Registers.Clear();
        if (value?.Registers is null) return;
        foreach (var reg in value.Registers)
            Registers.Add(reg);
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
                    Status = bp.IsEnabled ? "Armed" : "Disabled"
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
        var threadId = SelectedHit?.ThreadId ?? 0;
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

    // ── Placeholder commands — disabled until Phase 7 engine stepping support ──
    [RelayCommand(CanExecute = nameof(CanStep))] private void StepIn() { }
    [RelayCommand(CanExecute = nameof(CanStep))] private void StepOver() { }
    [RelayCommand(CanExecute = nameof(CanStep))] private void StepOut() { }
    [RelayCommand(CanExecute = nameof(CanStep))] private void Continue() { }
    private bool CanStep() => false;
}
