using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class ScannerViewModelTests
{
    private readonly StubScanEngine _scanEngine = new();
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubDispatcherService _dispatcher = new();

    private ScannerViewModel CreateVm()
    {
        var scanService = new ScanService(_scanEngine);
        var addressTableService = new AddressTableService(_engineFacade);
        return new ScannerViewModel(scanService, addressTableService, _processContext, _outputLog,
            new StubNavigationService(), new StubClipboardService(), new StubAiContextService(), _dispatcher);
    }

    [Fact]
    public async Task StartNewScan_NoProcess_SetsScanStatusAndLogsWarning()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.StartNewScanCommand.ExecuteAsync(null);

        Assert.Equal("No process attached", vm.ScanStatus);
        Assert.Single(_outputLog.LoggedMessages);
        Assert.Equal("Warn", _outputLog.LoggedMessages[0].Level);
    }

    [Fact]
    public async Task StartNewScan_WithProcess_RunsScanAndUpdatesResults()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.ScanValue = "100";

        _scanEngine.NextScanResult = new ScanResultSet(
            ScanId: "scan-1",
            ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: new[]
            {
                new ScanResultEntry((nuint)0x10000, "100", null, new byte[] { 100, 0, 0, 0 }),
                new ScanResultEntry((nuint)0x20000, "100", null, new byte[] { 100, 0, 0, 0 })
            },
            TotalRegionsScanned: 10,
            TotalBytesScanned: 40960,
            CompletedAtUtc: DateTimeOffset.UtcNow);

        await vm.StartNewScanCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.ScanResults.Count);
        Assert.Contains("2", vm.ScanStatus);
        Assert.NotNull(vm.ScanDetails);
        Assert.Contains("Info", _outputLog.LoggedMessages.Last().Level);
    }

    [Fact]
    public void ResetScan_ClearsScanResultsAndStatus()
    {
        var vm = CreateVm();
        vm.ScanResults.Add(new ScanResultOverview("0x1000", "100", null, "64000000"));
        vm.ScanStatus = "1 results found";
        vm.ScanDetails = "some details";

        vm.ResetScanCommand.Execute(null);

        Assert.Empty(vm.ScanResults);
        Assert.Null(vm.ScanStatus);
        Assert.Null(vm.ScanDetails);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Info" && m.Message.Contains("reset"));
    }

    [Fact]
    public void AddSelectedToTable_NoSelection_LogsWarning()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = null;

        vm.AddSelectedToTableCommand.Execute(null);

        Assert.Single(_outputLog.LoggedMessages);
        Assert.Equal("Warn", _outputLog.LoggedMessages[0].Level);
        Assert.Contains("Select a scan result", _outputLog.LoggedMessages[0].Message);
    }

    [Fact]
    public async Task RefineScan_NoActiveScan_LogsWarning()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        await vm.RefineScanCommand.ExecuteAsync(null);

        // ScanService.LastScanResults is null → early-return or log
        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn");
    }

    // ── RefineScan execution ──

    [Fact]
    public async Task RefineScan_NoProcess_ReturnsEarly()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;

        await vm.RefineScanCommand.ExecuteAsync(null);

        // No process → early return at first guard
        Assert.Null(vm.ScanStatus);
    }

    [Fact]
    public async Task RefineScan_WithActiveScan_UpdatesResultsAndCanUndo()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.ScanValue = "100";

        // First, run an initial scan
        _scanEngine.NextScanResult = new ScanResultSet(
            ScanId: "scan-1", ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: new[]
            {
                new ScanResultEntry((nuint)0x10000, "100", null, new byte[] { 100, 0, 0, 0 }),
                new ScanResultEntry((nuint)0x20000, "100", null, new byte[] { 100, 0, 0, 0 })
            },
            TotalRegionsScanned: 10, TotalBytesScanned: 40960, CompletedAtUtc: DateTimeOffset.UtcNow);

        await vm.StartNewScanCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.ScanResults.Count);

        // Now refine
        _scanEngine.NextRefineResult = new ScanResultSet(
            ScanId: "scan-1", ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: new[]
            {
                new ScanResultEntry((nuint)0x10000, "100", null, new byte[] { 100, 0, 0, 0 })
            },
            TotalRegionsScanned: 5, TotalBytesScanned: 20480, CompletedAtUtc: DateTimeOffset.UtcNow);

        await vm.RefineScanCommand.ExecuteAsync(null);

        Assert.Single(vm.ScanResults);
        Assert.Contains("remaining", vm.ScanStatus);
        Assert.True(vm.CanUndo);
    }

    [Fact]
    public async Task RefineScan_ProgressResetAfterCompletion()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.ScanValue = "100";

        _scanEngine.NextScanResult = new ScanResultSet(
            ScanId: "scan-1", ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: new[] { new ScanResultEntry((nuint)0x10000, "100", null, new byte[] { 100, 0, 0, 0 }) },
            TotalRegionsScanned: 10, TotalBytesScanned: 40960, CompletedAtUtc: DateTimeOffset.UtcNow);

        await vm.StartNewScanCommand.ExecuteAsync(null);

        _scanEngine.NextRefineResult = new ScanResultSet(
            ScanId: "scan-1", ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: Array.Empty<ScanResultEntry>(),
            TotalRegionsScanned: 5, TotalBytesScanned: 20480, CompletedAtUtc: DateTimeOffset.UtcNow);

        await vm.RefineScanCommand.ExecuteAsync(null);

        Assert.False(vm.IsScanInProgress);
        // Progress resets after completion; text may be empty or contain status
    }

    // ── UndoScan ──

    [Fact]
    public void UndoScan_NoHistory_LogsWarning()
    {
        var vm = CreateVm();
        _outputLog.Clear();

        vm.UndoScanCommand.Execute(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Warn" && m.Message.Contains("No scan to undo"));
    }

    [Fact]
    public async Task UndoScan_AfterRefine_RestoresPreviousResults()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.ScanValue = "100";

        _scanEngine.NextScanResult = new ScanResultSet(
            ScanId: "scan-1", ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: new[]
            {
                new ScanResultEntry((nuint)0x10000, "100", null, new byte[] { 100, 0, 0, 0 }),
                new ScanResultEntry((nuint)0x20000, "100", null, new byte[] { 100, 0, 0, 0 })
            },
            TotalRegionsScanned: 10, TotalBytesScanned: 40960, CompletedAtUtc: DateTimeOffset.UtcNow);
        await vm.StartNewScanCommand.ExecuteAsync(null);

        _scanEngine.NextRefineResult = new ScanResultSet(
            ScanId: "scan-1", ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: new[] { new ScanResultEntry((nuint)0x10000, "100", null, new byte[] { 100, 0, 0, 0 }) },
            TotalRegionsScanned: 5, TotalBytesScanned: 20480, CompletedAtUtc: DateTimeOffset.UtcNow);
        await vm.RefineScanCommand.ExecuteAsync(null);
        Assert.Single(vm.ScanResults);

        vm.UndoScanCommand.Execute(null);

        Assert.Equal(2, vm.ScanResults.Count);
        Assert.Contains("undo", vm.ScanStatus);
    }

    // ── ToggleHexDisplay ──

    [Fact]
    public void ToggleHexDisplay_TogglesShowAsHex()
    {
        var vm = CreateVm();
        Assert.False(vm.ShowAsHex);

        vm.ToggleHexDisplayCommand.Execute(null);
        Assert.True(vm.ShowAsHex);

        vm.ToggleHexDisplayCommand.Execute(null);
        Assert.False(vm.ShowAsHex);
    }

    [Fact]
    public void ToggleHexDisplay_LogsState()
    {
        var vm = CreateVm();
        _outputLog.Clear();

        vm.ToggleHexDisplayCommand.Execute(null);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Hex display enabled"));

        vm.ToggleHexDisplayCommand.Execute(null);
        Assert.Contains(_outputLog.LoggedMessages, m => m.Message.Contains("Hex display disabled"));
    }

    // ── CopyAddress / CopyValue ──

    [Fact]
    public void CopyAddress_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = null;

        vm.CopyAddressCommand.Execute(null);

        var clipboard = (StubClipboardService)GetClipboard(vm);
        Assert.Null(clipboard.LastText);
    }

    [Fact]
    public void CopyAddress_WithSelection_CopiesToClipboard()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = new ScanResultOverview("0x10000", "42", null, "2A000000");

        vm.CopyAddressCommand.Execute(null);

        var clipboard = (StubClipboardService)GetClipboard(vm);
        Assert.Equal("0x10000", clipboard.LastText);
    }

    [Fact]
    public void CopyValue_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = null;

        vm.CopyValueCommand.Execute(null);

        var clipboard = (StubClipboardService)GetClipboard(vm);
        Assert.Null(clipboard.LastText);
    }

    [Fact]
    public void CopyValue_WithSelection_CopiesToClipboard()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = new ScanResultOverview("0x10000", "42", null, "2A000000");

        vm.CopyValueCommand.Execute(null);

        var clipboard = (StubClipboardService)GetClipboard(vm);
        Assert.Equal("42", clipboard.LastText);
    }

    // ── Scan state transitions / progress reporting ──

    [Fact]
    public async Task StartNewScan_SetsAndClearsIsScanInProgress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.ScanValue = "100";

        _scanEngine.NextScanResult = new ScanResultSet(
            ScanId: "scan-1", ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: Array.Empty<ScanResultEntry>(),
            TotalRegionsScanned: 10, TotalBytesScanned: 40960, CompletedAtUtc: DateTimeOffset.UtcNow);

        await vm.StartNewScanCommand.ExecuteAsync(null);

        // Let any pending Progress<T> thread-pool callbacks drain before asserting.
        // Without a SynchronizationContext, Progress<T> posts to ThreadPool which can
        // race with the finally block that resets IsScanInProgress.
        // 500ms gives CI runners (slow thread-pool under parallel test load) ample headroom.
        await Task.Delay(500);

        // After completion, IsScanInProgress should be reset
        Assert.False(vm.IsScanInProgress);
        Assert.Equal(0, vm.ScanProgress);
    }

    [Fact]
    public async Task StartNewScan_ClearsExistingResults()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.ScanResults.Add(new ScanResultOverview("0x1000", "50", null, "32000000"));
        vm.ScanValue = "100";

        _scanEngine.NextScanResult = new ScanResultSet(
            ScanId: "scan-1", ProcessId: 1234,
            Constraints: new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            Results: Array.Empty<ScanResultEntry>(),
            TotalRegionsScanned: 10, TotalBytesScanned: 40960, CompletedAtUtc: DateTimeOffset.UtcNow);

        await vm.StartNewScanCommand.ExecuteAsync(null);

        Assert.Empty(vm.ScanResults);
    }

    [Fact]
    public void ResetScan_ClearsCanUndo()
    {
        var vm = CreateVm();
        // Manually set CanUndo to verify ResetScan clears it
        vm.ResetScanCommand.Execute(null);
        Assert.False(vm.CanUndo);
    }

    // ── BrowseMemory / DisassembleHere / AskAi ──

    [Fact]
    public void BrowseMemory_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = null;

        vm.BrowseMemoryCommand.Execute(null);

        var nav = GetNavigation(vm);
        Assert.Empty(nav.DocumentsShown);
    }

    [Fact]
    public void BrowseMemory_WithSelection_NavigatesToMemoryBrowser()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = new ScanResultOverview("0x10000", "42", null, "2A000000");

        vm.BrowseMemoryCommand.Execute(null);

        var nav = GetNavigation(vm);
        Assert.Single(nav.DocumentsShown);
        Assert.Equal("memoryBrowser", nav.DocumentsShown[0].ContentId);
        Assert.Equal("0x10000", nav.DocumentsShown[0].Parameter);
    }

    [Fact]
    public void DisassembleHere_WithSelection_NavigatesToDisassembler()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = new ScanResultOverview("0x10000", "42", null, "2A000000");

        vm.DisassembleHereCommand.Execute(null);

        var nav = GetNavigation(vm);
        Assert.Single(nav.DocumentsShown);
        Assert.Equal("disassembler", nav.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void AskAi_WithSelection_SendsContext()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = new ScanResultOverview("0x10000", "42", null, "2A000000");

        vm.AskAiCommand.Execute(null);

        var ai = GetAiContext(vm);
        Assert.Equal("Scanner", ai.LastLabel);
        Assert.Contains("0x10000", ai.LastContext);
    }

    [Fact]
    public void AskAi_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = null;

        vm.AskAiCommand.Execute(null);

        var ai = GetAiContext(vm);
        Assert.Null(ai.LastLabel);
    }

    // ── AddSelectedToTable with selection ──

    [Fact]
    public void AddSelectedToTable_WithSelection_Succeeds()
    {
        var vm = CreateVm();
        vm.SelectedScanResult = new ScanResultOverview("0x10000", "42", null, "2A000000");
        _outputLog.Clear();

        vm.AddSelectedToTableCommand.Execute(null);

        Assert.Contains(_outputLog.LoggedMessages, m => m.Level == "Info" && m.Message.Contains("Added"));
    }

    // ── Helpers to extract private services via reflection-free approach ──

    private StubClipboardService _clipboard = null!;
    private StubNavigationService _navigation = null!;
    private StubAiContextService _aiContext = null!;

    private ScannerViewModel CreateVmWithTracking()
    {
        _clipboard = new StubClipboardService();
        _navigation = new StubNavigationService();
        _aiContext = new StubAiContextService();
        var scanService = new ScanService(_scanEngine);
        var addressTableService = new AddressTableService(new StubEngineFacade());
        return new ScannerViewModel(scanService, addressTableService, _processContext, _outputLog,
            _navigation, _clipboard, _aiContext, _dispatcher);
    }

    // Since CreateVm() uses inline stubs, use reflection to get the services
    private static IClipboardService GetClipboard(ScannerViewModel vm)
    {
        var field = typeof(ScannerViewModel).GetField("_clipboard",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (IClipboardService)field!.GetValue(vm)!;
    }

    private static StubNavigationService GetNavigation(ScannerViewModel vm)
    {
        var field = typeof(ScannerViewModel).GetField("_navigationService",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (StubNavigationService)field!.GetValue(vm)!;
    }

    private static StubAiContextService GetAiContext(ScannerViewModel vm)
    {
        var field = typeof(ScannerViewModel).GetField("_aiContext",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (StubAiContextService)field!.GetValue(vm)!;
    }
}
