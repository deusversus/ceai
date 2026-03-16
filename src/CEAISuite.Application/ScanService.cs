using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed record ScanResultOverview(
    string Address,
    string CurrentValue,
    string? PreviousValue,
    string HexBytes);

public sealed record ScanSessionOverview(
    string ScanId,
    int ProcessId,
    string DataType,
    string ScanType,
    string? SearchValue,
    int ResultCount,
    int TotalRegionsScanned,
    string TotalBytesScanned,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<ScanResultOverview> Results);

public sealed class ScanService(IScanEngine scanEngine)
{
    private ScanResultSet? _lastScanResults;

    public ScanResultSet? LastScanResults => _lastScanResults;

    public async Task<ScanSessionOverview> StartScanAsync(
        int processId,
        MemoryDataType dataType,
        ScanType scanType,
        string? value,
        CancellationToken cancellationToken = default)
    {
        var constraints = new ScanConstraints(dataType, scanType, value?.Trim());
        var results = await scanEngine.StartScanAsync(processId, constraints, cancellationToken);
        _lastScanResults = results;
        return ToOverview(results);
    }

    public async Task<ScanSessionOverview> RefineScanAsync(
        ScanType scanType,
        string? value,
        CancellationToken cancellationToken = default)
    {
        if (_lastScanResults is null)
        {
            throw new InvalidOperationException("No active scan to refine. Start a scan first.");
        }

        var refinement = new ScanConstraints(_lastScanResults.Constraints.DataType, scanType, value?.Trim());
        var results = await scanEngine.RefineScanAsync(_lastScanResults, refinement, cancellationToken);
        _lastScanResults = results;
        return ToOverview(results);
    }

    public void ResetScan()
    {
        _lastScanResults = null;
    }

    private static ScanSessionOverview ToOverview(ScanResultSet resultSet) =>
        new(
            resultSet.ScanId,
            resultSet.ProcessId,
            resultSet.Constraints.DataType.ToString(),
            resultSet.Constraints.ScanType.ToString(),
            resultSet.Constraints.Value,
            resultSet.Results.Count,
            resultSet.TotalRegionsScanned,
            FormatBytes(resultSet.TotalBytesScanned),
            resultSet.CompletedAtUtc,
            resultSet.Results
                .Take(200)
                .Select(
                    entry => new ScanResultOverview(
                        $"0x{entry.Address:X}",
                        entry.CurrentValue,
                        entry.PreviousValue,
                        Convert.ToHexString(entry.RawBytes.ToArray())))
                .ToArray());

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} bytes"
        };
}
