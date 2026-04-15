using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Symbol engine backed by DbgHelp.dll. Loads PDB/export symbols and resolves
/// addresses to function names. DbgHelp is single-threaded — all calls are serialized via lock.
/// </summary>
public sealed class WindowsSymbolEngine : ISymbolEngine, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, IntPtr> _initializedProcesses = new();
    private bool _disposed;

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    // SymSetOptions flags
    private const uint SYMOPT_UNDNAME = 0x00000002;
    private const uint SYMOPT_DEFERRED_LOADS = 0x00000004;
    private const uint SYMOPT_LOAD_LINES = 0x00000010;
    private const uint SYMOPT_FAVOR_COMPRESSED = 0x00800000;

    public Task<bool> LoadSymbolsForModuleAsync(int processId, string moduleName, nuint baseAddress, long size) =>
        Task.Run(() =>
        {
            lock (_lock)
            {
                var handle = EnsureInitialized(processId);
                if (handle == IntPtr.Zero) return false;

                var result = SymLoadModuleEx(handle, IntPtr.Zero, moduleName, null,
                    (ulong)baseAddress, (uint)size, IntPtr.Zero, 0);

                // result == 0 means failure OR already loaded (check GetLastError)
                if (result == 0)
                {
                    var err = Marshal.GetLastWin32Error();
                    return err == 0; // 0 = already loaded, non-zero = real failure
                }
                return true;
            }
        });

    public SymbolInfo? ResolveAddress(nuint address)
    {
        lock (_lock)
        {
            // Find which process owns this address range
            foreach (var (_, handle) in _initializedProcesses)
            {
                var info = ResolveWithHandle(handle, address);
                if (info is not null) return info;
            }
            return null;
        }
    }

    public SourceLineInfo? ResolveSourceLine(nuint address)
    {
        lock (_lock)
        {
            foreach (var (_, handle) in _initializedProcesses)
            {
                var info = ResolveLineWithHandle(handle, address);
                if (info is not null) return info;
            }
            return null;
        }
    }

    /// <summary>Resolve both symbol and source line in a single lock acquisition (reduces lock overhead per instruction).</summary>
    public (SymbolInfo? Symbol, SourceLineInfo? Line) ResolveAddressAndLine(nuint address)
    {
        lock (_lock)
        {
            SymbolInfo? symbol = null;
            SourceLineInfo? line = null;
            foreach (var (_, handle) in _initializedProcesses)
            {
                symbol ??= ResolveWithHandle(handle, address);
                line ??= ResolveLineWithHandle(handle, address);
                if (symbol is not null && line is not null) break;
            }
            return (symbol, line);
        }
    }

    public void Cleanup(int processId)
    {
        lock (_lock)
        {
            if (_initializedProcesses.Remove(processId, out var handle))
            {
                SymCleanup(handle);
                CloseHandle(handle);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach (var (_, handle) in _initializedProcesses)
            {
                try { SymCleanup(handle); CloseHandle(handle); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[WindowsSymbolEngine] Cleanup failed for process handle: {ex.Message}"); }
            }
            _initializedProcesses.Clear();
        }
    }

    private IntPtr EnsureInitialized(int processId)
    {
        if (_initializedProcesses.TryGetValue(processId, out var existing))
            return existing;

        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        _ = SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_LOAD_LINES | SYMOPT_FAVOR_COMPRESSED);

        if (!SymInitialize(handle, null, invadeProcess: true))
        {
            CloseHandle(handle);
            return IntPtr.Zero;
        }

        _initializedProcesses[processId] = handle;
        return handle;
    }

    private static SymbolInfo? ResolveWithHandle(IntPtr handle, nuint address)
    {
        // SYMBOL_INFO struct: MaxNameLen=256, SizeOfStruct=88 (x64)
        const int maxNameLen = 256;
        var bufferSize = Marshal.SizeOf<SYMBOL_INFO>() + maxNameLen * sizeof(char);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // Zero the buffer and set required fields
            unsafe
            {
                new Span<byte>(buffer.ToPointer(), bufferSize).Clear();
            }
            Marshal.WriteInt32(buffer, 0, bufferSize);  // SizeOfStruct
            Marshal.WriteInt32(buffer, 4, maxNameLen);   // MaxNameLen

            if (!SymFromAddr(handle, (ulong)address, out var displacement, buffer))
                return null;

            // Read the Name field (offset 84 on x64 — after the fixed SYMBOL_INFO fields)
            var nameOffset = Marshal.OffsetOf<SYMBOL_INFO>(nameof(SYMBOL_INFO.Name)).ToInt32();
            var functionName = Marshal.PtrToStringAnsi(buffer + nameOffset);
            if (string.IsNullOrEmpty(functionName)) return null;

            // Read ModBase to determine module name
            var modBase = (ulong)Marshal.ReadInt64(buffer, Marshal.OffsetOf<SYMBOL_INFO>(nameof(SYMBOL_INFO.ModBase)).ToInt32());

            // Get module name from the module base
            var moduleName = GetModuleNameFromBase(handle, modBase);

            return new SymbolInfo(functionName, moduleName ?? "???", displacement);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? GetModuleNameFromBase(IntPtr handle, ulong modBase)
    {
        if (modBase == 0) return null;
        var buffer = new char[260];
        var len = SymGetModuleInfo64_ModuleName(handle, modBase, buffer);
        return len > 0 ? new string(buffer, 0, len) : null;
    }

    private static int SymGetModuleInfo64_ModuleName(IntPtr handle, ulong addr, char[] buffer)
    {
        // Use IMAGEHLP_MODULE64 to get module name — large struct, only need ModuleName field
        const int structSize = 1680; // sizeof(IMAGEHLP_MODULE64) on x64
        var buf = Marshal.AllocHGlobal(structSize);
        try
        {
            unsafe { new Span<byte>(buf.ToPointer(), structSize).Clear(); }
            Marshal.WriteInt32(buf, 0, structSize); // SizeOfStruct

            if (!SymGetModuleInfo64(handle, addr, buf))
                return 0;

            // ModuleName is at offset 32, 32 chars (TCHAR[32])
            var namePtr = buf + 32;
            var name = Marshal.PtrToStringAnsi(namePtr);
            if (name is null) return 0;
            name.AsSpan().CopyTo(buffer);
            return name.Length;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static SourceLineInfo? ResolveLineWithHandle(IntPtr handle, nuint address)
    {
        var line = new IMAGEHLP_LINE64();
        line.SizeOfStruct = (uint)Marshal.SizeOf<IMAGEHLP_LINE64>();
        if (!SymGetLineFromAddr64(handle, (ulong)address, out _, ref line))
            return null;

        var fileName = line.FileName != IntPtr.Zero ? Marshal.PtrToStringAnsi(line.FileName) : null;
        if (string.IsNullOrEmpty(fileName)) return null;

        return new SourceLineInfo(fileName, (int)line.LineNumber, (nuint)line.Address);
    }

    // ── DbgHelp P/Invoke ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct SYMBOL_INFO
    {
        public int SizeOfStruct;
        public int MaxNameLen;
        public uint TypeIndex;
        public uint Reserved0;
        public uint Reserved1;
        public uint Index;
        public uint Size;
        public ulong ModBase;
        public uint Flags;
        public ulong Value;
        public ulong Address;
        public uint Register;
        public uint Scope;
        public uint Tag;
        public int NameLen;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1)]
        public string Name;
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern uint SymSetOptions(uint symOptions);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymInitialize(IntPtr hProcess, string? userSearchPath, bool invadeProcess);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern ulong SymLoadModuleEx(IntPtr hProcess, IntPtr hFile, string imageName,
        string? moduleName, ulong baseOfDll, uint dllSize, IntPtr data, uint flags);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymFromAddr(IntPtr hProcess, ulong address, out ulong displacement, IntPtr symbolInfo);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymGetModuleInfo64(IntPtr hProcess, ulong addr, IntPtr moduleInfo);

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymCleanup(IntPtr hProcess);

    [DllImport("dbghelp.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymGetLineFromAddr64(IntPtr hProcess, ulong dwAddr,
        out uint pdwDisplacement, ref IMAGEHLP_LINE64 line);

    [StructLayout(LayoutKind.Sequential)]
    private struct IMAGEHLP_LINE64
    {
        public uint SizeOfStruct;
        public IntPtr Key;
        public uint LineNumber;
        public IntPtr FileName;  // PCHAR — ANSI string pointer
        public ulong Address;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
