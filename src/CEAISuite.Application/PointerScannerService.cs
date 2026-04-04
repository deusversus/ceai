using System.Text.Json;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

// ── Pointer Map File Format (.PTR) ──

/// <summary>Serializable pointer map for offline analysis and cross-restart comparison.</summary>
public sealed record PointerMapFile(
    string TargetProcess,
    nuint OriginalTargetAddress,
    DateTimeOffset ScanTimestamp,
    int MaxDepth,
    long MaxOffset,
    IReadOnlyList<PointerPath> Paths);

/// <summary>Result of comparing two pointer maps.</summary>
public sealed record PointerMapComparison(
    IReadOnlyList<PointerPath> CommonPaths,
    IReadOnlyList<PointerPath> OnlyInFirst,
    IReadOnlyList<PointerPath> OnlyInSecond,
    double OverlapRatio);

/// <summary>
/// A discovered pointer path from a static base to a target address.
/// </summary>
public sealed record PointerPath(
    string ModuleName,
    nuint ModuleBase,
    long ModuleOffset,
    IReadOnlyList<long> Offsets,
    nuint ResolvedAddress)
{
    /// <summary>CE-style display: "module.dll"+offset → [off1] → [off2] → target</summary>
    public string Display
    {
        get
        {
            var chain = string.Join(" → ", Offsets.Select(o => $"[+0x{o:X}]"));
            return $"\"{ModuleName}\"+{ModuleOffset:X} → {chain} → 0x{ResolvedAddress:X}";
        }
    }
}

/// <summary>
/// Scans process memory for pointer chains leading to a target address.
/// Similar to CE's pointer scanner but simplified for common use cases.
/// </summary>
public sealed class PointerScannerService(IEngineFacade engine)
{
    // ── Resume State ──
    private int _lastModuleIndex;
    private List<PointerPath> _partialResults = new();
    private nuint _lastTargetAddress;
    private int _lastMaxDepth;
    private long _lastMaxOffset;
    private IReadOnlyList<string>? _lastModuleFilter;
    private IReadOnlyList<ModuleDescriptor> _lastModules = Array.Empty<ModuleDescriptor>();
    private int _lastProcessId;

    /// <summary>True if a scan was cancelled and can be resumed.</summary>
    public bool CanResume => _partialResults.Count > 0 && _lastModuleIndex > 0;

    /// <summary>
    /// Scan for single-level pointers to a target address within loaded modules.
    /// </summary>
    public async Task<IReadOnlyList<PointerPath>> ScanForPointersAsync(
        int processId,
        nuint targetAddress,
        int maxDepth = 3,
        long maxOffset = 0x2000,
        IReadOnlyList<string>? moduleFilter = null,
        CancellationToken ct = default)
    {
        // Clear resume state on new scan
        _partialResults.Clear();
        _lastModuleIndex = 0;

        var attachment = await engine.AttachAsync(processId, ct);
        var modules = FilterModules(attachment.Modules, moduleFilter);

        // Save state for potential resume
        _lastTargetAddress = targetAddress;
        _lastMaxDepth = maxDepth;
        _lastMaxOffset = maxOffset;
        _lastModuleFilter = moduleFilter;
        _lastModules = modules;
        _lastProcessId = processId;

        return await ScanFromModuleIndex(processId, modules, targetAddress, maxDepth, maxOffset, 0, ct);
    }

    /// <summary>Resume a cancelled pointer scan from where it left off.</summary>
    public async Task<IReadOnlyList<PointerPath>> ResumeScanAsync(
        int processId,
        CancellationToken ct = default)
    {
        if (!CanResume) return _partialResults;

        var additionalResults = await ScanFromModuleIndex(
            processId, _lastModules, _lastTargetAddress, _lastMaxDepth, _lastMaxOffset,
            _lastModuleIndex, ct);

        // Merge partial + new
        _partialResults.AddRange(additionalResults);
        return _partialResults;
    }

    private async Task<IReadOnlyList<PointerPath>> ScanFromModuleIndex(
        int processId,
        IReadOnlyList<ModuleDescriptor> modules,
        nuint targetAddress,
        int maxDepth,
        long maxOffset,
        int startModuleIndex,
        CancellationToken ct)
    {
        var results = new List<PointerPath>();

        try
        {
            // Phase 1: Find all single-level pointers
            var level1 = await FindPointersToAddress(processId, modules, targetAddress, maxOffset, startModuleIndex, ct);
            foreach (var (addr, offset, mod) in level1)
            {
                results.Add(new PointerPath(mod.Name, mod.BaseAddress,
                    (long)addr - (long)mod.BaseAddress, new[] { offset }, targetAddress));
            }

            if (maxDepth < 2 || results.Count > 500) return results;

            // Phase 2: For each level-1 pointer, find pointers to IT
            foreach (var (l1Addr, l1Offset, l1Mod) in level1)
            {
                ct.ThrowIfCancellationRequested();
                var level2 = await FindPointersToAddress(processId, modules, l1Addr, maxOffset, 0, ct);
                foreach (var (l2Addr, l2Offset, l2Mod) in level2)
                {
                    results.Add(new PointerPath(l2Mod.Name, l2Mod.BaseAddress,
                        (long)l2Addr - (long)l2Mod.BaseAddress,
                        new[] { l2Offset, l1Offset }, targetAddress));
                }
                if (results.Count > 2000) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Save partial results for resume
            _partialResults = new List<PointerPath>(results);
            throw;
        }

        // Scan completed — clear resume state
        _lastModuleIndex = 0;
        _partialResults.Clear();
        return results;
    }

    private static IReadOnlyList<ModuleDescriptor> FilterModules(
        IReadOnlyList<ModuleDescriptor> modules, IReadOnlyList<string>? filter)
    {
        if (filter is null || filter.Count == 0) return modules;
        return modules.Where(m =>
            filter.Any(f => m.Name.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // ── Pointer Map File I/O ──

    private static readonly JsonSerializerOptions PtrJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new NuintJsonConverter() }
    };

    public static async Task SavePointerMapAsync(string filePath, PointerMapFile map)
    {
        var json = JsonSerializer.Serialize(map, PtrJsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    public static async Task<PointerMapFile> LoadPointerMapAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<PointerMapFile>(json, PtrJsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize pointer map file.");
    }

    // ── Pointer Map Comparison ──

    /// <summary>Compare two pointer maps by their chain signatures (module+offset+offsets).</summary>
    public static PointerMapComparison CompareMaps(PointerMapFile mapA, PointerMapFile mapB)
    {
        static string Key(PointerPath p) =>
            $"{p.ModuleName}+{p.ModuleOffset:X}:{string.Join(",", p.Offsets.Select(o => o.ToString("X")))}";

        var keysA = mapA.Paths.DistinctBy(Key).ToDictionary(Key, p => p);
        var keysB = mapB.Paths.DistinctBy(Key).ToDictionary(Key, p => p);

        var commonKeys = keysA.Keys.Intersect(keysB.Keys).ToList();
        var onlyInFirstKeys = keysA.Keys.Except(keysB.Keys).ToList();
        var onlyInSecondKeys = keysB.Keys.Except(keysA.Keys).ToList();

        var common = commonKeys.Select(k => keysA[k]).ToList();
        var onlyFirst = onlyInFirstKeys.Select(k => keysA[k]).ToList();
        var onlySecond = onlyInSecondKeys.Select(k => keysB[k]).ToList();

        var total = Math.Max(keysA.Count, keysB.Count);
        var overlap = total > 0 ? (double)common.Count / total : 0.0;

        return new PointerMapComparison(common, onlyFirst, onlySecond, overlap);
    }

    /// <summary>
    /// Re-walk a pointer chain and return a stability status.
    /// "Stable" = resolves to same target, "Drifted" = resolves but to a different address,
    /// "Broken" = chain is broken (null pointer in chain).
    /// </summary>
    public async Task<(string Status, nuint CurrentAddress)> ValidatePathAsync(
        int processId, PointerPath path, CancellationToken ct = default)
    {
        try
        {
            var attachment = await engine.AttachAsync(processId, ct);
            var mod = attachment.Modules.FirstOrDefault(m =>
                m.Name.Equals(path.ModuleName, StringComparison.OrdinalIgnoreCase));
            if (mod is null) return ("Broken", 0);

            var currentAddr = (nuint)((long)mod.BaseAddress + path.ModuleOffset);

            foreach (var offset in path.Offsets)
            {
                ct.ThrowIfCancellationRequested();
                var result = await engine.ReadMemoryAsync(processId, currentAddr, 8, ct);
                var ptrValue = BitConverter.ToUInt64(result.Bytes.ToArray(), 0);
                if (ptrValue == 0) return ("Broken", 0);
                currentAddr = (nuint)(ptrValue + (ulong)offset);
            }

            if (currentAddr == path.ResolvedAddress)
                return ("Stable", currentAddr);
            return ("Drifted", currentAddr);
        }
        catch
        {
            return ("Broken", 0);
        }
    }

    private async Task<List<(nuint Address, long Offset, ModuleDescriptor Module)>> FindPointersToAddress(
        int processId,
        IReadOnlyList<ModuleDescriptor> modules,
        nuint targetAddress,
        long maxOffset,
        int startModuleIndex,
        CancellationToken ct)
    {
        var found = new List<(nuint Address, long Offset, ModuleDescriptor Module)>();
        var ptrSize = 8; // assume x64

        for (int mi = startModuleIndex; mi < modules.Count; mi++)
        {
            var mod = modules[mi];
            _lastModuleIndex = mi;
            ct.ThrowIfCancellationRequested();
            if (mod.SizeBytes <= 0 || mod.SizeBytes > 100_000_000) continue; // skip huge/invalid modules

            try
            {
                // Read module memory in chunks
                var chunkSize = Math.Min((int)mod.SizeBytes, 0x100000); // 1MB chunks
                for (long offset = 0; offset < mod.SizeBytes; offset += chunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var readSize = (int)Math.Min(chunkSize, mod.SizeBytes - offset);
                    if (readSize < ptrSize) break;

                    try
                    {
                        var readAddr = (nuint)((long)mod.BaseAddress + offset);
                        var result = await engine.ReadMemoryAsync(processId, readAddr, readSize, ct);
                        var bytes = result.Bytes.ToArray();

                        // Scan for pointer values that point near targetAddress
                        for (var i = 0; i <= bytes.Length - ptrSize; i += 4) // 4-byte aligned scan
                        {
                            var ptrValue = BitConverter.ToUInt64(bytes, i);
                            if (ptrValue == 0) continue;

                            var diff = (long)targetAddress - (long)ptrValue;
                            if (diff >= 0 && diff <= maxOffset)
                            {
                                var ptrAddr = (nuint)((long)readAddr + i);
                                found.Add((ptrAddr, diff, mod));
                                if (found.Count > 1000) return found;
                            }
                        }
                    }
                    catch { /* unreadable region */ }
                }
            }
            catch { /* module access error */ }
        }

        return found;
    }
}
