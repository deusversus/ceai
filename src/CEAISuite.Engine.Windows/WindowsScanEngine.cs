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
                    var skippedRegions = 0; // 4H: Track skipped regions
                    var isTruncated = false; // 4F: Track if results hit the cap

                    foreach (var region in regions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (results.Count >= MaxScanResults)
                        {
                            isTruncated = true; // 4F: Results hit the 50K cap
                            break;
                        }

                        var regionBytes = ReadRegion(handle, region.BaseAddress, (int)Math.Min(region.RegionSize, 16 * 1024 * 1024));
                        if (regionBytes is null)
                        {
                            skippedRegions++; // 4H: Count skipped regions
                            continue;
                        }

                        totalBytesScanned += regionBytes.Length;
                        ScanRegionForInitial(region.BaseAddress, regionBytes, constraints, valueSize, results, cancellationToken);
                    }

                    // 4F: Check if we hit the cap after scanning
                    if (results.Count >= MaxScanResults)
                        isTruncated = true;

                    return new ScanResultSet(
                        $"scan-{Guid.NewGuid().ToString("N")[..8]}",
                        processId,
                        constraints,
                        results,
                        regions.Length,
                        totalBytesScanned,
                        DateTimeOffset.UtcNow,
                        isTruncated,
                        skippedRegions);
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
                    var isTruncated = false;

                    foreach (var previous in previousResults.Results)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (refined.Count >= MaxScanResults)
                        {
                            isTruncated = true;
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
                            ScanType.BiggerThan => CompareValues(currentValue, refinement.Value ?? "0", refinement.DataType) > 0,
                            ScanType.SmallerThan => CompareValues(currentValue, refinement.Value ?? "0", refinement.DataType) < 0,
                            ScanType.ValueBetween => IsValueBetween(currentValue, refinement.Value, refinement.DataType),
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
                        DateTimeOffset.UtcNow,
                        isTruncated);
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
        List<ScanResultEntry> results,
        CancellationToken cancellationToken = default)
    {
        // Array of Bytes scan uses pattern matching with wildcards
        if (constraints.ScanType == ScanType.ArrayOfBytes && !string.IsNullOrWhiteSpace(constraints.Value))
        {
            ScanRegionForAob(regionBase, regionBytes, constraints.Value, results);
            return;
        }

        var matchCount = 0; // 4G: Counter for periodic cancellation check
        for (var offset = 0; offset <= regionBytes.Length - valueSize; offset += valueSize)
        {
            if (results.Count >= MaxScanResults)
            {
                return;
            }

            // 4G: Check cancellation token every 10K iterations within the inner loop
            if (++matchCount % 10_000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var slice = regionBytes[offset..(offset + valueSize)];
            var currentValue = FormatValue(slice, constraints.DataType);

            var matches = constraints.ScanType switch
            {
                ScanType.ExactValue => string.Equals(currentValue, constraints.Value, StringComparison.OrdinalIgnoreCase),
                ScanType.UnknownInitialValue => true,
                ScanType.BiggerThan => CompareValues(currentValue, constraints.Value ?? "0", constraints.DataType) > 0,
                ScanType.SmallerThan => CompareValues(currentValue, constraints.Value ?? "0", constraints.DataType) < 0,
                ScanType.ValueBetween => IsValueBetween(currentValue, constraints.Value, constraints.DataType),
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
            MemoryDataType.Byte => bytes[0].ToString(),
            MemoryDataType.Int16 => BitConverter.ToInt16(bytes, 0).ToString(),
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
                MemoryDataType.Byte => byte.Parse(current, CultureInfo.InvariantCulture)
                    .CompareTo(byte.Parse(previous, CultureInfo.InvariantCulture)),
                MemoryDataType.Int16 => short.Parse(current, CultureInfo.InvariantCulture)
                    .CompareTo(short.Parse(previous, CultureInfo.InvariantCulture)),
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

    private static bool IsValueBetween(string current, string? bounds, MemoryDataType dataType)
    {
        if (string.IsNullOrWhiteSpace(bounds) || !bounds.Contains(';'))
            return false;

        var parts = bounds.Split(';', 2);
        if (parts.Length != 2)
            return false;

        try
        {
            return CompareValues(current, parts[0].Trim(), dataType) >= 0
                && CompareValues(current, parts[1].Trim(), dataType) <= 0;
        }
        catch
        {
            return false;
        }
    }

    private static int GetValueSize(MemoryDataType dataType) =>
        dataType switch
        {
            MemoryDataType.Byte => 1,
            MemoryDataType.Int16 => 2,
            MemoryDataType.Int32 => sizeof(int),
            MemoryDataType.Int64 => sizeof(long),
            MemoryDataType.Float => sizeof(float),
            MemoryDataType.Double => sizeof(double),
            MemoryDataType.Pointer => sizeof(long),
            _ => throw new ArgumentOutOfRangeException(nameof(dataType))
        };

    // ── Phase 7A: New IScanEngine overloads ──

    private const uint ProcessSuspendResume = 0x0800;
    private const uint MemMapped = 0x40000;

    public Task<ScanResultSet> StartScanAsync(
        int processId,
        ScanConstraints constraints,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var accessFlags = ProcessQueryInformation | ProcessVmRead;
            if (options.SuspendProcess) accessFlags |= ProcessSuspendResume;

            var handle = OpenProcess(accessFlags, false, processId);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException($"Unable to open process {processId}.");

            try
            {
                if (options.SuspendProcess) NtSuspendProcess(handle);
                try
                {
                    var regions = EnumerateRegionsCore(handle, options)
                        .Where(r => r.IsReadable && (!options.WritableOnly || r.IsWritable))
                        .ToArray();

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var valueSize = GetValueSize(constraints.DataType);
                    var step = options.Alignment > 0 ? options.Alignment : valueSize;
                    var threadCount = options.MaxThreads > 0 ? options.MaxThreads : Math.Max(1, Environment.ProcessorCount);

                    // Partition regions across threads
                    var chunks = Enumerable.Range(0, threadCount)
                        .Select(i => regions.Where((_, idx) => idx % threadCount == i).ToArray())
                        .ToArray();

                    var totalResults = 0;
                    var regionsCompleted = 0;
                    var totalBytesScanned = 0L;
                    var allResults = new List<ScanResultEntry>[threadCount];

                    Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount, CancellationToken = cancellationToken },
                        threadIdx =>
                        {
                            var threadHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
                            if (threadHandle == IntPtr.Zero) return;
                            try
                            {
                                var localResults = new List<ScanResultEntry>();
                                foreach (var region in chunks[threadIdx])
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    if (Interlocked.CompareExchange(ref totalResults, 0, 0) >= MaxScanResults) break;

                                    // Chunked reading for large regions (7A.6)
                                    const int chunkSize = 64 * 1024 * 1024;
                                    for (long regionOffset = 0; regionOffset < region.RegionSize; regionOffset += chunkSize)
                                    {
                                        var readSize = (int)Math.Min(region.RegionSize - regionOffset, chunkSize);
                                        var readAddr = region.BaseAddress + (nuint)regionOffset;
                                        var regionBytes = ReadRegion(threadHandle, readAddr, readSize);
                                        if (regionBytes is null) continue;

                                        Interlocked.Add(ref totalBytesScanned, regionBytes.Length);
                                        ScanRegionForInitialWithOptions(readAddr, regionBytes, constraints, valueSize, step, options, localResults, cancellationToken);
                                    }

                                    Interlocked.Increment(ref regionsCompleted);
                                    Interlocked.Add(ref totalResults, 0); // just a read barrier
                                    progress?.Report(new ScanProgress(regionsCompleted, regions.Length, totalBytesScanned,
                                        Interlocked.CompareExchange(ref totalResults, 0, 0), sw.Elapsed.TotalSeconds));
                                }
                                allResults[threadIdx] = localResults;
                            }
                            finally { CloseHandle(threadHandle); }
                        });

                    // Merge results
                    var merged = new List<ScanResultEntry>();
                    foreach (var list in allResults)
                    {
                        if (list is null) continue;
                        merged.AddRange(list);
                        if (merged.Count >= MaxScanResults) break;
                    }
                    if (merged.Count > MaxScanResults)
                        merged = merged.Take(MaxScanResults).ToList();

                    return new ScanResultSet(
                        $"scan-{Guid.NewGuid().ToString("N")[..8]}",
                        processId, constraints, merged, regions.Length,
                        Interlocked.Read(ref totalBytesScanned),
                        DateTimeOffset.UtcNow,
                        merged.Count >= MaxScanResults);
                }
                finally { if (options.SuspendProcess) NtResumeProcess(handle); }
            }
            finally { CloseHandle(handle); }
        }, cancellationToken);

    public Task<ScanResultSet> RefineScanAsync(
        ScanResultSet previousResults,
        ScanConstraints refinement,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, previousResults.ProcessId);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException($"Unable to open process {previousResults.ProcessId}.");

            try
            {
                var valueSize = GetValueSize(refinement.DataType);
                var refined = new List<ScanResultEntry>();

                foreach (var previous in previousResults.Results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (refined.Count >= MaxScanResults) break;

                    var buffer = new byte[valueSize];
                    if (!ReadProcessMemory(handle, (IntPtr)previous.Address, buffer, valueSize, out var bytesRead) || bytesRead != valueSize)
                        continue;

                    var currentValue = FormatValue(buffer, refinement.DataType);
                    var matches = EvaluateConstraint(currentValue, previous.CurrentValue, refinement, options, buffer);

                    if (matches)
                        refined.Add(new ScanResultEntry(previous.Address, currentValue, previous.CurrentValue, buffer));
                }

                return new ScanResultSet(
                    previousResults.ScanId, previousResults.ProcessId, refinement,
                    refined, previousResults.TotalRegionsScanned, previousResults.TotalBytesScanned,
                    DateTimeOffset.UtcNow, refined.Count >= MaxScanResults);
            }
            finally { CloseHandle(handle); }
        }, cancellationToken);

    public Task<ScanResultSet> GroupedScanAsync(
        int processId,
        IReadOnlyList<GroupedScanConstraint> groups,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException($"Unable to open process {processId}.");

            try
            {
                var regions = EnumerateRegionsCore(handle, options)
                    .Where(r => r.IsReadable && (!options.WritableOnly || r.IsWritable))
                    .ToArray();

                var results = new List<ScanResultEntry>();
                var totalBytesScanned = 0L;

                foreach (var region in regions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (results.Count >= MaxScanResults) break;

                    var regionBytes = ReadRegion(handle, region.BaseAddress, (int)Math.Min(region.RegionSize, 64 * 1024 * 1024));
                    if (regionBytes is null) continue;
                    totalBytesScanned += regionBytes.Length;

                    // Apply each group's constraints against the same buffer
                    foreach (var group in groups)
                    {
                        var valueSize = GetValueSize(group.Constraints.DataType);
                        var step = options.Alignment > 0 ? options.Alignment : valueSize;

                        for (var offset = 0; offset <= regionBytes.Length - valueSize; offset += step)
                        {
                            if (results.Count >= MaxScanResults) break;

                            var slice = regionBytes[offset..(offset + valueSize)];
                            var currentValue = FormatValue(slice, group.Constraints.DataType);
                            var matches = EvaluateInitialConstraint(currentValue, group.Constraints, options, slice);

                            if (matches)
                            {
                                var address = region.BaseAddress + (nuint)offset;
                                results.Add(new ScanResultEntry(address, currentValue, null, slice.ToArray(), group.Label));
                            }
                        }
                    }
                }

                var primaryConstraints = groups.Count > 0 ? groups[0].Constraints : new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, null);
                return new ScanResultSet(
                    $"scan-{Guid.NewGuid().ToString("N")[..8]}",
                    processId, primaryConstraints, results, regions.Length,
                    totalBytesScanned, DateTimeOffset.UtcNow,
                    results.Count >= MaxScanResults);
            }
            finally { CloseHandle(handle); }
        }, cancellationToken);

    // ── Scan helpers with options ──

    private static void ScanRegionForInitialWithOptions(
        nuint regionBase, byte[] regionBytes,
        ScanConstraints constraints, int valueSize, int step,
        ScanOptions options, List<ScanResultEntry> results,
        CancellationToken ct)
    {
        if (constraints.ScanType == ScanType.ArrayOfBytes && !string.IsNullOrWhiteSpace(constraints.Value))
        {
            ScanRegionForAob(regionBase, regionBytes, constraints.Value, results);
            return;
        }

        var matchCount = 0;
        for (var offset = 0; offset <= regionBytes.Length - valueSize; offset += step)
        {
            if (results.Count >= MaxScanResults) return;
            if (++matchCount % 10_000 == 0) ct.ThrowIfCancellationRequested();

            var slice = regionBytes[offset..(offset + valueSize)];
            var currentValue = FormatValue(slice, constraints.DataType);
            var matches = EvaluateInitialConstraint(currentValue, constraints, options, slice);

            if (matches)
            {
                var address = regionBase + (nuint)offset;
                results.Add(new ScanResultEntry(address, currentValue, null, slice.ToArray()));
            }
        }
    }

    private static bool EvaluateInitialConstraint(string currentValue, ScanConstraints constraints, ScanOptions options, byte[] rawBytes)
    {
        // Float epsilon for ExactValue
        if (options.FloatEpsilon.HasValue && constraints.ScanType == ScanType.ExactValue
            && constraints.Value is not null
            && (constraints.DataType is MemoryDataType.Float or MemoryDataType.Double))
        {
            if (constraints.DataType == MemoryDataType.Float
                && float.TryParse(constraints.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var targetF)
                && float.TryParse(currentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var currentF))
                return Math.Abs(currentF - targetF) <= options.FloatEpsilon.Value;

            if (constraints.DataType == MemoryDataType.Double
                && double.TryParse(constraints.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var targetD)
                && double.TryParse(currentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var currentD))
                return Math.Abs(currentD - targetD) <= options.FloatEpsilon.Value;
        }

        return constraints.ScanType switch
        {
            ScanType.ExactValue => string.Equals(currentValue, constraints.Value, StringComparison.OrdinalIgnoreCase),
            ScanType.UnknownInitialValue => true,
            ScanType.BiggerThan => CompareValues(currentValue, constraints.Value ?? "0", constraints.DataType) > 0,
            ScanType.SmallerThan => CompareValues(currentValue, constraints.Value ?? "0", constraints.DataType) < 0,
            ScanType.ValueBetween => IsValueBetween(currentValue, constraints.Value, constraints.DataType),
            _ => false
        };
    }

    private static bool EvaluateConstraint(string currentValue, string? previousValue, ScanConstraints refinement, ScanOptions options, byte[] rawBytes)
    {
        // BitChanged handling
        if (refinement.ScanType == ScanType.BitChanged && previousValue is not null)
        {
            if (byte.TryParse(previousValue, out var prevByte) && rawBytes.Length > 0)
            {
                var currByte = rawBytes[0];
                if (options.BitPosition.HasValue)
                    return ((prevByte >> options.BitPosition.Value) & 1) != ((currByte >> options.BitPosition.Value) & 1);
                return prevByte != currByte;
            }
            return false;
        }

        // Float epsilon for refinement ExactValue
        if (options.FloatEpsilon.HasValue && refinement.ScanType == ScanType.ExactValue
            && refinement.Value is not null
            && (refinement.DataType is MemoryDataType.Float or MemoryDataType.Double))
        {
            if (refinement.DataType == MemoryDataType.Float
                && float.TryParse(refinement.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var targetF)
                && float.TryParse(currentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var currentF))
                return Math.Abs(currentF - targetF) <= options.FloatEpsilon.Value;

            if (refinement.DataType == MemoryDataType.Double
                && double.TryParse(refinement.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var targetD)
                && double.TryParse(currentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var currentD))
                return Math.Abs(currentD - targetD) <= options.FloatEpsilon.Value;
        }

        return refinement.ScanType switch
        {
            ScanType.ExactValue => string.Equals(currentValue, refinement.Value, StringComparison.OrdinalIgnoreCase),
            ScanType.Increased => CompareValues(currentValue, previousValue ?? currentValue, refinement.DataType) > 0,
            ScanType.Decreased => CompareValues(currentValue, previousValue ?? currentValue, refinement.DataType) < 0,
            ScanType.Changed => !string.Equals(currentValue, previousValue, StringComparison.OrdinalIgnoreCase),
            ScanType.Unchanged => string.Equals(currentValue, previousValue, StringComparison.OrdinalIgnoreCase),
            ScanType.UnknownInitialValue => true,
            ScanType.BiggerThan => CompareValues(currentValue, refinement.Value ?? "0", refinement.DataType) > 0,
            ScanType.SmallerThan => CompareValues(currentValue, refinement.Value ?? "0", refinement.DataType) < 0,
            ScanType.ValueBetween => IsValueBetween(currentValue, refinement.Value, refinement.DataType),
            _ => false
        };
    }

    /// <summary>Region enumeration with options support (writable-only filter, memory-mapped files).</summary>
    private static List<MemoryRegionDescriptor> EnumerateRegionsCore(IntPtr processHandle, ScanOptions options)
    {
        var regions = new List<MemoryRegionDescriptor>();
        var address = nuint.Zero;
        var mbiSize = Marshal.SizeOf<MemoryBasicInformation>();

        while (true)
        {
            var bytesReturned = VirtualQueryEx(processHandle, (IntPtr)address, out var mbi, mbiSize);
            if (bytesReturned == 0) break;

            if (mbi.State == MemCommit && mbi.RegionSize > 0)
            {
                // Include MEM_MAPPED regions if option is set
                var isMapped = (mbi.Type & MemMapped) != 0;
                if (!isMapped || options.IncludeMemoryMappedFiles)
                {
                    var isReadable = IsReadableProtection(mbi.Protect);
                    var isWritable = IsWritableProtection(mbi.Protect);
                    var isExecutable = IsExecutableProtection(mbi.Protect);

                    regions.Add(new MemoryRegionDescriptor(
                        (nuint)(ulong)mbi.BaseAddress,
                        (long)(ulong)mbi.RegionSize,
                        isReadable, isWritable, isExecutable));
                }
            }

            var nextAddress = (ulong)mbi.BaseAddress + (ulong)mbi.RegionSize;
            if (nextAddress <= (ulong)address) break;
            address = (nuint)nextAddress;
        }

        return regions;
    }

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

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
