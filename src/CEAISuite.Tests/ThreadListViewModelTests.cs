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
}
