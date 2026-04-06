using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

/// <summary>
/// Monitors target process responsiveness after breakpoint/hook installs.
/// If the target becomes unresponsive, automatically removes the last operation
/// and marks the address+mode as unsafe.
/// </summary>
public sealed class ProcessWatchdogService : IDisposable
{
    private readonly ILogger<ProcessWatchdogService>? _logger;

    public ProcessWatchdogService(ILogger<ProcessWatchdogService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Optional diagnostic log callback: (source, level, message).</summary>
    public Action<string, string, string>? DiagnosticLog { get; set; }

    private void Log(string level, string message)
    {
        _logger?.LogDebug("[{Level}] {Message}", level, message);
        DiagnosticLog?.Invoke("Watchdog", level, message);
    }

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
            OnRollbackTriggered, _logger);

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

            // Step 3: For PageGuard installs, do early fast checks (storms happen within ms)
            // 6C: Extended to 500ms initial check + secondary check at 750ms
            if (string.Equals(mode, "PageGuard", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(500);
                if (!IsProcessResponsive(processId))
                {
                    // Immediate failure — don't wait the full verifyDelay
                    bool earlyRollbackOk = false;
                    try { earlyRollbackOk = await rollbackAction(); }
                    catch (Exception earlyRollbackEx)
                    {
                        Log("Error", $"Early rollback failed for {operationType} at 0x{address:X}: {earlyRollbackEx.Message}");
                    }

                    var earlyKey = MakeUnsafeKey(address, mode);
                    _unsafeAddresses[earlyKey] = new UnsafeAddressEntry(address, mode, operationType, DateTimeOffset.UtcNow, earlyRollbackOk);

                    OnAutoRollback?.Invoke(new WatchdogRollbackEvent(operationId, processId, address, operationType, mode, earlyRollbackOk, DateTimeOffset.UtcNow));

                    return new TransactionResult(false,
                        $"Process became unresponsive within 500ms of PageGuard install (guard storm likely). Rollback {(earlyRollbackOk ? "succeeded" : "FAILED")}. Address marked unsafe.",
                        TransactionPhase.Verify);
                }

                // 6C: Secondary check at 750ms catches gradual accumulation
                await Task.Delay(250); // 500 + 250 = 750ms total
                if (!IsProcessResponsive(processId))
                {
                    bool secondRollbackOk = false;
                    try { secondRollbackOk = await rollbackAction(); }
                    catch (Exception secondRollbackEx)
                    {
                        Log("Error", $"Secondary rollback failed for {operationType} at 0x{address:X}: {secondRollbackEx.Message}");
                    }

                    var secondKey = MakeUnsafeKey(address, mode);
                    _unsafeAddresses[secondKey] = new UnsafeAddressEntry(address, mode, operationType, DateTimeOffset.UtcNow, secondRollbackOk);

                    OnAutoRollback?.Invoke(new WatchdogRollbackEvent(operationId, processId, address, operationType, mode, secondRollbackOk, DateTimeOffset.UtcNow));

                    return new TransactionResult(false,
                        $"Process became unresponsive within 750ms of PageGuard install (gradual guard storm). Rollback {(secondRollbackOk ? "succeeded" : "FAILED")}. Address marked unsafe.",
                        TransactionPhase.Verify);
                }
            }

            // Step 4: Wait and verify the process is still responsive
            await Task.Delay(verifyDelayMs);

            bool postCheck = IsProcessResponsive(processId);
            if (!postCheck)
            {
                // Process became unresponsive — rollback
                bool rollbackOk = false;
                try { rollbackOk = await rollbackAction(); }
                catch (Exception rollbackEx)
                {
                    Log("Error", $"Rollback failed for {operationType} at 0x{address:X}: {rollbackEx.Message}");
                }

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
    /// Multi-signal health check. At least 2 of 3 signals must pass for "responsive".
    /// Signal 1: SendMessageTimeout(WM_NULL) — window message pump check.
    /// Signal 2: ReadProcessMemory canary — can we still read from the process?
    /// Signal 3: Thread time progress — is any thread actually executing?
    /// For windowless processes, signal 1 is skipped and 2+3 must both pass.
    /// </summary>
    internal static bool IsProcessResponsive(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            if (proc.HasExited) return false;

            int passed = 0;
            int total = 0;

            // Signal 1: Window message pump (skip for headless/no-window processes)
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                total++;
                var result = SendMessageTimeoutW(
                    proc.MainWindowHandle, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero,
                    0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _);
                if (result != IntPtr.Zero)
                    passed++;
            }

            // Signal 2: ReadProcessMemory canary — if we can read any byte, process is alive
            total++;
            var hProcess = OpenProcess(0x0010 /* PROCESS_VM_READ */, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    // Try to read from PEB base (always accessible if process is alive).
                    // We read 1 byte from a low, likely-valid address. If it fails, the
                    // process is likely wedged or dying.
                    var buffer = new byte[1];
                    if (ReadProcessMemory(hProcess, proc.MainModule?.BaseAddress ?? IntPtr.Zero,
                        buffer, 1, out var bytesRead) && bytesRead > 0)
                    {
                        passed++;
                    }
                }
                catch
                {
                    // MainModule may throw for access reasons — count as pass
                    // since OpenProcess succeeded (process exists and is accessible)
                    passed++;
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            // Signal 3: Thread time progress — check if total user+kernel time is advancing
            // 6A: Signal 3 should FAIL if CPU time hasn't advanced — don't give benefit of doubt
            total++;
            try
            {
                var totalTime1 = proc.TotalProcessorTime;
                Thread.Sleep(50); // Brief sample window
                proc.Refresh();
                if (!proc.HasExited)
                {
                    var totalTime2 = proc.TotalProcessorTime;
                    // If CPU time advanced, the process is executing (not fully wedged)
                    if (totalTime2 > totalTime1)
                        passed++;
                    // 6A: Zero CPU progress = signal FAILS (no automatic pass for idle)
                }
            }
            catch
            {
                // Can't read times — skip this signal
                total--;
            }

            // Responsive if at least 2/3 signals pass (or 2/2 for headless)
            return total > 0 && passed >= Math.Min(2, total);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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
    private readonly ILogger? _logger;

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
        Action<WatchdogMonitor, bool> onRollback,
        ILogger? logger = null)
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
        _logger = logger;
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
                        catch (Exception rollbackEx)
                        {
                            // 6B: Log rollback exception instead of swallowing
                            _logger?.LogWarning(rollbackEx, "Monitor rollback failed for {OperationType} at 0x{Address:X}", OperationType, Address);
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
