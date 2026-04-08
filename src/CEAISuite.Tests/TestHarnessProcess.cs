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

        // Send READY and parse PID response
        await process.StandardInput.WriteLineAsync("READY");
        await process.StandardInput.FlushAsync(CancellationToken.None);
        var response = await ReadLineWithTimeoutAsync(process, TimeSpan.FromSeconds(5), ct);
        if (response is null || !response.StartsWith("PID:", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected READY response: {response}");

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
