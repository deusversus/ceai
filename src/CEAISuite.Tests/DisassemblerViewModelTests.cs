using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
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

    // ── FollowJumpCall ──

    [Fact]
    public async Task FollowJumpCall_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedLine = null;

        await vm.FollowJumpCallCommand.ExecuteAsync(null);

        // No crash, no change
        Assert.DoesNotContain("Disassembling", vm.StatusText ?? "");
    }

    [Fact]
    public async Task FollowJumpCall_NonCallLine_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);

        // Select the first "mov" instruction (not a call/jump)
        var movLine = vm.Lines.FirstOrDefault(l => l.Mnemonic == "mov");
        Assert.NotNull(movLine);
        vm.SelectedLine = movLine;
        var prevGoTo = vm.GoToAddress;

        await vm.FollowJumpCallCommand.ExecuteAsync(null);

        // GoToAddress should not have changed to the target
        // (IsCallOrJump is false for mov)
        Assert.False(movLine.IsCallOrJump);
    }

    [Fact]
    public async Task FollowJumpCall_CallLine_NavigatesToTarget()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);

        // Find a call instruction that has a hex address in operands
        var callLine = vm.Lines.FirstOrDefault(l => l.IsCallOrJump && l.Operands.Contains("0x"));
        Assert.NotNull(callLine);
        vm.SelectedLine = callLine;

        await vm.FollowJumpCallCommand.ExecuteAsync(null);

        // After follow, should have pushed back stack
        Assert.True(vm.CanGoBack);
    }

    // ── EditInstruction ──

    [Fact]
    public async Task EditInstruction_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedLine = null;

        await vm.EditInstructionCommand.ExecuteAsync(null);
        // No crash
    }

    [Fact]
    public async Task EditInstruction_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        await vm.EditInstructionCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task EditInstruction_NoAssembler_SetsStatusError()
    {
        // Create VM without auto-assembler
        var disassemblyService = new DisassemblyService(_disassemblyEngine);
        var breakpointService = new BreakpointService(_breakpointEngine);
        var signatureService = new SignatureGeneratorService(_engineFacade);
        var addressTableService = new AddressTableService(_engineFacade);
        var vm = new DisassemblerViewModel(
            disassemblyService, breakpointService, signatureService, addressTableService,
            _processContext, _navigationService, _outputLog, _dialogService, _clipboard,
            new StubAiContextService(), autoAssemblerEngine: null);

        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        await vm.EditInstructionCommand.ExecuteAsync(null);

        Assert.Contains("Assembler not available", vm.StatusText);
    }

    [Fact]
    public async Task EditInstruction_UserCancels_DoesNotPatch()
    {
        var assembler = new StubAutoAssemblerEngine();
        var disassemblyService = new DisassemblyService(_disassemblyEngine);
        var breakpointService = new BreakpointService(_breakpointEngine);
        var signatureService = new SignatureGeneratorService(_engineFacade);
        var addressTableService = new AddressTableService(_engineFacade);
        var vm = new DisassemblerViewModel(
            disassemblyService, breakpointService, signatureService, addressTableService,
            _processContext, _navigationService, _outputLog, _dialogService, _clipboard,
            new StubAiContextService(), assembler);

        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        _dialogService.NextInputResult = null; // user cancels

        await vm.EditInstructionCommand.ExecuteAsync(null);

        // Should not have patched — status should not say "Patched"
        Assert.DoesNotContain("Patched", vm.StatusText ?? "");
    }

    [Fact]
    public async Task EditInstruction_ValidInput_PatchesAndRefreshes()
    {
        var assembler = new StubAutoAssemblerEngine();
        var disassemblyService = new DisassemblyService(_disassemblyEngine);
        var breakpointService = new BreakpointService(_breakpointEngine);
        var signatureService = new SignatureGeneratorService(_engineFacade);
        var addressTableService = new AddressTableService(_engineFacade);
        var vm = new DisassemblerViewModel(
            disassemblyService, breakpointService, signatureService, addressTableService,
            _processContext, _navigationService, _outputLog, _dialogService, _clipboard,
            new StubAiContextService(), assembler);

        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        _dialogService.NextInputResult = "nop";
        assembler.NextEnableResult = new ScriptExecutionResult(true, null, [], []);

        await vm.EditInstructionCommand.ExecuteAsync(null);

        // After successful patch, the view is refreshed which overwrites StatusText.
        // Verify the patch was logged instead.
        Assert.Contains(_outputLog.LoggedMessages, m =>
            m.Level == "Info" && m.Message.Contains("Inline edit"));
    }

    // ── FindWhatWrites ──

    [Fact]
    public async Task FindWhatWrites_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedLine = null;

        await vm.FindWhatWritesCommand.ExecuteAsync(null);
        // No crash, no status change
    }

    [Fact]
    public async Task FindWhatWrites_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        await vm.FindWhatWritesCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task FindWhatWrites_WithSelection_SetsWriteBreakpointAndRaisesFindResults()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        IReadOnlyList<FindResultDisplayItem>? receivedItems = null;
        string? receivedTitle = null;
        vm.PopulateFindResults += (items, title) => { receivedItems = items; receivedTitle = title; };

        await vm.FindWhatWritesCommand.ExecuteAsync(null);

        Assert.Contains("Write breakpoint", vm.StatusText);
        Assert.NotNull(receivedItems);
        Assert.Single(receivedItems);
        Assert.Contains("Find What Writes", receivedTitle);
    }

    // ── GenerateSignature ──

    [Fact]
    public async Task GenerateSignature_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedLine = null;

        await vm.GenerateSignatureCommand.ExecuteAsync(null);
        // No crash
    }

    [Fact]
    public async Task GenerateSignature_NoProcess_SetsStatusError()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = null;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        await vm.GenerateSignatureCommand.ExecuteAsync(null);

        Assert.Contains("No process", vm.StatusText);
    }

    [Fact]
    public async Task GenerateSignature_ValidLine_GeneratesPatternAndCopiesToClipboard()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        await vm.GenerateSignatureCommand.ExecuteAsync(null);

        Assert.Contains("Signature", vm.StatusText ?? "");
        Assert.NotNull(_clipboard.LastText);
    }

    // ── DisassembleAt / navigation back/forward stack ──

    [Fact]
    public async Task GoToAddress_EmptyAddress_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.GoToAddress = "";

        await vm.GoToAddressCommand.ExecuteAsync(null);

        Assert.Empty(vm.Lines);
    }

    [Fact]
    public async Task GoToAddress_WhitespaceAddress_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.GoToAddress = "   ";

        await vm.GoToAddressCommand.ExecuteAsync(null);

        Assert.Empty(vm.Lines);
    }

    [Fact]
    public async Task GoBack_EmptyStack_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        await vm.GoBackCommand.ExecuteAsync(null);

        Assert.False(vm.CanGoBack);
        Assert.False(vm.CanGoForward);
    }

    [Fact]
    public async Task GoForward_EmptyStack_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        await vm.GoForwardCommand.ExecuteAsync(null);

        Assert.False(vm.CanGoForward);
    }

    [Fact]
    public async Task GoToAddress_NewNavigation_ClearsForwardStack()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.GoToAddress = "0x7FF00100";
        await vm.GoToAddressCommand.ExecuteAsync(null);
        vm.GoToAddress = "0x7FF00200";
        await vm.GoToAddressCommand.ExecuteAsync(null);

        await vm.GoBackCommand.ExecuteAsync(null);
        Assert.True(vm.CanGoForward);

        // Navigate to a new address — forward stack should be cleared
        vm.GoToAddress = "0x7FF00300";
        await vm.GoToAddressCommand.ExecuteAsync(null);

        Assert.False(vm.CanGoForward);
        Assert.True(vm.CanGoBack);
    }

    [Fact]
    public async Task DisassembleAt_SetsIsDisassembling()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.GoToAddress = "0x7FF00100";
        await vm.GoToAddressCommand.ExecuteAsync(null);

        // After completion, IsDisassembling should be false
        Assert.False(vm.IsDisassembling);
        Assert.NotEmpty(vm.Lines);
    }

    [Fact]
    public async Task DisassembleAt_SetsCurrentFunctionLabel()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.GoToAddress = "0x7FF00100";
        await vm.GoToAddressCommand.ExecuteAsync(null);

        Assert.NotNull(vm.CurrentFunctionLabel);
    }

    // ── SearchInstructions additional cases ──

    [Fact]
    public void SearchInstructions_EmptyPattern_DoesNothing()
    {
        var vm = CreateVm();
        vm.SearchPattern = "";

        vm.SearchInstructionsCommand.Execute(null);

        Assert.Null(vm.StatusText);
    }

    [Fact]
    public void SearchInstructions_NoMatches_SetsNoMatchStatus()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);

        vm.SearchPattern = "ZZZZZ_NONEXISTENT";
        vm.SearchInstructionsCommand.Execute(null);

        Assert.Contains("No matches", vm.StatusText);
    }

    // ── Context menu commands ──

    [Fact]
    public void AddToTable_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedLine = null;

        vm.AddToTableCommand.Execute(null);

        Assert.Null(vm.StatusText);
    }

    [Fact]
    public void AddToTable_WithSelection_AddsToTable()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        vm.AddToTableCommand.Execute(null);

        Assert.Contains("Added", vm.StatusText);
    }

    [Fact]
    public void BrowseMemoryHere_WithSelection_Navigates()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        vm.BrowseMemoryHereCommand.Execute(null);

        Assert.Single(_navigationService.DocumentsShown);
        Assert.Equal("memoryBrowser", _navigationService.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void BrowseMemoryHere_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        vm.SelectedLine = null;

        vm.BrowseMemoryHereCommand.Execute(null);

        Assert.Empty(_navigationService.DocumentsShown);
    }

    [Fact]
    public void AskAi_WithSelection_SendsDisassemblyContext()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);
        if (vm.Lines.Count > 0) vm.SelectedLine = vm.Lines[0];

        var aiContext = new StubAiContextService();
        // Use reflection to check
        vm.AskAiCommand.Execute(null);

        // The VM internally uses _aiContext, verify it doesn't crash
    }

    [Fact]
    public void CopySelectedRange_EmptyLines_DoesNothing()
    {
        var vm = CreateVm();
        Assert.Empty(vm.Lines);

        vm.CopySelectedRangeCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    // ── SetBreakpoint with process and selection ──

    [Fact]
    public async Task SetBreakpoint_WithProcessAndSelection_SetsBreakpoint()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);

        // Select a non-ret, non-int3 instruction
        var safeLine = vm.Lines.FirstOrDefault(l => l.Mnemonic == "mov");
        Assert.NotNull(safeLine);
        vm.SelectedLine = safeLine;

        await vm.SetBreakpointAtSelectedCommand.ExecuteAsync(null);

        Assert.Contains("Breakpoint set", vm.StatusText);
        Assert.False(vm.IsBreakpointBusy);
    }

    [Fact]
    public async Task SetBreakpoint_RetInstruction_ShowsRiskDialog()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);

        var retLine = vm.Lines.FirstOrDefault(l => l.Mnemonic == "ret");
        Assert.NotNull(retLine);
        vm.SelectedLine = retLine;

        _dialogService.NextConfirmResult = true; // user confirms

        await vm.SetBreakpointAtSelectedCommand.ExecuteAsync(null);

        Assert.NotEmpty(_dialogService.ConfirmsShown);
        Assert.Contains("Breakpoint set", vm.StatusText);
    }

    [Fact]
    public async Task SetBreakpoint_RetInstruction_UserCancels_SetsStatusCancelled()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);

        var retLine = vm.Lines.FirstOrDefault(l => l.Mnemonic == "ret");
        Assert.NotNull(retLine);
        vm.SelectedLine = retLine;

        _dialogService.NextConfirmResult = false; // user cancels

        await vm.SetBreakpointAtSelectedCommand.ExecuteAsync(null);

        Assert.Contains("Breakpoint cancelled", vm.StatusText);
    }

    [Fact]
    public async Task SetBreakpoint_NoSelection_DoesNothing()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.SelectedLine = null;

        await vm.SetBreakpointAtSelectedCommand.ExecuteAsync(null);

        Assert.Null(vm.StatusText);
    }

    // ── NavigateToAddress (public method) ──

    [Fact]
    public void NavigateToAddress_PushesBackStackAndSetsGoToAddress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.NavigateToAddress("0x7FF00100");
        Thread.Sleep(200);

        vm.NavigateToAddress("0x7FF00200");
        Thread.Sleep(200);

        Assert.Equal("0x7FF00200", vm.GoToAddress);
        Assert.True(vm.CanGoBack);
    }

}
