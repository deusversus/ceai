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
    ValueBetween,
    BitChanged
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

public sealed record ScanOptions(
    int Alignment = 0,
    bool WritableOnly = true,
    bool SuspendProcess = false,
    float? FloatEpsilon = null,
    int MaxThreads = 0,
    bool IncludeMemoryMappedFiles = false,
    int? BitPosition = null,
    bool ShowAsHex = false);

public sealed record ScanProgress(
    int RegionsCompleted,
    int TotalRegions,
    long BytesScanned,
    int ResultsSoFar,
    double ElapsedSeconds);

public sealed record ScanHistoryEntry(
    ScanResultSet Results,
    ScanConstraints Constraints,
    DateTimeOffset Timestamp);

public sealed record GroupedScanConstraint(
    string Label,
    ScanConstraints Constraints);

public sealed record CustomTypeDefinition(
    string Name,
    int SizeBytes,
    IReadOnlyList<CustomTypeField> Fields);

public sealed record CustomTypeField(
    string Name,
    int OffsetBytes,
    MemoryDataType FieldType);

public sealed record ScanResultEntry(
    nuint Address,
    string CurrentValue,
    string? PreviousValue,
    IReadOnlyList<byte> RawBytes,
    string? GroupLabel = null);

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

    Task<ScanResultSet> StartScanAsync(
        int processId,
        ScanConstraints constraints,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ScanResultSet> RefineScanAsync(
        ScanResultSet previousResults,
        ScanConstraints refinement,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ScanResultSet> GroupedScanAsync(
        int processId,
        IReadOnlyList<GroupedScanConstraint> groups,
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
