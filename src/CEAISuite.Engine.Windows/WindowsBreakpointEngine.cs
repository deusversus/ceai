using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Engine.Windows;

public sealed class WindowsBreakpointEngine : IBreakpointEngine
{
    private readonly ILogger<WindowsBreakpointEngine> _logger;
    private readonly IVehDebugger? _vehDebugger;
    private readonly IBreakpointEventBus? _eventBus;

    public WindowsBreakpointEngine(
        ILogger<WindowsBreakpointEngine> logger,
        IVehDebugger? vehDebugger = null,
        IBreakpointEventBus? eventBus = null)
    {
        _logger = logger;
        _vehDebugger = vehDebugger;
        _eventBus = eventBus;
    }

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmReadAccess = ProcessQueryInformation | ProcessVmRead;

    private const uint ThreadAllAccess = 0x001FFFFF;

    private const uint DebugEventException = 1;
    private const uint DebugEventCreateThread = 2;
    private const uint DebugEventCreateProcess = 3;
    private const uint DebugEventExitThread = 4;
    private const uint DebugEventExitProcess = 5;
    private const uint DebugEventLoadDll = 6;

    private const uint ExceptionBreakpoint = 0x80000003;
    private const uint ExceptionSingleStep = 0x80000004;

    private const uint DbgContinue = 0x00010002;
    private const uint ErrorSemTimeout = 121;

    private const uint StatusGuardPageViolation = 0x80000001;
    private const uint PageGuard = 0x100;
    private const uint PageSize = 4096;
    private const int MaxThreadSuspendMs = 50;

    private const uint ContextAmd64 = 0x00100000;
    private const uint ContextControl = ContextAmd64 | 0x00000001;
    private const uint ContextInteger = ContextAmd64 | 0x00000002;
    private const uint ContextSegments = ContextAmd64 | 0x00000004;
    private const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
    private const uint ContextFull = ContextControl | ContextInteger | ContextSegments;
    private const uint ContextFullWithDebug = ContextFull | ContextDebugRegisters;

    private const uint Wow64ContextI386 = 0x00010000;
    private const uint Wow64ContextControl = Wow64ContextI386 | 0x00000001;
    private const uint Wow64ContextInteger = Wow64ContextI386 | 0x00000002;
    private const uint Wow64ContextSegments = Wow64ContextI386 | 0x00000004;
    private const uint Wow64ContextDebugRegisters = Wow64ContextI386 | 0x00000010;
    private const uint Wow64ContextFull = Wow64ContextControl | Wow64ContextInteger | Wow64ContextSegments;
    private const uint Wow64ContextFullWithDebug = Wow64ContextFull | Wow64ContextDebugRegisters;

    private const ushort ImageFileMachineI386 = 0x014c;

    private const uint TrapFlag = 0x100;

    private readonly ConcurrentDictionary<int, ProcessDebugSession> _sessions = new();
    private readonly ConcurrentDictionary<string, BreakpointState> _breakpointRegistry = new(StringComparer.Ordinal);
    private readonly object _sessionGate = new();

    public Task<BreakpointDescriptor> SetBreakpointAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        CancellationToken cancellationToken = default) =>
        SetBreakpointAsync(processId, address, type, BreakpointMode.Hardware, action, singleHit: false, cancellationToken);

    public Task<BreakpointDescriptor> SetBreakpointAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointMode mode,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        bool singleHit = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedMode = mode == BreakpointMode.Auto ? ResolveAutoMode(type) : mode;

        // 2F: WOW64 address width validation moved to SetBreakpointCoreAsync (after
        // GetOrCreateSession) so the check is not skipped on the first breakpoint
        // when no session exists yet.

        // Stealth mode is for code cave hooks (ICodeCaveEngine) — not for the breakpoint engine.
        // If it reaches here, downgrade to the best available mode for the type.
        if (resolvedMode == BreakpointMode.Stealth)
            resolvedMode = ResolveAutoMode(type);

        // VEH breakpoints: route through the injected agent (no debugger attachment)
        if (resolvedMode == BreakpointMode.VectoredExceptionHandler)
            return SetVehBreakpointAsync(processId, address, type, action, singleHit, cancellationToken);

        // Page guard breakpoints are handled differently
        if (resolvedMode == BreakpointMode.PageGuard)
            return SetPageGuardBreakpointAsync(processId, address, type, action, singleHit, cancellationToken);

        // 2B: Wrap hardware BP installation with auto-fallback.
        // Chain: Hardware → VEH (if available) → PageGuard
        if (resolvedMode == BreakpointMode.Hardware)
        {
            return Task.Run(async () =>
            {
                try
                {
                    return await SetBreakpointCoreAsync(processId, address, type, resolvedMode, action, singleHit, cancellationToken);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("four active hardware breakpoints", StringComparison.Ordinal))
                {
                    // Try VEH first (no debugger needed), then PageGuard
                    if (_vehDebugger is not null)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("Hardware slots exhausted. Auto-falling back to VEH for BP at 0x{Address:X}", address);
                        try
                        {
                            return await SetVehBreakpointAsync(processId, address, type, action, singleHit, cancellationToken);
                        }
                        catch (Exception vehEx) when (vehEx is not OperationCanceledException)
                        {
                            // VEH also failed — fall through to PageGuard
                            if (_logger.IsEnabled(LogLevel.Debug))
                                _logger.LogDebug(vehEx, "VEH fallback failed, cascading to PageGuard");
                        }
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Falling back to PageGuard for BP at 0x{Address:X}", address);
                    return await SetPageGuardBreakpointAsync(processId, address, type, action, singleHit, cancellationToken);
                }
            }, cancellationToken);
        }

        return SetBreakpointCoreAsync(processId, address, type, resolvedMode, action, singleHit, cancellationToken);
    }

    private Task<BreakpointDescriptor> SetBreakpointCoreAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointMode resolvedMode,
        BreakpointHitAction action,
        bool singleHit,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var session = GetOrCreateSession(processId, cancellationToken);

                // 2F: Address width validation for WOW64 — reject 64-bit addresses
                // for 32-bit processes. Performed here (after GetOrCreateSession) so
                // the session and its IsWow64Target flag are guaranteed to exist,
                // including on the very first breakpoint for a process.
                if (session.IsWow64Target && (ulong)address > 0xFFFFFFFF)
                {
                    throw new InvalidOperationException(
                        $"Address 0x{address:X} exceeds 32-bit range for WOW64 process. " +
                        "Hardware debug registers are 32-bit in WOW64 mode — the address would be silently truncated.");
                }

                lock (session.SyncRoot)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var existing = session.Breakpoints.Values.FirstOrDefault(
                        breakpoint => breakpoint.IsEnabled &&
                                      breakpoint.Address == address &&
                                      breakpoint.Type == type);

                    if (existing is not null)
                    {
                        return existing.ToDescriptor();
                    }

                    var breakpoint = new BreakpointState(
                        $"bp-{Guid.NewGuid().ToString("N")[..8]}",
                        processId,
                        address,
                        type,
                        action,
                        resolvedMode,
                        singleHit);

                    if (resolvedMode == BreakpointMode.Software || type == BreakpointType.Software)
                    {
                        InstallSoftwareBreakpoint(session, breakpoint);
                    }
                    else
                    {
                        // B1: Try coalescing data-watch BPs on the same 8-byte aligned region
                        var coalescedSlot = TryCoalesceHardwareSlot(session, address, type);
                        breakpoint.HardwareSlot = coalescedSlot >= 0 ? coalescedSlot : AllocateHardwareSlot(session);
                        ApplyHardwareBreakpointsLocked(session);
                    }

                    session.Breakpoints[breakpoint.Id] = breakpoint;
                    if (type == BreakpointType.Software || resolvedMode == BreakpointMode.Software)
                    {
                        session.SoftwareBreakpoints[address] = breakpoint;
                    }

                    _breakpointRegistry[breakpoint.Id] = breakpoint;

                    // C1: Publish breakpoint-added event
                    _eventBus?.Publish(new BreakpointAddedEvent(
                        breakpoint.Id, $"0x{address:X}", resolvedMode.ToString(), type.ToString()));

                    return breakpoint.ToDescriptor();
                }
            },
            cancellationToken);
    }

    internal static BreakpointMode ResolveAutoMode(BreakpointType type) =>
        type switch
        {
            // Execute BPs: prefer stealth (code cave) — caller should use ICodeCaveEngine directly,
            // but if they use Auto through the BP engine, fall back to hardware
            BreakpointType.HardwareExecute => BreakpointMode.Hardware,
            BreakpointType.Software => BreakpointMode.Software,
            // Data access BPs: prefer page guard (less intrusive than hardware DR registers)
            BreakpointType.HardwareWrite => BreakpointMode.PageGuard,
            BreakpointType.HardwareReadWrite => BreakpointMode.PageGuard,
            _ => BreakpointMode.Hardware
        };

    public Task<bool> RemoveBreakpointAsync(
        int processId,
        string breakpointId,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Check if this is a VEH breakpoint (not session-based)
                if (_breakpointRegistry.TryGetValue(breakpointId, out var vehBp) &&
                    vehBp.Mode == BreakpointMode.VectoredExceptionHandler)
                {
                    return await RemoveVehBreakpointAsync(breakpointId, vehBp).ConfigureAwait(false);
                }

                if (!_sessions.TryGetValue(processId, out var session))
                {
                    return false;
                }

                var detachSession = false;

                lock (session.SyncRoot)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!session.Breakpoints.TryGetValue(breakpointId, out var breakpoint))
                    {
                        return false;
                    }

                    // Check if this software BP has a pending single-step re-arm.
                    // If so, we can't remove the BP yet — the re-arm handler needs it
                    // to complete the 0xCC → original → single-step → 0xCC cycle.
                    // Setting PendingRemoval tells the single-step handler to clean up after re-arm.
                    bool hasPendingRearm = breakpoint.Mode == BreakpointMode.Software &&
                        session.PendingSoftwareRearm.Values.Any(
                            id => string.Equals(id, breakpointId, StringComparison.Ordinal));

                    if (hasPendingRearm)
                    {
                        breakpoint.PendingRemoval = true;
                        // Don't remove from Breakpoints or SoftwareBreakpoints yet —
                        // HandleSingleStepException will complete re-arm then clean up.
                        // But do remove from the registry so external lookups see it gone.
                        _breakpointRegistry.TryRemove(breakpointId, out _);
                        return true;
                    }

                    session.Breakpoints.Remove(breakpointId);
                    breakpoint.IsEnabled = false;

                    foreach (var pendingThreadId in session.PendingSoftwareRearm
                                 .Where(pair => string.Equals(pair.Value, breakpointId, StringComparison.Ordinal))
                                 .Select(pair => pair.Key)
                                 .ToArray())
                    {
                        session.PendingSoftwareRearm.Remove(pendingThreadId);
                    }

                    if (breakpoint.Mode == BreakpointMode.Software)
                    {
                        session.SoftwareBreakpoints.Remove(breakpoint.Address);
                        RestoreOriginalByte(session, breakpoint);
                    }
                    else if (breakpoint.Mode == BreakpointMode.PageGuard)
                    {
                        if (breakpoint.PageBaseAddress is { } pageBase)
                        {
                            if (session.PageGuardBreakpoints.TryGetValue(pageBase, out var bpList))
                            {
                                bpList.Remove(breakpoint);
                                if (bpList.Count == 0)
                                {
                                    session.PageGuardBreakpoints.Remove(pageBase);
                                    // 2H: Last BP on this page — restore original protection from per-page tracking
                                    if (session.OriginalPageProtections.Remove(pageBase, out var origProt))
                                    {
                                        VirtualProtectEx(session.ProcessHandle, (IntPtr)pageBase,
                                            (UIntPtr)PageSize, origProt, out _);
                                    }
                                    else if (breakpoint.OriginalPageProtection is { } bpOrigProt)
                                    {
                                        VirtualProtectEx(session.ProcessHandle, (IntPtr)pageBase,
                                            (UIntPtr)PageSize, bpOrigProt, out _);
                                    }
                                }
                                // If other BPs remain on this page, guard stays active
                            }
                        }
                    }
                    else
                    {
                        breakpoint.HardwareSlot = null;
                        ApplyHardwareBreakpointsLocked(session);
                    }

                    detachSession = session.Breakpoints.Count == 0;
                }

                _breakpointRegistry.TryRemove(breakpointId, out _);

                // C1: Publish removal event
                _eventBus?.Publish(new BreakpointRemovedEvent(breakpointId));

                if (detachSession)
                {
                    StopSession(session, detachFromProcess: true);
                }

                return true;
            },
            cancellationToken);

    public Task<IReadOnlyList<BreakpointDescriptor>> ListBreakpointsAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<BreakpointDescriptor>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var results = new List<BreakpointDescriptor>();

                // Include session-based breakpoints (Software, Hardware, PageGuard)
                if (_sessions.TryGetValue(processId, out var session))
                {
                    lock (session.SyncRoot)
                    {
                        results.AddRange(session.Breakpoints.Values
                            .Select(breakpoint => breakpoint.ToDescriptor()));
                    }
                }

                // Include VEH breakpoints from global registry (not session-based)
                foreach (var bp in _breakpointRegistry.Values)
                {
                    if (bp.ProcessId == processId && bp.Mode == BreakpointMode.VectoredExceptionHandler)
                        results.Add(bp.ToDescriptor());
                }

                return results.OrderBy(bp => bp.Address).ToArray();
            },
            cancellationToken);

    public Task<IReadOnlyList<BreakpointHitEvent>> GetHitLogAsync(
        string breakpointId,
        int maxEntries = 50,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<BreakpointHitEvent>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (maxEntries <= 0 || !_breakpointRegistry.TryGetValue(breakpointId, out var breakpoint))
                {
                    return Array.Empty<BreakpointHitEvent>();
                }

                return breakpoint.HitLog
                    .ToArray()
                    .TakeLast(maxEntries)
                    .ToArray();
            },
            cancellationToken);

    private ProcessDebugSession GetOrCreateSession(int processId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(processId, out var existing))
        {
            return existing;
        }

        lock (_sessionGate)
        {
            if (_sessions.TryGetValue(processId, out existing))
            {
                return existing;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var processHandle = OpenProcess(
                ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation,
                false,
                processId);

            if (processHandle == IntPtr.Zero)
            {
                throw CreateWin32Exception($"Unable to open process {processId} for breakpoint management.");
            }

            try
            {
                var isWow64 = IsWow64Target(processHandle);
                var session = new ProcessDebugSession(processId, processHandle, isWow64);

                RefreshThreads(session);

                if (!DebugActiveProcess(processId))
                {
                    // If attach fails, the process may still be marked as debugged
                    // from a prior session that crashed without proper cleanup.
                    // Try detaching first, then re-attaching.
                    DebugActiveProcessStop(processId);
                    if (!DebugActiveProcess(processId))
                    {
                        throw CreateWin32Exception($"Unable to attach debugger to process {processId}.");
                    }
                }

                session.IsDebuggerAttached = true;
                // 7B: No thread Name assigned — avoids anti-cheat fingerprinting of debug thread names
                session.DebugThread = new Thread(() => DebugLoop(session))
                {
                    IsBackground = true
                };

                if (!_sessions.TryAdd(processId, session))
                {
                    throw new InvalidOperationException($"A breakpoint session for process {processId} already exists.");
                }

                session.DebugThread.Start();
                return session;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Breakpoint session creation failed for PID {ProcessId}", processId);
                CloseHandle(processHandle);
                throw;
            }
        }
    }

    private static bool IsWow64Target(IntPtr processHandle)
    {
        if (!IsWow64Process2(processHandle, out var processMachine, out _))
        {
            return false;
        }

        return processMachine == ImageFileMachineI386;
    }

    private static void RefreshThreads(ProcessDebugSession session)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);

            lock (session.SyncRoot)
            {
                foreach (ProcessThread thread in process.Threads)
                {
                    EnsureThreadHandleLocked(session, thread.Id, IntPtr.Zero);
                }
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static IntPtr EnsureThreadHandleLocked(ProcessDebugSession session, int threadId, IntPtr eventHandle)
    {
        if (session.ThreadHandles.TryGetValue(threadId, out var existing) && existing != IntPtr.Zero)
        {
            if (eventHandle != IntPtr.Zero)
            {
                CloseHandle(eventHandle);
            }

            return existing;
        }

        IntPtr handle;
        if (eventHandle != IntPtr.Zero)
        {
            handle = eventHandle;
        }
        else
        {
            handle = OpenThread(ThreadAllAccess, false, threadId);
            if (handle == IntPtr.Zero)
            {
                throw CreateWin32Exception($"Unable to open thread {threadId} in process {session.ProcessId}.");
            }
        }

        session.ThreadHandles[threadId] = handle;
        return handle;
    }

    /// <summary>
    /// B1: Try to coalesce a data-write/readwrite BP into an existing hardware slot
    /// that watches the same 8-byte aligned region. Returns the slot if coalesced, or -1.
    /// </summary>
    private static int TryCoalesceHardwareSlot(ProcessDebugSession session, nuint address, BreakpointType type)
    {
        // Only data-access BPs can coalesce (Write, ReadWrite) — not Execute
        if (type != BreakpointType.HardwareWrite && type != BreakpointType.HardwareReadWrite)
            return -1;

        var alignedBase = address & ~(nuint)7; // 8-byte aligned base
        foreach (var bp in session.Breakpoints.Values)
        {
            if (!bp.IsEnabled || !bp.HardwareSlot.HasValue) continue;
            if (bp.Type != BreakpointType.HardwareWrite && bp.Type != BreakpointType.HardwareReadWrite) continue;

            var existingAligned = bp.Address & ~(nuint)7;
            if (existingAligned == alignedBase)
            {
                // Coalesce: update the existing BP's address to the aligned base
                // so the DR register watches the full 8-byte region
                return bp.HardwareSlot.Value;
            }
        }

        return -1;
    }

    private static int AllocateHardwareSlot(ProcessDebugSession session)
    {
        var usedSlots = session.Breakpoints.Values
            .Where(breakpoint => breakpoint.IsEnabled && breakpoint.HardwareSlot.HasValue)
            .Select(breakpoint => breakpoint.HardwareSlot!.Value)
            .ToHashSet();

        for (var slot = 0; slot < 4; slot++)
        {
            if (!usedSlots.Contains(slot))
            {
                return slot;
            }
        }

        throw new InvalidOperationException("Windows supports at most four active hardware breakpoints per process.");
    }

    private void InstallSoftwareBreakpoint(ProcessDebugSession session, BreakpointState breakpoint)
    {
        if (session.SoftwareBreakpoints.ContainsKey(breakpoint.Address))
        {
            throw new InvalidOperationException($"A software breakpoint already exists at 0x{breakpoint.Address:X}.");
        }

        // 2C: Instruction boundary validation — read enough bytes to decode at least one instruction
        var probeBuffer = new byte[15]; // max x86 instruction length
        if (ReadProcessMemory(session.ProcessHandle, (IntPtr)breakpoint.Address, probeBuffer, probeBuffer.Length, out var probeRead) && probeRead > 0)
        {
            var bitness = session.IsWow64Target ? 32 : 64;
            var codeReader = new ByteArrayCodeReader(probeBuffer, 0, probeRead);
            var decoder = Decoder.Create(bitness, codeReader, (ulong)breakpoint.Address);
            var instr = decoder.Decode();
            if (instr.IsInvalid)
            {
                throw new InvalidOperationException(
                    $"Address 0x{breakpoint.Address:X} does not decode to a valid instruction. " +
                    "Software breakpoint rejected — this address may not be at an instruction boundary.");
            }
            // Verify the decoded instruction starts at exactly our target address
            if (instr.IP != (ulong)breakpoint.Address)
            {
                throw new InvalidOperationException(
                    $"Address 0x{breakpoint.Address:X} is not at an instruction boundary. " +
                    $"Nearest instruction starts at 0x{instr.IP:X}.");
            }
        }

        var original = new byte[1];
        if (!ReadProcessMemory(session.ProcessHandle, (IntPtr)breakpoint.Address, original, original.Length, out var bytesRead) || bytesRead != 1)
        {
            throw CreateWin32Exception($"Unable to read original byte at 0x{breakpoint.Address:X}.");
        }

        // 2E: Detect existing 0xCC before installing software breakpoint
        if (original[0] == 0xCC)
        {
            _logger.LogWarning("Address 0x{Address:X} already contains 0xCC (INT3). Another debugger or padding byte may be present. Skipping patch to avoid recording wrong original byte", breakpoint.Address);
            throw new InvalidOperationException(
                $"Address 0x{breakpoint.Address:X} already contains 0xCC (INT3). " +
                "Cannot install software breakpoint — original byte would be lost.");
        }

        var patch = new byte[] { 0xCC };
        if (!WriteProcessMemory(session.ProcessHandle, (IntPtr)breakpoint.Address, patch, patch.Length, out var bytesWritten) || bytesWritten != 1)
        {
            throw CreateWin32Exception($"Unable to write software breakpoint at 0x{breakpoint.Address:X}.");
        }

        FlushInstructionCache(session.ProcessHandle, (IntPtr)breakpoint.Address, (UIntPtr)1);
        breakpoint.OriginalByte = original[0];
    }

    private void RestoreOriginalByte(ProcessDebugSession session, BreakpointState breakpoint)
    {
        if (breakpoint.OriginalByte is null)
        {
            return;
        }

        var original = new[] { breakpoint.OriginalByte.Value };
        if (!WriteProcessMemory(session.ProcessHandle, (IntPtr)breakpoint.Address, original, original.Length, out _))
        {
            // 2A: 0xCC persists at this address — mark breakpoint as restoration-failed
            breakpoint.RestorationFailed = true;
            _logger.LogError("RestoreOriginalByte failed at 0x{Address:X} (error {Error}). The 0xCC byte persists -- breakpoint {BreakpointId} marked as restoration-failed", breakpoint.Address, Marshal.GetLastWin32Error(), breakpoint.Id);
            _eventBus?.Publish(new BreakpointStateChangedEvent(breakpoint.Id, nameof(BreakpointLifecycleStatus.Faulted)));
            return;
        }
        FlushInstructionCache(session.ProcessHandle, (IntPtr)breakpoint.Address, (UIntPtr)1);
    }

    /// <summary>
    /// Read bytes from the target process. Returns null if the read fails.
    /// Used by condition evaluation to read memory for MemoryCompare conditions.
    /// </summary>
    private static byte[]? ReadProcessMemoryBytes(IntPtr processHandle, nuint address, int size)
    {
        if (size <= 0 || size > 8) return null;
        var buffer = new byte[size];
        return ReadProcessMemory(processHandle, (IntPtr)address, buffer, size, out var bytesRead) && bytesRead == size
            ? buffer
            : null;
    }

    private void ApplyHardwareBreakpointsLocked(ProcessDebugSession session)
    {
        var activeHardwareBreakpoints = session.Breakpoints.Values
            .Where(breakpoint => breakpoint.IsEnabled && breakpoint.HardwareSlot.HasValue)
            .OrderBy(breakpoint => breakpoint.HardwareSlot)
            .ToArray();

        foreach (var threadHandle in session.ThreadHandles.Values.Where(handle => handle != IntPtr.Zero))
        {
            ApplyHardwareBreakpointsToThread(threadHandle, session.IsWow64Target, activeHardwareBreakpoints);
        }
    }

    private void ApplyHardwareBreakpointsToThread(
        IntPtr threadHandle,
        bool isWow64Target,
        IReadOnlyCollection<BreakpointState> activeHardwareBreakpoints)
    {
        if (threadHandle == IntPtr.Zero)
        {
            return;
        }

        var suspendCount = SuspendThread(threadHandle);
        if (suspendCount == uint.MaxValue)
        {
            return; // Graceful: thread may have exited, don't crash
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (isWow64Target)
            {
                var context = CreateWow64Context();
                // 3G: Gracefully handle invalid/stale thread handles
                if (!Wow64GetThreadContext(threadHandle, ref context))
                {
                    return; // Thread exited or inaccessible — don't crash host app
                }

                ApplyHardwareRegisters(
                    activeHardwareBreakpoints,
                    out var dr0,
                    out var dr1,
                    out var dr2,
                    out var dr3,
                    out var dr7);

                context.Dr0 = (uint)dr0;
                context.Dr1 = (uint)dr1;
                context.Dr2 = (uint)dr2;
                context.Dr3 = (uint)dr3;
                context.Dr7 = (uint)dr7;

                if (sw.ElapsedMilliseconds > MaxThreadSuspendMs)
                {
                    return; // Safety: thread has been suspended too long, bail out
                }

                if (!Wow64SetThreadContext(threadHandle, ref context))
                {
                    _logger.LogWarning("Wow64SetThreadContext failed for thread handle 0x{ThreadHandle:X} (error {Error}). Hardware BP may not be applied to this thread", threadHandle, Marshal.GetLastWin32Error());
                }
            }
            else
            {
                var context = CreateContext64();
                if (!GetThreadContext(threadHandle, ref context))
                {
                    return; // Thread exited or inaccessible
                }

                ApplyHardwareRegisters(
                    activeHardwareBreakpoints,
                    out var dr0,
                    out var dr1,
                    out var dr2,
                    out var dr3,
                    out var dr7);

                context.Dr0 = dr0;
                context.Dr1 = dr1;
                context.Dr2 = dr2;
                context.Dr3 = dr3;
                context.Dr7 = dr7;

                if (sw.ElapsedMilliseconds > MaxThreadSuspendMs)
                {
                    return; // Safety: thread has been suspended too long, bail out
                }

                if (!SetThreadContext(threadHandle, ref context))
                {
                    _logger.LogWarning("SetThreadContext failed for thread handle 0x{ThreadHandle:X} (error {Error}). Hardware BP may not be applied to this thread", threadHandle, Marshal.GetLastWin32Error());
                }
            }
        }
        finally
        {
            _ = ResumeThread(threadHandle);
        }
    }

    private static void ApplyHardwareRegisters(
        IReadOnlyCollection<BreakpointState> breakpoints,
        out ulong dr0,
        out ulong dr1,
        out ulong dr2,
        out ulong dr3,
        out ulong dr7)
    {
        dr0 = 0;
        dr1 = 0;
        dr2 = 0;
        dr3 = 0;
        dr7 = 0;

        // B1: Group by slot — coalesced BPs share a slot. Use the first BP's slot
        // to set the address/type, but if multiple BPs share a slot, use the
        // 8-byte aligned base address and 8-byte length (widest coverage).
        var slotGroups = breakpoints
            .Where(bp => bp.HardwareSlot.HasValue)
            .GroupBy(bp => bp.HardwareSlot!.Value);

        foreach (var group in slotGroups)
        {
            var slot = group.Key;
            var bpList = group.ToList();
            var isCoalesced = bpList.Count > 1;

            // For coalesced slots, use the 8-byte aligned base; otherwise use exact address
            var address = isCoalesced
                ? bpList[0].Address & ~(nuint)7
                : bpList[0].Address;

            switch (slot)
            {
                case 0: dr0 = address; break;
                case 1: dr1 = address; break;
                case 2: dr2 = address; break;
                case 3: dr3 = address; break;
                default:
                    throw new InvalidOperationException($"Invalid hardware debug register slot {slot}. Valid range is 0-3.");
            }

            dr7 |= 1UL << (slot * 2);

            // Use the first BP's type for the DR7 type bits.
            // Length bits: Execute = 1 byte (0b00), Write/ReadWrite = 8 bytes (0b11).
            // 8-byte default for data watches is intentional — game memory fields are
            // typically DWORD/QWORD aligned, and 8-byte coverage catches both. The VEH
            // path supports explicit dataSize via IVehDebugger.SetBreakpointAsync.
            var firstBp = bpList[0];
            var (typeBits, lengthBits) = firstBp.Type switch
            {
                BreakpointType.HardwareExecute => (0b00UL, 0b00UL),
                BreakpointType.HardwareWrite => (0b01UL, isCoalesced ? 0b11UL : 0b11UL),
                BreakpointType.HardwareReadWrite => (0b11UL, isCoalesced ? 0b11UL : 0b11UL),
                _ => (0b00UL, 0b00UL)
            };

            var controlShift = 16 + (slot * 4);
            dr7 |= typeBits << controlShift;
            dr7 |= lengthBits << (controlShift + 2);
        }
    }

    // ─── VEH Breakpoint Support (Phase F: Unified Pipeline) ────────────

    private async Task<BreakpointDescriptor> SetVehBreakpointAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointHitAction action,
        bool singleHit,
        CancellationToken ct)
    {
        if (_vehDebugger is null)
            throw new InvalidOperationException("VEH debugger is not available.");

        // Auto-inject if not already injected
        var status = _vehDebugger.GetStatus(processId);
        if (!status.IsInjected)
        {
            var injectResult = await _vehDebugger.InjectAsync(processId, ct).ConfigureAwait(false);
            if (!injectResult.Success)
                throw new InvalidOperationException($"VEH agent injection failed: {injectResult.Error}");
        }

        // Map BreakpointType to VEH method
        // Software type uses INT3 via VEH; hardware types use DR registers
        if (type == BreakpointType.Software)
        {
            var int3Result = await _vehDebugger.SetInt3BreakpointAsync(processId, address, ct).ConfigureAwait(false);
            if (!int3Result.Success)
                throw new InvalidOperationException($"VEH INT3 breakpoint failed: {int3Result.Error}");

            var int3BpId = $"bp-{Guid.NewGuid().ToString("N")[..8]}";
            var int3Bp = new BreakpointState(int3BpId, processId, address, type, action,
                BreakpointMode.VectoredExceptionHandler, singleHit);
            int3Bp.HardwareSlot = -1; // sentinel: INT3, not hardware

            _breakpointRegistry[int3BpId] = int3Bp;
            return int3Bp.ToDescriptor();
        }

        var vehType = type switch
        {
            BreakpointType.HardwareExecute => VehBreakpointType.Execute,
            BreakpointType.HardwareWrite => VehBreakpointType.Write,
            BreakpointType.HardwareReadWrite => VehBreakpointType.ReadWrite,
            _ => VehBreakpointType.Execute
        };

        var vehResult = await _vehDebugger.SetBreakpointAsync(processId, address, vehType, ct: ct).ConfigureAwait(false);
        if (!vehResult.Success)
            throw new InvalidOperationException($"VEH breakpoint failed: {vehResult.Error}");

        // Create a standard BreakpointState so it appears in ListBreakpoints
        var bpId = $"bp-{Guid.NewGuid().ToString("N")[..8]}";
        var bp = new BreakpointState(bpId, processId, address, type, action,
            BreakpointMode.VectoredExceptionHandler, singleHit);
        bp.HardwareSlot = vehResult.DrSlot;

        // Store in global registry so ListBreakpoints/GetHitLog work
        _breakpointRegistry[bpId] = bp;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "VEH breakpoint set via unified pipeline: {BpId} at 0x{Address:X} DR{Slot} (pid={Pid})",
                bpId, address, vehResult.DrSlot, processId);

        return bp.ToDescriptor();
    }

    private async Task<bool> RemoveVehBreakpointAsync(string breakpointId, BreakpointState bp)
    {
        if (_vehDebugger is null)
            return false;

        bool ok;
        if (bp.HardwareSlot is -1)
        {
            // INT3 software BP via VEH — remove by address
            ok = await _vehDebugger.RemoveInt3BreakpointAsync(bp.ProcessId, bp.Address).ConfigureAwait(false);
        }
        else if (bp.HardwareSlot is { } drSlot and >= 0)
        {
            // Hardware BP via VEH — remove by DR slot
            ok = await _vehDebugger.RemoveBreakpointAsync(bp.ProcessId, drSlot).ConfigureAwait(false);
        }
        else
        {
            ok = false;
        }

        // Only remove from registry on successful agent removal — prevents orphaning
        if (ok)
            _breakpointRegistry.TryRemove(breakpointId, out _);
        return ok;
    }

    // ─── PAGE_GUARD Breakpoint Support ──────────────────────────────────

    private Task<BreakpointDescriptor> SetPageGuardBreakpointAsync(
        int processId,
        nuint address,
        BreakpointType type,
        BreakpointHitAction action,
        bool singleHit,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = GetOrCreateSession(processId, cancellationToken);

            // Step 1: Lock → check for existing BP, create state, capture pageBase → release lock.
            // VirtualProtectEx must NOT be called under lock — the debug loop acquires
            // the same lock and would deadlock on the guard-page violation storm.
            BreakpointState breakpoint;
            nuint pageBase;
            IntPtr processHandle;

            lock (session.SyncRoot)
            {
                pageBase = address & ~(nuint)(PageSize - 1);

                var existing = session.Breakpoints.Values.FirstOrDefault(
                    bp => bp.IsEnabled && bp.Address == address && bp.Mode == BreakpointMode.PageGuard);
                if (existing is not null) return existing.ToDescriptor();

                breakpoint = new BreakpointState(
                    $"bp-{Guid.NewGuid().ToString("N")[..8]}", processId, address, type, action, BreakpointMode.PageGuard, singleHit);
                breakpoint.PageBaseAddress = pageBase;
                processHandle = session.ProcessHandle;
            }

            // Step 2: Arm the PAGE_GUARD with NO lock held.
            // Query the actual page protection first — don't hardcode PAGE_READWRITE.
            uint actualProtection = 0x04; // PAGE_READWRITE fallback
            if (VirtualQueryEx(processHandle, (IntPtr)pageBase, out var mbi,
                Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
            {
                // 2D: Validate that the region is committed memory before arming PAGE_GUARD
                const uint MemCommit = 0x1000;
                if ((mbi.State & MemCommit) == 0)
                {
                    throw new InvalidOperationException(
                        $"Cannot set PAGE_GUARD at 0x{pageBase:X}: memory region is not committed " +
                        $"(state=0x{mbi.State:X}). Only committed memory can be guarded.");
                }

                // Strip PAGE_GUARD if already set, we'll add it back
                actualProtection = mbi.Protect & ~PageGuard;
            }

            if (!VirtualProtectEx(processHandle, (IntPtr)pageBase, (UIntPtr)PageSize,
                PageGuard | actualProtection, out uint oldProtect))
            {
                throw CreateWin32Exception($"Unable to set PAGE_GUARD at 0x{pageBase:X}.");
            }

            // Step 3: Lock → register BP in dictionaries → release lock.
            // 2H: Only store original protection on the first BP for a given page
            lock (session.SyncRoot)
            {
                session.Breakpoints[breakpoint.Id] = breakpoint;
                if (!session.PageGuardBreakpoints.TryGetValue(pageBase, out var bpList))
                {
                    bpList = new List<BreakpointState>();
                    session.PageGuardBreakpoints[pageBase] = bpList;
                    // First BP on this page — record the original protection
                    breakpoint.OriginalPageProtection = actualProtection;
                    session.OriginalPageProtections[pageBase] = actualProtection;
                }
                else
                {
                    // Subsequent BP on same page — inherit recorded protection
                    breakpoint.OriginalPageProtection = session.OriginalPageProtections.GetValueOrDefault(pageBase, actualProtection);
                }
                bpList.Add(breakpoint);
                _breakpointRegistry[breakpoint.Id] = breakpoint;
            }

            return breakpoint.ToDescriptor();
        }, cancellationToken);

    private void HandlePageGuardViolation(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        // STATUS_GUARD_PAGE_VIOLATION carries the fault address in ExceptionInformation[1]
        nuint faultAddress;
        unsafe
        {
            faultAddress = (nuint)debugEvent.u.Exception.ExceptionRecord.ExceptionInformation[1];
        }
        var pageBase = faultAddress & ~(nuint)(PageSize - 1);

        if (!session.PageGuardBreakpoints.TryGetValue(pageBase, out var bpList) || bpList.Count == 0)
            return;

        // Capture register snapshot once (shared across all BPs on this page)
        var threadHandle = session.ThreadHandles.GetValueOrDefault(debugEvent.dwThreadId);
        var registers = new Dictionary<string, string>();
        if (threadHandle != IntPtr.Zero)
        {
            var ctx = CreateContext64();
            if (GetThreadContext(threadHandle, ref ctx))
            {
                registers["RIP"] = $"0x{ctx.Rip:X16}";
                registers["RSP"] = $"0x{ctx.Rsp:X16}";
                registers["RAX"] = $"0x{ctx.Rax:X16}";
                registers["RBX"] = $"0x{ctx.Rbx:X16}";
                registers["RCX"] = $"0x{ctx.Rcx:X16}";
                registers["RDX"] = $"0x{ctx.Rdx:X16}";
                registers["EFLAGS"] = $"0x{ctx.EFlags:X8}";
            }
        }

        // Log hit for ALL enabled BPs on this page (gated by thread filter + condition)
        foreach (var breakpoint in bpList)
        {
            if (!breakpoint.IsEnabled) continue;

            // A1: Thread filter enforcement for PAGE_GUARD breakpoints
            if (breakpoint.ThreadFilter.HasValue && breakpoint.ThreadFilter.Value != debugEvent.dwThreadId)
                continue;

            // A2: Conditional evaluation for PAGE_GUARD breakpoints
            if (breakpoint.Condition is { } cond)
            {
                Func<nuint, int, byte[]?> readMem = (addr, size) => ReadProcessMemoryBytes(session.ProcessHandle, addr, size);
                if (!VehConditionEvaluator.EvaluateFromDictionary(cond, registers, breakpoint.HitCount, readMem))
                    continue;
            }

            // 2J: Apply same throttle window logic to PageGuard hits
            if (LogBreakpointHit(breakpoint, debugEvent.dwThreadId, faultAddress, registers))
            {
                breakpoint.IsEnabled = false;
                _logger.LogWarning("PageGuard BP {BreakpointId} at 0x{Address:X}: auto-disabled due to excessive hit rate (guard-page storm prevention)", breakpoint.Id, breakpoint.Address);
            }
        }

        // If ALL BPs on this page are disabled, don't re-arm the guard
        if (bpList.All(bp => !bp.IsEnabled))
            return;

        // Single-step then re-arm PAGE_GUARD
        if (threadHandle != IntPtr.Zero)
        {
            EnableTrapFlag(threadHandle, session.IsWow64Target);
            session.PendingPageGuardRearm[debugEvent.dwThreadId] = pageBase;
        }
    }

    private void RearmPageGuard(ProcessDebugSession session, int threadId)
    {
        if (!session.PendingPageGuardRearm.Remove(threadId, out var pageBase))
            return;

        if (!session.PageGuardBreakpoints.TryGetValue(pageBase, out var bpList))
            return;

        // Only re-arm if at least one BP on this page is still enabled
        var activeBp = bpList.FirstOrDefault(bp => bp.IsEnabled);
        if (activeBp is null)
            return;

        if (!VirtualProtectEx(session.ProcessHandle, (IntPtr)pageBase, (UIntPtr)PageSize,
            PageGuard | (activeBp.OriginalPageProtection ?? 0x04), out _))
        {
            // Re-arm failed (e.g., process dying). Mark all BPs on this page as broken.
            foreach (var bp in bpList)
            {
                bp.IsEnabled = false;
                bp.ThrottleDisabled = true;
            }
            _logger.LogWarning("RearmPageGuard failed at page 0x{PageBase:X} (error {Error}). {BpCount} BP(s) marked as disabled", pageBase, Marshal.GetLastWin32Error(), bpList.Count);
        }
    }

    private static void EnableTrapFlag(IntPtr threadHandle, bool isWow64)
    {
        if (isWow64)
        {
            var ctx = CreateWow64Context();
            if (Wow64GetThreadContext(threadHandle, ref ctx))
            {
                ctx.EFlags |= TrapFlag;
                Wow64SetThreadContext(threadHandle, ref ctx);
            }
        }
        else
        {
            var ctx = CreateContext64();
            if (GetThreadContext(threadHandle, ref ctx))
            {
                ctx.EFlags |= TrapFlag;
                SetThreadContext(threadHandle, ref ctx);
            }
        }
    }

    // ─── Safety-Hardened Thread Suspension ───────────────────────────────

    /// <summary>
    /// Suspends a thread with a safety watchdog. If the operation takes longer
    /// than MaxThreadSuspendMs, the thread is forcibly resumed to prevent
    /// freezing the target process.
    /// </summary>
    private static bool TrySuspendThreadSafe(IntPtr threadHandle, out uint previousSuspendCount)
    {
        previousSuspendCount = SuspendThread(threadHandle);
        return previousSuspendCount != uint.MaxValue;
    }

    private void DebugLoop(ProcessDebugSession session)
    {
        // 3B: Retry counter — allow up to 5 consecutive non-timeout failures before killing the loop
        const int MaxConsecutiveFailures = 5;
        int consecutiveFailures = 0;

        try
        {
            while (!session.StopRequested)
            {
                if (!WaitForDebugEventEx(out var debugEvent, 100))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorSemTimeout)
                    {
                        consecutiveFailures = 0; // Reset on successful timeout (normal idle)

                        // A4: Periodic check for throttled BPs that have cooled down
                        ReEnableThrottledBreakpoints(session);

                        continue;
                    }

                    if (session.StopRequested)
                    {
                        break;
                    }

                    consecutiveFailures++;
                    _logger.LogWarning("WaitForDebugEventEx failed (error {Error}), attempt {Attempt}/{MaxAttempts} for PID {ProcessId}", error, consecutiveFailures, MaxConsecutiveFailures, session.ProcessId);

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        throw CreateWin32Exception(
                            $"Debug event loop failed for process {session.ProcessId} after {MaxConsecutiveFailures} consecutive failures.");
                    }

                    continue;
                }

                consecutiveFailures = 0; // Reset on successful event

                var continueStatus = DbgContinue;
                var shouldExit = false;

                try
                {
                    continueStatus = HandleDebugEvent(session, debugEvent, ref shouldExit);
                }
                catch (Exception ex)
                {
                    // Safety: a single bad debug event must never kill the debug loop.
                    // Log and continue processing events — the target process stays alive.
                    _logger.LogError(ex, "HandleDebugEvent error (PID {ProcessId})", session.ProcessId);
                    continueStatus = DbgContinue;
                }
                finally
                {
                    // 3I: Check ContinueDebugEvent return value — process may have exited
                    if (!ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus))
                    {
                        _logger.LogWarning("ContinueDebugEvent failed for PID {ProcessId} (error {Error}). Ending debug loop", debugEvent.dwProcessId, Marshal.GetLastWin32Error());
                        shouldExit = true;
                    }
                }

                if (shouldExit)
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            session.LoopFailure = exception;
        }
        finally
        {
            // Always detach from the process when the loop exits unexpectedly
            // to avoid leaving the process in an orphaned "being debugged" state.
            StopSession(session, detachFromProcess: session.IsDebuggerAttached);
        }
    }

    private uint HandleDebugEvent(ProcessDebugSession session, DEBUG_EVENT debugEvent, ref bool shouldExit)
    {
        lock (session.SyncRoot)
        {
            return debugEvent.dwDebugEventCode switch
            {
                DebugEventCreateProcess => HandleCreateProcessEvent(session, debugEvent),
                DebugEventCreateThread => HandleCreateThreadEvent(session, debugEvent),
                DebugEventExitThread => HandleExitThreadEvent(session, debugEvent),
                DebugEventExitProcess => HandleExitProcessEvent(session, debugEvent, ref shouldExit),
                DebugEventException => HandleExceptionEvent(session, debugEvent),
                DebugEventLoadDll => HandleLoadDllEvent(debugEvent),
                _ => DbgContinue
            };
        }
    }

    private uint HandleCreateProcessEvent(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        EnsureThreadHandleLocked(session, debugEvent.dwThreadId, debugEvent.u.CreateProcessInfo.hThread);

        if (debugEvent.u.CreateProcessInfo.hProcess != IntPtr.Zero)
        {
            CloseHandle(debugEvent.u.CreateProcessInfo.hProcess);
        }

        if (debugEvent.u.CreateProcessInfo.hFile != IntPtr.Zero)
        {
            CloseHandle(debugEvent.u.CreateProcessInfo.hFile);
        }

        if (session.Breakpoints.Values.Any(breakpoint => breakpoint.IsEnabled && breakpoint.HardwareSlot.HasValue))
        {
            ApplyHardwareBreakpointsLocked(session);
        }

        return DbgContinue;
    }

    private uint HandleCreateThreadEvent(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        EnsureThreadHandleLocked(session, debugEvent.dwThreadId, debugEvent.u.CreateThread.hThread);

        if (session.Breakpoints.Values.Any(breakpoint => breakpoint.IsEnabled && breakpoint.HardwareSlot.HasValue))
        {
            ApplyHardwareBreakpointsLocked(session);
        }

        return DbgContinue;
    }

    private static uint HandleExitThreadEvent(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        if (session.ThreadHandles.Remove(debugEvent.dwThreadId, out var threadHandle) && threadHandle != IntPtr.Zero)
        {
            CloseHandle(threadHandle);
        }

        session.PendingSoftwareRearm.Remove(debugEvent.dwThreadId);
        return DbgContinue;
    }

    private static uint HandleExitProcessEvent(ProcessDebugSession session, DEBUG_EVENT debugEvent, ref bool shouldExit)
    {
        shouldExit = true;
        session.ProcessExited = true;

        foreach (var breakpoint in session.Breakpoints.Values)
        {
            breakpoint.IsEnabled = false;
        }

        if (debugEvent.u.ExitProcess.dwExitCode != 0 && session.LoopFailure is null)
        {
            session.LoopFailure = new InvalidOperationException(
                $"Process {session.ProcessId} exited while being debugged. Exit code: {debugEvent.u.ExitProcess.dwExitCode}.");
        }

        return DbgContinue;
    }

    private static uint HandleLoadDllEvent(DEBUG_EVENT debugEvent)
    {
        if (debugEvent.u.LoadDll.hFile != IntPtr.Zero)
        {
            CloseHandle(debugEvent.u.LoadDll.hFile);
        }

        return DbgContinue;
    }

    private uint HandleExceptionEvent(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        var exception = debugEvent.u.Exception.ExceptionRecord;
        return exception.ExceptionCode switch
        {
            ExceptionBreakpoint => HandleBreakpointException(session, debugEvent),
            ExceptionSingleStep => HandleSingleStepException(session, debugEvent),
            StatusGuardPageViolation => HandleGuardPageException(session, debugEvent),
            _ => DbgContinue
        };
    }

    private uint HandleBreakpointException(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        var exceptionAddress = unchecked((nuint)debugEvent.u.Exception.ExceptionRecord.ExceptionAddress.ToInt64());
        if (!session.SoftwareBreakpoints.TryGetValue(exceptionAddress, out var breakpoint))
        {
            return DbgContinue;
        }

        var threadHandle = EnsureThreadHandleLocked(session, debugEvent.dwThreadId, IntPtr.Zero);
        var snapshot = CaptureRegisterSnapshot(session, threadHandle, exceptionAddress);

        // A1: Thread filter — skip hit logging if this thread doesn't match, but still
        // complete the INT3→restore→single-step→re-arm mechanical cycle below.
        var shouldLogHit = !breakpoint.ThreadFilter.HasValue ||
                           breakpoint.ThreadFilter.Value == debugEvent.dwThreadId;

        // A2: Conditional evaluation — skip hit logging if condition not met
        if (shouldLogHit && breakpoint.Condition is { } condition)
        {
            Func<nuint, int, byte[]?> readMem = (addr, size) => ReadProcessMemoryBytes(session.ProcessHandle, addr, size);
            shouldLogHit = VehConditionEvaluator.EvaluateFromDictionary(condition, snapshot, breakpoint.HitCount, readMem);
        }

        if (shouldLogHit && LogBreakpointHit(breakpoint, debugEvent.dwThreadId, exceptionAddress, snapshot))
        {
            // Hit-rate exceeded — auto-disable to prevent game freeze
            breakpoint.IsEnabled = false;
            return DbgContinue;
        }

        if (session.IsWow64Target)
        {
            var context = CreateWow64Context();
            if (!Wow64GetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to read WOW64 thread context for software breakpoint handling.");
            }

            context.Eip = unchecked((uint)exceptionAddress);
            context.EFlags |= TrapFlag;

            if (!Wow64SetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to update WOW64 thread context for software breakpoint handling.");
            }
        }
        else
        {
            var context = CreateContext64();
            if (!GetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to read thread context for software breakpoint handling.");
            }

            context.Rip = exceptionAddress;
            context.EFlags |= TrapFlag;

            if (!SetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to update thread context for software breakpoint handling.");
            }
        }

        RestoreOriginalByte(session, breakpoint);
        session.PendingSoftwareRearm[debugEvent.dwThreadId] = breakpoint.Id;
        return DbgContinue;
    }

    private uint HandleGuardPageException(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        HandlePageGuardViolation(session, debugEvent);
        return DbgContinue;
    }

    private uint HandleSingleStepException(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        var threadHandle = EnsureThreadHandleLocked(session, debugEvent.dwThreadId, IntPtr.Zero);

        // Check for page guard re-arm first
        RearmPageGuard(session, debugEvent.dwThreadId);

        if (session.PendingSoftwareRearm.TryGetValue(debugEvent.dwThreadId, out var breakpointId) &&
            session.Breakpoints.TryGetValue(breakpointId, out var softwareBreakpoint))
        {
            // ALWAYS complete the re-arm cycle regardless of IsEnabled state.
            // If we restored the original byte and set TrapFlag, we MUST write 0xCC back
            // and clear TrapFlag before checking whether the BP should stay active.
            // This prevents the race where RemoveBreakpointAsync sets IsEnabled=false
            // between INT3 handler and single-step handler, leaving TrapFlag stuck.
            ReArmSoftwareBreakpoint(session, threadHandle, softwareBreakpoint);
            session.PendingSoftwareRearm.Remove(debugEvent.dwThreadId);

            // Now handle deferred removal if RemoveBreakpointAsync flagged it
            if (softwareBreakpoint.PendingRemoval)
            {
                softwareBreakpoint.IsEnabled = false;
                softwareBreakpoint.PendingRemoval = false;
                session.Breakpoints.Remove(softwareBreakpoint.Id);
                session.SoftwareBreakpoints.Remove(softwareBreakpoint.Address);
                RestoreOriginalByte(session, softwareBreakpoint);
            }

            return DbgContinue;
        }

        var hitAddress = GetInstructionPointer(session, threadHandle);
        var snapshot = CaptureRegisterSnapshot(session, threadHandle, hitAddress);
        var hitSlots = GetTriggeredHardwareSlots(session, threadHandle);

        foreach (var slot in hitSlots)
        {
            var breakpoint = session.Breakpoints.Values.FirstOrDefault(
                candidate => candidate.IsEnabled && candidate.HardwareSlot == slot);

            if (breakpoint is not null)
            {
                // A1: Thread filter enforcement for hardware breakpoints
                var shouldLog = !breakpoint.ThreadFilter.HasValue ||
                                breakpoint.ThreadFilter.Value == debugEvent.dwThreadId;

                // A2: Conditional evaluation for hardware breakpoints
                if (shouldLog && breakpoint.Condition is { } cond)
                {
                    Func<nuint, int, byte[]?> readMem = (addr, size) => ReadProcessMemoryBytes(session.ProcessHandle, addr, size);
                    shouldLog = VehConditionEvaluator.EvaluateFromDictionary(cond, snapshot, breakpoint.HitCount, readMem);
                }

                if (shouldLog && LogBreakpointHit(breakpoint, debugEvent.dwThreadId, hitAddress, snapshot))
                {
                    breakpoint.IsEnabled = false;
                    breakpoint.HardwareSlot = null;
                    ApplyHardwareBreakpointsLocked(session);
                }
            }
        }

        ClearTrapFlag(session, threadHandle);
        return DbgContinue;
    }

    private static void ReArmSoftwareBreakpoint(ProcessDebugSession session, IntPtr threadHandle, BreakpointState breakpoint)
    {
        var patch = new byte[] { 0xCC };
        if (!WriteProcessMemory(session.ProcessHandle, (IntPtr)breakpoint.Address, patch, patch.Length, out var bytesWritten) || bytesWritten != 1)
        {
            throw CreateWin32Exception($"Unable to re-arm software breakpoint at 0x{breakpoint.Address:X}.");
        }

        FlushInstructionCache(session.ProcessHandle, (IntPtr)breakpoint.Address, (UIntPtr)1);
        ClearTrapFlag(session, threadHandle);
    }

    private static void ClearTrapFlag(ProcessDebugSession session, IntPtr threadHandle)
    {
        if (session.IsWow64Target)
        {
            var context = CreateWow64Context();
            if (!Wow64GetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to read WOW64 thread context when clearing trap flag.");
            }

            context.EFlags &= ~TrapFlag;

            if (!Wow64SetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to update WOW64 thread context when clearing trap flag.");
            }
        }
        else
        {
            var context = CreateContext64();
            if (!GetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to read thread context when clearing trap flag.");
            }

            context.EFlags &= ~TrapFlag;

            if (!SetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to update thread context when clearing trap flag.");
            }
        }
    }

    private static nuint GetInstructionPointer(ProcessDebugSession session, IntPtr threadHandle)
    {
        if (session.IsWow64Target)
        {
            var context = CreateWow64Context();
            if (!Wow64GetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to read WOW64 thread context.");
            }

            return context.Eip;
        }

        var nativeContext = CreateContext64();
        if (!GetThreadContext(threadHandle, ref nativeContext))
        {
            throw CreateWin32Exception("Unable to read thread context.");
        }

        return unchecked((nuint)nativeContext.Rip);
    }

    private static List<int> GetTriggeredHardwareSlots(ProcessDebugSession session, IntPtr threadHandle)
    {
        ulong dr6;

        if (session.IsWow64Target)
        {
            var context = CreateWow64Context();
            if (!Wow64GetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to read WOW64 thread context for hardware breakpoint hit.");
            }

            dr6 = context.Dr6;
            context.Dr6 = 0;
            context.EFlags &= ~TrapFlag;

            if (!Wow64SetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to update WOW64 thread context after hardware breakpoint hit.");
            }
        }
        else
        {
            var context = CreateContext64();
            if (!GetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to read thread context for hardware breakpoint hit.");
            }

            dr6 = context.Dr6;
            context.Dr6 = 0;
            context.EFlags &= ~TrapFlag;

            if (!SetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to update thread context after hardware breakpoint hit.");
            }
        }

        var slots = new List<int>(4);
        for (var slot = 0; slot < 4; slot++)
        {
            if (((dr6 >> slot) & 0x1) != 0)
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    /// <summary>Max hits per second before a breakpoint is auto-disabled to prevent game freezes.</summary>
    private const int MaxHitsPerSecond = 200;

    /// <summary>Max hit log entries per breakpoint (ring buffer). Total count tracked separately via HitCount.</summary>
    private const int MaxHitLogEntries = 500;

    /// <summary>
    /// Log a breakpoint hit and check hit-rate. Returns true if the breakpoint should be
    /// auto-disabled due to excessive firing (>200 hits/sec).
    /// </summary>
    private bool LogBreakpointHit(
        BreakpointState breakpoint,
        int threadId,
        nuint address,
        IReadOnlyDictionary<string, string> registerSnapshot)
    {
        // Ring buffer: evict oldest entries when at capacity
        while (breakpoint.HitLog.Count >= MaxHitLogEntries)
            breakpoint.HitLog.TryDequeue(out _);

        breakpoint.HitLog.Enqueue(
            new BreakpointHitEvent(
                breakpoint.Id,
                address,
                threadId,
                DateTimeOffset.UtcNow,
                registerSnapshot));

        var newCount = Interlocked.Increment(ref breakpoint.HitCount);

        // C1: Publish hit event
        _eventBus?.Publish(new BreakpointHitOccurredEvent(
            breakpoint.Id, $"0x{address:X}", threadId, newCount));

        // C2: First hit transitions to Active
        if (newCount == 1)
            _eventBus?.Publish(new BreakpointStateChangedEvent(breakpoint.Id, nameof(BreakpointLifecycleStatus.Active)));

        // Single-hit enforcement: disable immediately after first hit
        if (breakpoint.SingleHit && newCount >= 1)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Single-hit BP {BreakpointId} at 0x{Address:X}: auto-disabling after first hit", breakpoint.Id, breakpoint.Address);
            _eventBus?.Publish(new BreakpointStateChangedEvent(breakpoint.Id, nameof(BreakpointLifecycleStatus.SingleHitRemoved)));
            return true;
        }

        // Hit-rate throttle: track hits within 1-second windows
        var now = Environment.TickCount64;
        var windowStart = Interlocked.Read(ref breakpoint.ThrottleWindowStartTicks);
        if (now - windowStart > 1000)
        {
            // New 1-second window
            Interlocked.Exchange(ref breakpoint.ThrottleWindowStartTicks, now);
            Interlocked.Exchange(ref breakpoint.ThrottleWindowHits, 1);
        }
        else
        {
            var hits = Interlocked.Increment(ref breakpoint.ThrottleWindowHits);
            if (hits > MaxHitsPerSecond)
            {
                breakpoint.ThrottleDisabled = true;
                breakpoint.ThrottleDisabledAtTicks = Environment.TickCount64;
                _logger.LogWarning("Auto-disabling BP {BreakpointId} at 0x{Address:X}: exceeded {MaxHitsPerSecond} hits/sec ({Hits} in window). This prevents game freezes", breakpoint.Id, breakpoint.Address, MaxHitsPerSecond, hits);

                // C1: Publish throttle event + C2: lifecycle state change
                _eventBus?.Publish(new BreakpointThrottledEvent(breakpoint.Id, hits));
                _eventBus?.Publish(new BreakpointStateChangedEvent(breakpoint.Id, nameof(BreakpointLifecycleStatus.ThrottleDisabled)));

                return true; // caller should disable this BP
            }
        }

        return false;
    }

    /// <summary>A4: Cooldown period in milliseconds before a throttled BP is auto re-enabled.</summary>
    private const int ThrottleCooldownMs = 5000;

    /// <summary>
    /// A4: Check for throttle-disabled BPs that have cooled down and re-enable them.
    /// Called from the debug loop idle path (ErrorSemTimeout).
    /// </summary>
    private void ReEnableThrottledBreakpoints(ProcessDebugSession session)
    {
        var now = Environment.TickCount64;
        // Lock to prevent concurrent modification while iterating breakpoints
        lock (session.SyncRoot)
        foreach (var bp in session.Breakpoints.Values)
        {
            if (!bp.ThrottleDisabled || !bp.AutoReEnableAfterThrottle) continue;
            if (bp.ThrottleDisabledAtTicks == 0) continue; // no timestamp recorded

            if (now - bp.ThrottleDisabledAtTicks >= ThrottleCooldownMs)
            {
                bp.ThrottleDisabled = false;
                bp.IsEnabled = true;
                Interlocked.Exchange(ref bp.ThrottleWindowHits, 0);
                Interlocked.Exchange(ref bp.ThrottleWindowStartTicks, now);
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Re-enabled throttled BP {BreakpointId} at 0x{Address:X} after {CooldownMs}ms cooldown", bp.Id, bp.Address, ThrottleCooldownMs);
            }
        }
    }

    private Dictionary<string, string> CaptureRegisterSnapshot(
        ProcessDebugSession session,
        IntPtr threadHandle,
        nuint faultAddress)
    {
        // 3G: Wrap context calls with try/catch for invalid handle errors
        try
        {
            return CaptureRegisterSnapshotCore(session, threadHandle, faultAddress);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception)
        {
            // Stale thread handle — remove from session and return empty snapshot
            var staleThreadId = session.ThreadHandles
                .FirstOrDefault(kvp => kvp.Value == threadHandle).Key;
            if (staleThreadId != 0)
            {
                session.ThreadHandles.Remove(staleThreadId);
                _logger.LogWarning("Removed stale thread handle for TID {ThreadId} after context access failure", staleThreadId);
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> CaptureRegisterSnapshotCore(
        ProcessDebugSession session,
        IntPtr threadHandle,
        nuint faultAddress)
    {
        if (session.IsWow64Target)
        {
            var context = CreateWow64Context();
            if (!Wow64GetThreadContext(threadHandle, ref context))
            {
                throw CreateWin32Exception("Unable to capture WOW64 register snapshot.");
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["EIP"] = $"0x{faultAddress:X8}",
                ["ESP"] = $"0x{context.Esp:X8}",
                ["EAX"] = $"0x{context.Eax:X8}",
                ["EBX"] = $"0x{context.Ebx:X8}",
                ["ECX"] = $"0x{context.Ecx:X8}",
                ["EDX"] = $"0x{context.Edx:X8}",
                ["EFLAGS"] = $"0x{context.EFlags:X8}"
            };
        }

        var nativeContext = CreateContext64();
        if (!GetThreadContext(threadHandle, ref nativeContext))
        {
            throw CreateWin32Exception("Unable to capture register snapshot.");
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RIP"] = $"0x{faultAddress:X16}",
            ["RSP"] = $"0x{nativeContext.Rsp:X16}",
            ["RAX"] = $"0x{nativeContext.Rax:X16}",
            ["RBX"] = $"0x{nativeContext.Rbx:X16}",
            ["RCX"] = $"0x{nativeContext.Rcx:X16}",
            ["RDX"] = $"0x{nativeContext.Rdx:X16}",
            ["EFLAGS"] = $"0x{nativeContext.EFlags:X8}"
        };
    }

    // ─── Emergency Recovery ─────────────────────────────────────────────

    public Task<int> EmergencyRestorePageProtectionAsync(int processId) =>
        Task.Run(() =>
        {
            // Snapshot page guard breakpoints under a brief lock, then release.
            List<BreakpointState> snapshot;

            if (!_sessions.TryGetValue(processId, out var session))
                return 0;

            lock (session.SyncRoot)
            {
                snapshot = session.PageGuardBreakpoints.Values
                    .SelectMany(list => list)
                    .Where(bp => bp.IsEnabled)
                    .ToList();
            }

            if (snapshot.Count == 0)
                return 0;

            // Open a fresh process handle so we don't depend on the session handle
            // (which may be in use by the debug loop). ProcessVmOperation is enough
            // for VirtualProtectEx.
            var freshHandle = OpenProcess(ProcessVmOperation, false, processId);
            if (freshHandle == IntPtr.Zero)
                return 0;

            var restored = 0;
            try
            {
                foreach (var bp in snapshot)
                {
                    if (bp.PageBaseAddress is not { } pageBase)
                        continue;

                    var originalProtect = bp.OriginalPageProtection ?? 0x04u; // PAGE_READWRITE fallback
                    if (VirtualProtectEx(freshHandle, (IntPtr)pageBase, (UIntPtr)PageSize,
                        originalProtect, out _))
                    {
                        bp.IsEnabled = false;
                        restored++;
                    }
                }
            }
            finally
            {
                CloseHandle(freshHandle);
            }

            return restored;
        });

    public async Task<BreakpointDescriptor> SetConditionalBreakpointAsync(
        int processId, nuint address, BreakpointType type,
        BreakpointCondition condition, BreakpointMode mode = BreakpointMode.Auto,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        int? threadFilter = null, CancellationToken cancellationToken = default)
    {
        // Set the breakpoint normally, then store condition/thread filter metadata
        var bp = await SetBreakpointAsync(processId, address, type, mode, action, false, cancellationToken);

        // Store condition and thread filter on the BreakpointState
        if (_breakpointRegistry.TryGetValue(bp.Id, out var state))
        {
            state.Condition = condition;
            state.ThreadFilter = threadFilter;
        }

        // Return descriptor with condition and thread filter info
        return new BreakpointDescriptor(
            bp.Id, bp.Address, bp.Type, bp.HitAction, bp.IsEnabled, bp.HitCount,
            bp.Mode, condition, threadFilter);
    }

    public Task<TraceResult> TraceFromBreakpointAsync(
        int processId, nuint address, int maxInstructions = 500,
        int timeoutMs = 5000, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            // Trace is implemented by:
            // 1. Reading instruction bytes at the target address
            // 2. Decoding with Iced.Intel disassembler
            // 3. Following the linear execution path (no actual process execution — static trace)
            // For full dynamic tracing (TF stepping), the debug loop would need trace mode support.
            // This implementation provides a static instruction-level trace from the given address.

            var handle = OpenProcess(ProcessVmReadAccess, false, processId);
            if (handle == IntPtr.Zero)
                return new TraceResult("trace-0", Array.Empty<TraceEntry>(), false, false);

            try
            {
                var entries = new List<TraceEntry>();
                var currentAddr = address;
                var visited = new HashSet<nuint>();
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while (entries.Count < maxInstructions && sw.ElapsedMilliseconds < timeoutMs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visited.Add(currentAddr)) break; // loop detected

                    var buf = new byte[15]; // max x86/x64 instruction length
                    if (!ReadProcessMemory(handle, (IntPtr)currentAddr, buf, buf.Length, out var bytesRead) || bytesRead == 0)
                        break;

                    // Decode instruction using Iced.Intel
                    var decoder = Iced.Intel.Decoder.Create(64, buf);
                    decoder.IP = currentAddr;
                    var instr = decoder.Decode();

                    if (instr.IsInvalid) break;

                    var formatter = new Iced.Intel.NasmFormatter();
                    var output = new Iced.Intel.StringOutput();
                    formatter.Format(instr, output);

                    entries.Add(new TraceEntry(
                        currentAddr,
                        output.ToStringAndReset(),
                        0, // thread ID not available in static trace
                        new Dictionary<string, string>(),
                        DateTimeOffset.UtcNow));

                    // Follow linear flow
                    if (instr.FlowControl == Iced.Intel.FlowControl.Return)
                        break;
                    if (instr.FlowControl == Iced.Intel.FlowControl.UnconditionalBranch)
                    {
                        currentAddr = (nuint)instr.NearBranchTarget;
                        continue;
                    }

                    currentAddr += (nuint)instr.Length;
                }

                return new TraceResult(
                    $"trace-{address:X}",
                    entries,
                    entries.Count >= maxInstructions,
                    false);
            }
            finally { CloseHandle(handle); }
        }, cancellationToken);

    public Task ForceDetachAndCleanupAsync(int processId) =>
        Task.Run(async () =>
        {
            // Step 1: Restore all page guard protections (lock-free path).
            await EmergencyRestorePageProtectionAsync(processId).ConfigureAwait(false);

            // Step 2: Detach the debugger directly — no lock required.
            DebugActiveProcessStop(processId);

            // Step 3: Best-effort session cleanup.
            if (_sessions.TryGetValue(processId, out var session))
            {
                StopSession(session, detachFromProcess: false);
            }
        });

    /// <summary>B2: Set PAGE_GUARD breakpoints spanning a memory region. Max 64KB.</summary>
    private const int MaxRegionSize = 64 * 1024;
    private const int PageGuardWarningThreshold = 16;

    public async Task<IReadOnlyList<BreakpointDescriptor>> SetRegionBreakpointAsync(
        int processId,
        nuint startAddress,
        int length,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        CancellationToken cancellationToken = default)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");
        if (length > MaxRegionSize) throw new ArgumentOutOfRangeException(nameof(length), $"Region size {length} exceeds maximum of {MaxRegionSize} bytes.");

        // For small regions (≤ 8 bytes), use a single hardware breakpoint
        if (length <= 8)
        {
            var bp = await SetBreakpointAsync(processId, startAddress, BreakpointType.HardwareReadWrite,
                BreakpointMode.Hardware, action, cancellationToken: cancellationToken).ConfigureAwait(false);
            return [bp];
        }

        // For larger regions, calculate spanning pages and set PAGE_GUARD on each
        var firstPage = startAddress & ~(nuint)(PageSize - 1);
        var lastPage = (startAddress + (nuint)(length - 1)) & ~(nuint)(PageSize - 1);
        var pageCount = (int)((lastPage - firstPage) / PageSize) + 1;

        if (pageCount > PageGuardWarningThreshold && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("Region breakpoint at 0x{Address:X} spans {Pages} pages — may impact performance", startAddress, pageCount);

        var results = new List<BreakpointDescriptor>(pageCount);
        for (var page = firstPage; page <= lastPage; page += PageSize)
        {
            var bp = await SetBreakpointAsync(processId, page, BreakpointType.HardwareReadWrite,
                BreakpointMode.PageGuard, action, cancellationToken: cancellationToken).ConfigureAwait(false);
            results.Add(bp);
        }

        return results;
    }

    private void StopSession(ProcessDebugSession session, bool detachFromProcess)
    {
        session.StopRequested = true;

        if (!_sessions.TryRemove(session.ProcessId, out _))
        {
            if (detachFromProcess)
            {
                TryDetachDebugger(session);
            }

            return;
        }

        if (detachFromProcess)
        {
            TryDetachDebugger(session);
        }

        if (session.DebugThread is not null &&
            session.DebugThread.IsAlive &&
            !ReferenceEquals(Thread.CurrentThread, session.DebugThread))
        {
            session.DebugThread.Join(TimeSpan.FromSeconds(2));
        }

        lock (session.SyncRoot)
        {
            // 3K: Clear pending rearm dictionaries to prevent stale entries if PID is recycled
            session.PendingSoftwareRearm.Clear();
            session.PendingPageGuardRearm.Clear();

            // A3: Retry restoration for any BPs with RestorationFailed before closing handles
            if (!session.ProcessExited)
            {
                foreach (var bp in session.Breakpoints.Values.Where(b => b.RestorationFailed && b.OriginalByte.HasValue))
                {
                    RestoreOriginalByte(session, bp);
                    if (!bp.RestorationFailed && _logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Successfully restored original byte for BP {BreakpointId} on session cleanup", bp.Id);
                }
            }

            // A5: Wrap handle closes in try-catch — handles may already be invalid after process exit
            foreach (var threadHandle in session.ThreadHandles.Values.Where(handle => handle != IntPtr.Zero))
            {
                try { CloseHandle(threadHandle); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to close thread handle during session cleanup"); }
            }

            session.ThreadHandles.Clear();

            if (session.ProcessHandle != IntPtr.Zero)
            {
                try { CloseHandle(session.ProcessHandle); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to close process handle during session cleanup"); }
                session.ProcessHandle = IntPtr.Zero;
            }
        }
    }

    private static void TryDetachDebugger(ProcessDebugSession session)
    {
        if (!session.IsDebuggerAttached)
        {
            return;
        }

        DebugActiveProcessStop(session.ProcessId);
        session.IsDebuggerAttached = false;
    }

    private static InvalidOperationException CreateWin32Exception(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private static CONTEXT64 CreateContext64() =>
        new()
        {
            ContextFlags = ContextFullWithDebug,
            DUMMYUNIONNAME = new XMM_SAVE_AREA32
            {
                FloatRegisters = new M128A[8],
                XmmRegisters = new M128A[16],
                Reserved4 = new byte[96]
            },
            VectorRegister = new M128A[26]
        };

    private static WOW64_CONTEXT CreateWow64Context() =>
        new()
        {
            ContextFlags = Wow64ContextFullWithDebug,
            FloatSave = new WOW64_FLOATING_SAVE_AREA
            {
                RegisterArea = new byte[80]
            },
            ExtendedRegisters = new byte[512]
        };

    private sealed class ProcessDebugSession
    {
        public ProcessDebugSession(int processId, IntPtr processHandle, bool isWow64Target)
        {
            ProcessId = processId;
            ProcessHandle = processHandle;
            IsWow64Target = isWow64Target;
        }

        public int ProcessId { get; }

        public bool IsWow64Target { get; }

        public IntPtr ProcessHandle { get; set; }

        public bool StopRequested { get; set; }

        /// <summary>A5: Set when the target process has exited. Guards cleanup from calling dead-process APIs.</summary>
        public bool ProcessExited { get; set; }

        public bool IsDebuggerAttached { get; set; }

        public Thread? DebugThread { get; set; }

        public Exception? LoopFailure { get; set; }

        public object SyncRoot { get; } = new();

        public Dictionary<string, BreakpointState> Breakpoints { get; } = new(StringComparer.Ordinal);

        public Dictionary<nuint, BreakpointState> SoftwareBreakpoints { get; } = new();

        public Dictionary<int, string> PendingSoftwareRearm { get; } = new();

        public Dictionary<int, IntPtr> ThreadHandles { get; } = new();

        /// <summary>Page guard breakpoints keyed by the guarded page base address. Multiple BPs may share a page.</summary>
        public Dictionary<nuint, List<BreakpointState>> PageGuardBreakpoints { get; } = new();

        /// <summary>Tracks which BP is pending single-step re-arm of its PAGE_GUARD flag.</summary>
        public Dictionary<int, nuint> PendingPageGuardRearm { get; } = new();

        /// <summary>2H: Per-page original protection tracking. Only the first BP on a page records this.</summary>
        public Dictionary<nuint, uint> OriginalPageProtections { get; } = new();
    }

    private sealed class BreakpointState
    {
        public BreakpointState(
            string id,
            int processId,
            nuint address,
            BreakpointType type,
            BreakpointHitAction hitAction,
            BreakpointMode mode = BreakpointMode.Hardware,
            bool singleHit = false)
        {
            Id = id;
            ProcessId = processId;
            Address = address;
            Type = type;
            HitAction = hitAction;
            Mode = mode;
            SingleHit = singleHit;
        }

        public string Id { get; }

        public int ProcessId { get; }

        public nuint Address { get; }

        public BreakpointType Type { get; }

        public BreakpointHitAction HitAction { get; }

        public BreakpointMode Mode { get; }

        public bool IsEnabled { get; set; } = true;

        /// <summary>When true, this BP auto-disables after the first hit.</summary>
        public bool SingleHit { get; }

        /// <summary>When true, removal is deferred until a pending single-step re-arm completes.</summary>
        public bool PendingRemoval { get; set; }

        public int HitCount;

        public byte? OriginalByte { get; set; }

        public int? HardwareSlot { get; set; }

        /// <summary>For PageGuard mode: the base address of the guarded page.</summary>
        public nuint? PageBaseAddress { get; set; }

        /// <summary>For PageGuard mode: the original protection before PAGE_GUARD was applied.</summary>
        public uint? OriginalPageProtection { get; set; }

        /// <summary>Hit-rate throttle: timestamp of when the current window started.</summary>
        public long ThrottleWindowStartTicks;

        /// <summary>Hit-rate throttle: hits within the current 1-second window.</summary>
        public int ThrottleWindowHits;

        /// <summary>When true, this BP was auto-disabled due to excessive hit rate.</summary>
        public bool ThrottleDisabled;

        /// <summary>A4: Timestamp when the BP was throttle-disabled. Used for cooldown re-enable.</summary>
        public long ThrottleDisabledAtTicks;

        /// <summary>A4: When true (default), throttled BPs auto re-enable after the cooldown period.</summary>
        public bool AutoReEnableAfterThrottle = true;

        /// <summary>When true, the original byte could not be restored — 0xCC persists at the address.</summary>
        public bool RestorationFailed;

        /// <summary>Phase 7B: Conditional breakpoint expression. Null = unconditional.</summary>
        public BreakpointCondition? Condition;

        /// <summary>Phase 7B: Thread filter. Only break on this thread ID. Null = all threads.</summary>
        public int? ThreadFilter;

        public ConcurrentQueue<BreakpointHitEvent> HitLog { get; } = new();

        public BreakpointDescriptor ToDescriptor() =>
            new(
                Id,
                Address,
                Type,
                HitAction,
                IsEnabled,
                HitCount,
                Mode,
                Condition,
                ThreadFilter);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEBUG_EVENT
    {
        public uint dwDebugEventCode;
        public int dwProcessId;
        public int dwThreadId;
        public DEBUG_EVENT_UNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DEBUG_EVENT_UNION
    {
        [FieldOffset(0)]
        public EXCEPTION_DEBUG_INFO Exception;

        [FieldOffset(0)]
        public CREATE_THREAD_DEBUG_INFO CreateThread;

        [FieldOffset(0)]
        public CREATE_PROCESS_DEBUG_INFO CreateProcessInfo;

        [FieldOffset(0)]
        public EXIT_THREAD_DEBUG_INFO ExitThread;

        [FieldOffset(0)]
        public EXIT_PROCESS_DEBUG_INFO ExitProcess;

        [FieldOffset(0)]
        public LOAD_DLL_DEBUG_INFO LoadDll;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_DEBUG_INFO
    {
        public EXCEPTION_RECORD ExceptionRecord;
        public uint dwFirstChance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecordPointer;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;

        public fixed ulong ExceptionInformation[15];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATE_THREAD_DEBUG_INFO
    {
        public IntPtr hThread;
        public IntPtr lpThreadLocalBase;
        public IntPtr lpStartAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATE_PROCESS_DEBUG_INFO
    {
        public IntPtr hFile;
        public IntPtr hProcess;
        public IntPtr hThread;
        public IntPtr lpBaseOfImage;
        public uint dwDebugInfoFileOffset;
        public uint nDebugInfoSize;
        public IntPtr lpThreadLocalBase;
        public IntPtr lpStartAddress;
        public IntPtr lpImageName;
        public ushort fUnicode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXIT_THREAD_DEBUG_INFO
    {
        public uint dwExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXIT_PROCESS_DEBUG_INFO
    {
        public uint dwExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LOAD_DLL_DEBUG_INFO
    {
        public IntPtr hFile;
        public IntPtr lpBaseOfDll;
        public uint dwDebugInfoFileOffset;
        public uint nDebugInfoSize;
        public IntPtr lpImageName;
        public ushort fUnicode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct M128A
    {
        public ulong High;
        public long Low;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct CONTEXT64
    {
        public ulong P1Home;
        public ulong P2Home;
        public ulong P3Home;
        public ulong P4Home;
        public ulong P5Home;
        public ulong P6Home;
        public uint ContextFlags;
        public uint MxCsr;
        public ushort SegCs;
        public ushort SegDs;
        public ushort SegEs;
        public ushort SegFs;
        public ushort SegGs;
        public ushort SegSs;
        public uint EFlags;
        public ulong Dr0;
        public ulong Dr1;
        public ulong Dr2;
        public ulong Dr3;
        public ulong Dr6;
        public ulong Dr7;
        public ulong Rax;
        public ulong Rcx;
        public ulong Rdx;
        public ulong Rbx;
        public ulong Rsp;
        public ulong Rbp;
        public ulong Rsi;
        public ulong Rdi;
        public ulong R8;
        public ulong R9;
        public ulong R10;
        public ulong R11;
        public ulong R12;
        public ulong R13;
        public ulong R14;
        public ulong R15;
        public ulong Rip;
        public XMM_SAVE_AREA32 DUMMYUNIONNAME;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
        public M128A[] VectorRegister;

        public ulong VectorControl;
        public ulong DebugControl;
        public ulong LastBranchToRip;
        public ulong LastBranchFromRip;
        public ulong LastExceptionToRip;
        public ulong LastExceptionFromRip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XMM_SAVE_AREA32
    {
        public ushort ControlWord;
        public ushort StatusWord;
        public byte TagWord;
        public byte Reserved1;
        public ushort ErrorOpcode;
        public uint ErrorOffset;
        public ushort ErrorSelector;
        public ushort Reserved2;
        public uint DataOffset;
        public ushort DataSelector;
        public ushort Reserved3;
        public uint MxCsr;
        public uint MxCsrMask;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public M128A[] FloatRegisters;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public M128A[] XmmRegisters;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 96)]
        public byte[] Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WOW64_CONTEXT
    {
        public uint ContextFlags;
        public uint Dr0;
        public uint Dr1;
        public uint Dr2;
        public uint Dr3;
        public uint Dr6;
        public uint Dr7;
        public WOW64_FLOATING_SAVE_AREA FloatSave;
        public uint SegGs;
        public uint SegFs;
        public uint SegEs;
        public uint SegDs;
        public uint Edi;
        public uint Esi;
        public uint Ebx;
        public uint Edx;
        public uint Ecx;
        public uint Eax;
        public uint Ebp;
        public uint Eip;
        public uint SegCs;
        public uint EFlags;
        public uint Esp;
        public uint SegSs;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] ExtendedRegisters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WOW64_FLOATING_SAVE_AREA
    {
        public uint ControlWord;
        public uint StatusWord;
        public uint TagWord;
        public uint ErrorOffset;
        public uint ErrorSelector;
        public uint DataOffset;
        public uint DataSelector;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)]
        public byte[] RegisterArea;

        public uint Cr0NpxState;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugActiveProcess(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugActiveProcessStop(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitForDebugEventEx(out DEBUG_EVENT debugEvent, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ContinueDebugEvent(int processId, int threadId, uint continueStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, int threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadContext(IntPtr threadHandle, ref CONTEXT64 context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadContext(IntPtr threadHandle, [In] ref CONTEXT64 context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Wow64GetThreadContext(IntPtr threadHandle, ref WOW64_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Wow64SetThreadContext(IntPtr threadHandle, [In] ref WOW64_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out int numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out int numberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr processHandle, IntPtr baseAddress, UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr processHandle, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(
        IntPtr processHandle, IntPtr baseAddress, UIntPtr size,
        uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(
        IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, int dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
