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
    private readonly Stack<ScanHistoryEntry> _scanHistory = new();
    private const int MaxHistoryDepth = 20;

    // ── Custom Type Registry ──
    private readonly Dictionary<string, CustomTypeDefinition> _customTypes = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterCustomType(CustomTypeDefinition typeDef) => _customTypes[typeDef.Name] = typeDef;
    public void UnregisterCustomType(string name) => _customTypes.Remove(name);

    public ScanResultSet? LastScanResults => _lastScanResults;

    /// <summary>True if there are previous scan results to undo to.</summary>
    public bool CanUndo => _scanHistory.Count > 0;

    /// <summary>Number of undo steps available.</summary>
    public int UndoDepth => _scanHistory.Count;

    public async Task<ScanSessionOverview> StartScanAsync(
        int processId,
        MemoryDataType dataType,
        ScanType scanType,
        string? value,
        CancellationToken cancellationToken = default)
    {
        var constraints = new ScanConstraints(dataType, scanType, value?.Trim());
        var results = await scanEngine.StartScanAsync(processId, constraints, cancellationToken);
        _scanHistory.Clear(); // new scan clears history
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

        // Push current results to history before overwriting
        if (_scanHistory.Count >= MaxHistoryDepth)
        {
            // Drop oldest entry by rebuilding stack (Stack doesn't support random removal)
            // items[0] = top (most recent), items[^1] = bottom (oldest)
            var items = _scanHistory.ToArray();
            _scanHistory.Clear();
            // Keep items 0..MaxHistoryDepth-2 (drop the oldest), push in reverse to preserve order
            for (int i = MaxHistoryDepth - 2; i >= 0; i--)
                _scanHistory.Push(items[i]);
        }
        _scanHistory.Push(new ScanHistoryEntry(_lastScanResults, _lastScanResults.Constraints, DateTimeOffset.UtcNow));

        var refinement = new ScanConstraints(_lastScanResults.Constraints.DataType, scanType, value?.Trim());
        var results = await scanEngine.RefineScanAsync(_lastScanResults, refinement, cancellationToken);
        _lastScanResults = results;
        return ToOverview(results);
    }

    /// <summary>Undo the last refinement, restoring the previous scan results.</summary>
    public ScanSessionOverview? UndoScan()
    {
        if (_scanHistory.Count == 0) return null;
        _lastScanResults = _scanHistory.Pop().Results;
        return ToOverview(_lastScanResults);
    }

    public void ResetScan()
    {
        _lastScanResults = null;
        _scanHistory.Clear();
    }

    public Task<IReadOnlyList<MemoryRegionDescriptor>> EnumerateRegionsAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        scanEngine.EnumerateRegionsAsync(processId, cancellationToken);

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
