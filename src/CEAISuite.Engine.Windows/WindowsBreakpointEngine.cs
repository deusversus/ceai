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
        Task.Run(
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
                        Guid.NewGuid().ToString("N"),
                        processId,
                        address,
                        type,
                        action);

                    if (type == BreakpointType.Software)
                    {
                        InstallSoftwareBreakpoint(session, breakpoint);
                    }
                    else
                    {
                        breakpoint.HardwareSlot = AllocateHardwareSlot(session);
                        ApplyHardwareBreakpointsLocked(session);
                    }

                    session.Breakpoints[breakpoint.Id] = breakpoint;
                    if (type == BreakpointType.Software)
                    {
                        session.SoftwareBreakpoints[address] = breakpoint;
                    }

                    _breakpointRegistry[breakpoint.Id] = breakpoint;
                    return breakpoint.ToDescriptor();
                }
            },
            cancellationToken);

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
                    throw CreateWin32Exception($"Unable to attach debugger to process {processId}.");
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
            throw CreateWin32Exception("Unable to suspend thread for hardware breakpoint update.");
        }

        try
        {
            if (isWow64Target)
            {
                var context = CreateWow64Context();
                if (!Wow64GetThreadContext(threadHandle, ref context))
                {
                    throw CreateWin32Exception("Unable to read WOW64 thread context.");
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

                if (!Wow64SetThreadContext(threadHandle, ref context))
                {
                    throw CreateWin32Exception("Unable to write WOW64 thread context.");
                }
            }
            else
            {
                var context = CreateContext64();
                if (!GetThreadContext(threadHandle, ref context))
                {
                    throw CreateWin32Exception("Unable to read thread context.");
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

                if (!SetThreadContext(threadHandle, ref context))
                {
                    throw CreateWin32Exception("Unable to write thread context.");
                }
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
            StopSession(session, detachFromProcess: false);
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
        LogBreakpointHit(breakpoint, debugEvent.dwThreadId, exceptionAddress, snapshot);

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

    private uint HandleSingleStepException(ProcessDebugSession session, DEBUG_EVENT debugEvent)
    {
        var threadHandle = EnsureThreadHandleLocked(session, debugEvent.dwThreadId, IntPtr.Zero);

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
                LogBreakpointHit(breakpoint, debugEvent.dwThreadId, hitAddress, snapshot);
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

    private static void LogBreakpointHit(
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
    }

    private sealed class BreakpointState
    {
        public BreakpointState(
            string id,
            int processId,
            nuint address,
            BreakpointType type,
            BreakpointHitAction hitAction)
        {
            Id = id;
            ProcessId = processId;
            Address = address;
            Type = type;
            HitAction = hitAction;
        }

        public string Id { get; }

        public int ProcessId { get; }

        public nuint Address { get; }

        public BreakpointType Type { get; }

        public BreakpointHitAction HitAction { get; }

        public bool IsEnabled { get; set; } = true;

        public int HitCount;

        public byte? OriginalByte { get; set; }

        public int? HardwareSlot { get; set; }

        public ConcurrentQueue<BreakpointHitEvent> HitLog { get; } = new();

        public BreakpointDescriptor ToDescriptor() =>
            new(
                Id,
                Address,
                Type,
                HitAction,
                IsEnabled,
                HitCount);
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
    private struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecordPointer;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
        public nuint[] ExceptionInformation;
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
}
