using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class DisassemblerViewModelTests
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubDisassemblyEngine _disassemblyEngine = new();
    private readonly StubBreakpointEngine _breakpointEngine = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubDialogService _dialogService = new();
    private readonly StubNavigationService _navigationService = new();
    private readonly StubClipboardService _clipboard = new();

    private DisassemblerViewModel CreateVm()
    {
        var disassemblyService = new DisassemblyService(_disassemblyEngine);
        var breakpointService = new BreakpointService(_breakpointEngine);
        var signatureService = new SignatureGeneratorService(_engineFacade);
        var addressTableService = new AddressTableService(_engineFacade);

        return new DisassemblerViewModel(
            disassemblyService, breakpointService, signatureService, addressTableService,
            _processContext, _navigationService, _outputLog, _dialogService, _clipboard,
            new StubAiContextService());
    }

    [Fact]
    public async Task GoToAddress_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.GoToAddress = "0x7FF00100";

        await vm.GoToAddressCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task GoToAddress_ValidAddress_PopulatesLines()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.GoToAddress = "0x7FF00100";

        await vm.GoToAddressCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.Lines);
        Assert.Equal(11, vm.Lines.Count);
        Assert.Equal("mov", vm.Lines[0].Mnemonic);
    }

    [Fact]
    public async Task GoToAddress_PushesBackStack()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.GoToAddress = "0x7FF00100";
        await vm.GoToAddressCommand.ExecuteAsync(null);
        Assert.False(vm.CanGoBack);

        vm.GoToAddress = "0x7FF00200";
        await vm.GoToAddressCommand.ExecuteAsync(null);
        Assert.True(vm.CanGoBack);
    }

    [Fact]
    public async Task GoBack_RestoresPreviousAddress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.GoToAddress = "0x7FF00100";
        await vm.GoToAddressCommand.ExecuteAsync(null);
        vm.GoToAddress = "0x7FF00200";
        await vm.GoToAddressCommand.ExecuteAsync(null);

        await vm.GoBackCommand.ExecuteAsync(null);

        Assert.Equal("0x7FF00100", vm.GoToAddress);
        Assert.True(vm.CanGoForward);
        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public async Task GoForward_AfterGoBack_RestoresForwardAddress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.GoToAddress = "0x7FF00100";
        await vm.GoToAddressCommand.ExecuteAsync(null);
        vm.GoToAddress = "0x7FF00200";
        await vm.GoToAddressCommand.ExecuteAsync(null);
        await vm.GoBackCommand.ExecuteAsync(null);

        await vm.GoForwardCommand.ExecuteAsync(null);

        Assert.Equal("0x7FF00200", vm.GoToAddress);
        Assert.False(vm.CanGoForward);
    }

    [Fact]
    public void SearchInstructions_MatchesFound_SelectsFirst()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        // Manually populate lines to avoid async
        vm.NavigateToAddress("0x7FF00100");
        // Wait for async to complete (fire-and-forget in NavigateToAddress)
        Thread.Sleep(200);

        vm.SearchPattern = "call";
        vm.SearchInstructionsCommand.Execute(null);

        Assert.NotNull(vm.SelectedLine);
        Assert.Contains("match", vm.StatusText ?? "");
    }

    [Fact]
    public void SearchInstructions_InvalidRegex_SetsStatusError()
    {
        var vm = CreateVm();
        vm.SearchPattern = "[invalid";

        vm.SearchInstructionsCommand.Execute(null);

        Assert.Contains("Invalid regex", vm.StatusText);
    }

    [Fact]
    public void CopySelectedRange_PopulatesClipboard()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);

        vm.CopySelectedRangeCommand.Execute(null);

        Assert.NotNull(_clipboard.LastText);
        Assert.Contains("mov", _clipboard.LastText);
    }

    [Fact]
    public async Task SetBreakpoint_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        await vm.SetBreakpointAtSelectedCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }
}
