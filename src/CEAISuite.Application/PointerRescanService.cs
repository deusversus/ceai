using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Result of re-resolving a pointer path after game update or rebasing.
/// </summary>
public sealed record PointerRescanResult(
    PointerPath OriginalPath,
    nuint? NewResolvedAddress,
    bool IsValid,
    double StabilityScore,
    string Status);

/// <summary>
/// Re-resolves previously discovered pointer paths when the game updates,
/// rebases modules, or restarts. Validates and rates paths by stability.
/// </summary>
public sealed class PointerRescanService(IEngineFacade engine)
{
    /// <summary>
    /// Re-resolve a single pointer path and check if it still points to a valid target.
    /// Walks the chain: module_base + module_offset → [+off1] → [+off2] → ... → final address
    /// </summary>
    public async Task<PointerRescanResult> RescanPathAsync(
        int processId, PointerPath path, nuint? expectedValue = null,
        CancellationToken ct = default)
    {
        try
        {
            var attachment = await engine.AttachAsync(processId, ct);
            var module = attachment.Modules.FirstOrDefault(m =>
                m.Name.Equals(path.ModuleName, StringComparison.OrdinalIgnoreCase));

            if (module is null)
                return new PointerRescanResult(path, null, false, 0, $"Module '{path.ModuleName}' not found");

            // Start from module base + offset
            nuint currentAddr = (nuint)((long)module.BaseAddress + path.ModuleOffset);

            // Walk each offset in the chain
            foreach (var offset in path.Offsets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var readResult = await engine.ReadValueAsync(processId, currentAddr, MemoryDataType.Pointer, ct);
                    if (!nuint.TryParse(readResult.DisplayValue.Replace("0x", ""),
                        System.Globalization.NumberStyles.HexNumber, null, out var ptrValue))
                        return new PointerRescanResult(path, null, false, 0, $"Failed to read pointer at 0x{currentAddr:X}");

                    if (ptrValue == 0)
                        return new PointerRescanResult(path, null, false, 0, $"Null pointer at 0x{currentAddr:X}");

                    currentAddr = (nuint)((long)ptrValue + offset);
                }
                catch (Exception ex)
                {
                    return new PointerRescanResult(path, null, false, 0, $"Read error at 0x{currentAddr:X}: {ex.Message}");
                }
            }

            // Determine stability score
            double stability = 1.0;
            bool valid = true;

            // Check if the resolved address is in a reasonable range
            if (currentAddr < 0x10000 || currentAddr > 0x7FFFFFFFFFFF)
            {
                stability *= 0.2;
                valid = false;
            }

            // If we know the expected value, check it
            if (expectedValue.HasValue)
            {
                if (currentAddr == expectedValue.Value)
                    stability = 1.0;
                else
                {
                    // Within 0x1000 — might be rebased
                    long diff = Math.Abs((long)currentAddr - (long)expectedValue.Value);
                    if (diff < 0x1000)
                        stability = 0.7;
                    else
                        stability = 0.1;
                    valid = diff < 0x1000;
                }
            }

            // Bonus: static module base paths are more stable
            if (path.Offsets.Count <= 2) stability = Math.Min(1.0, stability + 0.1);
            if (path.ModuleName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                stability = Math.Min(1.0, stability + 0.05);

            string status = valid ? "Valid" : "Address may have shifted";
            return new PointerRescanResult(path, currentAddr, valid, Math.Round(stability, 2), status);
        }
        catch (Exception ex)
        {
            return new PointerRescanResult(path, null, false, 0, $"Rescan failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-resolve multiple pointer paths and rank by stability.
    /// </summary>
    public async Task<IReadOnlyList<PointerRescanResult>> RescanAllAsync(
        int processId, IReadOnlyList<PointerPath> paths, nuint? expectedValue = null,
        CancellationToken ct = default)
    {
        var results = new List<PointerRescanResult>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RescanPathAsync(processId, path, expectedValue, ct));
        }

        return results.OrderByDescending(r => r.StabilityScore)
                      .ThenBy(r => r.OriginalPath.Offsets.Count)
                      .ToList();
    }

    /// <summary>
    /// Find which of the existing paths still resolve to the same address, re-scan for new ones if none do.
    /// </summary>
    public async Task<(IReadOnlyList<PointerRescanResult> Validated, bool NeedsFreshScan)> ValidateAndRecoverAsync(
        int processId, IReadOnlyList<PointerPath> existingPaths, nuint originalTarget,
        CancellationToken ct = default)
    {
        var results = await RescanAllAsync(processId, existingPaths, originalTarget, ct);
        var validPaths = results.Where(r => r.IsValid).ToList();

        // If no paths are still valid, caller should run a fresh pointer scan
        return (results, validPaths.Count == 0);
    }
}
