using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

/// <summary>
/// In-memory scan engine that returns canned results for ViewModel tests.
/// </summary>
public sealed class StubScanEngine : IScanEngine
{
    public ScanResultSet? NextScanResult { get; set; }
    public ScanResultSet? NextRefineResult { get; set; }
    public IReadOnlyList<MemoryRegionDescriptor>? NextRegions { get; set; }
    public bool SuspendProcessCalled { get; private set; }
    public ScanOptions? LastOptions { get; private set; }
    public List<ScanProgress> ReportedProgress { get; } = new();

    public Task<IReadOnlyList<MemoryRegionDescriptor>> EnumerateRegionsAsync(
        int processId, CancellationToken cancellationToken = default) =>
        Task.FromResult(NextRegions ?? (IReadOnlyList<MemoryRegionDescriptor>)Array.Empty<MemoryRegionDescriptor>());

    public Task<ScanResultSet> StartScanAsync(
        int processId, ScanConstraints constraints, CancellationToken cancellationToken = default)
    {
        var result = NextScanResult ?? new ScanResultSet(
            ScanId: "test-scan",
            ProcessId: processId,
            Constraints: constraints,
            Results: Array.Empty<ScanResultEntry>(),
            TotalRegionsScanned: 10,
            TotalBytesScanned: 40960,
            CompletedAtUtc: DateTimeOffset.UtcNow);
        return Task.FromResult(result);
    }

    public Task<ScanResultSet> RefineScanAsync(
        ScanResultSet previousResults, ScanConstraints refinement, CancellationToken cancellationToken = default)
    {
        var result = NextRefineResult ?? new ScanResultSet(
            ScanId: previousResults.ScanId,
            ProcessId: previousResults.ProcessId,
            Constraints: refinement,
            Results: Array.Empty<ScanResultEntry>(),
            TotalRegionsScanned: 5,
            TotalBytesScanned: 20480,
            CompletedAtUtc: DateTimeOffset.UtcNow);
        return Task.FromResult(result);
    }

    public Task<ScanResultSet> StartScanAsync(
        int processId, ScanConstraints constraints, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        if (options.SuspendProcess) SuspendProcessCalled = true;
        progress?.Report(new ScanProgress(1, 1, 1024, 0, 0.01));
        ReportedProgress.Add(new ScanProgress(1, 1, 1024, 0, 0.01));
        return StartScanAsync(processId, constraints, cancellationToken);
    }

    public Task<ScanResultSet> RefineScanAsync(
        ScanResultSet previousResults, ScanConstraints refinement, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        return RefineScanAsync(previousResults, refinement, cancellationToken);
    }

    public Task<ScanResultSet> GroupedScanAsync(
        int processId, IReadOnlyList<GroupedScanConstraint> groups, ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        var results = new List<ScanResultEntry>();
        foreach (var group in groups)
        {
            results.Add(new ScanResultEntry((nuint)0x1000, "100", null, new byte[] { 100, 0, 0, 0 }, group.Label));
        }
        var primaryConstraints = groups.Count > 0
            ? groups[0].Constraints
            : new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, null);
        return Task.FromResult(new ScanResultSet(
            "grouped-scan", processId, primaryConstraints, results, 1, 1024, DateTimeOffset.UtcNow));
    }
}
