using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class JournalViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubDialogService _dialogService = new();

    private JournalViewModel CreateVm()
    {
        var patchUndoService = new PatchUndoService(_engineFacade);
        var operationJournal = new OperationJournal();
        return new JournalViewModel(patchUndoService, operationJournal, _outputLog, _dialogService);
    }

    [Fact]
    public void RefreshPatchHistory_Empty_SetsEmptyCollection()
    {
        var vm = CreateVm();

        vm.RefreshPatchHistoryCommand.Execute(null);

        Assert.NotNull(vm.PatchHistory);
        Assert.Empty(vm.PatchHistory);
    }

    [Fact]
    public void RefreshJournal_Empty_SetsEmptyCollection()
    {
        var vm = CreateVm();

        vm.RefreshJournalCommand.Execute(null);

        Assert.NotNull(vm.JournalEntries);
        Assert.Empty(vm.JournalEntries);
    }

    [Fact]
    public async Task UndoPatchAsync_NothingToUndo_LogsInfo()
    {
        var vm = CreateVm();

        await vm.UndoPatchCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Nothing to undo"));
    }

    [Fact]
    public async Task RollbackAllAsync_UserCancels_NoAction()
    {
        var vm = CreateVm();
        _dialogService.NextConfirmResult = false;

        await vm.RollbackAllCommand.ExecuteAsync(null);

        // User denied the confirm dialog, so no rollback happens
        Assert.Empty(_outputLog.LoggedMessages);
    }

    [Fact]
    public async Task RollbackAllAsync_UserConfirms_LogsInfo()
    {
        var vm = CreateVm();
        _dialogService.NextConfirmResult = true;

        await vm.RollbackAllCommand.ExecuteAsync(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Info" && m.Message.Contains("rolled back"));
    }

    [Fact]
    public async Task RollbackSelectedEntryAsync_NoSelection_DoesNotThrow()
    {
        var vm = CreateVm();
        vm.SelectedJournalEntry = null;

        await vm.RollbackSelectedEntryCommand.ExecuteAsync(null);

        // No exception, no log entries (early return)
    }

    [Fact]
    public void Constructor_InitializesProperties()
    {
        var vm = CreateVm();

        Assert.NotNull(vm.PatchHistory);
        Assert.NotNull(vm.JournalEntries);
        Assert.Null(vm.SelectedJournalEntry);
    }
}
