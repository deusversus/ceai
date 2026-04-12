using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Host-side VEH debugger engine. Injects veh_agent.dll into the target process
/// and communicates via shared memory + named events to set/remove hardware breakpoints
/// (DR0-DR3) without DebugActiveProcess, bypassing common anti-debug checks.
/// </summary>
public sealed class WindowsVehDebugger : IVehDebugger, IDisposable
{
    private readonly ILogger<WindowsVehDebugger> _logger;
    private readonly ConcurrentDictionary<int, VehProcessState> _states = new();
    private bool _disposed;

    public WindowsVehDebugger(ILogger<WindowsVehDebugger>? logger = null)
    {
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<WindowsVehDebugger>();
    }

    // ─── Shared Memory Constants (must match veh_agent.c exactly) ───

    private const uint ShmMagic = 0xCEAE;
    private const uint ShmVersion = 1;
    private const int ShmHeaderSize = 0x40;
    private const int HitEntrySize = 128;
    private const int MaxHits = 256;
    private const int ShmTotalSize = ShmHeaderSize + MaxHits * HitEntrySize;

    // Header field offsets
    private const int OffsetMagic = 0x00;
    private const int OffsetVersion = 0x04;
    private const int OffsetCommandSlot = 0x08;
    private const int OffsetCommandResult = 0x0C;
    private const int OffsetCommandArg0 = 0x10;
    private const int OffsetCommandArg1 = 0x18;
    private const int OffsetCommandArg2 = 0x1C;
    private const int OffsetHitWriteIndex = 0x20;
    private const int OffsetHitReadIndex = 0x24;
    private const int OffsetHitCount = 0x28;
    private const int OffsetAgentStatus = 0x2C;
    private const int OffsetMaxHits = 0x30;

    // Hit entry field offsets (relative to entry start)
    private const int HitOffsetAddress = 0x00;
    private const int HitOffsetThreadId = 0x08;
    private const int HitOffsetType = 0x0C;
    private const int HitOffsetDr6 = 0x10;
    private const int HitOffsetRax = 0x18;
    private const int HitOffsetRbx = 0x20;
    private const int HitOffsetRcx = 0x28;
    private const int HitOffsetRdx = 0x30;
    private const int HitOffsetRsi = 0x38;
    private const int HitOffsetRdi = 0x40;
    private const int HitOffsetRsp = 0x48;
    private const int HitOffsetRbp = 0x50;
    private const int HitOffsetR8 = 0x58;
    private const int HitOffsetR9 = 0x60;
    private const int HitOffsetR10 = 0x68;
    private const int HitOffsetR11 = 0x70;
    private const int HitOffsetTimestamp = 0x78;

    // Commands (host -> agent)
    private const int CmdIdle = 0;
    private const int CmdSetBp = 1;
    private const int CmdRemoveBp = 2;
    private const int CmdShutdown = 3;

    // Agent status
    private const int StatusLoading = 0;
    private const int StatusReady = 1;
    private const int StatusError = 2;
    private const int StatusShutdown = 3;

    // Win32 constants
    private const uint ProcessAllAccess = 0x001FFFFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0x000F001F;
    private const uint WaitTimeout = 258;
    private const int MaxDrSlots = 4;

    // ─── Internal State ─────────────────────────────────────────────

    private sealed class VehProcessState : IDisposable
    {
        public IntPtr ProcessHandle;
        public IntPtr SharedMemoryHandle;   // CreateFileMapping handle
        public IntPtr SharedMemoryPtr;      // MapViewOfFile pointer
        public IntPtr CommandEvent;         // named auto-reset event
        public IntPtr HitEvent;             // named auto-reset event
        public int ProcessId;
        public readonly int[] ActiveDrSlots = new int[MaxDrSlots]; // 0=free, 1=in-use
        public int TotalHits;
        public string? TempAgentPath;       // temp copy of veh_agent.dll

        public void Dispose()
        {
            if (SharedMemoryPtr != IntPtr.Zero)
            {
                UnmapViewOfFile(SharedMemoryPtr);
                SharedMemoryPtr = IntPtr.Zero;
            }

            if (SharedMemoryHandle != IntPtr.Zero)
            {
                CloseHandle(SharedMemoryHandle);
                SharedMemoryHandle = IntPtr.Zero;
            }

            if (CommandEvent != IntPtr.Zero)
            {
                CloseHandle(CommandEvent);
                CommandEvent = IntPtr.Zero;
            }

            if (HitEvent != IntPtr.Zero)
            {
                CloseHandle(HitEvent);
                HitEvent = IntPtr.Zero;
            }

            if (ProcessHandle != IntPtr.Zero)
            {
                CloseHandle(ProcessHandle);
                ProcessHandle = IntPtr.Zero;
            }
        }
    }

    // ─── IVehDebugger ───────────────────────────────────────────────

    public async Task<VehInjectResult> InjectAsync(int processId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_states.ContainsKey(processId))
            return new VehInjectResult(false, $"VEH agent already injected into process {processId}");

        return await Task.Run(() => InjectCore(processId, ct), ct).ConfigureAwait(false);
    }

    public async Task<bool> EjectAsync(int processId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryGetValue(processId, out var state))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("EjectAsync: no VEH state for process {ProcessId}", processId);
            return false;
        }

        return await Task.Run(() => EjectCore(state), ct).ConfigureAwait(false);
    }

    public async Task<VehBreakpointResult> SetBreakpointAsync(
        int processId, nuint address, VehBreakpointType type, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryGetValue(processId, out var state))
            return new VehBreakpointResult(false, Error: "VEH agent not injected");

        return await Task.Run(() => SetBreakpointCore(state, address, type), ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveBreakpointAsync(int processId, int drSlot, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryGetValue(processId, out var state))
            return false;

        return await Task.Run(() => RemoveBreakpointCore(state, drSlot), ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<VehHitEvent> GetHitStreamAsync(
        int processId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_states.TryGetValue(processId, out var state))
            yield break;

        while (!ct.IsCancellationRequested)
        {
            // Read hit indices from shared memory
            var writeIdx = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetHitWriteIndex);
            var readIdx = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetHitReadIndex);

            while (readIdx < writeIdx)
            {
                var slot = readIdx % MaxHits;
                var entryPtr = state.SharedMemoryPtr + ShmHeaderSize + slot * HitEntrySize;
                var hit = ParseHitEntry(entryPtr);
                if (hit is not null)
                {
                    state.TotalHits++;
                    yield return hit;
                }

                readIdx++;
                Marshal.WriteInt32(state.SharedMemoryPtr + OffsetHitReadIndex, readIdx);
            }

            // Wait for hit event or timeout (50ms polling interval)
            _ = WaitForSingleObject(state.HitEvent, 50);
            await Task.Yield(); // allow cancellation check
        }
    }

    public VehStatus GetStatus(int processId)
    {
        if (!_states.TryGetValue(processId, out var state))
            return new VehStatus(false, 0, 0);

        var activeCount = 0;
        for (int i = 0; i < MaxDrSlots; i++)
        {
            if (state.ActiveDrSlots[i] != 0)
                activeCount++;
        }

        return new VehStatus(true, activeCount, state.TotalHits);
    }

    // ─── Core Implementation ────────────────────────────────────────

    private VehInjectResult InjectCore(int processId, CancellationToken ct)
    {
        // 1. Find the agent DLL
        var agentPath = FindAgentDll();
        if (agentPath is null)
            return new VehInjectResult(false, "veh_agent.dll not found. Build the native agent first.");

        // 2. Open target process
        var hProcess = OpenProcess(ProcessAllAccess, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError("Failed to open process {ProcessId} (Win32 error {Error})", processId, err);
            return new VehInjectResult(false, $"Failed to open process {processId} (Win32 error {err})");
        }

        var state = new VehProcessState { ProcessHandle = hProcess, ProcessId = processId };

        try
        {
            // 3. Create named shared memory
            var shmName = $"Local\\CEAISuite_VEH_{processId}";
            state.SharedMemoryHandle = CreateFileMappingA(
                new IntPtr(-1), // INVALID_HANDLE_VALUE
                IntPtr.Zero,
                PageReadWrite,
                0,
                (uint)ShmTotalSize,
                shmName);

            if (state.SharedMemoryHandle == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("CreateFileMapping failed (Win32 error {Error})", err);
                state.Dispose();
                return new VehInjectResult(false, $"Failed to create shared memory (Win32 error {err})");
            }

            // 4. Map view
            state.SharedMemoryPtr = MapViewOfFile(
                state.SharedMemoryHandle, FileMapAllAccess, 0, 0, (UIntPtr)ShmTotalSize);

            if (state.SharedMemoryPtr == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("MapViewOfFile failed (Win32 error {Error})", err);
                state.Dispose();
                return new VehInjectResult(false, $"Failed to map shared memory (Win32 error {err})");
            }

            // 5. Initialize header
            unsafe
            {
                NativeMemory.Clear(state.SharedMemoryPtr.ToPointer(), (nuint)ShmTotalSize);
            }

            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetMagic, unchecked((int)ShmMagic));
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetVersion, (int)ShmVersion);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetAgentStatus, StatusLoading);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetMaxHits, MaxHits);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandSlot, CmdIdle);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandResult, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetHitWriteIndex, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetHitReadIndex, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetHitCount, 0);

            // 6. Create named events
            var cmdEventName = $"Local\\CEAISuite_VEH_Cmd_{processId}";
            var hitEventName = $"Local\\CEAISuite_VEH_Hit_{processId}";

            state.CommandEvent = CreateEventA(IntPtr.Zero, false, false, cmdEventName);
            if (state.CommandEvent == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("CreateEvent (cmd) failed (Win32 error {Error})", err);
                state.Dispose();
                return new VehInjectResult(false, $"Failed to create command event (Win32 error {err})");
            }

            state.HitEvent = CreateEventA(IntPtr.Zero, false, false, hitEventName);
            if (state.HitEvent == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("CreateEvent (hit) failed (Win32 error {Error})", err);
                state.Dispose();
                return new VehInjectResult(false, $"Failed to create hit event (Win32 error {err})");
            }

            // 7. Copy agent DLL to temp so we don't lock the original
            var tempDir = Path.Combine(Path.GetTempPath(), "CEAISuite_VEH");
            Directory.CreateDirectory(tempDir);
            var tempAgentPath = Path.Combine(tempDir, $"veh_agent_{processId}.dll");
            File.Copy(agentPath, tempAgentPath, overwrite: true);
            state.TempAgentPath = tempAgentPath;

            // 8. Inject via LoadLibraryW + CreateRemoteThread
            if (!LoadLibraryInTarget(hProcess, tempAgentPath))
            {
                var err = Marshal.GetLastWin32Error();
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("DLL injection failed for process {ProcessId} (Win32 error {Error})", processId, err);
                state.Dispose();
                return new VehInjectResult(false, $"DLL injection failed (Win32 error {err})");
            }

            // 9. Wait for agent to signal STATUS_READY (poll, 5s timeout)
            const int timeoutMs = 5000;
            const int pollIntervalMs = 25;
            int elapsed = 0;

            while (elapsed < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                var status = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetAgentStatus);
                if (status == StatusReady)
                    break;
                if (status == StatusError)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.LogError("VEH agent reported STATUS_ERROR during init");
                    state.Dispose();
                    return new VehInjectResult(false, "VEH agent reported error during initialization");
                }

                Thread.Sleep(pollIntervalMs);
                elapsed += pollIntervalMs;
            }

            if (elapsed >= timeoutMs)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("VEH agent did not become ready within {Timeout}ms", timeoutMs);
                state.Dispose();
                return new VehInjectResult(false, $"VEH agent did not become ready within {timeoutMs}ms");
            }

            // 10. Store state
            if (!_states.TryAdd(processId, state))
            {
                // Race condition — another thread injected first
                state.Dispose();
                return new VehInjectResult(false, "VEH agent was injected by another thread");
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("VEH agent injected into process {ProcessId}", processId);

            return new VehInjectResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Unexpected error during VEH injection into process {ProcessId}", processId);
            state.Dispose();
            return new VehInjectResult(false, $"Unexpected error: {ex.Message}");
        }
    }

    private bool EjectCore(VehProcessState state)
    {
        try
        {
            // 1. Send CMD_SHUTDOWN
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandSlot, CmdShutdown);
            SetEvent(state.CommandEvent);

            // 2. Wait for agent to acknowledge shutdown (3s timeout)
            const int timeoutMs = 3000;
            const int pollIntervalMs = 25;
            int elapsed = 0;

            while (elapsed < timeoutMs)
            {
                var status = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetAgentStatus);
                if (status == StatusShutdown)
                    break;

                Thread.Sleep(pollIntervalMs);
                elapsed += pollIntervalMs;
            }

            if (elapsed >= timeoutMs && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("VEH agent did not acknowledge shutdown within {Timeout}ms for process {ProcessId}",
                    timeoutMs, state.ProcessId);

            // 3. Clean up
            var tempPath = state.TempAgentPath;
            _states.TryRemove(state.ProcessId, out _);
            state.Dispose();

            // 4. Delete temp agent DLL (best effort)
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch (IOException)
                {
                    // DLL may still be loaded — will be cleaned up on next inject or app exit
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Could not delete temp agent DLL: {Path}", tempPath);
                }
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("VEH agent ejected from process {ProcessId}", state.ProcessId);

            return true;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error during VEH eject from process {ProcessId}", state.ProcessId);
            return false;
        }
    }

    private VehBreakpointResult SetBreakpointCore(VehProcessState state, nuint address, VehBreakpointType type)
    {
        // 1. Find first free DR slot
        int drSlot = -1;
        for (int i = 0; i < MaxDrSlots; i++)
        {
            if (Interlocked.CompareExchange(ref state.ActiveDrSlots[i], 1, 0) == 0)
            {
                drSlot = i;
                break;
            }
        }

        if (drSlot == -1)
            return new VehBreakpointResult(false, Error: "All 4 hardware breakpoint slots (DR0-DR3) are in use");

        try
        {
            // 2. Write command arguments to shared memory
            Marshal.WriteInt64(state.SharedMemoryPtr + OffsetCommandArg0, unchecked((long)(ulong)address));
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandArg1, (int)type);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandArg2, drSlot);

            // 3. Write command (must be last — agent polls this)
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandResult, int.MinValue); // sentinel
            Thread.MemoryBarrier();
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandSlot, CmdSetBp);

            // 4. Signal command event
            SetEvent(state.CommandEvent);

            // 5. Wait for result (poll, 3s timeout)
            const int timeoutMs = 3000;
            const int pollIntervalMs = 10;
            int elapsed = 0;

            while (elapsed < timeoutMs)
            {
                var result = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetCommandResult);
                if (result != int.MinValue)
                {
                    if (result == 0)
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation(
                                "VEH breakpoint set: DR{DrSlot} at 0x{Address:X} type={Type} (pid={ProcessId})",
                                drSlot, (ulong)address, type, state.ProcessId);
                        return new VehBreakpointResult(true, drSlot);
                    }

                    // Agent reported failure
                    Interlocked.Exchange(ref state.ActiveDrSlots[drSlot], 0);
                    return new VehBreakpointResult(false, Error: $"Agent failed to set breakpoint (result={result})");
                }

                Thread.Sleep(pollIntervalMs);
                elapsed += pollIntervalMs;
            }

            // Timeout — release slot
            Interlocked.Exchange(ref state.ActiveDrSlots[drSlot], 0);
            return new VehBreakpointResult(false, Error: "Timeout waiting for agent to set breakpoint");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref state.ActiveDrSlots[drSlot], 0);
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error setting VEH breakpoint at 0x{Address:X}", (ulong)address);
            return new VehBreakpointResult(false, Error: $"Unexpected error: {ex.Message}");
        }
    }

    private bool RemoveBreakpointCore(VehProcessState state, int drSlot)
    {
        if (drSlot is < 0 or >= MaxDrSlots)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Invalid DR slot {DrSlot} for removal", drSlot);
            return false;
        }

        if (state.ActiveDrSlots[drSlot] == 0)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("DR slot {DrSlot} is not active", drSlot);
            return false;
        }

        try
        {
            // Write command arguments
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandArg2, drSlot);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandResult, int.MinValue);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandSlot, CmdRemoveBp);
            SetEvent(state.CommandEvent);

            // Wait for result
            const int timeoutMs = 3000;
            const int pollIntervalMs = 10;
            int elapsed = 0;

            while (elapsed < timeoutMs)
            {
                var result = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetCommandResult);
                if (result != int.MinValue)
                {
                    Interlocked.Exchange(ref state.ActiveDrSlots[drSlot], 0);

                    if (result == 0)
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation(
                                "VEH breakpoint removed: DR{DrSlot} (pid={ProcessId})", drSlot, state.ProcessId);
                        return true;
                    }

                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Agent failed to remove breakpoint DR{DrSlot} (result={Result})", drSlot, result);
                    return false;
                }

                Thread.Sleep(pollIntervalMs);
                elapsed += pollIntervalMs;
            }

            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Timeout waiting for agent to remove breakpoint DR{DrSlot}", drSlot);
            return false;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error removing VEH breakpoint DR{DrSlot}", drSlot);
            return false;
        }
    }

    // ─── Hit Entry Parsing ──────────────────────────────────────────

    private static VehHitEvent? ParseHitEntry(IntPtr entryPtr)
    {
        try
        {
            var address = (nuint)(ulong)Marshal.ReadInt64(entryPtr + HitOffsetAddress);
            var threadId = Marshal.ReadInt32(entryPtr + HitOffsetThreadId);
            var hitType = Marshal.ReadInt32(entryPtr + HitOffsetType);
            var dr6 = (nuint)(ulong)Marshal.ReadInt64(entryPtr + HitOffsetDr6);
            var timestamp = Marshal.ReadInt64(entryPtr + HitOffsetTimestamp);

            var regs = new RegisterSnapshot(
                Rax: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetRax),
                Rbx: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetRbx),
                Rcx: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetRcx),
                Rdx: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetRdx),
                Rsi: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetRsi),
                Rdi: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetRdi),
                Rsp: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetRsp),
                Rbp: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetRbp),
                R8: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetR8),
                R9: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetR9),
                R10: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetR10),
                R11: (ulong)Marshal.ReadInt64(entryPtr + HitOffsetR11));

            var bpType = hitType switch
            {
                1 => VehBreakpointType.Write,
                2 => VehBreakpointType.ReadWrite,
                _ => VehBreakpointType.Execute,
            };

            return new VehHitEvent(address, threadId, bpType, dr6, regs, timestamp);
        }
        catch
        {
            // Corrupted entry — skip
            return null;
        }
    }

    // ─── Agent DLL Location ─────────────────────────────────────────

    private static string? FindAgentDll()
    {
        // Check next to our assembly first
        var assemblyDir = Path.GetDirectoryName(typeof(WindowsVehDebugger).Assembly.Location);
        if (assemblyDir is not null)
        {
            var path = Path.Combine(assemblyDir, "veh_agent.dll");
            if (File.Exists(path)) return path;
        }

        // Check native/veh_agent/ relative to working directory
        var devPath = Path.Combine("native", "veh_agent", "veh_agent.dll");
        if (File.Exists(devPath)) return Path.GetFullPath(devPath);

        return null;
    }

    // ─── DLL Injection ──────────────────────────────────────────────

    /// <summary>Load a DLL into the target process using CreateRemoteThread + LoadLibraryW.</summary>
    private static bool LoadLibraryInTarget(IntPtr processHandle, string dllPath)
    {
        var pathBytes = Encoding.Unicode.GetBytes(dllPath + '\0');
        var pathAlloc = VirtualAllocEx(
            processHandle, IntPtr.Zero, (IntPtr)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);

        if (pathAlloc == IntPtr.Zero)
            return false;

        try
        {
            if (!WriteProcessMemory(processHandle, pathAlloc, pathBytes, pathBytes.Length, out _))
                return false;

            var kernel32 = GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero) return false;

            var loadLibAddr = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibAddr == IntPtr.Zero) return false;

            var threadHandle = CreateRemoteThread(
                processHandle, IntPtr.Zero, 0, loadLibAddr, pathAlloc, 0, out _);

            if (threadHandle == IntPtr.Zero) return false;

            _ = WaitForSingleObject(threadHandle, 5000);
            _ = CloseHandle(threadHandle);
            return true;
        }
        finally
        {
            VirtualFreeEx(processHandle, pathAlloc, IntPtr.Zero, MemRelease);
        }
    }

    // ─── IDisposable ────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _states)
        {
            try
            {
                // Best-effort shutdown: send CMD_SHUTDOWN before disposing handles
                var state = kvp.Value;
                if (state.SharedMemoryPtr != IntPtr.Zero && state.CommandEvent != IntPtr.Zero)
                {
                    Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandSlot, CmdShutdown);
                    SetEvent(state.CommandEvent);
                    Thread.Sleep(100); // brief wait for agent to acknowledge
                }

                var tempPath = state.TempAgentPath;
                state.Dispose();

                if (tempPath is not null)
                {
                    try { File.Delete(tempPath); }
                    catch (IOException) { /* best effort */ }
                }
            }
            catch
            {
                // Suppress exceptions during disposal
            }
        }

        _states.Clear();
    }

    // ─── P/Invoke ───────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr processHandle, IntPtr address, IntPtr size,
        uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        IntPtr processHandle, IntPtr baseAddress, IntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out int bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize,
        IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr CreateFileMappingA(
        IntPtr hFile, IntPtr lpAttributes, uint flProtect,
        uint dwMaximumSizeHigh, uint dwMaximumSizeLow,
        string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr CreateEventA(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);
}
