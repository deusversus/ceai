using CEAISuite.Application;
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

    private ScannerViewModel CreateVm()
    {
        var scanService = new ScanService(_scanEngine);
        var addressTableService = new AddressTableService(_engineFacade);
        return new ScannerViewModel(scanService, addressTableService, _processContext, _outputLog,
            new StubNavigationService(), new StubClipboardService(), new StubAiContextService());
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
}
