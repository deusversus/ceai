using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Desktop.Services;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase 9D: UI lifecycle smoke tests.
/// Verifies that all major ViewModels can be constructed from stubs without exceptions,
/// that their default property values are sane, and that commands are initialized.
/// These run without a WPF Dispatcher (all async dispatch goes through StubDispatcherService).
/// </summary>
public class UILifecycleTests
{
    private readonly StubEngineFacade _engine = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubNavigationService _navigation = new();
    private readonly StubClipboardService _clipboard = new();
    private readonly StubDialogService _dialog = new();
    private readonly StubDispatcherService _dispatcher = new();
    private readonly StubAiContextService _aiContext = new();

    // ── OutputLogViewModel ──

    [Fact]
    public void OutputLogViewModel_Constructs()
    {
        var vm = new OutputLogViewModel(_outputLog, _clipboard);
        Assert.NotNull(vm);
    }

    [Fact]
    public void OutputLogViewModel_CommandsInitialized()
    {
        var vm = new OutputLogViewModel(_outputLog, _clipboard);
        Assert.NotNull(vm.ClearCommand);
        Assert.NotNull(vm.CopyAllCommand);
    }

    // ── FindResultsViewModel ──

    [Fact]
    public void FindResultsViewModel_Constructs()
    {
        var vm = new FindResultsViewModel();
        Assert.NotNull(vm);
    }

    // ── ScannerViewModel ──

    [Fact]
    public void ScannerViewModel_Constructs()
    {
        var vm = CreateScannerVm();
        Assert.NotNull(vm);
    }

    [Fact]
    public void ScannerViewModel_DefaultProperties()
    {
        var vm = CreateScannerVm();
        Assert.False(vm.IsScanInProgress);
        Assert.Equal(0, vm.ScanProgress);
        Assert.Equal(string.Empty, vm.ScanProgressText);
    }

    [Fact]
    public void ScannerViewModel_CommandsInitialized()
    {
        var vm = CreateScannerVm();
        Assert.NotNull(vm.StartNewScanCommand);
    }

    private ScannerViewModel CreateScannerVm()
    {
        var scanService = new ScanService(new StubScanEngine());
        var addressTableService = new AddressTableService(_engine);
        return new ScannerViewModel(scanService, addressTableService,
            _processContext, _outputLog, _navigation, _clipboard, _aiContext);
    }

    // ── PointerScannerViewModel ──

    [Fact]
    public void PointerScannerViewModel_Constructs()
    {
        var vm = CreatePointerScannerVm();
        Assert.NotNull(vm);
        Assert.False(vm.IsScanning);
    }

    private PointerScannerViewModel CreatePointerScannerVm()
    {
        var service = new PointerScannerService(_engine);
        var addressTableService = new AddressTableService(_engine);
        return new PointerScannerViewModel(service, addressTableService,
            _processContext, _outputLog, _clipboard, _navigation, _aiContext, _dialog);
    }

    // ── AddressTableViewModel ──

    [Fact]
    public void AddressTableViewModel_Constructs()
    {
        var vm = CreateAddressTableVm();
        Assert.NotNull(vm);
    }

    [Fact]
    public void AddressTableViewModel_DefaultProperties()
    {
        var vm = CreateAddressTableVm();
        Assert.False(vm.IsBreakpointBusy);
        Assert.Null(vm.SelectedNode);
    }

    [Fact]
    public void AddressTableViewModel_Disposes()
    {
        var vm = CreateAddressTableVm();
        vm.Dispose(); // should not throw
    }

    private AddressTableViewModel CreateAddressTableVm()
    {
        var addressTableService = new AddressTableService(_engine);
        var exportService = new AddressTableExportService();
        var breakpointService = new BreakpointService(null);
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptService = new ScriptGenerationService();

        return new AddressTableViewModel(
            addressTableService, exportService, _processContext,
            autoAssemblerEngine: null,
            breakpointService, disassemblyService, scriptService,
            _dialog, _outputLog, _dispatcher, _navigation);
    }

    // ── DisassemblerViewModel ──

    [Fact]
    public void DisassemblerViewModel_Constructs()
    {
        var vm = CreateDisassemblerVm();
        Assert.NotNull(vm);
    }

    [Fact]
    public void DisassemblerViewModel_DefaultProperties()
    {
        var vm = CreateDisassemblerVm();
        Assert.False(vm.IsBreakpointBusy);
        Assert.False(vm.IsDisassembling);
        Assert.Null(vm.SelectedLine);
        Assert.Equal(string.Empty, vm.GoToAddress);
    }

    private DisassemblerViewModel CreateDisassemblerVm()
    {
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var breakpointService = new BreakpointService(null);
        var signatureService = new SignatureGeneratorService(_engine);
        var addressTableService = new AddressTableService(_engine);

        return new DisassemblerViewModel(
            disassemblyService, breakpointService, signatureService,
            addressTableService, _processContext, _navigation,
            _outputLog, _dialog, _clipboard, _aiContext);
    }

    // ── SnapshotsViewModel ──

    [Fact]
    public void SnapshotsViewModel_Constructs()
    {
        var snapshotService = new MemorySnapshotService(_engine);
        var vm = new SnapshotsViewModel(snapshotService, _processContext, _outputLog);
        Assert.NotNull(vm);
    }

    // ── StructureDissectorViewModel ──

    [Fact]
    public void StructureDissectorViewModel_Constructs()
    {
        var dissectorService = new StructureDissectorService(_engine);
        var addressTableService = new AddressTableService(_engine);
        var vm = new StructureDissectorViewModel(dissectorService, _processContext,
            _outputLog, _clipboard, _navigation, addressTableService, _aiContext);
        Assert.NotNull(vm);
    }

    // ── ScriptEditorViewModel ──

    [Fact]
    public void ScriptEditorViewModel_Constructs()
    {
        var addressTableService = new AddressTableService(_engine);
        var scriptService = new ScriptGenerationService();
        var vm = new ScriptEditorViewModel(addressTableService, null, scriptService,
            _processContext, _outputLog);
        Assert.NotNull(vm);
    }

    // ── WorkspaceViewModel ──

    [Fact]
    public void WorkspaceViewModel_Constructs()
    {
        var sessionService = new SessionService(new StubSessionRepository());
        var vm = new WorkspaceViewModel(sessionService, _outputLog, _dialog);
        Assert.NotNull(vm);
    }

    // ── BreakpointsViewModel ──

    [Fact]
    public void BreakpointsViewModel_Constructs()
    {
        var breakpointService = new BreakpointService(null);
        var vm = new BreakpointsViewModel(breakpointService, new StubCodeCaveEngine(),
            _processContext, _outputLog);
        Assert.NotNull(vm);
    }

    // ── ModuleListViewModel ──

    [Fact]
    public void ModuleListViewModel_Constructs()
    {
        var vm = new ModuleListViewModel(_engine, _processContext, _outputLog,
            _navigation, _clipboard, _aiContext);
        Assert.NotNull(vm);
    }

    // ── ThreadListViewModel ──

    [Fact]
    public void ThreadListViewModel_Constructs()
    {
        var vm = new ThreadListViewModel(new StubCallStackEngine(), _engine,
            _processContext, _outputLog, _navigation, _clipboard, _aiContext);
        Assert.NotNull(vm);
    }

    // ── MemoryRegionsViewModel ──

    [Fact]
    public void MemoryRegionsViewModel_Constructs()
    {
        var vm = new MemoryRegionsViewModel(new StubScanEngine(), _engine,
            _processContext, _outputLog, _navigation, _clipboard, _aiContext);
        Assert.NotNull(vm);
    }

    // ── InspectionViewModel ──

    [Fact]
    public void InspectionViewModel_Constructs()
    {
        var repo = new StubSessionRepository();
        var dashboardService = new WorkspaceDashboardService(_engine, repo);
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var breakpointService = new BreakpointService(null);
        var addressTableService = new AddressTableService(_engine);

        var vm = new InspectionViewModel(dashboardService, _processContext,
            disassemblyService, breakpointService, addressTableService,
            _dialog, _outputLog);
        Assert.NotNull(vm);
    }

    // ── ProcessListViewModel ──

    [Fact]
    public void ProcessListViewModel_ConstructsAndDisposes()
    {
        var dashboardService = new WorkspaceDashboardService(_engine, new StubSessionRepository());
        var vm = new ProcessListViewModel(dashboardService, _processContext, _outputLog);
        Assert.NotNull(vm);
        vm.Dispose(); // should not throw
    }
}
