using System.Collections.Concurrent;
using System.Diagnostics;

namespace CEAISuite.Application;

/// <summary>
/// Monitors target process responsiveness after breakpoint/hook installs.
/// If the target becomes unresponsive, automatically removes the last operation
/// and marks the address+mode as unsafe.
/// </summary>
public sealed class ProcessWatchdogService : IDisposable
{
    // Configuration
    public int HeartbeatIntervalMs { get; set; } = 500;
    public int UnresponsiveThresholdMs { get; set; } = 3000;
    public int MaxRetries { get; set; } = 2;

    // Unsafe address registry: address+mode combos that caused freezes
    private readonly ConcurrentDictionary<string, UnsafeAddressEntry> _unsafeAddresses = new();

    // Active watchdog monitors
    private readonly ConcurrentDictionary<string, WatchdogMonitor> _monitors = new();

    // Event for reporting auto-rollback to UI/AI
    public event Action<WatchdogRollbackEvent>? OnAutoRollback;

    /// <summary>
    /// Start monitoring a process after a BP/hook install.
    /// Returns a disposable guard; dispose it when the operation should stop being monitored
    /// (e.g., user explicitly wants to keep the BP).
    /// </summary>
    public WatchdogGuard StartMonitoring(
        int processId,
        string operationId,
        nuint address,
        string operationType, // "Breakpoint" or "CodeCaveHook"
        string mode,          // "Hardware", "PageGuard", "Stealth", etc.
        Func<Task<bool>> rollbackAction)
    {
        var monitor = new WatchdogMonitor(
            processId, operationId, address, operationType, mode,
            rollbackAction, HeartbeatIntervalMs, UnresponsiveThresholdMs,
            OnRollbackTriggered);

        _monitors[operationId] = monitor;
        monitor.Start();

        return new WatchdogGuard(operationId, this);
    }

    /// <summary>Check if an address+mode is marked unsafe from prior freezes.</summary>
    public bool IsUnsafe(nuint address, string mode) =>
        _unsafeAddresses.ContainsKey(MakeUnsafeKey(address, mode));

    /// <summary>Get all unsafe address entries.</summary>
    public IReadOnlyList<UnsafeAddressEntry> GetUnsafeAddresses() =>
        _unsafeAddresses.Values.ToArray();

    /// <summary>Clear unsafe status for an address (user override).</summary>
    public void ClearUnsafe(nuint address, string mode) =>
        _unsafeAddresses.TryRemove(MakeUnsafeKey(address, mode), out _);

    /// <summary>
    /// Install a breakpoint/hook with transactional safety. If the process becomes
    /// unresponsive after install, automatically rolls back.
    /// </summary>
    public async Task<TransactionResult> InstallWithTransactionAsync(
        int processId,
        string operationId,
        nuint address,
        string operationType,
        string mode,
        Func<Task> installAction,
        Func<Task<bool>> rollbackAction,
        int verifyDelayMs = 1500)
    {
        // Step 1: Verify process is responsive before install
        bool preCheck = IsProcessResponsive(processId);
        if (!preCheck)
            return new TransactionResult(false, "Process was already unresponsive before install.", TransactionPhase.PreCheck);

        try
        {
            // Step 2: Execute the install
            await installAction();

            // Step 3: Wait and verify the process is still responsive
            await Task.Delay(verifyDelayMs);

            bool postCheck = IsProcessResponsive(processId);
            if (!postCheck)
            {
                // Process became unresponsive — rollback
                bool rollbackOk = false;
                try { rollbackOk = await rollbackAction(); }
                catch { /* rollback failed */ }

                // Mark as unsafe
                var key = MakeUnsafeKey(address, mode);
                _unsafeAddresses[key] = new UnsafeAddressEntry(address, mode, operationType, DateTimeOffset.UtcNow, rollbackOk);

                OnAutoRollback?.Invoke(new WatchdogRollbackEvent(operationId, processId, address, operationType, mode, rollbackOk, DateTimeOffset.UtcNow));

                return new TransactionResult(false,
                    $"Process became unresponsive after install. Rollback {(rollbackOk ? "succeeded" : "FAILED")}. Address marked unsafe.",
                    TransactionPhase.Verify);
            }

            // Step 4: Committed — start ongoing monitoring
            StartMonitoring(processId, operationId, address, operationType, mode, rollbackAction);

            return new TransactionResult(true, "Install committed. Watchdog monitoring active.", TransactionPhase.Committed);
        }
        catch (Exception ex)
        {
            // Install itself failed — no rollback needed
            return new TransactionResult(false, $"Install failed: {ex.Message}", TransactionPhase.Install);
        }
    }

    internal void StopMonitoring(string operationId)
    {
        if (_monitors.TryRemove(operationId, out var monitor))
            monitor.Dispose();
    }

    private void OnRollbackTriggered(WatchdogMonitor monitor, bool rollbackSucceeded)
    {
        // Mark address+mode as unsafe
        var key = MakeUnsafeKey(monitor.Address, monitor.Mode);
        _unsafeAddresses[key] = new UnsafeAddressEntry(
            monitor.Address,
            monitor.Mode,
            monitor.OperationType,
            DateTimeOffset.UtcNow,
            rollbackSucceeded);

        // Remove from active monitors
        _monitors.TryRemove(monitor.OperationId, out _);

        // L2: Write freeze telemetry
        WriteFreezeTelementry(monitor, rollbackSucceeded);

        // Fire event
        OnAutoRollback?.Invoke(new WatchdogRollbackEvent(
            monitor.OperationId,
            monitor.ProcessId,
            monitor.Address,
            monitor.OperationType,
            monitor.Mode,
            rollbackSucceeded,
            DateTimeOffset.UtcNow));
    }

    private static void WriteFreezeTelementry(WatchdogMonitor monitor, bool rollbackSucceeded)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CEAISuite", "logs");
            Directory.CreateDirectory(logDir);

            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                operationId = monitor.OperationId,
                processId = monitor.ProcessId,
                address = $"0x{monitor.Address:X}",
                operationType = monitor.OperationType,
                mode = monitor.Mode,
                rollbackSucceeded,
                diagnostics = new
                {
                    description = "Process became unresponsive after breakpoint/hook installation"
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var fileName = $"freeze-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{monitor.OperationId[..8]}.json";
            File.WriteAllText(Path.Combine(logDir, fileName), json);
        }
        catch
        {
            // Telemetry should never throw
        }
    }

    /// <summary>
    /// Check if the target process is responsive.
    /// Shared between transaction pre/post-checks and WatchdogMonitor polling.
    /// </summary>
    internal static bool IsProcessResponsive(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            if (proc.HasExited) return false;
            if (proc.MainWindowHandle == IntPtr.Zero)
            {
                return !proc.HasExited;
            }
            return proc.Responding;
        }
        catch
        {
            return false;
        }
    }

    private static string MakeUnsafeKey(nuint address, string mode) =>
        $"0x{address:X}|{mode}";

    public void Dispose()
    {
        foreach (var monitor in _monitors.Values)
            monitor.Dispose();
        _monitors.Clear();
    }
}

/// <summary>Records an address+mode that previously caused a process freeze.</summary>
public sealed record UnsafeAddressEntry(
    nuint Address,
    string Mode,
    string OperationType,
    DateTimeOffset FreezeDetectedUtc,
    bool RollbackSucceeded);

/// <summary>Raised when the watchdog auto-removes a BP/hook due to unresponsive process.</summary>
public sealed record WatchdogRollbackEvent(
    string OperationId,
    int ProcessId,
    nuint Address,
    string OperationType,
    string Mode,
    bool RollbackSucceeded,
    DateTimeOffset TimestampUtc);

/// <summary>Indicates which phase of a transactional install succeeded or failed.</summary>
public enum TransactionPhase { PreCheck, Install, Verify, Committed }

/// <summary>Result of <see cref="ProcessWatchdogService.InstallWithTransactionAsync"/>.</summary>
public sealed record TransactionResult(
    bool Success,
    string Message,
    TransactionPhase Phase);

/// <summary>Dispose to stop monitoring an operation.</summary>
public sealed class WatchdogGuard(string operationId, ProcessWatchdogService service) : IDisposable
{
    private int _disposed;
    public string OperationId => operationId;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            service.StopMonitoring(operationId);
    }
}

/// <summary>
/// Background monitor that polls process responsiveness using Process.Responding
/// and triggers rollback if the process becomes unresponsive.
/// </summary>
internal sealed class WatchdogMonitor : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private readonly Func<Task<bool>> _rollbackAction;
    private readonly Action<WatchdogMonitor, bool> _onRollback;
    private readonly int _heartbeatMs;
    private readonly int _thresholdMs;

    public int ProcessId { get; }
    public string OperationId { get; }
    public nuint Address { get; }
    public string OperationType { get; }
    public string Mode { get; }

    public WatchdogMonitor(
        int processId, string operationId, nuint address,
        string operationType, string mode,
        Func<Task<bool>> rollbackAction,
        int heartbeatMs, int thresholdMs,
        Action<WatchdogMonitor, bool> onRollback)
    {
        ProcessId = processId;
        OperationId = operationId;
        Address = address;
        OperationType = operationType;
        Mode = mode;
        _rollbackAction = rollbackAction;
        _heartbeatMs = heartbeatMs;
        _thresholdMs = thresholdMs;
        _onRollback = onRollback;
    }

    public void Start()
    {
        _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        // Grace period: wait a bit after install to let the target settle
        await Task.Delay(Math.Max(_heartbeatMs, 500), ct).ConfigureAwait(false);

        int consecutiveUnresponsive = 0;
        int checksNeeded = Math.Max(1, _thresholdMs / _heartbeatMs);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool responsive = IsProcessResponsive(ProcessId);

                if (!responsive)
                {
                    consecutiveUnresponsive++;
                    if (consecutiveUnresponsive >= checksNeeded)
                    {
                        // Process is hung — trigger rollback
                        bool success = false;
                        try
                        {
                            success = await _rollbackAction().ConfigureAwait(false);
                        }
                        catch
                        {
                            // Rollback itself failed — still report
                        }
                        _onRollback(this, success);
                        return; // Stop monitoring
                    }
                }
                else
                {
                    consecutiveUnresponsive = 0;
                }

                await Task.Delay(_heartbeatMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Process might have exited — stop monitoring
                return;
            }
        }
    }

    /// <summary>
    /// Check if the target process is responsive.
    /// Delegates to the shared implementation in ProcessWatchdogService.
    /// </summary>
    private static bool IsProcessResponsive(int processId) =>
        ProcessWatchdogService.IsProcessResponsive(processId);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
