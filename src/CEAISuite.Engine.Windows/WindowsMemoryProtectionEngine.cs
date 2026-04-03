using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

public sealed class WindowsMemoryProtectionEngine : IMemoryProtectionEngine
{
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;

    /// <summary>
    /// Changes the memory protection for a region in the target process.
    /// <para>
    /// KNOWN LIMITATION (4E - TOCTOU race): Between VirtualProtectEx reading the old protection
    /// and the caller using it, another thread in the target process may change the protection.
    /// For critical paths (e.g., hook installation), use the suspend-all-threads pattern to
    /// ensure consistency.
    /// </para>
    /// </summary>
    public Task<ProtectionChangeResult> ChangeProtectionAsync(
        int processId, nuint address, long size, MemoryProtection newProtection,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var hProcess = OpenProcess(ProcessVmOperation, false, processId);
            if (hProcess == IntPtr.Zero)
                throw new InvalidOperationException($"Cannot open process {processId}: {Marshal.GetLastWin32Error()}");

            try
            {
                if (!VirtualProtectEx(hProcess, (IntPtr)(long)address, (UIntPtr)(ulong)size,
                    (uint)newProtection, out uint oldProtect))
                    throw new InvalidOperationException($"VirtualProtectEx failed: {Marshal.GetLastWin32Error()}");

                return new ProtectionChangeResult(address, size, (MemoryProtection)oldProtect, newProtection);
            }
            finally { CloseHandle(hProcess); }
        }, cancellationToken);

    public Task<MemoryAllocation> AllocateAsync(
        int processId, long size, MemoryProtection protection,
        nuint preferredAddress = 0, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var hProcess = OpenProcess(ProcessVmOperation, false, processId);
            if (hProcess == IntPtr.Zero)
                throw new InvalidOperationException($"Cannot open process {processId}: {Marshal.GetLastWin32Error()}");

            try
            {
                var preferred = (IntPtr)(long)preferredAddress;
                var result = VirtualAllocEx(hProcess, preferred, (UIntPtr)(ulong)size,
                    MemCommit | MemReserve, (uint)protection);

                if (result == IntPtr.Zero)
                    throw new InvalidOperationException($"VirtualAllocEx failed: {Marshal.GetLastWin32Error()}");

                return new MemoryAllocation((nuint)(ulong)result, size, protection);
            }
            finally { CloseHandle(hProcess); }
        }, cancellationToken);

    public Task<bool> FreeAsync(int processId, nuint address, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var hProcess = OpenProcess(ProcessVmOperation, false, processId);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                return VirtualFreeEx(hProcess, (IntPtr)(long)address, UIntPtr.Zero, MemRelease);
            }
            finally { CloseHandle(hProcess); }
        }, cancellationToken);

    public Task<MemoryRegionDescriptor> QueryProtectionAsync(
        int processId, nuint address, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var hProcess = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            if (hProcess == IntPtr.Zero)
                throw new InvalidOperationException($"Cannot open process {processId}: {Marshal.GetLastWin32Error()}");

            try
            {
                var mbi = new MEMORY_BASIC_INFORMATION();
                if (VirtualQueryEx(hProcess, (IntPtr)(long)address, ref mbi, (UIntPtr)Marshal.SizeOf(mbi)) == UIntPtr.Zero)
                    throw new InvalidOperationException($"VirtualQueryEx failed: {Marshal.GetLastWin32Error()}");

                var prot = (MemoryProtection)mbi.Protect;
                return new MemoryRegionDescriptor(
                    (nuint)(ulong)mbi.BaseAddress,
                    (long)mbi.RegionSize,
                    IsReadable(prot),
                    IsWritable(prot),
                    IsExecutable(prot));
            }
            finally { CloseHandle(hProcess); }
        }, cancellationToken);

    private static bool IsReadable(MemoryProtection p) =>
        p.HasFlag(MemoryProtection.ReadOnly) || p.HasFlag(MemoryProtection.ReadWrite) ||
        p.HasFlag(MemoryProtection.ExecuteRead) || p.HasFlag(MemoryProtection.ExecuteReadWrite) ||
        p.HasFlag(MemoryProtection.WriteCopy) || p.HasFlag(MemoryProtection.ExecuteWriteCopy);

    private static bool IsWritable(MemoryProtection p) =>
        p.HasFlag(MemoryProtection.ReadWrite) || p.HasFlag(MemoryProtection.ExecuteReadWrite) ||
        p.HasFlag(MemoryProtection.WriteCopy) || p.HasFlag(MemoryProtection.ExecuteWriteCopy);

    private static bool IsExecutable(MemoryProtection p) =>
        p.HasFlag(MemoryProtection.Execute) || p.HasFlag(MemoryProtection.ExecuteRead) ||
        p.HasFlag(MemoryProtection.ExecuteReadWrite) || p.HasFlag(MemoryProtection.ExecuteWriteCopy);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(
        IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(
        IntPtr hProcess, IntPtr lpAddress, ref MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);
}
