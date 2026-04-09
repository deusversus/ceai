using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ThreadListViewModelTests
{
    private readonly StubCallStackEngine _callStackEngine = new();
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubNavigationService _navigationService = new();

    private ThreadListViewModel CreateVm() =>
        new(_callStackEngine, _engineFacade, _processContext, _outputLog, _navigationService,
            new StubClipboardService(), new StubAiContextService());

    [Fact]
    public async Task Refresh_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task Refresh_WithProcess_PopulatesThreads()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        // StubCallStackEngine.WalkAllThreadsAsync returns 1 thread (id=0) with NextFrames

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Threads);
        Assert.Contains("thread", vm.StatusText);
    }

    [Fact]
    public async Task ExpandStack_PopulatesFrames()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        await vm.ExpandStackCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.ExpandedStack);
    }

    // ── RefreshAsync additional tests ──

    [Fact]
    public async Task Refresh_WithProcess_SetsStatusWithThreadCount()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("thread", vm.StatusText);
        Assert.Contains("1", vm.StatusText); // 1 thread from StubCallStackEngine
    }

    [Fact]
    public async Task Refresh_ClearsExistingThreadsAndStack()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        // First refresh
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Threads);

        // Second refresh should clear and repopulate
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Single(vm.Threads); // StubCallStackEngine returns 1 thread
    }

    // ── Thread list population ──

    [Fact]
    public async Task Refresh_ThreadHasCorrectProperties()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        await vm.RefreshCommand.ExecuteAsync(null);

        var thread = vm.Threads[0];
        Assert.Equal(0, thread.ThreadId);
        Assert.Equal("Running", thread.State);
        Assert.Contains("0x", thread.CurrentInstruction);
    }

    // ── Selection changes ──

    [Fact]
    public async Task SelectedThread_CanBeSet()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SelectedThread = vm.Threads[0];

        Assert.NotNull(vm.SelectedThread);
        Assert.Equal(0, vm.SelectedThread.ThreadId);
    }

    // ── ExpandStack additional ──

    [Fact]
    public async Task ExpandStack_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedThread = null;

        await vm.ExpandStackCommand.ExecuteAsync(null);

        Assert.Empty(vm.ExpandedStack);
    }

    [Fact]
    public async Task ExpandStack_NoProcess_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        _processContext.AttachedProcessId = null;

        await vm.ExpandStackCommand.ExecuteAsync(null);

        Assert.Empty(vm.ExpandedStack);
    }

    [Fact]
    public async Task ExpandStack_SetsStatusWithFrameCount()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        await vm.ExpandStackCommand.ExecuteAsync(null);

        Assert.Contains("frame", vm.StatusText);
    }

    [Fact]
    public async Task ExpandStack_FrameHasCorrectProperties()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        await vm.ExpandStackCommand.ExecuteAsync(null);

        var frame = vm.ExpandedStack[0];
        Assert.Equal(0, frame.FrameIndex);
        Assert.StartsWith("0x", frame.InstructionPointer);
        Assert.Contains("+0x", frame.ModuleOffset);
        Assert.StartsWith("0x", frame.ReturnAddress);
    }

    // ── NavigateToInstruction ──

    [Fact]
    public async Task NavigateToInstruction_WithSelection_NavigatesToDisassembler()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        vm.NavigateToInstructionCommand.Execute(null);

        Assert.Single(_navigationService.DocumentsShown);
        Assert.Equal("disassembler", _navigationService.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void NavigateToInstruction_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedThread = null;

        vm.NavigateToInstructionCommand.Execute(null);

        Assert.Empty(_navigationService.DocumentsShown);
    }

    // ── CopyThreadId ──

    [Fact]
    public async Task CopyThreadId_WithSelection_CopiesToClipboard()
    {
        var clipboard = new StubClipboardService();
        var vm = new ThreadListViewModel(_callStackEngine, _engineFacade, _processContext, _outputLog,
            _navigationService, clipboard, new StubAiContextService());
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        vm.CopyThreadIdCommand.Execute(null);

        Assert.Equal("0", clipboard.LastText);
    }

    [Fact]
    public void CopyThreadId_NoSelection_DoesNothing()
    {
        var clipboard = new StubClipboardService();
        var vm = new ThreadListViewModel(_callStackEngine, _engineFacade, _processContext, _outputLog,
            _navigationService, clipboard, new StubAiContextService());
        vm.SelectedThread = null;

        vm.CopyThreadIdCommand.Execute(null);

        Assert.Null(clipboard.LastText);
    }

    // ── CopyInstructionPointer ──

    [Fact]
    public async Task CopyInstructionPointer_WithSelection_CopiesToClipboard()
    {
        var clipboard = new StubClipboardService();
        var vm = new ThreadListViewModel(_callStackEngine, _engineFacade, _processContext, _outputLog,
            _navigationService, clipboard, new StubAiContextService());
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        vm.CopyInstructionPointerCommand.Execute(null);

        Assert.NotNull(clipboard.LastText);
        Assert.Contains("0x", clipboard.LastText);
    }

    // ── BrowseStack ──

    [Fact]
    public async Task BrowseStack_WithSelection_NavigatesToMemoryBrowser()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        vm.BrowseStackCommand.Execute(null);

        Assert.Single(_navigationService.DocumentsShown);
        Assert.Equal("memoryBrowser", _navigationService.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void BrowseStack_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedThread = null;

        vm.BrowseStackCommand.Execute(null);

        Assert.Empty(_navigationService.DocumentsShown);
    }

    // ── AskAi ──

    [Fact]
    public async Task AskAi_WithSelection_SendsContext()
    {
        var aiContext = new StubAiContextService();
        var vm = new ThreadListViewModel(_callStackEngine, _engineFacade, _processContext, _outputLog,
            _navigationService, new StubClipboardService(), aiContext);
        _processContext.AttachedProcessId = 1234;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedThread = vm.Threads[0];

        vm.AskAiCommand.Execute(null);

        Assert.Equal("Thread", aiContext.LastLabel);
        Assert.Contains("Thread 0", aiContext.LastContext);
    }

    [Fact]
    public void AskAi_NoSelection_DoesNothing()
    {
        var aiContext = new StubAiContextService();
        var vm = new ThreadListViewModel(_callStackEngine, _engineFacade, _processContext, _outputLog,
            _navigationService, new StubClipboardService(), aiContext);
        vm.SelectedThread = null;

        vm.AskAiCommand.Execute(null);

        Assert.Null(aiContext.LastLabel);
    }
}
