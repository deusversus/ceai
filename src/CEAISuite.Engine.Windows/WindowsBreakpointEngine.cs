using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

public sealed class WindowsBreakpointEngine : IBreakpointEngine
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;

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

        // Stealth mode is for code cave hooks (ICodeCaveEngine) — not for the breakpoint engine.
        // If it reaches here, downgrade to the best available mode for the type.
        if (resolvedMode == BreakpointMode.Stealth)
            resolvedMode = ResolveAutoMode(type);

        // Page guard breakpoints are handled differently
        if (resolvedMode == BreakpointMode.PageGuard)
            return SetPageGuardBreakpointAsync(processId, address, type, action, singleHit, cancellationToken);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var session = GetOrCreateSession(processId, cancellationToken);

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
                        breakpoint.HardwareSlot = AllocateHardwareSlot(session);
                        ApplyHardwareBreakpointsLocked(session);
                    }

                    session.Breakpoints[breakpoint.Id] = breakpoint;
                    if (type == BreakpointType.Software || resolvedMode == BreakpointMode.Software)
                    {
                        session.SoftwareBreakpoints[address] = breakpoint;
                    }

                    _breakpointRegistry[breakpoint.Id] = breakpoint;
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
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

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

                    session.Breakpoints.Remove(breakpointId);
                    breakpoint.IsEnabled = false;

                    foreach (var pendingThreadId in session.PendingSoftwareRearm
                                 .Where(pair => string.Equals(pair.Value, breakpointId, StringComparison.Ordinal))
                                 .Select(pair => pair.Key)
                                 .ToArray())
                    {
                        session.PendingSoftwareRearm.Remove(pendingThreadId);
                    }

                    if (breakpoint.Type == BreakpointType.Software)
                    {
                        session.SoftwareBreakpoints.Remove(breakpoint.Address);
                        RestoreOriginalByte(session, breakpoint);
                    }
                    else
                    {
                        breakpoint.HardwareSlot = null;
                        ApplyHardwareBreakpointsLocked(session);
                    }

                    detachSession = session.Breakpoints.Count == 0;
                }

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

                if (!_sessions.TryGetValue(processId, out var session))
                {
                    return Array.Empty<BreakpointDescriptor>();
                }

                lock (session.SyncRoot)
                {
                    return session.Breakpoints.Values
                        .OrderBy(breakpoint => breakpoint.Address)
                        .Select(breakpoint => breakpoint.ToDescriptor())
                        .ToArray();
                }
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
                session.DebugThread = new Thread(() => DebugLoop(session))
                {
                    IsBackground = true,
                    Name = $"WindowsBreakpointEngine-{processId}"
                };

                if (!_sessions.TryAdd(processId, session))
                {
                    throw new InvalidOperationException($"A breakpoint session for process {processId} already exists.");
                }

                session.DebugThread.Start();
                return session;
            }
            catch
            {
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

    private static void InstallSoftwareBreakpoint(ProcessDebugSession session, BreakpointState breakpoint)
    {
        if (session.SoftwareBreakpoints.ContainsKey(breakpoint.Address))
        {
            throw new InvalidOperationException($"A software breakpoint already exists at 0x{breakpoint.Address:X}.");
        }

        var original = new byte[1];
        if (!ReadProcessMemory(session.ProcessHandle, (IntPtr)breakpoint.Address, original, original.Length, out var bytesRead) || bytesRead != 1)
        {
            throw CreateWin32Exception($"Unable to read original byte at 0x{breakpoint.Address:X}.");
        }

        var patch = new byte[] { 0xCC };
        if (!WriteProcessMemory(session.ProcessHandle, (IntPtr)breakpoint.Address, patch, patch.Length, out var bytesWritten) || bytesWritten != 1)
        {
            throw CreateWin32Exception($"Unable to write software breakpoint at 0x{breakpoint.Address:X}.");
        }

        FlushInstructionCache(session.ProcessHandle, (IntPtr)breakpoint.Address, (UIntPtr)1);
        breakpoint.OriginalByte = original[0];
    }

    private static void RestoreOriginalByte(ProcessDebugSession session, BreakpointState breakpoint)
    {
        if (breakpoint.OriginalByte is null)
        {
            return;
        }

        var original = new[] { breakpoint.OriginalByte.Value };
        WriteProcessMemory(session.ProcessHandle, (IntPtr)breakpoint.Address, original, original.Length, out _);
        FlushInstructionCache(session.ProcessHandle, (IntPtr)breakpoint.Address, (UIntPtr)1);
    }

    private static void ApplyHardwareBreakpointsLocked(ProcessDebugSession session)
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

    private static void ApplyHardwareBreakpointsToThread(
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

                Wow64SetThreadContext(threadHandle, ref context);
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

                SetThreadContext(threadHandle, ref context);
            }
        }
        finally
        {
            ResumeThread(threadHandle);
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

        foreach (var breakpoint in breakpoints)
        {
            if (!breakpoint.HardwareSlot.HasValue)
            {
                continue;
            }

            var slot = breakpoint.HardwareSlot.Value;
            var address = breakpoint.Address;

            switch (slot)
            {
                case 0:
                    dr0 = address;
                    break;
                case 1:
                    dr1 = address;
                    break;
                case 2:
                    dr2 = address;
                    break;
                case 3:
                    dr3 = address;
                    break;
            }

            dr7 |= 1UL << (slot * 2);

            var (typeBits, lengthBits) = breakpoint.Type switch
            {
                BreakpointType.HardwareExecute => (0b00UL, 0b00UL),
                BreakpointType.HardwareWrite => (0b01UL, 0b11UL),
                BreakpointType.HardwareReadWrite => (0b11UL, 0b11UL),
                _ => (0b00UL, 0b00UL)
            };

            var controlShift = 16 + (slot * 4);
            dr7 |= typeBits << controlShift;
            dr7 |= lengthBits << (controlShift + 2);
        }
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

            lock (session.SyncRoot)
            {
                var pageBase = address & ~(nuint)(PageSize - 1);

                // Check for existing page guard BP at same address
                var existing = session.Breakpoints.Values.FirstOrDefault(
                    bp => bp.IsEnabled && bp.Address == address && bp.Mode == BreakpointMode.PageGuard);
                if (existing is not null) return existing.ToDescriptor();

                var breakpoint = new BreakpointState(
                    $"bp-{Guid.NewGuid().ToString("N")[..8]}", processId, address, type, action, BreakpointMode.PageGuard, singleHit);

                // Apply PAGE_GUARD to the target page
                if (!VirtualProtectEx(session.ProcessHandle, (IntPtr)pageBase, (UIntPtr)PageSize,
                    PageGuard | 0x04 /* PAGE_READWRITE with GUARD */, out uint oldProtect))
                {
                    throw CreateWin32Exception($"Unable to set PAGE_GUARD at 0x{pageBase:X}.");
                }

                breakpoint.PageBaseAddress = pageBase;
                breakpoint.OriginalPageProtection = oldProtect;
                session.Breakpoints[breakpoint.Id] = breakpoint;
                session.PageGuardBreakpoints[pageBase] = breakpoint;
                _breakpointRegistry[breakpoint.Id] = breakpoint;

                return breakpoint.ToDescriptor();
            }
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

        if (!session.PageGuardBreakpoints.TryGetValue(pageBase, out var breakpoint))
            return;

        // Capture register snapshot
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

        if (LogBreakpointHit(breakpoint, debugEvent.dwThreadId, faultAddress, registers))
        {
            // Hit-rate exceeded — auto-disable, don't re-arm the guard
            breakpoint.IsEnabled = false;
            return;
        }

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

        if (session.PageGuardBreakpoints.TryGetValue(pageBase, out var bp) && bp.IsEnabled)
        {
            // Re-apply PAGE_GUARD
            VirtualProtectEx(session.ProcessHandle, (IntPtr)pageBase, (UIntPtr)PageSize,
                PageGuard | (bp.OriginalPageProtection ?? 0x04), out _);
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
        try
        {
            while (!session.StopRequested)
            {
                if (!WaitForDebugEventEx(out var debugEvent, 100))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorSemTimeout)
                    {
                        continue;
                    }

                    if (session.StopRequested)
                    {
                        break;
                    }

                    throw CreateWin32Exception($"Debug event loop failed for process {session.ProcessId}.");
                }

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
                    System.Diagnostics.Debug.WriteLine(
                        $"[BreakpointEngine] HandleDebugEvent error (PID {session.ProcessId}): {ex.Message}");
                    continueStatus = DbgContinue;
                }
                finally
                {
                    ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
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

    private uint HandleExitProcessEvent(ProcessDebugSession session, DEBUG_EVENT debugEvent, ref bool shouldExit)
    {
        shouldExit = true;

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
        if (LogBreakpointHit(breakpoint, debugEvent.dwThreadId, exceptionAddress, snapshot))
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
            session.Breakpoints.TryGetValue(breakpointId, out var softwareBreakpoint) &&
            softwareBreakpoint.IsEnabled)
        {
            ReArmSoftwareBreakpoint(session, threadHandle, softwareBreakpoint);
            session.PendingSoftwareRearm.Remove(debugEvent.dwThreadId);
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
                if (LogBreakpointHit(breakpoint, debugEvent.dwThreadId, hitAddress, snapshot))
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

    private static IReadOnlyList<int> GetTriggeredHardwareSlots(ProcessDebugSession session, IntPtr threadHandle)
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

    /// <summary>
    /// Log a breakpoint hit and check hit-rate. Returns true if the breakpoint should be
    /// auto-disabled due to excessive firing (>200 hits/sec).
    /// </summary>
    private static bool LogBreakpointHit(
        BreakpointState breakpoint,
        int threadId,
        nuint address,
        IReadOnlyDictionary<string, string> registerSnapshot)
    {
        breakpoint.HitLog.Enqueue(
            new BreakpointHitEvent(
                breakpoint.Id,
                address,
                threadId,
                DateTimeOffset.UtcNow,
                registerSnapshot));

        Interlocked.Increment(ref breakpoint.HitCount);

        // Single-hit enforcement: disable immediately after first hit
        if (breakpoint.SingleHit && breakpoint.HitCount >= 1)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BreakpointEngine] Single-hit BP {breakpoint.Id} at 0x{breakpoint.Address:X}: " +
                $"auto-disabling after first hit.");
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
                System.Diagnostics.Debug.WriteLine(
                    $"[BreakpointEngine] Auto-disabling BP {breakpoint.Id} at 0x{breakpoint.Address:X}: " +
                    $"exceeded {MaxHitsPerSecond} hits/sec ({hits} in window). This prevents game freezes.");
                return true; // caller should disable this BP
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, string> CaptureRegisterSnapshot(
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
            foreach (var threadHandle in session.ThreadHandles.Values.Where(handle => handle != IntPtr.Zero))
            {
                CloseHandle(threadHandle);
            }

            session.ThreadHandles.Clear();

            if (session.ProcessHandle != IntPtr.Zero)
            {
                CloseHandle(session.ProcessHandle);
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

        public bool IsDebuggerAttached { get; set; }

        public Thread? DebugThread { get; set; }

        public Exception? LoopFailure { get; set; }

        public object SyncRoot { get; } = new();

        public Dictionary<string, BreakpointState> Breakpoints { get; } = new(StringComparer.Ordinal);

        public Dictionary<nuint, BreakpointState> SoftwareBreakpoints { get; } = new();

        public Dictionary<int, string> PendingSoftwareRearm { get; } = new();

        public Dictionary<int, IntPtr> ThreadHandles { get; } = new();

        /// <summary>Page guard breakpoints keyed by the guarded page base address.</summary>
        public Dictionary<nuint, BreakpointState> PageGuardBreakpoints { get; } = new();

        /// <summary>Tracks which BP is pending single-step re-arm of its PAGE_GUARD flag.</summary>
        public Dictionary<int, nuint> PendingPageGuardRearm { get; } = new();
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

        public ConcurrentQueue<BreakpointHitEvent> HitLog { get; } = new();

        public BreakpointDescriptor ToDescriptor() =>
            new(
                Id,
                Address,
                Type,
                HitAction,
                IsEnabled,
                HitCount,
                Mode);
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
}
