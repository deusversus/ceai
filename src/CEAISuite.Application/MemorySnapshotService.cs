using System.Globalization;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Captures memory snapshots and computes diffs between them.
/// Enables before/after comparison workflows for game state analysis.
/// </summary>
public sealed class MemorySnapshotService
{
    private readonly IEngineFacade _engine;
    private readonly Dictionary<string, MemorySnapshot> _snapshots = new(StringComparer.Ordinal);

    public MemorySnapshotService(IEngineFacade engine) => _engine = engine;

    /// <summary>Capture a snapshot of a memory region.</summary>
    public async Task<MemorySnapshot> CaptureAsync(
        int processId, nuint address, int length, string? label = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _engine.ReadMemoryAsync(processId, address, length, cancellationToken).ConfigureAwait(false);
        var snapshot = new MemorySnapshot(
            Id: $"snap-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}",
            Label: label ?? $"Snapshot @ 0x{address:X}",
            ProcessId: processId,
            BaseAddress: address,
            Data: result.Bytes.ToArray(),
            CapturedAt: DateTimeOffset.UtcNow);

        _snapshots[snapshot.Id] = snapshot;
        return snapshot;
    }

    /// <summary>Compare two snapshots and return differences.</summary>
    public SnapshotDiff Compare(string snapshotIdA, string snapshotIdB)
    {
        if (!_snapshots.TryGetValue(snapshotIdA, out var a))
            throw new KeyNotFoundException($"Snapshot '{snapshotIdA}' not found.");
        if (!_snapshots.TryGetValue(snapshotIdB, out var b))
            throw new KeyNotFoundException($"Snapshot '{snapshotIdB}' not found.");

        return ComputeDiff(a, b);
    }

    /// <summary>Compare a snapshot with current live memory.</summary>
    public async Task<SnapshotDiff> CompareWithLiveAsync(
        string snapshotId, CancellationToken cancellationToken = default)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var snap))
            throw new KeyNotFoundException($"Snapshot '{snapshotId}' not found.");

        var live = await CaptureAsync(snap.ProcessId, snap.BaseAddress, snap.Data.Length, "Live", cancellationToken).ConfigureAwait(false);
        return ComputeDiff(snap, live);
    }

    public IReadOnlyList<MemorySnapshot> ListSnapshots() =>
        _snapshots.Values.OrderByDescending(s => s.CapturedAt).ToList();

    public bool DeleteSnapshot(string snapshotId) => _snapshots.Remove(snapshotId);

    public MemorySnapshot? GetSnapshot(string snapshotId) =>
        _snapshots.TryGetValue(snapshotId, out var s) ? s : null;

    private static SnapshotDiff ComputeDiff(MemorySnapshot a, MemorySnapshot b)
    {
        var changes = new List<SnapshotChange>();
        int minLen = Math.Min(a.Data.Length, b.Data.Length);

        int i = 0;
        while (i < minLen)
        {
            if (a.Data[i] != b.Data[i])
            {
                // Find contiguous changed region
                int start = i;
                while (i < minLen && a.Data[i] != b.Data[i]) i++;
                int len = i - start;

                var oldBytes = new byte[len];
                var newBytes = new byte[len];
                Array.Copy(a.Data, start, oldBytes, 0, len);
                Array.Copy(b.Data, start, newBytes, 0, len);

                changes.Add(new SnapshotChange(
                    Offset: start,
                    Address: a.BaseAddress + (nuint)start,
                    Length: len,
                    OldBytes: oldBytes,
                    NewBytes: newBytes,
                    Interpretation: InterpretChange(oldBytes, newBytes)));
            }
            else
            {
                i++;
            }
        }

        // Handle size difference
        if (a.Data.Length != b.Data.Length)
        {
            changes.Add(new SnapshotChange(
                Offset: minLen,
                Address: a.BaseAddress + (nuint)minLen,
                Length: Math.Abs(a.Data.Length - b.Data.Length),
                OldBytes: a.Data.Length > minLen ? a.Data[minLen..] : [],
                NewBytes: b.Data.Length > minLen ? b.Data[minLen..] : [],
                Interpretation: "Size difference"));
        }

        return new SnapshotDiff(
            SnapshotA: a,
            SnapshotB: b,
            Changes: changes,
            TotalBytesCompared: minLen,
            ChangedByteCount: changes.Sum(c => c.Length));
    }

    private static string InterpretChange(byte[] old, byte[] @new)
    {
        if (old.Length == 4 && @new.Length == 4)
        {
            int oldInt = BitConverter.ToInt32(old);
            int newInt = BitConverter.ToInt32(@new);
            float oldFloat = BitConverter.ToSingle(old);
            float newFloat = BitConverter.ToSingle(@new);

            var parts = new List<string>();
            parts.Add($"Int32: {oldInt} → {newInt} (Δ{newInt - oldInt:+#;-#;0})");

            if (IsReasonableFloat(oldFloat) && IsReasonableFloat(newFloat))
                parts.Add($"Float: {oldFloat:F4} → {newFloat:F4}");

            return string.Join(" | ", parts);
        }

        if (old.Length == 8 && @new.Length == 8)
        {
            long oldLong = BitConverter.ToInt64(old);
            long newLong = BitConverter.ToInt64(@new);
            double oldDouble = BitConverter.ToDouble(old);
            double newDouble = BitConverter.ToDouble(@new);

            var parts = new List<string>();
            parts.Add($"Int64: {oldLong} → {newLong}");

            if (IsReasonableDouble(oldDouble) && IsReasonableDouble(newDouble))
                parts.Add($"Double: {oldDouble:F4} → {newDouble:F4}");

            return string.Join(" | ", parts);
        }

        return $"{old.Length} byte(s) changed";
    }

    private static bool IsReasonableFloat(float v) =>
        !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) < 1e10f;

    private static bool IsReasonableDouble(double v) =>
        !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e15;
}

public sealed record MemorySnapshot(
    string Id,
    string Label,
    int ProcessId,
    nuint BaseAddress,
    byte[] Data,
    DateTimeOffset CapturedAt);

public sealed record SnapshotChange(
    int Offset,
    nuint Address,
    int Length,
    byte[] OldBytes,
    byte[] NewBytes,
    string Interpretation);

public sealed record SnapshotDiff(
    MemorySnapshot SnapshotA,
    MemorySnapshot SnapshotB,
    IReadOnlyList<SnapshotChange> Changes,
    int TotalBytesCompared,
    int ChangedByteCount);
