using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class WorkspaceViewModelTests
{
    private readonly StubSessionRepository _repository = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubDialogService _dialogService = new();

    private WorkspaceViewModel CreateVm() =>
        new(new SessionService(_repository), _outputLog, _dialogService);

    [Fact]
    public async Task Refresh_PopulatesSessions()
    {
        _repository.AddCannedSession("session-001", "game.exe", 1234, 5, 10);
        var vm = CreateVm();

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Sessions);
        Assert.Equal("session-001", vm.Sessions[0].Id);
        Assert.Equal("game.exe", vm.Sessions[0].ProcessName);
    }

    [Fact]
    public void LoadSelected_RaisesEvent()
    {
        var vm = CreateVm();
        string? loadedId = null;
        vm.LoadSessionRequested += id => loadedId = id;
        vm.Sessions.Add(new() { Id = "session-002", ProcessName = "test" });
        vm.SelectedSession = vm.Sessions[0];

        vm.LoadSelectedCommand.Execute(null);

        Assert.Equal("session-002", loadedId);
    }

    [Fact]
    public async Task DeleteSelected_RemovesFromList()
    {
        _repository.AddCannedSession("session-003", "game.exe", 1234, 5, 10);
        _dialogService.NextConfirmResult = true;
        var vm = CreateVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSession = vm.Sessions[0];

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(vm.Sessions);
    }

    [Fact]
    public void NoSelection_LoadDoesNothing()
    {
        var vm = CreateVm();
        string? loadedId = null;
        vm.LoadSessionRequested += id => loadedId = id;
        vm.SelectedSession = null;

        vm.LoadSelectedCommand.Execute(null);

        Assert.Null(loadedId);
    }
}
