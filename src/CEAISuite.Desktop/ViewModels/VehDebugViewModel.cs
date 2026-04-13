using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

/// <summary>
/// ViewModel for the VEH Debugger panel — agent lifecycle, breakpoint management,
/// real-time hit stream, and stealth mode controls.
/// </summary>
#pragma warning disable CA1001 // CTS lifecycle managed by Start/StopHitStream commands
public partial class VehDebugViewModel : ObservableObject
#pragma warning restore CA1001
{
    private readonly VehDebugService _service;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly IDispatcherService _dispatcher;
    private CancellationTokenSource? _hitStreamCts;
    private Task? _hitStreamTask;

    public VehDebugViewModel(
        VehDebugService service,
        IProcessContext processContext,
        IOutputLog outputLog,
        IDispatcherService dispatcher)
    {
        _service = service;
        _processContext = processContext;
        _outputLog = outputLog;
        _dispatcher = dispatcher;
    }

    // ─── Observable State ───────────────────────────────────────────

    [ObservableProperty]
    private bool _isAgentInjected;

    [ObservableProperty]
    private string _agentStatus = "Not injected";

    [ObservableProperty]
    private bool _isStealthActive;

    [ObservableProperty]
    private ObservableCollection<VehBreakpointDisplayItem> _breakpoints = new();

    [ObservableProperty]
    private VehBreakpointDisplayItem? _selectedBreakpoint;

    [ObservableProperty]
    private ObservableCollection<VehHitDisplayItem> _hitStream = new();

    [ObservableProperty]
    private bool _isHitStreamRunning;

    [ObservableProperty]
    private int _overflowCount;

    [ObservableProperty]
    private string _healthStatus = "";

    [ObservableProperty]
    private string _errorMessage = "";

    // ─── New Breakpoint Fields ──────────────────────────────────────

    [ObservableProperty]
    private string _newBpAddress = "";

    [ObservableProperty]
    private VehBreakpointType _selectedBpType = VehBreakpointType.Execute;

    [ObservableProperty]
    private int _selectedDataSize = 8;

    public VehBreakpointType[] AvailableBpTypes { get; } =
        [VehBreakpointType.Execute, VehBreakpointType.Write, VehBreakpointType.ReadWrite];

    public int[] AvailableDataSizes { get; } = [1, 2, 4, 8];

    private const int MaxHitStreamItems = 500;

    // ─── Agent Lifecycle Commands ───────────────────────────────────

    [RelayCommand]
    private async Task InjectAgentAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            var result = await _service.InjectAsync(pid);
            if (result.Success)
            {
                _outputLog.Append("VEH", "Info", "Agent injected successfully.");
                RefreshStatus();
            }
            else
            {
                _outputLog.Append("VEH", "Error", $"Injection failed: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            _outputLog.Append("VEH", "Error", $"Injection error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task EjectAgentAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        await StopHitStreamAsync();
        try
        {
            await _service.EjectAsync(pid);
            _outputLog.Append("VEH", "Info", "Agent ejected.");
            _dispatcher.Invoke(() =>
            {
                Breakpoints.Clear();
                HitStream.Clear();
            });
            RefreshStatus();
        }
        catch (Exception ex)
        {
            _outputLog.Append("VEH", "Error", $"Ejection error: {ex.Message}");
        }
    }

    // ─── Breakpoint Commands ────────────────────────────────────────

    [RelayCommand]
    private async Task SetBreakpointAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        if (string.IsNullOrWhiteSpace(NewBpAddress)) return;
        ErrorMessage = "";

        nuint addr;
        try
        {
            addr = ParseAddress(NewBpAddress);
        }
        catch (FormatException)
        {
            ErrorMessage = $"Invalid address: '{NewBpAddress}' — use hex (e.g. 0x400000)";
            return;
        }
        catch (OverflowException)
        {
            ErrorMessage = $"Address too large: '{NewBpAddress}'";
            return;
        }

        try
        {
            var result = await _service.SetBreakpointAsync(pid, addr, SelectedBpType, SelectedDataSize);
            if (result.Success)
            {
                _outputLog.Append("VEH", "Info",
                    $"BP set at 0x{addr:X} (DR{result.DrSlot}, {SelectedBpType}, {SelectedDataSize}B)");
                _dispatcher.Invoke(() => Breakpoints.Add(new VehBreakpointDisplayItem
                {
                    DrSlot = result.DrSlot,
                    Address = $"0x{addr:X}",
                    Type = SelectedBpType.ToString(),
                    DataSize = SelectedDataSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    HitCount = 0
                }));
                NewBpAddress = "";
            }
            else
            {
                ErrorMessage = $"Failed: {result.Error}";
                _outputLog.Append("VEH", "Error", $"Set BP failed: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
            _outputLog.Append("VEH", "Error", $"Set BP error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RemoveBreakpointAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        if (SelectedBreakpoint is not { } bp) return;

        try
        {
            var ok = await _service.RemoveBreakpointAsync(pid, bp.DrSlot);
            if (ok)
            {
                _outputLog.Append("VEH", "Info", $"BP removed from DR{bp.DrSlot}.");
                _dispatcher.Invoke(() => Breakpoints.Remove(bp));
            }
        }
        catch (Exception ex)
        {
            _outputLog.Append("VEH", "Error", $"Remove BP error: {ex.Message}");
        }
    }

    // ─── Stealth Commands ───────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleStealthAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            bool ok;
            if (IsStealthActive)
            {
                ok = await _service.DisableStealthAsync(pid);
                if (ok) _outputLog.Append("VEH", "Info", "Stealth disabled.");
            }
            else
            {
                ok = await _service.EnableStealthAsync(pid);
                if (ok) _outputLog.Append("VEH", "Info", "Stealth enabled — DR cloaked, module hidden.");
            }
            RefreshStatus();
        }
        catch (Exception ex)
        {
            _outputLog.Append("VEH", "Error", $"Stealth toggle error: {ex.Message}");
        }
    }

    // ─── Hit Stream ─────────────────────────────────────────────────

    [RelayCommand]
    private void StartHitStream()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        if (_hitStreamCts is not null) return; // already running

        _hitStreamCts = new CancellationTokenSource();
        var ct = _hitStreamCts.Token;
        _dispatcher.Invoke(() => IsHitStreamRunning = true);

        _hitStreamTask = Task.Run(async () =>
        {
            try
            {
                if (_service is { IsAvailable: true })
                {
                    await foreach (var hit in _service.GetHitStreamAsync(pid, ct))
                    {
                        _dispatcher.Invoke(() =>
                        {
                            HitStream.Insert(0, new VehHitDisplayItem
                            {
                                Address = $"0x{(ulong)hit.Address:X}",
                                ThreadId = hit.ThreadId,
                                Type = hit.Type.ToString(),
                                Rax = $"0x{hit.Registers.Rax:X}",
                                Rcx = $"0x{hit.Registers.Rcx:X}",
                                Rdx = $"0x{hit.Registers.Rdx:X}",
                                Rsp = $"0x{hit.Registers.Rsp:X}"
                            });

                            // Cap the display list
                            while (HitStream.Count > MaxHitStreamItems)
                                HitStream.RemoveAt(HitStream.Count - 1);

                            // Update hit count on matching breakpoint by DR6
                            IncrementBreakpointHitCounts(hit);
                        });
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on stop */ }
            catch (Exception ex)
            {
                _outputLog.Append("VEH", "Error", $"Hit stream error: {ex.Message}");
            }
            finally
            {
                _dispatcher.Invoke(() => IsHitStreamRunning = false);
            }
        }, ct);
    }

    [RelayCommand]
    private async Task StopHitStreamAsync()
    {
        var cts = _hitStreamCts;
        var task = _hitStreamTask;
        _hitStreamCts = null;
        _hitStreamTask = null;

        if (cts is not null)
        {
            cts.Cancel();
            // Wait for background task to complete before disposing
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
            }
            cts.Dispose();
        }

        _dispatcher.Invoke(() => IsHitStreamRunning = false);
    }

    [RelayCommand]
    private void ClearHitStream()
    {
        _dispatcher.Invoke(() => HitStream.Clear());
    }

    /// <summary>Increment hit count on the breakpoint display item matching the hit's DR slot.</summary>
    private void IncrementBreakpointHitCounts(VehHitEvent hit)
    {
        // Determine DR slot from DR6 bits 0-3
        var dr6 = (ulong)hit.Dr6;
        for (int i = 0; i < 4; i++)
        {
            if ((dr6 & (1UL << i)) != 0)
            {
                var bp = Breakpoints.FirstOrDefault(b => b.DrSlot == i);
                if (bp is not null)
                    bp.HitCount++;
                break;
            }
        }
    }

    // ─── Status Refresh ─────────────────────────────────────────────

    public void RefreshStatus()
    {
        if (_processContext.AttachedProcessId is not { } pid)
        {
            _dispatcher.Invoke(() =>
            {
                IsAgentInjected = false;
                AgentStatus = "No process attached";
                IsStealthActive = false;
                HealthStatus = "";
            });
            return;
        }

        var status = _service.GetStatus(pid);
        _dispatcher.Invoke(() =>
        {
            IsAgentInjected = status.IsInjected;
            IsStealthActive = status.StealthMode == VehStealthMode.Active;
            OverflowCount = status.OverflowCount;
            HealthStatus = status.AgentHealth.ToString();

            if (!status.IsInjected)
            {
                AgentStatus = "Not injected";
            }
            else
            {
                var stealth = status.StealthMode == VehStealthMode.Active ? " | STEALTH" : "";
                AgentStatus = $"Active ({status.AgentHealth}{stealth}) — {status.ActiveBreakpoints}/4 BPs, {status.TotalHits} hits";
            }
        });
    }

    // ─── Helper: GetHitStreamAsync passthrough ──────────────────────

    private static nuint ParseAddress(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return nuint.Parse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }
}

// ─── Display Items ──────────────────────────────────────────────────

public sealed class VehBreakpointDisplayItem
{
    public int DrSlot { get; init; }
    public string Address { get; init; } = "";
    public string Type { get; init; } = "";
    public string DataSize { get; init; } = "";
    public int HitCount { get; set; }
}

public sealed class VehHitDisplayItem
{
    public string Address { get; init; } = "";
    public int ThreadId { get; init; }
    public string Type { get; init; } = "";
    public string Rax { get; init; } = "";
    public string Rcx { get; init; } = "";
    public string Rdx { get; init; } = "";
    public string Rsp { get; init; } = "";
}
