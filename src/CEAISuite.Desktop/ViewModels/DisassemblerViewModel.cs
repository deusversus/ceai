using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class DisassemblerViewModel : ObservableObject
{
    private readonly DisassemblyService _disassemblyService;
    private readonly BreakpointService _breakpointService;
    private readonly SignatureGeneratorService _signatureService;
    private readonly AddressTableService _addressTableService;
    private readonly IProcessContext _processContext;
    private readonly INavigationService _navigationService;
    private readonly IOutputLog _outputLog;
    private readonly IDialogService _dialogService;
    private readonly IClipboardService _clipboard;
    private readonly IAutoAssemblerEngine? _autoAssemblerEngine;

    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly Dictionary<string, string> _comments = new();
    private readonly Dictionary<string, string> _labels = new();

    /// <summary>Raised when a "Find What Writes" search produces results for the Find Results panel.</summary>
    public event Action<IReadOnlyList<FindResultDisplayItem>, string>? PopulateFindResults;

    public DisassemblerViewModel(
        DisassemblyService disassemblyService,
        BreakpointService breakpointService,
        SignatureGeneratorService signatureService,
        AddressTableService addressTableService,
        IProcessContext processContext,
        INavigationService navigationService,
        IOutputLog outputLog,
        IDialogService dialogService,
        IClipboardService clipboard,
        IAiContextService aiContext,
        IAutoAssemblerEngine? autoAssemblerEngine = null)
    {
        _disassemblyService = disassemblyService;
        _breakpointService = breakpointService;
        _signatureService = signatureService;
        _addressTableService = addressTableService;
        _processContext = processContext;
        _navigationService = navigationService;
        _outputLog = outputLog;
        _dialogService = dialogService;
        _clipboard = clipboard;
        _autoAssemblerEngine = autoAssemblerEngine;
        _aiContext = aiContext;
    }

    private readonly IAiContextService _aiContext;

    [ObservableProperty] private string _goToAddress = "";
    [ObservableProperty] private ObservableCollection<DisassemblyLineDisplayItem> _lines = new();
    [ObservableProperty] private DisassemblyLineDisplayItem? _selectedLine;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string _searchPattern = "";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private string? _currentFunctionLabel;
    [ObservableProperty] private bool _isDisassembling;
    [ObservableProperty] private bool _isBreakpointBusy;

    [RelayCommand]
    private async Task GoToAddressAsync()
    {
        if (string.IsNullOrWhiteSpace(GoToAddress)) return;
        if (!string.IsNullOrWhiteSpace(_currentAddress))
        {
            _backStack.Push(_currentAddress);
            _forwardStack.Clear();
        }
        await DisassembleAtAsync(GoToAddress);
        UpdateNavButtons();
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (_backStack.Count == 0) return;
        if (!string.IsNullOrWhiteSpace(_currentAddress))
            _forwardStack.Push(_currentAddress);
        var addr = _backStack.Pop();
        await DisassembleAtAsync(addr);
        UpdateNavButtons();
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (_forwardStack.Count == 0) return;
        if (!string.IsNullOrWhiteSpace(_currentAddress))
            _backStack.Push(_currentAddress);
        var addr = _forwardStack.Pop();
        await DisassembleAtAsync(addr);
        UpdateNavButtons();
    }

    [RelayCommand]
    private async Task FollowJumpCallAsync()
    {
        if (SelectedLine is not { IsCallOrJump: true } line) return;
        // Extract target address from operands (e.g., "0x7FF12340" or "sub_7FF12340")
        var match = Regex.Match(line.Operands, @"0x[0-9A-Fa-f]+");
        if (!match.Success) return;
        if (!string.IsNullOrWhiteSpace(_currentAddress))
        {
            _backStack.Push(_currentAddress);
            _forwardStack.Clear();
        }
        await DisassembleAtAsync(match.Value);
        UpdateNavButtons();
    }

    [RelayCommand]
    private async Task SetBreakpointAtSelectedAsync()
    {
        if (SelectedLine is null) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }

        // Gap 6: Risk assessment — warn if setting BP in a hot code area
        var isHotCode = SelectedLine.Mnemonic.StartsWith("ret", StringComparison.OrdinalIgnoreCase)
            || SelectedLine.Mnemonic.Equals("int3", StringComparison.OrdinalIgnoreCase)
            || (SelectedLine.XrefLabel is not null && SelectedLine.XrefLabel.Contains("ntdll", StringComparison.OrdinalIgnoreCase));
        if (isHotCode)
        {
            var proceed = _dialogService.Confirm(
                $"Setting a breakpoint at {SelectedLine.Address} ({SelectedLine.Mnemonic}) may be risky.\n" +
                "This instruction is in a sensitive area (system code or return instruction).\nProceed?",
                "Risk Assessment");
            if (!proceed) { StatusText = "Breakpoint cancelled."; return; }
        }

        IsBreakpointBusy = true;
        try
        {
            var bp = await _breakpointService.SetBreakpointAsync(pid.Value, SelectedLine.Address);
            StatusText = $"Breakpoint set at {SelectedLine.Address} ({bp.Mode})";
            _outputLog.Append("Disasm", "Info", $"Breakpoint set at {SelectedLine.Address}");
        }
        catch (Exception ex) { StatusText = $"Failed: {ex.Message}"; }
        finally { IsBreakpointBusy = false; }
    }

    [RelayCommand]
    private async Task SetVehBreakpointAtSelectedAsync()
    {
        if (SelectedLine is null) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }

        IsBreakpointBusy = true;
        try
        {
            var bp = await _breakpointService.SetBreakpointAsync(
                pid.Value, SelectedLine.Address,
                BreakpointType.HardwareExecute,
                BreakpointMode.VectoredExceptionHandler);
            StatusText = $"VEH breakpoint set at {SelectedLine.Address} ({bp.Mode})";
            _outputLog.Append("Disasm", "Info", $"VEH breakpoint set at {SelectedLine.Address}");
        }
        catch (Exception ex) { StatusText = $"VEH BP failed: {ex.Message}"; }
        finally { IsBreakpointBusy = false; }
    }

    [RelayCommand]
    private async Task FindWhatWritesAsync()
    {
        if (SelectedLine is null) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        IsBreakpointBusy = true;
        try
        {
            // Set a write-breakpoint at the selected address to capture writers
            var bp = await _breakpointService.SetBreakpointAsync(
                pid.Value, SelectedLine.Address,
                CEAISuite.Engine.Abstractions.BreakpointType.HardwareWrite,
                CEAISuite.Engine.Abstractions.BreakpointHitAction.LogAndContinue);

            StatusText = $"Write breakpoint set at {SelectedLine.Address} — trigger writes in target, then check Hit Log.";
            _outputLog.Append("Disasm", "Info", $"Find What Writes: BP {bp.Id} at {SelectedLine.Address}");

            var items = new List<FindResultDisplayItem>
            {
                new() { Address = SelectedLine.Address, Instruction = $"{SelectedLine.Mnemonic} {SelectedLine.Operands}",
                         Module = SelectedLine.ModuleOffset ?? "", Context = $"Write BP {bp.Id} armed" }
            };
            PopulateFindResults?.Invoke(items, $"Find What Writes to {SelectedLine.Address}");
        }
        catch (Exception ex) { StatusText = $"Failed: {ex.Message}"; }
        finally { IsBreakpointBusy = false; }
    }

    [RelayCommand]
    private async Task GenerateSignatureAsync()
    {
        if (SelectedLine is null) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        try
        {
            if (!TryParseHex(SelectedLine.Address, out var addr))
            { StatusText = "Invalid address."; return; }

            var sig = await _signatureService.GenerateAsync(pid.Value, (nuint)addr);
            var uniqueness = await _signatureService.TestUniquenessAsync(
                pid.Value,
                SelectedLine.ModuleOffset?.Split('+')[0] ?? "",
                sig.Pattern);

            var resultText = $"AOB: {sig.Pattern}\nLength: {sig.Length} bytes\nMatches: {uniqueness}";
            _clipboard.SetText(sig.Pattern);
            StatusText = $"Signature: {sig.Pattern} ({uniqueness} match{(uniqueness == 1 ? "" : "es")}) — copied";
            _outputLog.Append("Disasm", "Info", resultText);
        }
        catch (Exception ex) { StatusText = $"Failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void CopySelectedRange()
    {
        if (Lines.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var line in Lines)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{line.Address}  {line.HexBytes,-24} {line.Mnemonic,-8} {line.Operands}");
        }
        _clipboard.SetText(sb.ToString());
        StatusText = $"Copied {Lines.Count} lines to clipboard.";
    }

    [RelayCommand]
    private async Task EditInstructionAsync()
    {
        if (SelectedLine is null) return;
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }
        if (_autoAssemblerEngine is null) { StatusText = "Assembler not available."; return; }

        var currentInstr = $"{SelectedLine.Mnemonic} {SelectedLine.Operands}";
        var newInstr = _dialogService.ShowInput(
            $"Edit instruction at {SelectedLine.Address}:",
            "Inline Assembly",
            currentInstr);
        if (string.IsNullOrWhiteSpace(newInstr) || newInstr == currentInstr) return;

        // Build a minimal AA script to assemble the new instruction at the address
        var script = $"[ENABLE]\n{SelectedLine.Address}:\n{newInstr}\n[DISABLE]\n";
        try
        {
            var result = await _autoAssemblerEngine.EnableAsync(pid.Value, script);
            if (result.Success)
            {
                StatusText = $"Patched {SelectedLine.Address}: {newInstr}";
                _outputLog.Append("Disasm", "Info", $"Inline edit at {SelectedLine.Address}: {currentInstr} → {newInstr}");
                // Refresh the view
                if (_currentAddress is not null)
                    await DisassembleAtAsync(_currentAddress);
            }
            else
            {
                StatusText = $"Assembly failed: {result.Error}";
            }
        }
        catch (Exception ex) { StatusText = $"Failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void SearchInstructions()
    {
        if (string.IsNullOrWhiteSpace(SearchPattern)) return;
        try
        {
            var regex = new Regex(SearchPattern, RegexOptions.IgnoreCase);
            var matches = Lines.Where(l =>
                regex.IsMatch(l.Mnemonic) || regex.IsMatch(l.Operands) || regex.IsMatch(l.HexBytes))
                .ToList();

            if (matches.Count > 0)
            {
                SelectedLine = matches[0];
                StatusText = $"{matches.Count} match(es) for '{SearchPattern}'";
            }
            else
            {
                StatusText = $"No matches for '{SearchPattern}'";
            }
        }
        catch (ArgumentException ex)
        {
            StatusText = $"Invalid regex: {ex.Message}";
        }
    }

    /// <summary>Navigate to a specific address (called externally by NavigationService).</summary>
    public void NavigateToAddress(string address)
    {
        if (!string.IsNullOrWhiteSpace(_currentAddress))
        {
            _backStack.Push(_currentAddress);
            _forwardStack.Clear();
        }
        GoToAddress = address;
        _ = DisassembleAtAsync(address);
        UpdateNavButtons();
    }

    private string? _currentAddress;

    private async Task DisassembleAtAsync(string address)
    {
        var pid = _processContext.AttachedProcessId;
        if (pid is null) { StatusText = "No process attached."; return; }

        IsDisassembling = true;
        StatusText = $"Disassembling at {address}...";
        try
        {
            var result = await _disassemblyService.DisassembleAtAsync(pid.Value, address, 50);
            _currentAddress = address;
            GoToAddress = address;
            CurrentFunctionLabel = result.Summary;

            // Build module lookup for symbol resolution
            var modules = _processContext.CurrentInspection?.Modules;

            Lines.Clear();
            foreach (var instr in result.Lines)
            {
                var isCallJmp = instr.Mnemonic.StartsWith("call", StringComparison.OrdinalIgnoreCase)
                    || instr.Mnemonic.StartsWith("jmp", StringComparison.OrdinalIgnoreCase)
                    || instr.Mnemonic.StartsWith("j", StringComparison.OrdinalIgnoreCase); // jz, jnz, je, etc.

                var item = new DisassemblyLineDisplayItem
                {
                    Address = instr.Address,
                    HexBytes = instr.HexBytes,
                    Mnemonic = instr.Mnemonic,
                    Operands = instr.Operands,
                    IsCallOrJump = isCallJmp,
                    SymbolName = instr.SymbolName,
                    ModuleOffset = instr.SymbolName ?? ResolveModuleOffset(instr.Address, modules),
                    XrefLabel = isCallJmp ? ResolveXrefTarget(instr.Operands, modules) : null
                };

                // Restore user comments/labels
                if (_comments.TryGetValue(instr.Address, out var comment))
                    item.Comment = comment;
                if (_labels.TryGetValue(instr.Address, out var label))
                    item.Label = label;

                Lines.Add(item);
            }
            StatusText = $"{Lines.Count} instructions at {result.StartAddress}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _outputLog.Append("Disasm", "Error", ex.Message);
        }
        finally { IsDisassembling = false; }
    }

    private void UpdateNavButtons()
    {
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
    }

    /// <summary>Resolve an instruction address to "module+0xOffset" form.</summary>
    private static string? ResolveModuleOffset(string addressHex, IReadOnlyList<ModuleOverview>? modules)
    {
        if (modules is null || modules.Count == 0) return null;
        if (!TryParseHex(addressHex, out var addr)) return null;

        foreach (var mod in modules)
        {
            if (!TryParseHex(mod.BaseAddress, out var modBase)) continue;
            if (!ulong.TryParse(mod.Size.Replace(",", "", StringComparison.Ordinal), out var modSize)) continue;
            if (addr >= modBase && addr < modBase + modSize)
                return $"{mod.Name}+0x{addr - modBase:X}";
        }
        return null;
    }

    /// <summary>Resolve a call/jump target operand to "module+0xOffset".</summary>
    private static string? ResolveXrefTarget(string operands, IReadOnlyList<ModuleOverview>? modules)
    {
        var match = Regex.Match(operands, @"0x[0-9A-Fa-f]+");
        if (!match.Success) return null;
        return ResolveModuleOffset(match.Value, modules);
    }

    // ── Cross-panel context menu commands ──

    [RelayCommand]
    private void AddToTable()
    {
        if (SelectedLine is null) return;
        _addressTableService.AddEntry(SelectedLine.Address, Engine.Abstractions.MemoryDataType.ByteArray, "",
            $"{SelectedLine.Mnemonic} {SelectedLine.Operands}");
        StatusText = $"Added {SelectedLine.Address} to address table.";
    }

    [RelayCommand]
    private void BrowseMemoryHere()
    {
        if (SelectedLine is null) return;
        _navigationService.ShowDocument("memoryBrowser", SelectedLine.Address);
    }

    [RelayCommand]
    private void AskAi()
    {
        if (SelectedLine is null) return;
        _aiContext.SendContext("Disassembler",
            $"Instruction at {SelectedLine.Address}: {SelectedLine.Mnemonic} {SelectedLine.Operands} (Bytes: {SelectedLine.HexBytes})");
    }

    private static bool TryParseHex(string text, out ulong value)
    {
        value = 0;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
