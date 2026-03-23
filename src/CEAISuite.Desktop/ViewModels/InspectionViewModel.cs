using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class InspectionViewModel : ObservableObject
{
    private readonly WorkspaceDashboardService _dashboardService;
    private readonly IProcessContext _processContext;
    private readonly DisassemblyService _disassemblyService;
    private readonly BreakpointService _breakpointService;
    private readonly AddressTableService _addressTableService;
    private readonly IDialogService _dialogService;
    private readonly IOutputLog _outputLog;

    public InspectionViewModel(
        WorkspaceDashboardService dashboardService,
        IProcessContext processContext,
        DisassemblyService disassemblyService,
        BreakpointService breakpointService,
        AddressTableService addressTableService,
        IDialogService dialogService,
        IOutputLog outputLog)
    {
        _dashboardService = dashboardService;
        _processContext = processContext;
        _disassemblyService = disassemblyService;
        _breakpointService = breakpointService;
        _addressTableService = addressTableService;
        _dialogService = dialogService;
        _outputLog = outputLog;

        _processContext.ProcessChanged += () =>
        {
            CurrentInspection = _processContext.CurrentInspection;
        };
    }

    [ObservableProperty]
    private string _address = "0x0";

    [ObservableProperty]
    private string _value = "0";

    [ObservableProperty]
    private MemoryDataType _selectedDataType = MemoryDataType.Int32;

    [ObservableProperty]
    private string _disassemblyAddress = "0x0";

    [ObservableProperty]
    private string _bpAddress = "0x0";

    [ObservableProperty]
    private BreakpointType _selectedBpType = BreakpointType.Software;

    [ObservableProperty]
    private ProcessInspectionOverview? _currentInspection;

    [ObservableProperty]
    private DisassemblyOverview? _disassembly;

    [ObservableProperty]
    private string? _breakpointStatus;

    [RelayCommand]
    private async Task ReadAddressAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid || CurrentInspection is null)
        {
            _outputLog.Append("Inspection", "Warn", "Inspect a process first.");
            return;
        }

        try
        {
            var probe = await _dashboardService.ReadAddressAsync(pid, Address, SelectedDataType);

            CurrentInspection = CurrentInspection with
            {
                ManualProbe = probe,
                LastWriteMessage = null,
                StatusMessage = $"Read {probe.DataType} from {probe.Address}."
            };
            _outputLog.Append("Inspection", "Info", $"Read {probe.DataType} from {probe.Address}.");
        }
        catch (Exception ex)
        {
            _outputLog.Append("Inspection", "Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task WriteAddressAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid || CurrentInspection is null)
        {
            _outputLog.Append("Inspection", "Warn", "Inspect a process first.");
            return;
        }

        if (!_dialogService.Confirm("Confirm memory write",
            $"Write {Value} as {SelectedDataType} to {Address} in process {CurrentInspection.ProcessName}?"))
        {
            return;
        }

        try
        {
            var message = await _dashboardService.WriteAddressAsync(pid, Address, SelectedDataType, Value);

            CurrentInspection = CurrentInspection with
            {
                LastWriteMessage = message,
                StatusMessage = message
            };
            _outputLog.Append("Inspection", "Info", message);
        }
        catch (Exception ex)
        {
            _outputLog.Append("Inspection", "Error", ex.Message);
        }
    }

    [RelayCommand]
    private void AddManualToTable()
    {
        if (CurrentInspection?.ManualProbe is not { } probe)
        {
            _outputLog.Append("Inspection", "Warn", "Read an address first.");
            return;
        }

        _addressTableService.AddEntry(probe.Address, SelectedDataType, probe.DisplayValue);
        _outputLog.Append("Inspection", "Info", $"Added {probe.Address} to address table.");
    }

    [RelayCommand]
    private async Task DisassembleAtAddressAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid || CurrentInspection is null)
        {
            _outputLog.Append("Inspection", "Warn", "Inspect a process before disassembling.");
            return;
        }

        try
        {
            var overview = await _disassemblyService.DisassembleAtAsync(pid, DisassemblyAddress);
            Disassembly = overview;
            _outputLog.Append("Inspection", "Info", overview.Summary);
        }
        catch (Exception ex)
        {
            _outputLog.Append("Inspection", "Error", $"Disassembly failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetBreakpointAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid || CurrentInspection is null) return;

        try
        {
            var bp = await _breakpointService.SetBreakpointAsync(pid, BpAddress, SelectedBpType);
            BreakpointStatus = $"Breakpoint {bp.Id} set at {bp.Address}";
            _outputLog.Append("Inspection", "Info", $"Breakpoint {bp.Id} set at {bp.Address}");
        }
        catch (Exception ex)
        {
            _outputLog.Append("Inspection", "Error", $"Breakpoint error: {ex.Message}");
        }
    }

    /// <summary>Update the inspection from MainWindow (e.g. after InspectSelectedProcess).</summary>
    public void SetInspection(ProcessInspectionOverview? inspection)
    {
        CurrentInspection = inspection;
    }
}
