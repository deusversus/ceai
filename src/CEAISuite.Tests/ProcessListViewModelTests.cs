using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ProcessListViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();

    private ProcessListViewModel CreateVm()
    {
        var dashboardService = new WorkspaceDashboardService(_engineFacade, new StubSessionRepository());
        return new ProcessListViewModel(dashboardService, _processContext, _outputLog);
    }

    [Fact]
    public async Task Refresh_PopulatesProcessList()
    {
        var vm = CreateVm();

        await vm.RefreshCommand.ExecuteAsync(null);

        // StubEngineFacade returns 3 test processes
        Assert.NotEmpty(vm.Processes);
    }
}
