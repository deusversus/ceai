namespace CEAISuite.Engine.Abstractions;

public enum ScanType
{
    ExactValue,
    UnknownInitialValue,
    Increased,
    Decreased,
    Changed,
    Unchanged,
    ArrayOfBytes,
    BiggerThan,
    SmallerThan,
    ValueBetween
}

public sealed record MemoryRegionDescriptor(
    nuint BaseAddress,
    long RegionSize,
    bool IsReadable,
    bool IsWritable,
    bool IsExecutable);

public sealed record ScanConstraints(
    MemoryDataType DataType,
    ScanType ScanType,
    string? Value);

public sealed record ScanResultEntry(
    nuint Address,
    string CurrentValue,
    string? PreviousValue,
    IReadOnlyList<byte> RawBytes);

public sealed record ScanResultSet(
    string ScanId,
    int ProcessId,
    ScanConstraints Constraints,
    IReadOnlyList<ScanResultEntry> Results,
    int TotalRegionsScanned,
    long TotalBytesScanned,
    DateTimeOffset CompletedAtUtc,
    bool IsTruncated = false,
    int SkippedRegions = 0);

public interface IScanEngine
{
    Task<IReadOnlyList<MemoryRegionDescriptor>> EnumerateRegionsAsync(
        int processId,
        CancellationToken cancellationToken = default);

    Task<ScanResultSet> StartScanAsync(
        int processId,
        ScanConstraints constraints,
        CancellationToken cancellationToken = default);

    Task<ScanResultSet> RefineScanAsync(
        ScanResultSet previousResults,
        ScanConstraints refinement,
        CancellationToken cancellationToken = default);
}
