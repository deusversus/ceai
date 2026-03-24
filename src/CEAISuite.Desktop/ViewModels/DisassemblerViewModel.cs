using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
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
        IDialogService dialogService)
    {
        _disassemblyService = disassemblyService;
        _breakpointService = breakpointService;
        _signatureService = signatureService;
        _addressTableService = addressTableService;
        _processContext = processContext;
        _navigationService = navigationService;
        _outputLog = outputLog;
        _dialogService = dialogService;
    }

    [ObservableProperty] private string _goToAddress = "";
    [ObservableProperty] private ObservableCollection<DisassemblyLineDisplayItem> _lines = new();
    [ObservableProperty] private DisassemblyLineDisplayItem? _selectedLine;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string _searchPattern = "";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private string? _currentFunctionLabel;
    [ObservableProperty] private bool _isDisassembling;

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
        try
        {
            var bp = await _breakpointService.SetBreakpointAsync(pid.Value, SelectedLine.Address);
            StatusText = $"Breakpoint set at {SelectedLine.Address} ({bp.Mode})";
            _outputLog.Append("Disasm", "Info", $"Breakpoint set at {SelectedLine.Address}");
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
            sb.AppendLine($"{line.Address}  {line.HexBytes,-24} {line.Mnemonic,-8} {line.Operands}");
        }
        System.Windows.Clipboard.SetText(sb.ToString());
        StatusText = $"Copied {Lines.Count} lines to clipboard.";
    }

    [RelayCommand]
    private async Task SearchInstructionsAsync()
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
        await Task.CompletedTask;
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
                    IsCallOrJump = isCallJmp
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
}
