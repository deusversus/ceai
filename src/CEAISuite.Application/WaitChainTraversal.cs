using System.Globalization;
using System.Runtime.InteropServices;

namespace CEAISuite.Application;

/// <summary>
/// Result of deadlock detection for a process.
/// </summary>
public sealed record DeadlockResult(bool HasDeadlock, IReadOnlyList<ThreadWaitInfo> WaitChains);

/// <summary>
/// Information about a single thread's wait chain.
/// </summary>
public sealed record ThreadWaitInfo(int ThreadId, string WaitDescription, bool IsDeadlocked);

/// <summary>
/// Detects thread-level deadlocks using the Windows Wait Chain Traversal (WCT) API.
/// This provides deeper insight than process-level responsiveness checks by analyzing
/// actual thread wait chains and detecting cycles.
/// </summary>
public sealed class DeadlockDetector : IDisposable
{
    private IntPtr _wctSession;
    private bool _disposed;
    private readonly bool _isSupported;

    public DeadlockDetector()
    {
        _isSupported = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (_isSupported)
        {
            try
            {
                _wctSession = NativeMethods.OpenThreadWaitChainSession(0, IntPtr.Zero);
            }
            catch (DllNotFoundException)
            {
                _isSupported = false;
            }
            catch (EntryPointNotFoundException)
            {
                _isSupported = false;
            }
        }
    }

    /// <summary>
    /// Detects deadlocks among threads in the specified process.
    /// </summary>
    /// <param name="processId">The target process ID.</param>
    /// <returns>A <see cref="DeadlockResult"/> describing any deadlocks found.</returns>
    public DeadlockResult DetectDeadlocks(int processId)
    {
        if (!_isSupported || _wctSession == IntPtr.Zero)
        {
            return new DeadlockResult(false, Array.Empty<ThreadWaitInfo>());
        }

        var waitChains = new List<ThreadWaitInfo>();

        int[] threadIds;
        try
        {
            threadIds = EnumerateProcessThreads(processId);
        }
        catch (Exception)
        {
            // Process may have exited or we lack permissions
            return new DeadlockResult(false, Array.Empty<ThreadWaitInfo>());
        }

        if (threadIds.Length == 0)
        {
            return new DeadlockResult(false, Array.Empty<ThreadWaitInfo>());
        }

        bool hasDeadlock = false;

        foreach (int threadId in threadIds)
        {
            var info = AnalyzeThreadWaitChain(threadId);
            if (info is not null)
            {
                waitChains.Add(info);
                if (info.IsDeadlocked)
                {
                    hasDeadlock = true;
                }
            }
        }

        return new DeadlockResult(hasDeadlock, waitChains);
    }

    /// <summary>
    /// Enumerates all thread IDs belonging to the specified process using Toolhelp32.
    /// </summary>
    private static int[] EnumerateProcessThreads(int processId)
    {
        const uint TH32CS_SNAPTHREAD = 0x00000004;
        var threadIds = new List<int>();

        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return Array.Empty<int>();
        }

        try
        {
            var entry = new NativeMethods.THREADENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.THREADENTRY32>()
            };

            if (NativeMethods.Thread32First(snapshot, ref entry))
            {
                do
                {
                    if (entry.th32OwnerProcessID == (uint)processId)
                    {
                        threadIds.Add((int)entry.th32ThreadID);
                    }

                    entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.THREADENTRY32>();
                } while (NativeMethods.Thread32Next(snapshot, ref entry));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return threadIds.ToArray();
    }

    /// <summary>
    /// Analyzes the wait chain for a single thread.
    /// Returns null if the thread's wait chain cannot be retrieved.
    /// </summary>
    private ThreadWaitInfo? AnalyzeThreadWaitChain(int threadId)
    {
        if (_wctSession == IntPtr.Zero)
        {
            return null;
        }

        const int WCT_MAX_NODE_COUNT = 16;
        var nodes = new NativeMethods.WAITCHAIN_NODE_INFO[WCT_MAX_NODE_COUNT];
        int nodeCount = WCT_MAX_NODE_COUNT;
        int isCycle = 0;

        bool success = NativeMethods.GetThreadWaitChain(
            _wctSession,
            IntPtr.Zero,
            0, // Flags: synchronous call
            (uint)threadId,
            ref nodeCount,
            nodes,
            out isCycle);

        if (!success)
        {
            return null;
        }

        // No wait chain nodes means the thread isn't waiting
        if (nodeCount <= 0)
        {
            return null;
        }

        bool isDeadlocked = isCycle != 0;
        string description = BuildWaitDescription(nodes, nodeCount, isDeadlocked);

        return new ThreadWaitInfo(threadId, description, isDeadlocked);
    }

    /// <summary>
    /// Builds a human-readable description of the wait chain.
    /// </summary>
    private static string BuildWaitDescription(NativeMethods.WAITCHAIN_NODE_INFO[] nodes, int count, bool isCycle)
    {
        var parts = new List<string>();

        for (int i = 0; i < count && i < nodes.Length; i++)
        {
            ref readonly var node = ref nodes[i];
            string objectType = node.ObjectType switch
            {
                NativeMethods.WCT_OBJECT_TYPE.CriticalSection => "CriticalSection",
                NativeMethods.WCT_OBJECT_TYPE.SendMessage => "SendMessage",
                NativeMethods.WCT_OBJECT_TYPE.Mutex => "Mutex",
                NativeMethods.WCT_OBJECT_TYPE.Alpc => "ALPC",
                NativeMethods.WCT_OBJECT_TYPE.ComActivation => "COM",
                NativeMethods.WCT_OBJECT_TYPE.ThreadWait => "ThreadWait",
                NativeMethods.WCT_OBJECT_TYPE.ProcessWait => "ProcessWait",
                NativeMethods.WCT_OBJECT_TYPE.Thread => "Thread",
                NativeMethods.WCT_OBJECT_TYPE.ComCallback => "COMCallback",
                NativeMethods.WCT_OBJECT_TYPE.Unknown => "Unknown",
                _ => string.Format(CultureInfo.InvariantCulture, "Type({0})", (int)node.ObjectType),
            };

            string status = node.ObjectStatus switch
            {
                NativeMethods.WCT_OBJECT_STATUS.Blocked => "[Blocked]",
                NativeMethods.WCT_OBJECT_STATUS.Running => "[Running]",
                NativeMethods.WCT_OBJECT_STATUS.PidOnly => "[PidOnly]",
                NativeMethods.WCT_OBJECT_STATUS.PidOnlyRpcss => "[PidOnlyRpcss]",
                NativeMethods.WCT_OBJECT_STATUS.Owned => "[Owned]",
                NativeMethods.WCT_OBJECT_STATUS.NotOwned => "[NotOwned]",
                NativeMethods.WCT_OBJECT_STATUS.Abandoned => "[Abandoned]",
                NativeMethods.WCT_OBJECT_STATUS.Error => "[Error]",
                _ => string.Format(CultureInfo.InvariantCulture, "[Status({0})]", (int)node.ObjectStatus),
            };

            parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}{1}", objectType, status));
        }

        string chain = string.Join(" -> ", parts);
        if (isCycle)
        {
            chain = string.Format(CultureInfo.InvariantCulture, "DEADLOCK CYCLE: {0}", chain);
        }

        return chain;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_wctSession != IntPtr.Zero && _isSupported)
            {
                try
                {
                    NativeMethods.CloseThreadWaitChainSession(_wctSession);
                }
                catch (Exception)
                {
                    // Best-effort cleanup
                }

                _wctSession = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// P/Invoke declarations for Wait Chain Traversal and Toolhelp32 APIs.
    /// </summary>
    internal static class NativeMethods
    {
        internal static readonly IntPtr InvalidHandleValue = new(-1);

        // --- Wait Chain Traversal (Advapi32.dll) ---

        [DllImport("Advapi32.dll", SetLastError = true)]
        internal static extern IntPtr OpenThreadWaitChainSession(
            uint flags,
            IntPtr callback);

        [DllImport("Advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetThreadWaitChain(
            IntPtr wctHandle,
            IntPtr context,
            uint flags,
            uint threadId,
            ref int nodeCount,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)]
            [In, Out] WAITCHAIN_NODE_INFO[] nodeInfoArray,
            out int isCycle);

        [DllImport("Advapi32.dll", SetLastError = true)]
        internal static extern void CloseThreadWaitChainSession(IntPtr wctHandle);

        // --- Toolhelp32 (Kernel32.dll) ---

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        // --- WCT Enums ---

        internal enum WCT_OBJECT_TYPE
        {
            CriticalSection = 1,
            SendMessage = 2,
            Mutex = 3,
            Alpc = 4,
            ComActivation = 5,
            ThreadWait = 6,
            ProcessWait = 7,
            Thread = 8,
            ComCallback = 9,
            Unknown = 10,
            Max = 11
        }

        internal enum WCT_OBJECT_STATUS
        {
            NoAccess = 1,
            Running = 2,
            Blocked = 3,
            PidOnly = 4,
            PidOnlyRpcss = 5,
            Owned = 6,
            NotOwned = 7,
            Abandoned = 8,
            Unknown = 9,
            Error = 10,
            Max = 11
        }

        // --- WCT Structs ---

        [StructLayout(LayoutKind.Sequential)]
        internal struct WAITCHAIN_NODE_INFO
        {
            public WCT_OBJECT_TYPE ObjectType;
            public WCT_OBJECT_STATUS ObjectStatus;

            // Union: For thread nodes, these are the thread/process IDs.
            // For lock nodes, the object name is here. We use a fixed-size
            // buffer to cover both cases in the union.
            public WCT_THREAD_OR_LOCK_INFO Info;
        }

        [StructLayout(LayoutKind.Explicit, Size = 280)]
        internal struct WCT_THREAD_OR_LOCK_INFO
        {
            // Thread info fields
            [FieldOffset(0)] public uint ProcessId;
            [FieldOffset(4)] public uint ThreadId;
            [FieldOffset(8)] public uint WaitTime;
            [FieldOffset(12)] public uint ContextSwitches;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct THREADENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ThreadID;
            public uint th32OwnerProcessID;
            public int tpBasePri;
            public int tpDeltaPri;
            public uint dwFlags;
        }
    }
}
