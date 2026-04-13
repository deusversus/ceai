using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CEAISuite.Tests;

/// <summary>
/// Launches and controls the CEAISuite.Tests.Harness dummy process for integration tests.
/// </summary>
internal sealed class TestHarnessProcess : IAsyncDisposable
{
    private readonly Process _process;
    private bool _disposed;

    public int ProcessId { get; }

    private TestHarnessProcess(Process process, int pid)
    {
        _process = process;
        ProcessId = pid;
    }

    public static async Task<TestHarnessProcess> StartAsync(CancellationToken ct = default)
    {
        // Find harness exe relative to test assembly
        var testDir = AppContext.BaseDirectory;
        var harnessExe = Path.Combine(testDir, "CEAISuite.Tests.Harness.exe");
        if (!File.Exists(harnessExe))
            throw new FileNotFoundException($"Test harness not found at {harnessExe}");

        var psi = new ProcessStartInfo(harnessExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start harness");

        // Send READY and parse PID response — retry on empty/unexpected lines
        // (CI runners may emit blank lines or .NET runtime output before the harness is ready)
        string? response = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await process.StandardInput.WriteLineAsync("READY");
            await process.StandardInput.FlushAsync(CancellationToken.None);
            response = await ReadLineWithTimeoutAsync(process, TimeSpan.FromSeconds(5), ct);
            if (response is not null && response.StartsWith("PID:", StringComparison.Ordinal))
                break;
            // Drain unexpected line and retry
            await Task.Delay(200, ct);
        }

        if (response is null || !response.StartsWith("PID:", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected READY response after 3 attempts: {response}");

        var pid = int.Parse(response.AsSpan(4), provider: CultureInfo.InvariantCulture);
        return new TestHarnessProcess(process, pid);
    }

    public async Task<string?> SendCommandAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync(CancellationToken.None);
        return await ReadLineWithTimeoutAsync(_process, timeout ?? TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>Send a command that makes the process unresponsive (no response expected).</summary>
    public async Task SendFireAndForgetAsync(string command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync(CancellationToken.None);
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await process.StandardOutput.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ── Typed helper methods for new harness commands ──

    /// <summary>Allocate RWX memory in the harness process, filled with 0xDE. Returns the address.</summary>
    /// <remarks>Retries once on empty response (CI startup race condition).</remarks>
    public async Task<nuint> AllocAsync(int size, CancellationToken ct = default)
    {
        var resp = await SendCommandAsync($"ALLOC {size}", ct: ct);
        if (resp is null || !resp.StartsWith("ALLOC_OK:", StringComparison.Ordinal))
        {
            // Retry once — CI runners may have slow harness startup
            await Task.Delay(200, ct);
            resp = await SendCommandAsync($"ALLOC {size}", ct: ct);
        }
        if (resp is null || !resp.StartsWith("ALLOC_OK:", StringComparison.Ordinal))
            throw new InvalidOperationException($"ALLOC failed: {resp}");
        return nuint.Parse(resp.AsSpan(9), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>List all native OS thread IDs in the harness process.</summary>
    public async Task<IReadOnlyList<int>> GetThreadsAsync(CancellationToken ct = default)
    {
        var resp = await SendCommandAsync("THREADS", ct: ct);
        if (resp is null || !resp.StartsWith("THREADS:", StringComparison.Ordinal))
            throw new InvalidOperationException($"THREADS failed: {resp}");
        return resp[8..].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>Get the main module base address and size.</summary>
    public async Task<(nuint BaseAddress, long Size)> GetModuleInfoAsync(CancellationToken ct = default)
    {
        var resp = await SendCommandAsync("MODULE_INFO", ct: ct);
        if (resp is null || !resp.StartsWith("MODULE_INFO:", StringComparison.Ordinal))
            throw new InvalidOperationException($"MODULE_INFO failed: {resp}");
        var colonIdx = resp.IndexOf(':', 12);
        var baseAddr = nuint.Parse(resp.AsSpan(12, colonIdx - 12), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var size = long.Parse(resp.AsSpan(colonIdx + 1), CultureInfo.InvariantCulture);
        return (baseAddr, size);
    }

    /// <summary>Write arbitrary bytes at an address in the harness's own memory.</summary>
    public async Task WriteAtAsync(nuint address, byte[] bytes, CancellationToken ct = default)
    {
        var hex = Convert.ToHexString(bytes);
        var resp = await SendCommandAsync($"WRITE_AT {address:X} {hex}", ct: ct);
        if (resp is not "WRITE_AT_OK")
            throw new InvalidOperationException($"WRITE_AT failed: {resp}");
    }

    /// <summary>Start a background thread that writes to an address every N ms. Returns native thread ID.</summary>
    public async Task<int> StartWriteLoopAsync(nuint address, int intervalMs, CancellationToken ct = default)
    {
        var resp = await SendCommandAsync($"WRITE_LOOP {address:X} {intervalMs}", ct: ct);
        if (resp is null || !resp.StartsWith("WRITE_LOOP_OK:", StringComparison.Ordinal))
            throw new InvalidOperationException($"WRITE_LOOP failed: {resp}");
        return int.Parse(resp.AsSpan(14), CultureInfo.InvariantCulture);
    }

    /// <summary>Start a background thread that calls code at an address every N ms. Returns native thread ID.</summary>
    public async Task<int> StartExecLoopAsync(nuint address, int intervalMs, CancellationToken ct = default)
    {
        var resp = await SendCommandAsync($"EXEC_LOOP {address:X} {intervalMs}", ct: ct);
        if (resp is null || !resp.StartsWith("EXEC_LOOP_OK:", StringComparison.Ordinal))
            throw new InvalidOperationException($"EXEC_LOOP failed: {resp}");
        return int.Parse(resp.AsSpan(13), CultureInfo.InvariantCulture);
    }

    /// <summary>Call code at an address once on a new thread. Returns native thread ID after execution.</summary>
    public async Task<int> CallFuncAsync(nuint address, CancellationToken ct = default)
    {
        var resp = await SendCommandAsync($"CALL_FUNC {address:X}", TimeSpan.FromSeconds(10), ct);
        if (resp is null || !resp.StartsWith("CALL_FUNC_OK:", StringComparison.Ordinal))
            throw new InvalidOperationException($"CALL_FUNC failed: {resp}");
        return int.Parse(resp.AsSpan(13), CultureInfo.InvariantCulture);
    }

    /// <summary>Stop a previously started loop thread.</summary>
    public async Task StopLoopAsync(int nativeThreadId, CancellationToken ct = default)
    {
        try
        {
            var resp = await SendCommandAsync($"STOP_LOOP {nativeThreadId}", TimeSpan.FromSeconds(5), ct);
            if (resp is not "STOP_LOOP_OK")
                System.Diagnostics.Debug.WriteLine($"STOP_LOOP returned unexpected: {resp}");
        }
        catch (Exception ex)
        {
            // Best-effort cleanup — harness may have already exited or be unresponsive on CI
            System.Diagnostics.Debug.WriteLine($"STOP_LOOP failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>Create a thread with N-deep call stack. Returns (threadId, baseAddress).</summary>
    public async Task<(int ThreadId, nuint BaseAddress)> NestCallsAsync(int depth, CancellationToken ct = default)
    {
        var resp = await SendCommandAsync($"NEST_CALLS {depth}", ct: ct);
        if (resp is null || !resp.StartsWith("NEST_CALLS_OK:", StringComparison.Ordinal))
            throw new InvalidOperationException($"NEST_CALLS failed: {resp}");
        var colonIdx = resp.IndexOf(':', 14);
        var tid = int.Parse(resp.AsSpan(14, colonIdx - 14), CultureInfo.InvariantCulture);
        var addr = nuint.Parse(resp.AsSpan(colonIdx + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (tid, addr);
    }

    /// <summary>Toggle page protection on an address. Returns the old protection value.</summary>
    public async Task<uint> ProtectFlipAsync(nuint address, uint newProtection, CancellationToken ct = default)
    {
        var resp = await SendCommandAsync($"PROTECT_FLIP {address:X} {newProtection:X}", ct: ct);
        if (resp is null || !resp.StartsWith("PROTECT_FLIP_OK:", StringComparison.Ordinal))
            throw new InvalidOperationException($"PROTECT_FLIP failed: {resp}");
        return uint.Parse(resp.AsSpan(16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Start a background thread writing random Int32 values at the address. Returns thread ID.</summary>
    public async Task<int> StartValueChurnAsync(nuint address, int intervalMs, CancellationToken ct = default)
    {
        var resp = await SendCommandAsync($"VALUE_CHURN {address:X} {intervalMs}", ct: ct);
        if (resp is null || !resp.StartsWith("VALUE_CHURN_OK:", StringComparison.Ordinal))
            throw new InvalidOperationException($"VALUE_CHURN failed: {resp}");
        return int.Parse(resp.AsSpan(15), CultureInfo.InvariantCulture);
    }

    /// <summary>Allocate a pointer chain that re-allocates the final block periodically. Returns (baseAddress, threadId).</summary>
    public async Task<(nuint BaseAddress, int ThreadId)> StartPointerChainAsync(int depth, int reallocIntervalMs, CancellationToken ct = default)
    {
        var resp = await SendCommandAsync($"POINTER_CHAIN {depth} {reallocIntervalMs}", ct: ct);
        if (resp is null || !resp.StartsWith("POINTER_CHAIN_OK:", StringComparison.Ordinal))
            throw new InvalidOperationException($"POINTER_CHAIN failed: {resp}");
        var colonIdx = resp.IndexOf(':', 17);
        var addr = nuint.Parse(resp.AsSpan(17, colonIdx - 17), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var tid = int.Parse(resp.AsSpan(colonIdx + 1), CultureInfo.InvariantCulture);
        return (addr, tid);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch { /* Best effort cleanup */ }
        finally
        {
            _process.Dispose();
        }
    }
}
