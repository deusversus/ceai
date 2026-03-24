using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

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
    /// <summary>
    /// Scan for single-level pointers to a target address within loaded modules.
    /// </summary>
    public async Task<IReadOnlyList<PointerPath>> ScanForPointersAsync(
        int processId,
        nuint targetAddress,
        int maxDepth = 3,
        long maxOffset = 0x2000,
        CancellationToken ct = default)
    {
        var attachment = await engine.AttachAsync(processId, ct);
        var results = new List<PointerPath>();

        // Phase 1: Find all single-level pointers (addresses that contain targetAddress ± maxOffset)
        var level1 = await FindPointersToAddress(processId, attachment.Modules, targetAddress, maxOffset, ct);
        foreach (var (addr, offset, mod) in level1)
        {
            results.Add(new PointerPath(mod.Name, mod.BaseAddress,
                (long)addr - (long)mod.BaseAddress, new[] { offset }, targetAddress));
        }

        if (maxDepth < 2 || results.Count > 500) return results;

        // Phase 2: For each level-1 pointer, find pointers to IT
        var level1Addrs = level1.Select(x => x.Address).ToHashSet();
        foreach (var (l1Addr, l1Offset, l1Mod) in level1)
        {
            ct.ThrowIfCancellationRequested();
            var level2 = await FindPointersToAddress(processId, attachment.Modules, l1Addr, maxOffset, ct);
            foreach (var (l2Addr, l2Offset, l2Mod) in level2)
            {
                results.Add(new PointerPath(l2Mod.Name, l2Mod.BaseAddress,
                    (long)l2Addr - (long)l2Mod.BaseAddress,
                    new[] { l2Offset, l1Offset }, targetAddress));
            }
            if (results.Count > 2000) break;
        }

        return results;
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
        CancellationToken ct)
    {
        var found = new List<(nuint Address, long Offset, ModuleDescriptor Module)>();
        var ptrSize = 8; // assume x64

        foreach (var mod in modules)
        {
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
