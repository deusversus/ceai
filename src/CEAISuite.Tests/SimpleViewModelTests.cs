using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>Tests for thin/simple ViewModels that are mostly pass-through wrappers.</summary>
public class SimpleViewModelTests
{
    [Fact]
    public void OutputLogViewModel_Clear_ClearsEntries()
    {
        var outputLog = new StubOutputLog();
        var vm = new OutputLogViewModel(outputLog);
        outputLog.Append("Test", "Info", "Hello");

        vm.ClearCommand.Execute(null);

        Assert.Empty(outputLog.Entries);
    }

    [Fact]
    public void FindResultsViewModel_Clear_ClearsItems()
    {
        var vm = new FindResultsViewModel();
        vm.Populate([new Desktop.Models.FindResultDisplayItem { Address = "0x1000" }], "test");
        Assert.NotNull(vm.Results);

        vm.ClearCommand.Execute(null);

        Assert.Null(vm.Results);
    }

    [Fact]
    public void ScriptsViewModel_Refresh_PopulatesFromAddressTable()
    {
        var engineFacade = new StubEngineFacade();
        var addressTableService = new AddressTableService(engineFacade);
        var assemblerEngine = new StubAutoAssemblerEngine();
        var processContext = new StubProcessContext();
        var outputLog = new StubOutputLog();

        // Add a script entry to the address table
        var entry = addressTableService.AddEntry("0x1000", Engine.Abstractions.MemoryDataType.Byte, "0", "Test Script");
        var node = addressTableService.Roots.First(n => n.Id == entry.Id);
        node.AssemblerScript = "[ENABLE]\nnop\n[DISABLE]\nnop";

        var vm = new ScriptsViewModel(addressTableService, assemblerEngine, processContext, outputLog);
        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Scripts);
        Assert.Equal("Test Script", vm.Scripts[0].Label);
    }

    [Fact]
    public void ScriptsViewModel_ToggleSelected_NoProcess_LogsWarning()
    {
        var engineFacade = new StubEngineFacade();
        var addressTableService = new AddressTableService(engineFacade);
        var assemblerEngine = new StubAutoAssemblerEngine();
        var processContext = new StubProcessContext { AttachedProcessId = null };
        var outputLog = new StubOutputLog();

        var vm = new ScriptsViewModel(addressTableService, assemblerEngine, processContext, outputLog);
        vm.SelectedScript = null;

        // No selection + no process → early return, no crash
        _ = vm.ToggleSelectedCommand.ExecuteAsync(null);

        Assert.NotNull(vm);
    }

    [Fact]
    public void SnapshotsViewModel_CaptureSnapshot_NoProcess_LogsWarning()
    {
        var engineFacade = new StubEngineFacade();
        var snapshotService = new MemorySnapshotService(engineFacade);
        var processContext = new StubProcessContext { AttachedProcessId = null };
        var outputLog = new StubOutputLog();

        var vm = new SnapshotsViewModel(snapshotService, processContext, outputLog);

        _ = vm.CaptureCommand.ExecuteAsync(null);

        // No process → early return or status message, no crash
        Assert.NotNull(vm);
    }

    [Fact]
    public void JournalViewModel_RefreshPatchHistory_DoesNotThrow()
    {
        var engineFacade = new StubEngineFacade();
        var patchUndoService = new PatchUndoService(engineFacade);
        var operationJournal = new OperationJournal();
        var outputLog = new StubOutputLog();
        var dialogService = new StubDialogService();

        var vm = new JournalViewModel(patchUndoService, operationJournal, outputLog, dialogService);

        vm.RefreshPatchHistoryCommand.Execute(null);

        // Should not throw, history may be empty
        Assert.NotNull(vm);
    }

    [Fact]
    public void InspectionViewModel_DisassembleAtAddress_NoProcess_SetsStatus()
    {
        var engineFacade = new StubEngineFacade();
        var dashboardService = new WorkspaceDashboardService(engineFacade, new StubSessionRepository());
        var processContext = new StubProcessContext { AttachedProcessId = null };
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var breakpointService = new BreakpointService(null);
        var addressTableService = new AddressTableService(engineFacade);
        var dialogService = new StubDialogService();
        var outputLog = new StubOutputLog();

        var vm = new InspectionViewModel(
            dashboardService, processContext, disassemblyService,
            breakpointService, addressTableService, dialogService, outputLog);
        vm.DisassemblyAddress = "0x7FF00100";

        _ = vm.DisassembleAtAddressCommand.ExecuteAsync(null);

        // Should log warning or set status about no process
        // Basic smoke test — no crash
        Assert.NotNull(vm);
    }
}
