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
    private readonly ILuaScriptEngine? _luaEngine;
    private readonly ConcurrentDictionary<int, VehProcessState> _states = new();
    private bool _disposed;

    public WindowsVehDebugger(ILogger<WindowsVehDebugger>? logger = null, ILuaScriptEngine? luaEngine = null)
    {
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<WindowsVehDebugger>();
        _luaEngine = luaEngine;
    }

    // ─── Shared Memory Constants (must match veh_agent.c exactly) ───

    private const uint ShmMagic = 0xCEAE;
    private const uint ShmVersion = 2;
    private const int ShmHeaderSize = 0x40;
    private const int HitEntrySize = 128;
    private const int DefaultMaxHits = 4096;

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
    private const int OffsetOverflowCount = 0x34;
    private const int OffsetCommandArg3 = 0x38;
    private const int OffsetHeartbeat = 0x3C;

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
    private const int CmdRefreshThreads = 4;
    private const int CmdEnableStealth = 5;
    private const int CmdDisableStealth = 6;

    // Agent status
    private const int StatusLoading = 0;
    private const int StatusReady = 1;
    private const int StatusError = 2;
    private const int StatusShutdown = 3;

    // Win32 constants
    private const uint ProcessAllAccess = 0x001FFFFF;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0x000F001F;
    private const uint WaitTimeout = 258;
    private const int MaxDrSlots = 4;

    // Health monitoring
    private const int HeartbeatTimeoutMs = 2000;

    // ─── Internal State ─────────────────────────────────────────────

    private sealed class VehProcessState : IDisposable
    {
        public IntPtr ProcessHandle;
        public IntPtr SharedMemoryHandle;   // CreateFileMapping handle
        public IntPtr SharedMemoryPtr;      // MapViewOfFile pointer
        public IntPtr CommandEvent;         // named auto-reset event
        public IntPtr HitEvent;             // named auto-reset event
        public readonly SemaphoreSlim CommandLock = new(1, 1); // serialize command protocol
        public int ProcessId;
        public int MaxHits = DefaultMaxHits;
        public bool IsWow64Target;
        public readonly int[] ActiveDrSlots = new int[MaxDrSlots]; // 0=free, 1=in-use
        public readonly BreakpointCondition?[] Conditions = new BreakpointCondition?[MaxDrSlots];
        public readonly string?[] LuaCallbacks = new string?[MaxDrSlots];
        public readonly int[] SlotHitCounts = new int[MaxDrSlots]; // per-slot hit count for conditions
        public int TotalHits;
        public int LastOverflowCount;       // track overflow count for delta detection
        public bool StealthActive;          // stealth mode state
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

            CommandLock.Dispose();

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
        int processId, nuint address, VehBreakpointType type,
        int dataSize = 8, BreakpointCondition? condition = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryGetValue(processId, out var state))
            return new VehBreakpointResult(false, Error: "VEH agent not injected");

        // Validate data size
        if (dataSize is not (1 or 2 or 4 or 8))
            return new VehBreakpointResult(false, Error: $"Invalid data size {dataSize}. Must be 1, 2, 4, or 8.");

        // Execute type always uses 1-byte length
        if (type == VehBreakpointType.Execute)
            dataSize = 1;

        // WOW64 address validation
        if (state.IsWow64Target && (ulong)address > 0xFFFFFFFF)
            return new VehBreakpointResult(false, Error: "Address exceeds 32-bit range for WOW64 target process.");

        var result = await Task.Run(() => SetBreakpointCore(state, address, type, dataSize), ct).ConfigureAwait(false);

        // Store condition for host-side evaluation
        if (result.Success && condition is not null)
            state.Conditions[result.DrSlot] = condition;

        return result;
    }

    public async Task<bool> RemoveBreakpointAsync(int processId, int drSlot, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryGetValue(processId, out var state))
            return false;

        return await Task.Run(() => RemoveBreakpointCore(state, drSlot), ct).ConfigureAwait(false);
    }

    public void RegisterLuaCallback(int processId, int drSlot, string luaFunctionName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_states.TryGetValue(processId, out var state)) return;
        if (drSlot is < 0 or >= MaxDrSlots) return;
        state.LuaCallbacks[drSlot] = luaFunctionName;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Lua callback '{Callback}' registered for DR{DrSlot} (pid={ProcessId})",
                luaFunctionName, drSlot, processId);
    }

    public void UnregisterLuaCallback(int processId, int drSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_states.TryGetValue(processId, out var state)) return;
        if (drSlot is < 0 or >= MaxDrSlots) return;
        state.LuaCallbacks[drSlot] = null;
    }

    public async Task<bool> RefreshThreadsAsync(int processId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryGetValue(processId, out var state))
            return false;

        return await Task.Run(() => SendSimpleCommand(state, CmdRefreshThreads), ct).ConfigureAwait(false);
    }

    public async Task<bool> EnableStealthAsync(int processId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryGetValue(processId, out var state))
            return false;

        var ok = await Task.Run(() => SendSimpleCommand(state, CmdEnableStealth), ct).ConfigureAwait(false);
        if (ok)
        {
            state.StealthActive = true;
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("VEH stealth enabled for process {ProcessId}", processId);
        }
        return ok;
    }

    public async Task<bool> DisableStealthAsync(int processId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_states.TryGetValue(processId, out var state))
            return false;

        var ok = await Task.Run(() => SendSimpleCommand(state, CmdDisableStealth), ct).ConfigureAwait(false);
        if (ok)
        {
            state.StealthActive = false;
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("VEH stealth disabled for process {ProcessId}", processId);
        }
        return ok;
    }

    public async IAsyncEnumerable<VehHitEvent> GetHitStreamAsync(
        int processId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_states.TryGetValue(processId, out var state))
            yield break;

        while (!ct.IsCancellationRequested)
        {
            // Check for overflow
            var overflowCount = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetOverflowCount);
            if (overflowCount > state.LastOverflowCount)
            {
                var dropped = overflowCount - state.LastOverflowCount;
                state.LastOverflowCount = overflowCount;
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("VEH ring buffer overflow: {Dropped} hits dropped (pid={ProcessId})",
                        dropped, processId);
            }

            // Read hit indices from shared memory
            var writeIdx = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetHitWriteIndex);
            var readIdx = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetHitReadIndex);

            while (readIdx < writeIdx)
            {
                var slot = readIdx % state.MaxHits;
                var entryPtr = state.SharedMemoryPtr + ShmHeaderSize + slot * HitEntrySize;
                var hit = ParseHitEntry(entryPtr);
                if (hit is not null)
                {
                    state.TotalHits++;

                    // Determine which DR slot triggered this hit (from DR6 bits 0-3)
                    var drSlot = DetermineTriggeredSlot((ulong)hit.Dr6);

                    // Evaluate condition if one is set for this slot
                    var shouldYield = true;
                    if (drSlot >= 0 && drSlot < MaxDrSlots)
                    {
                        state.SlotHitCounts[drSlot]++;

                        var condition = state.Conditions[drSlot];
                        if (condition is not null)
                        {
                            shouldYield = VehConditionEvaluator.Evaluate(
                                condition, hit, state.SlotHitCounts[drSlot],
                                (addr, size) => ReadTargetMemory(state.ProcessHandle, addr, size));
                        }

                        // Fire Lua callback if registered (fires regardless of condition)
                        var luaCallback = state.LuaCallbacks[drSlot];
                        if (luaCallback is not null && _luaEngine is not null)
                        {
                            try
                            {
                                var regDict = RegisterSnapshotToDict(hit.Registers);
                                var bpHit = new BreakpointHitEvent(
                                    $"veh-dr{drSlot}",
                                    hit.Address,
                                    hit.ThreadId,
                                    DateTimeOffset.UtcNow,
                                    regDict);
                                // Fire-and-forget with fault observation to prevent unobserved task exceptions
#pragma warning disable CS4014
                                _luaEngine.InvokeBreakpointCallbackAsync(luaCallback, bpHit, ct)
                                    .ContinueWith(t =>
                                    {
                                        if (_logger.IsEnabled(LogLevel.Warning))
                                            _logger.LogWarning(t.Exception?.InnerException,
                                                "Lua callback '{Callback}' async fault for DR{DrSlot}",
                                                luaCallback, drSlot);
                                    }, TaskContinuationOptions.OnlyOnFaulted);
#pragma warning restore CS4014
                            }
                            catch (Exception ex)
                            {
                                if (_logger.IsEnabled(LogLevel.Warning))
                                    _logger.LogWarning(ex, "Lua callback '{Callback}' failed for DR{DrSlot}",
                                        luaCallback, drSlot);
                            }
                        }
                    }

                    if (shouldYield)
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

        var overflowCount = 0;
        var agentHealth = VehAgentHealth.Unknown;

        if (state.SharedMemoryPtr != IntPtr.Zero)
        {
            overflowCount = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetOverflowCount);

            // Check heartbeat for health
            var heartbeatTick = (uint)Marshal.ReadInt32(state.SharedMemoryPtr + OffsetHeartbeat);
            if (heartbeatTick == 0)
            {
                agentHealth = VehAgentHealth.Unknown; // agent hasn't written heartbeat yet
            }
            else
            {
                var currentTick = (uint)Environment.TickCount;
                var elapsed = currentTick - heartbeatTick;
                agentHealth = elapsed <= HeartbeatTimeoutMs
                    ? VehAgentHealth.Healthy
                    : VehAgentHealth.Unresponsive;
            }
        }

        var stealthMode = state.StealthActive ? VehStealthMode.Active : VehStealthMode.None;
        return new VehStatus(true, activeCount, state.TotalHits, overflowCount, agentHealth, stealthMode);
    }

    // ─── Core Implementation ────────────────────────────────────────

    private VehInjectResult InjectCore(int processId, CancellationToken ct)
    {
        // 1. Detect target bitness to select correct agent DLL
        var isWow64 = IsTargetWow64(processId);

        // 2. Find the agent DLL
        var agentPath = FindAgentDll(isWow64);
        if (agentPath is null)
        {
            var dllName = isWow64 ? "veh_agent_x86.dll" : "veh_agent.dll";
            return new VehInjectResult(false, $"{dllName} not found. Build the native agent first.");
        }

        // 3. Open target process
        var hProcess = OpenProcess(ProcessAllAccess, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError("Failed to open process {ProcessId} (Win32 error {Error})", processId, err);
            return new VehInjectResult(false, $"Failed to open process {processId} (Win32 error {err})");
        }

        var maxHits = DefaultMaxHits;
        var shmTotalSize = ShmHeaderSize + maxHits * HitEntrySize;
        var state = new VehProcessState
        {
            ProcessHandle = hProcess,
            ProcessId = processId,
            MaxHits = maxHits,
            IsWow64Target = isWow64
        };

        try
        {
            // 4. Create named shared memory
            var shmName = $"Local\\CEAISuite_VEH_{processId}";
            state.SharedMemoryHandle = CreateFileMappingA(
                new IntPtr(-1), // INVALID_HANDLE_VALUE
                IntPtr.Zero,
                PageReadWrite,
                0,
                (uint)shmTotalSize,
                shmName);

            if (state.SharedMemoryHandle == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("CreateFileMapping failed (Win32 error {Error})", err);
                state.Dispose();
                return new VehInjectResult(false, $"Failed to create shared memory (Win32 error {err})");
            }

            // 5. Map view
            state.SharedMemoryPtr = MapViewOfFile(
                state.SharedMemoryHandle, FileMapAllAccess, 0, 0, (UIntPtr)shmTotalSize);

            if (state.SharedMemoryPtr == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("MapViewOfFile failed (Win32 error {Error})", err);
                state.Dispose();
                return new VehInjectResult(false, $"Failed to map shared memory (Win32 error {err})");
            }

            // 6. Initialize header
            unsafe
            {
                NativeMemory.Clear(state.SharedMemoryPtr.ToPointer(), (nuint)shmTotalSize);
            }

            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetMagic, unchecked((int)ShmMagic));
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetVersion, (int)ShmVersion);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetAgentStatus, StatusLoading);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetMaxHits, maxHits);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandSlot, CmdIdle);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandResult, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetHitWriteIndex, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetHitReadIndex, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetHitCount, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetOverflowCount, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandArg3, 0);
            Marshal.WriteInt32(state.SharedMemoryPtr + OffsetHeartbeat, 0);

            // 7. Create named events
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

            // 8. Copy agent DLL to temp so we don't lock the original
            var tempDir = Path.Combine(Path.GetTempPath(), "CEAISuite_VEH");
            Directory.CreateDirectory(tempDir);
            // Obfuscated filename — avoids "veh", "agent", "debug", "cheat" in the name
            var randomSuffix = Guid.NewGuid().ToString("N")[..8];
            var tempAgentPath = Path.Combine(tempDir, $"msvcrt_p140_{randomSuffix}.dll");
            File.Copy(agentPath, tempAgentPath, overwrite: true);
            state.TempAgentPath = tempAgentPath;

            // 9. Inject via LoadLibraryW + CreateRemoteThread
            if (!LoadLibraryInTarget(hProcess, tempAgentPath))
            {
                var err = Marshal.GetLastWin32Error();
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("DLL injection failed for process {ProcessId} (Win32 error {Error})", processId, err);
                state.Dispose();
                return new VehInjectResult(false, $"DLL injection failed (Win32 error {err})");
            }

            // 10. Wait for agent to signal STATUS_READY (poll, 5s timeout)
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

            // 11. Store state
            if (!_states.TryAdd(processId, state))
            {
                // Race condition — another thread injected first
                state.Dispose();
                return new VehInjectResult(false, "VEH agent was injected by another thread");
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("VEH agent injected into process {ProcessId} (WOW64={IsWow64})",
                    processId, isWow64);

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

    private VehBreakpointResult SetBreakpointCore(VehProcessState state, nuint address, VehBreakpointType type, int dataSize)
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
            // Serialize command protocol — only one command can be in-flight at a time
            state.CommandLock.Wait();
            try
            {
                // 2. Write command arguments to shared memory
                Marshal.WriteInt64(state.SharedMemoryPtr + OffsetCommandArg0, unchecked((long)(ulong)address));
                Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandArg1, (int)type);
                Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandArg2, drSlot);
                Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandArg3, dataSize);

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
                                    "VEH breakpoint set: DR{DrSlot} at 0x{Address:X} type={Type} size={DataSize} (pid={ProcessId})",
                                    drSlot, (ulong)address, type, dataSize, state.ProcessId);
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
            finally
            {
                state.CommandLock.Release();
            }
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
            state.CommandLock.Wait();
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
                        if (result == 0)
                        {
                            // Only clear slot state on successful removal
                            Interlocked.Exchange(ref state.ActiveDrSlots[drSlot], 0);
                            state.Conditions[drSlot] = null;
                            state.LuaCallbacks[drSlot] = null;
                            state.SlotHitCounts[drSlot] = 0;

                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation(
                                    "VEH breakpoint removed: DR{DrSlot} (pid={ProcessId})", drSlot, state.ProcessId);
                            return true;
                        }

                        // Agent failed — do NOT clear condition/callback state since BP is still active
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
            finally
            {
                state.CommandLock.Release();
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error removing VEH breakpoint DR{DrSlot}", drSlot);
            return false;
        }
    }

    /// <summary>Send a simple command with no args (e.g., CMD_REFRESH_THREADS).</summary>
    private bool SendSimpleCommand(VehProcessState state, int command)
    {
        try
        {
            state.CommandLock.Wait();
            try
            {
                Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandResult, int.MinValue);
                Thread.MemoryBarrier();
                Marshal.WriteInt32(state.SharedMemoryPtr + OffsetCommandSlot, command);
                SetEvent(state.CommandEvent);

                const int timeoutMs = 3000;
                const int pollIntervalMs = 10;
                int elapsed = 0;

                while (elapsed < timeoutMs)
                {
                    var result = Marshal.ReadInt32(state.SharedMemoryPtr + OffsetCommandResult);
                    if (result != int.MinValue)
                        return result == 0;

                    Thread.Sleep(pollIntervalMs);
                    elapsed += pollIntervalMs;
                }

                return false;
            }
            finally
            {
                state.CommandLock.Release();
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error sending command {Command} to VEH agent", command);
            return false;
        }
    }

    // ─── Hit Entry Parsing ──────────────────────────────────────────

    /// <summary>Convert VEH RegisterSnapshot to dictionary for BreakpointHitEvent compatibility.</summary>
    private static Dictionary<string, string> RegisterSnapshotToDict(RegisterSnapshot regs) =>
        new Dictionary<string, string>
        {
            ["RAX"] = $"0x{regs.Rax:X}", ["RBX"] = $"0x{regs.Rbx:X}",
            ["RCX"] = $"0x{regs.Rcx:X}", ["RDX"] = $"0x{regs.Rdx:X}",
            ["RSI"] = $"0x{regs.Rsi:X}", ["RDI"] = $"0x{regs.Rdi:X}",
            ["RSP"] = $"0x{regs.Rsp:X}", ["RBP"] = $"0x{regs.Rbp:X}",
            ["R8"] = $"0x{regs.R8:X}", ["R9"] = $"0x{regs.R9:X}",
            ["R10"] = $"0x{regs.R10:X}", ["R11"] = $"0x{regs.R11:X}",
        };

    /// <summary>Read memory from the target process for condition evaluation.</summary>
    private static byte[]? ReadTargetMemory(IntPtr processHandle, nuint address, int size)
    {
        if (processHandle == IntPtr.Zero || size <= 0 || size > 64)
            return null;
        var buffer = new byte[size];
        return ReadProcessMemory(processHandle, (IntPtr)(nint)address, buffer, size, out var bytesRead) && bytesRead > 0
            ? buffer
            : null;
    }

    /// <summary>Determine which DR slot (0-3) triggered from DR6 bits 0-3. Returns -1 if none.</summary>
    private static int DetermineTriggeredSlot(ulong dr6)
    {
        for (int i = 0; i < 4; i++)
        {
            if ((dr6 & (1UL << i)) != 0)
                return i;
        }
        return -1;
    }

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

    // ─── WOW64 Detection ───────────────────────────────────────────

    private static bool IsTargetWow64(int processId)
    {
        // On a 64-bit OS, check if target is 32-bit (WOW64)
        if (!Environment.Is64BitOperatingSystem)
            return false;

        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(ProcessQueryInformation, false, processId);
            if (hProcess == IntPtr.Zero)
                return false;

            if (IsWow64Process(hProcess, out bool isWow64))
                return isWow64;

            return false;
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                CloseHandle(hProcess);
        }
    }

    // ─── Agent DLL Location ─────────────────────────────────────────

    private static string? FindAgentDll(bool wow64)
    {
        var dllName = wow64 ? "veh_agent_x86.dll" : "veh_agent.dll";

        // Check next to our assembly first
        var assemblyDir = Path.GetDirectoryName(typeof(WindowsVehDebugger).Assembly.Location);
        if (assemblyDir is not null)
        {
            var path = Path.Combine(assemblyDir, dllName);
            if (File.Exists(path)) return path;
        }

        // Check native/veh_agent/ relative to working directory
        var devPath = Path.Combine("native", "veh_agent", dllName);
        if (File.Exists(devPath)) return Path.GetFullPath(devPath);

        // Fallback: if looking for x86 but only x64 exists (dev environment), return null
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);
}
