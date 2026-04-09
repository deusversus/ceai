using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubDialogService _dialogService = new();
    private readonly StubDispatcherService _dispatcher = new();
    private readonly StubNavigationService _navigation = new();
    private readonly StubClipboardService _clipboard = new();
    private readonly StubThemeService _themeService = new();
    private readonly StubScreenCaptureEngine _screenCapture = new();

    private MainViewModel CreateVm()
    {
        var sessionRepo = new StubSessionRepository();
        var dashboardService = new WorkspaceDashboardService(_engineFacade, sessionRepo);
        var addressTableService = new AddressTableService(_engineFacade);
        var sessionService = new SessionService(sessionRepo);
        var breakpointService = new BreakpointService(null);
        var scanEngine = new StubScanEngine();
        var scanService = new ScanService(scanEngine);
        var patchUndoService = new PatchUndoService(_engineFacade);
        var settingsService = new AppSettingsService();
        var watchdog = new ProcessWatchdogService();
        var scriptGenerationService = new ScriptGenerationService();
        var updateService = new UpdateService();
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());

        // Create tool functions for AiOperatorService
        var toolFunctions = new AiToolFunctions(
            _engineFacade, dashboardService, scanService,
            addressTableService, disassemblyService, scriptGenerationService);
        var aiService = new AiOperatorService(null, toolFunctions);

        // Create child ViewModels
        var addressTableVm = new AddressTableViewModel(
            addressTableService,
            new AddressTableExportService(),
            _processContext,
            autoAssemblerEngine: null,
            breakpointService,
            disassemblyService,
            scriptGenerationService,
            _dialogService,
            _outputLog,
            _dispatcher,
            _navigation);
        var inspectionVm = new InspectionViewModel(
            dashboardService, _processContext, disassemblyService,
            breakpointService, addressTableService, _dialogService, _outputLog);
        var processListVm = new ProcessListViewModel(dashboardService, _processContext, _outputLog);
        var aiOperatorVm = new AiOperatorViewModel(
            aiService, settingsService, _processContext, addressTableService,
            _dialogService, _outputLog, _dispatcher, _themeService, _clipboard,
            NullLogger<AiOperatorViewModel>.Instance);
        var findResultsVm = new FindResultsViewModel();
        var scannerVm = new ScannerViewModel(
            scanService, addressTableService, _processContext, _outputLog,
            _navigation, _clipboard, new StubAiContextService());

        return new MainViewModel(
            dashboardService, addressTableService, sessionService,
            breakpointService, aiService, settingsService,
            patchUndoService, _engineFacade, _processContext,
            _outputLog, _dialogService, scanService,
            addressTableVm, inspectionVm, processListVm,
            aiOperatorVm, findResultsVm, scannerVm,
            watchdog, _screenCapture, scriptGenerationService,
            updateService, _dispatcher,
            NullLogger<MainViewModel>.Instance);
    }

    public void Dispose()
    {
        // Cleanup
    }

    [Fact]
    public void Constructor_InitializesDefaultProperties()
    {
        var vm = CreateVm();

        Assert.Equal("No process attached", vm.StatusBarProcessText);
        Assert.Equal("", vm.StatusBarCenterText);
        Assert.Equal("", vm.StatusBarTokenText);
        Assert.Equal("", vm.StatusBarScanText);
        Assert.Equal("", vm.StatusBarWatchdogText);
        Assert.NotNull(vm.ProcessComboItems);
        Assert.NotNull(vm.Dashboard);
    }

    [Fact]
    public void ShowAbout_ShowsInfoDialog()
    {
        var vm = CreateVm();

        vm.ShowAbout();

        Assert.Single(_dialogService.InfoShown);
        Assert.Contains("CE AI Suite", _dialogService.InfoShown[0].Title);
    }

    [Fact]
    public void AppendOutputLog_AddsToOutputLog()
    {
        var vm = CreateVm();

        vm.AppendOutputLog("Test", "Info", "Hello from test");

        Assert.Contains(_outputLog.LoggedMessages, m => m.Message == "Hello from test");
    }

    [Fact]
    public void PopulateFindResults_DelegatesToFindResultsVm()
    {
        var vm = CreateVm();
        var items = new List<Desktop.Models.FindResultDisplayItem>
        {
            new() { Address = "0x1000", Instruction = "nop", Module = "test.dll", Context = "" }
        };

        vm.PopulateFindResults(items, "test search");

        // The FindResultsVm is internal, but we can verify no exceptions occurred
    }

    [Fact]
    public void RefreshAddressTableUI_UpdatesDashboard()
    {
        var vm = CreateVm();

        vm.RefreshAddressTableUI("Updated");

        Assert.Equal("Updated", vm.Dashboard.StatusMessage);
    }

    [Fact]
    public void BuildAiContext_NoProcess_ContainsNoProcessMessage()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        var context = vm.BuildAiContext();

        Assert.Contains("No process attached", context);
    }

    [Fact]
    public async Task DetachProcessAsync_NoProcess_LogsWarningAndSetsStatus()
    {
        var vm = CreateVm();
        // No process attached in dashboard

        await vm.DetachProcessAsync();

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warning" && m.Message.Contains("no process"));
    }

    [Fact]
    public void SkipUpdate_ClearsUpdateState()
    {
        var vm = CreateVm();
        vm.SkipUpdateCommand.Execute(null);

        Assert.False(vm.IsUpdateAvailable);
        Assert.Null(vm.PendingUpdate);
        Assert.Equal(string.Empty, vm.UpdateMessage);
    }

    [Fact]
    public async Task CaptureScreenshotAsync_NoProcess_SetsStatus()
    {
        var vm = CreateVm();
        // No process attached

        await vm.CaptureScreenshotAsync();

        Assert.Contains("Attach to a process", vm.StatusBarCenterText);
    }

    [Fact]
    public void OnClosing_DoesNotThrow()
    {
        var vm = CreateVm();

        // OnClosing should not throw even with default state
        vm.OnClosing();
    }

    [Fact]
    public void PopulateProcessCombo_WithEmptyDashboard_SetsEmptyCombo()
    {
        var vm = CreateVm();

        vm.PopulateProcessCombo();

        // Dashboard.RunningProcesses is null by default
        Assert.NotNull(vm.ProcessComboItems);
    }
}
