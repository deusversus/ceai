using System.Globalization;
using System.Runtime.InteropServices;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

public sealed class WindowsScanEngine : IScanEngine
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    private const uint MemCommit = 0x1000;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageReadonly = 0x02;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecute = 0x10;

    private const int MaxScanResults = 50_000;

    public Task<IReadOnlyList<MemoryRegionDescriptor>> EnumerateRegionsAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<MemoryRegionDescriptor>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unable to open process {processId} for region enumeration.");
                }

                try
                {
                    return EnumerateRegionsCore(handle);
                }
                finally
                {
                    CloseHandle(handle);
                }
            },
            cancellationToken);

    public Task<ScanResultSet> StartScanAsync(
        int processId,
        ScanConstraints constraints,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unable to open process {processId} for scanning.");
                }

                try
                {
                    var regions = EnumerateRegionsCore(handle)
                        .Where(r => r.IsReadable && r.IsWritable)
                        .ToArray();

                    var valueSize = GetValueSize(constraints.DataType);
                    var results = new List<ScanResultEntry>();
                    var totalBytesScanned = 0L;

                    foreach (var region in regions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (results.Count >= MaxScanResults)
                        {
                            break;
                        }

                        var regionBytes = ReadRegion(handle, region.BaseAddress, (int)Math.Min(region.RegionSize, 16 * 1024 * 1024));
                        if (regionBytes is null)
                        {
                            continue;
                        }

                        totalBytesScanned += regionBytes.Length;
                        ScanRegionForInitial(region.BaseAddress, regionBytes, constraints, valueSize, results);
                    }

                    return new ScanResultSet(
                        Guid.NewGuid().ToString("N")[..12],
                        processId,
                        constraints,
                        results,
                        regions.Length,
                        totalBytesScanned,
                        DateTimeOffset.UtcNow);
                }
                finally
                {
                    CloseHandle(handle);
                }
            },
            cancellationToken);

    public Task<ScanResultSet> RefineScanAsync(
        ScanResultSet previousResults,
        ScanConstraints refinement,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, previousResults.ProcessId);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Unable to open process {previousResults.ProcessId} for rescan.");
                }

                try
                {
                    var valueSize = GetValueSize(refinement.DataType);
                    var refined = new List<ScanResultEntry>();

                    foreach (var previous in previousResults.Results)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (refined.Count >= MaxScanResults)
                        {
                            break;
                        }

                        var buffer = new byte[valueSize];
                        if (!ReadProcessMemory(handle, (IntPtr)previous.Address, buffer, valueSize, out var bytesRead) || bytesRead != valueSize)
                        {
                            continue;
                        }

                        var currentValue = FormatValue(buffer, refinement.DataType);
                        var matches = refinement.ScanType switch
                        {
                            ScanType.ExactValue => string.Equals(currentValue, refinement.Value, StringComparison.OrdinalIgnoreCase),
                            ScanType.Increased => CompareValues(currentValue, previous.CurrentValue, refinement.DataType) > 0,
                            ScanType.Decreased => CompareValues(currentValue, previous.CurrentValue, refinement.DataType) < 0,
                            ScanType.Changed => !string.Equals(currentValue, previous.CurrentValue, StringComparison.OrdinalIgnoreCase),
                            ScanType.Unchanged => string.Equals(currentValue, previous.CurrentValue, StringComparison.OrdinalIgnoreCase),
                            ScanType.UnknownInitialValue => true,
                            _ => false
                        };

                        if (matches)
                        {
                            refined.Add(new ScanResultEntry(previous.Address, currentValue, previous.CurrentValue, buffer));
                        }
                    }

                    return new ScanResultSet(
                        previousResults.ScanId,
                        previousResults.ProcessId,
                        refinement,
                        refined,
                        previousResults.TotalRegionsScanned,
                        previousResults.TotalBytesScanned,
                        DateTimeOffset.UtcNow);
                }
                finally
                {
                    CloseHandle(handle);
                }
            },
            cancellationToken);

    private static List<MemoryRegionDescriptor> EnumerateRegionsCore(IntPtr processHandle)
    {
        var regions = new List<MemoryRegionDescriptor>();
        var address = nuint.Zero;
        var mbiSize = Marshal.SizeOf<MemoryBasicInformation>();

        while (true)
        {
            var bytesReturned = VirtualQueryEx(processHandle, (IntPtr)address, out var mbi, mbiSize);
            if (bytesReturned == 0)
            {
                break;
            }

            if (mbi.State == MemCommit && mbi.RegionSize > 0)
            {
                var isReadable = IsReadableProtection(mbi.Protect);
                var isWritable = IsWritableProtection(mbi.Protect);
                var isExecutable = IsExecutableProtection(mbi.Protect);

                regions.Add(new MemoryRegionDescriptor(
                    (nuint)(ulong)mbi.BaseAddress,
                    (long)(ulong)mbi.RegionSize,
                    isReadable,
                    isWritable,
                    isExecutable));
            }

            var nextAddress = (ulong)mbi.BaseAddress + (ulong)mbi.RegionSize;
            if (nextAddress <= (ulong)address)
            {
                break;
            }

            address = (nuint)nextAddress;
        }

        return regions;
    }

    private static bool IsReadableProtection(uint protect) =>
        (protect & (PageReadWrite | PageWriteCopy | PageReadonly |
                    PageExecuteReadWrite | PageExecuteWriteCopy | PageExecuteRead)) != 0;

    private static bool IsWritableProtection(uint protect) =>
        (protect & (PageReadWrite | PageWriteCopy | PageExecuteReadWrite | PageExecuteWriteCopy)) != 0;

    private static bool IsExecutableProtection(uint protect) =>
        (protect & (PageExecute | PageExecuteRead | PageExecuteReadWrite | PageExecuteWriteCopy)) != 0;

    private static byte[]? ReadRegion(IntPtr processHandle, nuint baseAddress, int size)
    {
        var buffer = new byte[size];
        if (!ReadProcessMemory(processHandle, (IntPtr)baseAddress, buffer, size, out var bytesRead) || bytesRead <= 0)
        {
            return null;
        }

        return bytesRead == size ? buffer : buffer[..bytesRead];
    }

    private static void ScanRegionForInitial(
        nuint regionBase,
        byte[] regionBytes,
        ScanConstraints constraints,
        int valueSize,
        List<ScanResultEntry> results)
    {
        // Array of Bytes scan uses pattern matching with wildcards
        if (constraints.ScanType == ScanType.ArrayOfBytes && !string.IsNullOrWhiteSpace(constraints.Value))
        {
            ScanRegionForAob(regionBase, regionBytes, constraints.Value, results);
            return;
        }

        for (var offset = 0; offset <= regionBytes.Length - valueSize; offset += valueSize)
        {
            if (results.Count >= MaxScanResults)
            {
                return;
            }

            var slice = regionBytes[offset..(offset + valueSize)];
            var currentValue = FormatValue(slice, constraints.DataType);

            var matches = constraints.ScanType switch
            {
                ScanType.ExactValue => string.Equals(currentValue, constraints.Value, StringComparison.OrdinalIgnoreCase),
                ScanType.UnknownInitialValue => true,
                _ => false
            };

            if (matches)
            {
                var address = regionBase + (nuint)offset;
                results.Add(new ScanResultEntry(address, currentValue, null, slice.ToArray()));
            }
        }
    }

    private static void ScanRegionForAob(
        nuint regionBase,
        byte[] regionBytes,
        string pattern,
        List<ScanResultEntry> results)
    {
        // Parse "48 8B 05 ?? ?? ?? ?? 48 89" pattern: ?? = wildcard
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var patternBytes = new byte[parts.Length];
        var wildcardMask = new bool[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "?" or "??" or "*")
            {
                wildcardMask[i] = true;
            }
            else
            {
                patternBytes[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        for (var offset = 0; offset <= regionBytes.Length - parts.Length; offset++)
        {
            if (results.Count >= MaxScanResults) return;

            var match = true;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!wildcardMask[i] && regionBytes[offset + i] != patternBytes[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                var address = regionBase + (nuint)offset;
                var slice = regionBytes[offset..(offset + parts.Length)];
                results.Add(new ScanResultEntry(address, Convert.ToHexString(slice), null, slice));
            }
        }
    }

    private static string FormatValue(byte[] bytes, MemoryDataType dataType) =>
        dataType switch
        {
            MemoryDataType.Int32 => BitConverter.ToInt32(bytes, 0).ToString(),
            MemoryDataType.Int64 => BitConverter.ToInt64(bytes, 0).ToString(),
            MemoryDataType.Float => BitConverter.ToSingle(bytes, 0).ToString("G9"),
            MemoryDataType.Double => BitConverter.ToDouble(bytes, 0).ToString("G17"),
            _ => Convert.ToHexString(bytes)
        };

    private static int CompareValues(string current, string previous, MemoryDataType dataType)
    {
        try
        {
            return dataType switch
            {
                MemoryDataType.Int32 => int.Parse(current, CultureInfo.InvariantCulture)
                    .CompareTo(int.Parse(previous, CultureInfo.InvariantCulture)),
                MemoryDataType.Int64 => long.Parse(current, CultureInfo.InvariantCulture)
                    .CompareTo(long.Parse(previous, CultureInfo.InvariantCulture)),
                MemoryDataType.Float => float.Parse(current, CultureInfo.InvariantCulture)
                    .CompareTo(float.Parse(previous, CultureInfo.InvariantCulture)),
                MemoryDataType.Double => double.Parse(current, CultureInfo.InvariantCulture)
                    .CompareTo(double.Parse(previous, CultureInfo.InvariantCulture)),
                _ => string.Compare(current, previous, StringComparison.OrdinalIgnoreCase)
            };
        }
        catch
        {
            return 0;
        }
    }

    private static int GetValueSize(MemoryDataType dataType) =>
        dataType switch
        {
            MemoryDataType.Int32 => sizeof(int),
            MemoryDataType.Int64 => sizeof(long),
            MemoryDataType.Float => sizeof(float),
            MemoryDataType.Double => sizeof(double),
            MemoryDataType.Pointer => sizeof(long),
            _ => throw new ArgumentOutOfRangeException(nameof(dataType))
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
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
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(
        IntPtr processHandle,
        IntPtr address,
        out MemoryBasicInformation buffer,
        int length);

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
    private static extern bool CloseHandle(IntPtr handle);
}
