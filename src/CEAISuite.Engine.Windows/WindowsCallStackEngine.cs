using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Walks the call stack of a thread using RBP-chain walking with RSP heuristic fallback.
/// Provides frame-by-frame unwinding with module resolution.
/// </summary>
public sealed class WindowsCallStackEngine : ICallStackEngine
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ThreadGetContext = 0x0008;
    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadQueryInformation = 0x0040;

    private const uint ContextAmd64 = 0x00100000;
    private const uint ContextControl = ContextAmd64 | 0x00000001;
    private const uint ContextInteger = ContextAmd64 | 0x00000002;
    private const uint ContextFull = ContextControl | ContextInteger;

    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineI386 = 0x014c;

    /// <summary>
    /// Walk the call stack of a specific thread.
    /// </summary>
    public Task<IReadOnlyList<CallStackFrame>> WalkStackAsync(
        int processId, int threadId, IReadOnlyList<ModuleDescriptor> modules, int maxFrames = 64,
        CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<CallStackFrame>>(() => WalkStack(processId, threadId, modules, maxFrames), ct);

    /// <summary>
    /// Walk the call stack of all threads in a process.
    /// </summary>
    public Task<IReadOnlyDictionary<int, IReadOnlyList<CallStackFrame>>> WalkAllThreadsAsync(
        int processId, IReadOnlyList<ModuleDescriptor> modules, int maxFrames = 32,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var result = new Dictionary<int, IReadOnlyList<CallStackFrame>>();
            var snapshot = CreateToolhelp32Snapshot(0x00000004 /* TH32CS_SNAPTHREAD */, 0);
            if (snapshot == IntPtr.Zero) return (IReadOnlyDictionary<int, IReadOnlyList<CallStackFrame>>)result;

            try
            {
                var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
                if (Thread32First(snapshot, ref entry))
                {
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        if (entry.th32OwnerProcessID == processId)
                        {
                            try
                            {
                                var frames = WalkStack(processId, (int)entry.th32ThreadID, modules, maxFrames);
                                if (frames.Count > 0)
                                    result[(int)entry.th32ThreadID] = frames;
                            }
                            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[WindowsCallStackEngine] Thread {entry.th32ThreadID} walk failed (may have exited): {ex.Message}"); }
                        }
                    } while (Thread32Next(snapshot, ref entry));
                }
            }
            finally { CloseHandle(snapshot); }

            return (IReadOnlyDictionary<int, IReadOnlyList<CallStackFrame>>)result;
        }, ct);

    private static List<CallStackFrame> WalkStack(
        int processId, int threadId, IReadOnlyList<ModuleDescriptor> modules, int maxFrames)
    {
        var frames = new List<CallStackFrame>();
        var hProcess = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (hProcess == IntPtr.Zero) return frames;

        var hThread = OpenThread(ThreadGetContext | ThreadSuspendResume | ThreadQueryInformation, false, threadId);
        if (hThread == IntPtr.Zero)
        {
            CloseHandle(hProcess);
            return frames;
        }

        try
        {
            // Suspend thread to get stable context
            uint prevCount = SuspendThread(hThread);
            if (prevCount == 0xFFFFFFFF) return frames;

            try
            {
                var ctx = new CONTEXT64 { ContextFlags = ContextFull };
                if (!GetThreadContext(hThread, ref ctx))
                    return frames;

                // Manual RBP-chain walking (fast, doesn't need dbghelp)
                nuint rip = (nuint)ctx.Rip;
                nuint rsp = (nuint)ctx.Rsp;
                nuint rbp = (nuint)ctx.Rbp;

                // Frame 0: current instruction
                var (modName, modOffset) = ResolveModule(rip, modules);
                frames.Add(new CallStackFrame(0, rip, 0, rsp, rbp, modName, modOffset));

                // Walk RBP chain for additional frames
                var visited = new HashSet<nuint>();
                for (int i = 1; i < maxFrames; i++)
                {
                    if (rbp == 0 || rbp < 0x10000 || !visited.Add(rbp)) break;

                    // Read saved RBP and return address from stack frame
                    var frameBytes = new byte[16];
                    if (!ReadProcessMemory(hProcess, checked((IntPtr)(long)rbp), frameBytes, 16, out int bytesRead) || bytesRead < 16)
                        break;

                    nuint savedRbp = (nuint)BitConverter.ToUInt64(frameBytes, 0);
                    nuint returnAddr = (nuint)BitConverter.ToUInt64(frameBytes, 8);

                    if (returnAddr == 0 || returnAddr < 0x10000) break;

                    var (mName, mOff) = ResolveModule(returnAddr, modules);
                    frames.Add(new CallStackFrame(i, returnAddr, 0, (nuint)((long)rbp + 16), savedRbp, mName, mOff));

                    rbp = savedRbp;
                }

                // If RBP walking failed (leaf functions / optimized code), try RSP-based heuristic scan
                if (frames.Count < 3)
                {
                    ScanStackForReturnAddresses(hProcess, rsp, modules, frames, maxFrames);
                }
            }
            finally
            {
                _ = ResumeThread(hThread);
            }
        }
        finally
        {
            CloseHandle(hThread);
            CloseHandle(hProcess);
        }

        return frames;
    }

    private static void ScanStackForReturnAddresses(
        IntPtr hProcess, nuint stackPointer, IReadOnlyList<ModuleDescriptor> modules,
        List<CallStackFrame> frames, int maxFrames)
    {
        // Read a chunk of stack memory and look for values that look like return addresses
        var stackBytes = new byte[4096];
        if (!ReadProcessMemory(hProcess, checked((IntPtr)(long)stackPointer), stackBytes, stackBytes.Length, out int read) || read < 8)
            return;

        var seen = new HashSet<nuint>(frames.Select(f => f.InstructionPointer));
        for (int i = 0; i < read - 7 && frames.Count < maxFrames; i += 8)
        {
            var candidate = (nuint)BitConverter.ToUInt64(stackBytes, i);
            if (candidate < 0x10000 || candidate > 0x7FFFFFFFFFFF) continue;
            if (seen.Contains(candidate)) continue;

            // Check if this candidate is within a known module's code range
            var (modName, modOffset) = ResolveModule(candidate, modules);
            if (modName is not null && modOffset >= 0 && modOffset < 100_000_000)
            {
                seen.Add(candidate);
                frames.Add(new CallStackFrame(
                    frames.Count, candidate, 0,
                    (nuint)((long)stackPointer + i), 0,
                    modName, modOffset));
            }
        }
    }

    private static (string? ModuleName, long Offset) ResolveModule(nuint address, IReadOnlyList<ModuleDescriptor> modules)
    {
        foreach (var mod in modules)
        {
            long offset = (long)address - (long)mod.BaseAddress;
            if (offset >= 0 && offset < mod.SizeBytes)
                return (mod.Name, offset);
        }
        return (null, (long)address);
    }

    // ── P/Invoke ──

    [StructLayout(LayoutKind.Sequential)]
    private struct CONTEXT64
    {
        public ulong P1Home, P2Home, P3Home, P4Home, P5Home, P6Home;
        public uint ContextFlags;
        public uint MxCsr;
        public ushort SegCs, SegDs, SegEs, SegFs, SegGs, SegSs;
        public uint EFlags;
        public ulong Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
        public ulong Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi;
        public ulong R8, R9, R10, R11, R12, R13, R14, R15;
        public ulong Rip;
        // FPU/SSE state follows but we only need basic registers
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] FloatingPointData;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
        public ulong[] VectorRegisters;
        public ulong VectorControl;
        public ulong DebugControl;
        public ulong LastBranchToRip, LastBranchFromRip;
        public ulong LastExceptionToRip, LastExceptionFromRip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, int dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT64 lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);
}
