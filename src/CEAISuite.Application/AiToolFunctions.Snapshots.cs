using CEAISuite.Application.AgentLoop;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    // ── Memory Snapshot Tools ──

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Capture a memory snapshot for later comparison. Stores a copy of the bytes at the given address range.")]
    public async Task<string> CaptureSnapshot(
        [Description("Process ID")] int processId,
        [Description("Start address as hex")] string address,
        [Description("Number of bytes to capture")] int length,
        [Description("Optional label for this snapshot")] string? label = null)
    {
        if (snapshotService is null) return "Snapshot service not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            var snap = await snapshotService.CaptureAsync(processId, addr, length, label);
            return $"Snapshot '{snap.Label}' captured: {snap.Data.Length} bytes at 0x{snap.BaseAddress:X} (ID: {snap.Id})";
        }
        catch (Exception ex) { return $"CaptureSnapshot failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Compare two snapshots and show what changed between them. Useful for before/after analysis.")]
    public string CompareSnapshots(
        [Description("First snapshot ID")] string snapshotIdA,
        [Description("Second snapshot ID")] string snapshotIdB)
    {
        if (snapshotService is null) return "Snapshot service not available.";
        try
        {
            var diff = snapshotService.Compare(snapshotIdA, snapshotIdB);
            if (diff.Changes.Count == 0)
                return $"No differences found ({diff.TotalBytesCompared} bytes compared).";

            var cap = _limits.MaxSnapshotDiffEntries;
            var lines = diff.Changes.Take(cap).Select(c =>
                $"  +0x{c.Offset:X4}: {BitConverter.ToString(c.OldBytes).Replace("-", " ")} → " +
                $"{BitConverter.ToString(c.NewBytes).Replace("-", " ")} ({c.Interpretation})");
            var extra = diff.Changes.Count > cap ? $"\n  ... and {diff.Changes.Count - cap} more changes" : "";
            return $"Found {diff.Changes.Count} change(s), {diff.ChangedByteCount} byte(s) modified (showing {Math.Min(cap, diff.Changes.Count)}):\n{string.Join('\n', lines)}{extra}";
        }
        catch (Exception ex) { return $"CompareSnapshots failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Compare a previous snapshot with the current live memory state.")]
    public async Task<string> CompareSnapshotWithLive(
        [Description("Snapshot ID to compare against live memory")] string snapshotId)
    {
        if (snapshotService is null) return "Snapshot service not available.";
        try
        {
            var diff = await snapshotService.CompareWithLiveAsync(snapshotId);
            if (diff.Changes.Count == 0)
                return $"Memory unchanged since snapshot ({diff.TotalBytesCompared} bytes compared).";

            var cap = _limits.MaxSnapshotDiffEntries;
            var lines = diff.Changes.Take(cap).Select(c =>
                $"  +0x{c.Offset:X4}: {BitConverter.ToString(c.OldBytes).Replace("-", " ")} → " +
                $"{BitConverter.ToString(c.NewBytes).Replace("-", " ")} ({c.Interpretation})");
            var extra = diff.Changes.Count > cap ? $"\n  ... and {diff.Changes.Count - cap} more changes" : "";
            return $"Found {diff.Changes.Count} change(s) since snapshot (showing {Math.Min(cap, diff.Changes.Count)}):\n{string.Join('\n', lines)}{extra}";
        }
        catch (Exception ex) { return $"CompareSnapshotWithLive failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all captured memory snapshots.")]
    public string ListSnapshots()
    {
        if (snapshotService is null) return "Snapshot service not available.";
        var snaps = snapshotService.ListSnapshots();
        if (snaps.Count == 0) return "No snapshots captured.";

        var lines = snaps.Take(50).Select(s =>
            $"  {s.Id}: \"{s.Label}\" — {s.Data.Length} bytes @ 0x{s.BaseAddress:X} ({s.CapturedAt.ToLocalTime():g})");
        return $"{snaps.Count} snapshot(s):\n{string.Join('\n', lines)}";
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Delete a memory snapshot by ID.")]
    public string DeleteSnapshot(
        [Description("Snapshot ID to delete")] string snapshotId)
    {
        if (snapshotService is null) return "Snapshot service not available.";
        return snapshotService.DeleteSnapshot(snapshotId) ? $"Snapshot '{snapshotId}' deleted." : $"Snapshot '{snapshotId}' not found.";
    }

    // ── Pointer Rescan Tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Re-resolve a pointer path to verify it still works after game restart/update. Walks the chain from module base through offsets.")]
    public async Task<string> RescanPointerPath(
        [Description("Process ID")] int processId,
        [Description("Module name (e.g. GameAssembly.dll)")] string moduleName,
        [Description("Module offset as hex (e.g. 0x1A2B3C)")] string moduleOffset,
        [Description("Comma-separated offsets as hex (e.g. 0x10,0x30,0x8)")] string offsets,
        [Description("Optional: expected target address as hex")] string? expectedAddress = null)
    {
        if (pointerRescanService is null) return "Pointer rescan service not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            var modOffset = (long)ParseAddress(moduleOffset);
            var offsetList = offsets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(o => (long)ParseAddress(o)).ToList();

            var path = new PointerPath(moduleName, 0, modOffset, offsetList, 0);
            nuint? expected = expectedAddress is not null ? ParseAddress(expectedAddress) : null;
            var result = await pointerRescanService.RescanPathAsync(processId, path, expected);

            return $"Pointer path {path.Display}\n" +
                   $"  Status: {result.Status}\n" +
                   $"  Resolved: {(result.NewResolvedAddress.HasValue ? $"0x{result.NewResolvedAddress.Value:X}" : "N/A")}\n" +
                   $"  Valid: {result.IsValid} | Stability: {result.StabilityScore:P0}";
        }
        catch (Exception ex) { return $"RescanPointerPath failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Validate multiple pointer paths and rank by stability. Returns which paths still work and which need fresh scanning.")]
    public async Task<string> ValidatePointerPaths(
        [Description("Process ID")] int processId,
        [Description("Original target address as hex")] string targetAddress)
    {
        if (pointerRescanService is null) return "Pointer rescan service not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            // Get pointer paths from the address table entries that have pointer chains
            var entries = addressTableService.Entries;
            var pointerEntries = entries.Where(e => e.Label.Contains('→') || e.Label.Contains("ptr", StringComparison.OrdinalIgnoreCase)).ToList();

            var target = ParseAddress(targetAddress);
            if (pointerEntries.Count == 0)
                return "No pointer path entries found in the address table. Use ScanForPointers first.";

            return $"Found {pointerEntries.Count} pointer-related entries. Use RescanPointerPath on individual paths for validation.";
        }
        catch (Exception ex) { return $"ValidatePointerPaths failed: {ex.Message}"; }
    }

    // ── Call Stack Tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Walk the call stack of a thread in the attached process. Shows function call chain with module offsets.")]
    public async Task<string> GetCallStack(
        [Description("Process ID")] int processId,
        [Description("Thread ID (use 0 for main thread)")] int threadId = 0,
        [Description("Maximum frames to capture")] int maxFrames = 32)
    {
        if (callStackEngine is null) return "Call stack engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            var attachment = await engineFacade.AttachAsync(processId);

            // If threadId is 0, enumerate threads and pick main
            if (threadId == 0)
            {
                var allStacks = await callStackEngine.WalkAllThreadsAsync(processId, attachment.Modules, maxFrames);
                if (allStacks.Count == 0) return "No thread stacks could be captured.";

                // Pick the thread with the most frames (likely main)
                var best = allStacks.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
                if (best.Key == 0 && best.Value is null) return "No thread stacks could be captured.";
                threadId = best.Key;
                var frames = best.Value;
                return FormatCallStack(threadId, frames);
            }

            var stack = await callStackEngine.WalkStackAsync(processId, threadId, attachment.Modules, maxFrames);
            return FormatCallStack(threadId, stack);
        }
        catch (Exception ex) { return $"GetCallStack failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Walk call stacks of all threads in the process. Returns frames per thread with module resolution.")]
    public async Task<string> GetAllThreadStacks(
        [Description("Process ID")] int processId,
        [Description("Maximum frames per thread")] int maxFrames = 0)
    {
        if (maxFrames <= 0) maxFrames = _limits.MaxStackFrames;
        if (callStackEngine is null) return "Call stack engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            var attachment = await engineFacade.AttachAsync(processId);
            var allStacks = await callStackEngine.WalkAllThreadsAsync(processId, attachment.Modules, maxFrames);

            if (allStacks.Count == 0) return "No thread stacks could be captured.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Captured stacks for {allStacks.Count} thread(s):");
            foreach (var (tid, frames) in allStacks.OrderByDescending(kv => kv.Value.Count).Take(4))
            {
                sb.AppendLine(FormatCallStack(tid, frames));
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"GetAllThreadStacks failed: {ex.Message}"; }
    }

    private static string FormatCallStack(int threadId, IReadOnlyList<CallStackFrame> frames)
    {
        if (frames.Count == 0) return $"Thread {threadId}: no frames captured";

        var lines = frames.Select(f =>
        {
            var location = f.ModuleName is not null
                ? $"{f.ModuleName}+0x{f.ModuleOffset:X}"
                : $"0x{f.InstructionPointer:X}";
            return $"  #{f.FrameIndex}: {location} (RSP=0x{f.StackPointer:X})";
        });
        return $"Thread {threadId} ({frames.Count} frames):\n{string.Join('\n', lines)}";
    }

}
