using System.Collections.ObjectModel;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class ThreadListViewModel : ObservableObject
{
    private readonly ICallStackEngine _callStackEngine;
    private readonly IEngineFacade _engineFacade;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly INavigationService _navigationService;

    private readonly IClipboardService _clipboard;
    private readonly IAiContextService _aiContext;

    public ThreadListViewModel(
        ICallStackEngine callStackEngine,
        IEngineFacade engineFacade,
        IProcessContext processContext,
        IOutputLog outputLog,
        INavigationService navigationService,
        IClipboardService clipboard,
        IAiContextService aiContext)
    {
        _callStackEngine = callStackEngine;
        _engineFacade = engineFacade;
        _processContext = processContext;
        _outputLog = outputLog;
        _navigationService = navigationService;
        _clipboard = clipboard;
        _aiContext = aiContext;

        _processContext.ProcessChanged += () => _ = RefreshAsync();
    }

    [ObservableProperty] private ObservableCollection<ThreadDisplayItem> _threads = new();
    [ObservableProperty] private ThreadDisplayItem? _selectedThread;
    [ObservableProperty] private ObservableCollection<CallStackFrameDisplayItem> _expandedStack = new();
    [ObservableProperty] private string? _statusText;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        try
        {
            var attachment = await _engineFacade.AttachAsync(pid.Value);
            var allStacks = await _callStackEngine.WalkAllThreadsAsync(pid.Value, attachment.Modules, 8);

            Threads.Clear();
            ExpandedStack.Clear();
            foreach (var (threadId, frames) in allStacks.OrderByDescending(kv => kv.Value.Count))
            {
                var topFrame = frames.FirstOrDefault();
                Threads.Add(new ThreadDisplayItem
                {
                    ThreadId = threadId,
                    State = frames.Count > 0 ? "Running" : "Unknown",
                    CurrentInstruction = topFrame is not null ? $"0x{topFrame.InstructionPointer:X}" : "—",
                    Module = topFrame?.ModuleName ?? ""
                });
            }
            StatusText = $"{Threads.Count} thread(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _outputLog.Append("Threads", "Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExpandStackAsync()
    {
        if (SelectedThread is null) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) return;
        try
        {
            var attachment = await _engineFacade.AttachAsync(pid.Value);
            var frames = await _callStackEngine.WalkStackAsync(
                pid.Value, SelectedThread.ThreadId, attachment.Modules, 64);

            ExpandedStack.Clear();
            foreach (var frame in frames)
            {
                ExpandedStack.Add(new CallStackFrameDisplayItem
                {
                    FrameIndex = frame.FrameIndex,
                    InstructionPointer = $"0x{frame.InstructionPointer:X}",
                    ModuleOffset = frame.ModuleName is not null
                        ? $"{frame.ModuleName}+0x{frame.ModuleOffset:X}"
                        : $"0x{frame.InstructionPointer:X}",
                    ReturnAddress = $"0x{frame.ReturnAddress:X}"
                });
            }
            StatusText = $"Thread {SelectedThread.ThreadId}: {ExpandedStack.Count} frame(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void NavigateToInstruction()
    {
        if (SelectedThread is null) return;
        _navigationService.ShowDocument("disassembler", SelectedThread.CurrentInstruction);
    }

    // ── Cross-panel context menu commands ──

    [RelayCommand]
    private void CopyThreadId()
    {
        if (SelectedThread is null) return;
        _clipboard.SetText(SelectedThread.ThreadId.ToString());
    }

    [RelayCommand]
    private void CopyInstructionPointer()
    {
        if (SelectedThread is null) return;
        _clipboard.SetText(SelectedThread.CurrentInstruction);
    }

    [RelayCommand]
    private void BrowseStack()
    {
        if (SelectedThread is null) return;
        _navigationService.ShowDocument("memoryBrowser", SelectedThread.CurrentInstruction);
    }

    [RelayCommand]
    private void AskAi()
    {
        if (SelectedThread is null) return;
        _aiContext.SendContext("Thread",
            $"Thread {SelectedThread.ThreadId} ({SelectedThread.State}) at {SelectedThread.CurrentInstruction} in {SelectedThread.Module}");
    }
}
