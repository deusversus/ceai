using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Host-side Mono introspection engine. Injects mono_agent.dll into the target process
/// and communicates via named pipe IPC to enumerate domains, classes, fields, methods,
/// and invoke managed code in Unity/Mono processes.
///
/// The named pipe protocol is JSON line-delimited: host sends a command object,
/// agent responds with a result object. Each line is \n-terminated UTF-8 JSON.
///
/// Command format: { "cmd": "enum_domains", ... }
/// Response format: { "ok": true, "data": [...] } or { "ok": false, "error": "..." }
/// </summary>
public sealed class WindowsMonoEngine : IMonoEngine, IDisposable
{
    private readonly ILogger<WindowsMonoEngine> _logger;
    private readonly ConcurrentDictionary<int, MonoProcessState> _states = new();
    private bool _disposed;

    public WindowsMonoEngine(ILogger<WindowsMonoEngine>? logger = null)
    {
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<WindowsMonoEngine>();
    }

    // ── Process State ──

    private sealed class MonoProcessState : IDisposable
    {
        public int ProcessId { get; init; }
        public IntPtr ProcessHandle { get; init; }
        public string PipeName { get; init; } = "";
        public string? MonoVersion { get; set; }
        public int LastKnownDomainCount { get; set; }
        public NamedPipeClientStream? Pipe { get; set; }
        public StreamReader? PipeReader { get; set; }
        public StreamWriter? PipeWriter { get; set; }
        public readonly SemaphoreSlim CommandLock = new(1, 1);

        public void Dispose()
        {
            PipeWriter?.Dispose();
            PipeReader?.Dispose();
            Pipe?.Dispose();
            CommandLock.Dispose();
            if (ProcessHandle != IntPtr.Zero)
                CloseHandle(ProcessHandle);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    // ── Injection ──

    public async Task<MonoInjectResult> InjectAsync(int processId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // M5: Use TryAdd with a sentinel to prevent concurrent injection for the same PID
        var sentinel = new MonoProcessState { ProcessId = processId };
        if (!_states.TryAdd(processId, sentinel))
            return new MonoInjectResult(false, Error: $"Mono agent already injected into process {processId}");

        try
        {
            return await Task.Run(() => InjectCore(processId, ct), ct).ConfigureAwait(false);
        }
        catch
        {
            // Remove sentinel on failure so retry is possible
            _states.TryRemove(processId, out _);
            throw;
        }
    }

    public async Task<bool> EjectAsync(int processId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryRemove(processId, out var state))
            return false;

        try
        {
            // Send shutdown command
            await SendCommandAsync(state, new { cmd = "shutdown" }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "Failed to send shutdown to Mono agent (pid={ProcessId})", processId);
        }

        state.Dispose();
        return true;
    }

    public MonoStatus GetStatus(int processId)
    {
        if (!_states.TryGetValue(processId, out var state))
            return new MonoStatus(false, null, 0, MonoAgentHealth.Unknown);

        var health = state.Pipe?.IsConnected == true ? MonoAgentHealth.Healthy : MonoAgentHealth.Unresponsive;
        return new MonoStatus(true, state.MonoVersion, state.LastKnownDomainCount, health);
    }

    // ── Domain & Assembly Enumeration ──

    public async Task<IReadOnlyList<MonoDomain>> EnumDomainsAsync(int processId, CancellationToken ct = default)
    {
        var state = GetState(processId);
        using var response = await SendCommandAsync(state, new { cmd = "enum_domains" }, ct).ConfigureAwait(false);
        var domains = DeserializeList<MonoDomain>(response, "domains");
        state.LastKnownDomainCount = domains.Count; // L6: cache for GetStatus
        return domains;
    }

    public async Task<IReadOnlyList<MonoAssembly>> EnumAssembliesAsync(int processId, nuint domainHandle, CancellationToken ct = default)
    {
        var state = GetState(processId);
        using var response = await SendCommandAsync(state, new { cmd = "enum_assemblies", domain = (ulong)domainHandle }, ct).ConfigureAwait(false);
        return DeserializeList<MonoAssembly>(response, "assemblies");
    }

    // ── Class & Type Introspection ──

    public async Task<MonoClass?> FindClassAsync(int processId, nuint imageHandle, string namespaceName, string className, CancellationToken ct = default)
    {
        var state = GetState(processId);
        using var response = await SendCommandAsync(state, new { cmd = "find_class", image = (ulong)imageHandle, ns = namespaceName, name = className }, ct).ConfigureAwait(false);
        return response.RootElement.TryGetProperty("class", out var classProp) && classProp.ValueKind != JsonValueKind.Null
            ? classProp.Deserialize<MonoClass>(JsonOptions)
            : null;
    }

    public async Task<IReadOnlyList<MonoField>> EnumFieldsAsync(int processId, nuint classHandle, CancellationToken ct = default)
    {
        var state = GetState(processId);
        using var response = await SendCommandAsync(state, new { cmd = "enum_fields", @class = (ulong)classHandle }, ct).ConfigureAwait(false);
        return DeserializeList<MonoField>(response, "fields");
    }

    public async Task<IReadOnlyList<MonoMethod>> EnumMethodsAsync(int processId, nuint classHandle, CancellationToken ct = default)
    {
        var state = GetState(processId);
        using var response = await SendCommandAsync(state, new { cmd = "enum_methods", @class = (ulong)classHandle }, ct).ConfigureAwait(false);
        return DeserializeList<MonoMethod>(response, "methods");
    }

    // ── Field Access ──

    public async Task<byte[]?> GetStaticFieldValueAsync(int processId, nuint classHandle, nuint fieldHandle, int size, CancellationToken ct = default)
    {
        var state = GetState(processId);
        using var response = await SendCommandAsync(state, new { cmd = "get_static_field", @class = (ulong)classHandle, field = (ulong)fieldHandle, size }, ct).ConfigureAwait(false);
        if (response.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.String)
            return Convert.FromBase64String(dataProp.GetString()!);
        return null;
    }

    public async Task<bool> SetStaticFieldValueAsync(int processId, nuint classHandle, nuint fieldHandle, byte[] value, CancellationToken ct = default)
    {
        var state = GetState(processId);
        using var response = await SendCommandAsync(state, new { cmd = "set_static_field", @class = (ulong)classHandle, field = (ulong)fieldHandle, data = Convert.ToBase64String(value) }, ct).ConfigureAwait(false);
        return response.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
    }

    // ── Method Invocation ──

    public async Task<MonoInvokeResult> InvokeMethodAsync(int processId, nuint methodHandle, nuint instanceHandle,
        nuint[]? args = null, CancellationToken ct = default)
    {
        var state = GetState(processId);
        using var response = await SendCommandAsync(state, new { cmd = "invoke_method", method = (ulong)methodHandle, instance = (ulong)instanceHandle, args = args?.Select(a => (ulong)a).ToArray() }, ct).ConfigureAwait(false);

        if (response.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            var retVal = response.RootElement.TryGetProperty("return_value", out var rv) ? (nuint)rv.GetUInt64() : (nuint)0;
            return new MonoInvokeResult(true, retVal);
        }

        var error = response.RootElement.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
        return new MonoInvokeResult(false, Error: error);
    }

    // ── Named Pipe IPC ──

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private MonoProcessState GetState(int processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_states.TryGetValue(processId, out var state))
            throw new InvalidOperationException($"Mono agent not injected into process {processId}. Call InjectAsync first.");
        return state;
    }

    private static async Task<JsonDocument> SendCommandAsync(MonoProcessState state, object command, CancellationToken ct)
    {
        await state.CommandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.PipeWriter is null || state.PipeReader is null)
                throw new InvalidOperationException("Named pipe not connected to Mono agent.");

            var json = JsonSerializer.Serialize(command, JsonOptions);
            await state.PipeWriter.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await state.PipeWriter.FlushAsync(ct).ConfigureAwait(false);

            var responseLine = await state.PipeReader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Mono agent disconnected (pipe closed).");

            return JsonDocument.Parse(responseLine);
        }
        finally
        {
            state.CommandLock.Release();
        }
    }

    private static List<T> DeserializeList<T>(JsonDocument doc, string propertyName)
    {
        if (doc.RootElement.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
            return prop.Deserialize<List<T>>(JsonOptions) ?? [];
        return [];
    }

    // ── Injection Core ──

    private MonoInjectResult InjectCore(int processId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Open the target process
        var processHandle = OpenProcess(ProcessAllAccess, false, processId);
        if (processHandle == IntPtr.Zero)
            return new MonoInjectResult(false, Error: $"Cannot open process {processId} (error {Marshal.GetLastWin32Error()})");

        try
        {
            // 2. Locate the agent DLL
            var agentPath = FindAgentDll();
            if (agentPath is null)
                return new MonoInjectResult(false, Error: "mono_agent.dll not found. Build it with native/mono_agent/build.cmd");

            // 3. Inject the agent DLL
            if (!LoadLibraryInTarget(processHandle, agentPath))
            {
                return new MonoInjectResult(false, Error: "Failed to inject mono_agent.dll via CreateRemoteThread+LoadLibraryW");
            }

            // 4. Connect to the agent's named pipe
            var pipeName = $"CEAISuite_Mono_{processId}";
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                pipe.Connect(timeout: 5000);
            }
            catch (Exception pipeEx)
            {
                pipe.Dispose();
                var reason = pipeEx is TimeoutException
                    ? "did not appear within 5 seconds. The target process may not have Mono loaded."
                    : $"connection failed: {pipeEx.Message}";
                return new MonoInjectResult(false, Error: $"Mono agent injected but named pipe '{pipeName}' {reason}");
            }

            var reader = new StreamReader(pipe, Encoding.UTF8);
            var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = false };

            var state = new MonoProcessState
            {
                ProcessId = processId,
                ProcessHandle = processHandle,
                PipeName = pipeName,
                Pipe = pipe,
                PipeReader = reader,
                PipeWriter = writer
            };

            // 5. Handshake: read the agent's hello line
            try
            {
                var helloLine = reader.ReadLine();
                if (helloLine is not null)
                {
                    using var hello = JsonDocument.Parse(helloLine);
                    if (hello.RootElement.TryGetProperty("mono_version", out var ver))
                        state.MonoVersion = ver.GetString();
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "Failed to read Mono agent handshake");
            }

            _states[processId] = state;

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Mono agent injected into PID {ProcessId} (Mono {Version})",
                    processId, state.MonoVersion ?? "unknown");

            return new MonoInjectResult(true, state.MonoVersion);
        }
        catch (Exception ex)
        {
            CloseHandle(processHandle);
            return new MonoInjectResult(false, Error: ex.Message);
        }
    }

    private static string? FindAgentDll()
    {
        // Look relative to the running assembly first (bin/), then native/mono_agent/
        var assemblyDir = Path.GetDirectoryName(typeof(WindowsMonoEngine).Assembly.Location) ?? ".";
        var candidates = new[]
        {
            Path.Combine(assemblyDir, "mono_agent.dll"),
            Path.Combine(assemblyDir, "..", "..", "..", "..", "native", "mono_agent", "mono_agent.dll"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // ── DLL Injection (same pattern as VEH debugger) ──

    private static bool LoadLibraryInTarget(IntPtr processHandle, string dllPath)
    {
        var pathBytes = Encoding.Unicode.GetBytes(dllPath + '\0');
        var pathAlloc = VirtualAllocEx(processHandle, IntPtr.Zero, (IntPtr)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
        if (pathAlloc == IntPtr.Zero) return false;

        try
        {
            if (!WriteProcessMemory(processHandle, pathAlloc, pathBytes, pathBytes.Length, out _))
                return false;

            var kernel32 = GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero) return false;

            var loadLibAddr = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibAddr == IntPtr.Zero) return false;

            var threadHandle = CreateRemoteThread(processHandle, IntPtr.Zero, 0, loadLibAddr, pathAlloc, 0, out _);
            if (threadHandle == IntPtr.Zero) return false;

            var waitResult = WaitForSingleObject(threadHandle, 5000);
            CloseHandle(threadHandle);
            return waitResult == 0;
        }
        finally
        {
            VirtualFreeEx(processHandle, pathAlloc, IntPtr.Zero, MemRelease);
        }
    }

    // ── IDisposable ──

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _states)
        {
            try { kvp.Value.Dispose(); } catch { /* best-effort */ }
        }
        _states.Clear();
    }

    // ── P/Invoke ──

    private const uint ProcessAllAccess = 0x001FFFFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize,
        IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
