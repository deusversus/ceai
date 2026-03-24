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
}
