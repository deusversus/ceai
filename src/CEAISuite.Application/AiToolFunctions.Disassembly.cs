using CEAISuite.Application.AgentLoop;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Resolve symbolic expression (module+offset) to live address.")]
    public async Task<string> ResolveSymbol(
        [Description("Process ID")] int processId,
        [Description("Expression: 'module.dll+offset', 'module.dll', or hex")] string expression)
    {
        var normalized = expression.Trim();

        // Check for module+offset pattern
        var plusIdx = normalized.IndexOf('+');
        if (plusIdx > 0)
        {
            var modulePart = normalized[..plusIdx].Trim();
            var offsetPart = normalized[(plusIdx + 1)..].Trim();
            if (offsetPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                offsetPart = offsetPart[2..];

            if (!ulong.TryParse(offsetPart, System.Globalization.NumberStyles.HexNumber, null, out var offset))
                return $"Cannot parse offset '{offsetPart}' as hex.";

            var attachment = await engineFacade.AttachAsync(processId);
            var mod = attachment.Modules.FirstOrDefault(m =>
                m.Name.Equals(modulePart, StringComparison.OrdinalIgnoreCase));

            if (mod is null)
                return $"Module '{modulePart}' not found. Loaded modules: {string.Join(", ", attachment.Modules.Select(m => m.Name).Take(10))}...";

            var resolvedAddr = (ulong)mod.BaseAddress + offset;
            bool inRange = offset < (ulong)mod.SizeBytes;

            return ToJson(new
            {
                expression = normalized,
                resolvedAddress = $"0x{resolvedAddr:X}",
                module = mod.Name,
                moduleBase = $"0x{(ulong)mod.BaseAddress:X}",
                offset = $"0x{offset:X}",
                isResolved = true,
                inModuleRange = inRange,
                warning = inRange ? (string?)null : $"Offset 0x{offset:X} exceeds module size (0x{mod.SizeBytes:X})"
            });
        }

        // Bare module name (contains '.') — resolve to base address
        if (normalized.Contains('.') && !normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var attachment = await engineFacade.AttachAsync(processId);
            var mod = attachment.Modules.FirstOrDefault(m =>
                m.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));

            if (mod is not null)
            {
                return ToJson(new
                {
                    expression = normalized,
                    resolvedAddress = $"0x{(ulong)mod.BaseAddress:X}",
                    module = mod.Name,
                    moduleBase = $"0x{(ulong)mod.BaseAddress:X}",
                    sizeBytes = mod.SizeBytes,
                    offset = "0x0",
                    isResolved = true
                });
            }

            return $"Module '{normalized}' not found. Loaded modules: {string.Join(", ", attachment.Modules.Select(m => m.Name).Take(10))}...";
        }

        // Raw address — just validate and return
        var addr = ParseAddress(normalized);
        return ToJson(new
        {
            expression = normalized,
            resolvedAddress = $"0x{(ulong)addr:X}",
            module = (string?)null,
            moduleBase = (string?)null,
            offset = (string?)null,
            isResolved = true
        });
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Identify artifact type from an ID (hook, BP, script, etc).")]
    public Task<string> IdentifyArtifact(
        [Description("The artifact ID to look up")] string id)
    {
        // Prefix-based fast path
        if (id.StartsWith("hook-", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToJson(new { id, type = "hook", description = "Code cave stealth hook. Use RemoveCodeCaveHook / GetCodeCaveHookHits to manage." }));
        if (id.StartsWith("bp-", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToJson(new { id, type = "breakpoint", description = "Breakpoint. Use RemoveBreakpoint / GetBreakpointHitLog to manage." }));
        if (id.StartsWith("script-", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToJson(new { id, type = "script", description = "Script entry in address table. Use ToggleScript / DisableScript / ViewScript to manage." }));
        if (id.StartsWith("addr-", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToJson(new { id, type = "address", description = "Address table entry. Use EditTableEntry / RemoveTableEntry to manage." }));
        if (id.StartsWith("group-", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToJson(new { id, type = "group", description = "Address table group node. Use ListAddressTable to see contents." }));
        if (id.StartsWith("scan-", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToJson(new { id, type = "scan", description = "Scan result set. Use GetScanResults / RefineScan to work with results." }));

        // Fallback: search active stores for legacy unprefixed IDs
        var node = addressTableService.FindNode(id);
        if (node is not null)
        {
            var kind = node.IsGroup ? "group" : (node.IsScriptEntry ? "script" : "address");
            return Task.FromResult(ToJson(new { id, type = kind, description = $"Address table node '{node.Label}' (legacy unprefixed ID)." }));
        }

        return Task.FromResult(ToJson(new { id, type = "unknown", description = "ID not recognized. It may be expired, removed, or from a different session." }));
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Disassemble code at an address. Accepts hex or symbolic.")]
    public async Task<string> Disassemble(
        [Description("Process ID")] int processId,
        [Description("Start address (hex or symbolic like module+offset)")] string address)
    {
        // Resolve symbolic address (module+offset or bare module name) to raw hex
        var resolvedAddress = await TryResolveToHex(processId, address);

        // Pre-check: warn if the target address is not in executable memory
        string? execWarning = null;
        string? protectionString = null;
        try
        {
            if (memoryProtectionEngine is not null)
            {
                var addr = ParseAddress(resolvedAddress);
                var region = await memoryProtectionEngine.QueryProtectionAsync(processId, addr);
                if (!region.IsExecutable)
                {
                    protectionString = (region.IsReadable, region.IsWritable) switch
                    {
                        (true, true) => "RW (Read/Write)",
                        (true, false) => "R (Read-only)",
                        (false, true) => "W (Write-only)",
                        _ => "NoAccess"
                    };
                    execWarning = "Address is not in executable memory — decoded instructions are likely meaningless data. Consider using BrowseMemory or HexDump instead.";
                }
            }
        }
        catch
        {
            // Protection query failed (e.g., address not mapped) — proceed without warning
        }

        var overview = await disassemblyService.DisassembleAtAsync(processId, resolvedAddress);

        var symbolicNote = resolvedAddress != address.Trim()
            ? $"Resolved '{address.Trim()}' → {resolvedAddress}"
            : (string?)null;

        var cap = _limits.MaxDisassemblyInstructions;
        var capped = overview.Lines.Take(cap).ToList();
        var wasTruncated = overview.Lines.Count > cap;

        if (execWarning is not null)
        {
            return ToJson(new
            {
                warning = execWarning,
                protection = protectionString,
                resolved = symbolicNote,
                startAddress = overview.StartAddress,
                instructions = capped.Select(l => new { l.Address, l.HexBytes, l.Mnemonic, l.Operands, l.SymbolName }),
                count = overview.Lines.Count,
                returned = capped.Count,
                truncated = wasTruncated,
                summary = overview.Summary
            });
        }

        return ToJson(new
        {
            resolved = symbolicNote,
            startAddress = overview.StartAddress,
            instructions = capped.Select(l => new { l.Address, l.HexBytes, l.Mnemonic, l.Operands, l.SymbolName }),
            count = overview.Lines.Count,
            returned = capped.Count,
            truncated = wasTruncated,
            summary = overview.Summary
        });
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Unbounded)]
    [Description("List memory regions. Shows address, size, R/W/X flags.")]
    public async Task<string> ListMemoryRegions(
        [Description("Process ID")] int processId,
        [Description("Filter: all, readable, writable, executable")] string filter = "readable")
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var regions = await scanService.EnumerateRegionsAsync(processId);

            // Load modules for ownership annotation
            IReadOnlyList<ModuleDescriptor>? modules = null;
            try
            {
                var attachment = await engineFacade.AttachAsync(processId);
                modules = attachment.Modules;
            }
            catch (Exception ex) { logger?.LogDebug(ex, "ListMemoryRegions module lookup failed"); }

            var filtered = filter.ToLowerInvariant() switch
            {
                "writable" => regions.Where(r => r.IsWritable).ToList(),
                "executable" => regions.Where(r => r.IsExecutable).ToList(),
                "all" => regions.ToList(),
                _ => regions.Where(r => r.IsReadable).ToList()
            };

            if (filtered.Count == 0) return $"No {filter} memory regions found.";

            var cap = _limits.MaxListRegions;
            var totalSize = filtered.Sum(r => r.RegionSize);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Memory regions ({filtered.Count} {filter}, {FormatBytes(totalSize)} total, showing {Math.Min(cap, filtered.Count)}):");
            foreach (var r in filtered.Take(cap))
            {
                var flags = $"{(r.IsReadable ? "R" : "-")}{(r.IsWritable ? "W" : "-")}{(r.IsExecutable ? "X" : "-")}";
                var moduleName = FindOwningModule(r.BaseAddress, r.RegionSize, modules);
                var moduleTag = moduleName is not null ? $" [{moduleName}]" : "";
                sb.AppendLine($"  0x{r.BaseAddress:X} [{FormatBytes(r.RegionSize),-10}] {flags}{moduleTag}");
            }
            if (filtered.Count > cap)
                sb.AppendLine($"  ... and {filtered.Count - cap} more regions (use filter to narrow)");
            return sb.ToString();
        }
        catch (Exception ex) { return $"ListMemoryRegions failed: {ex.Message}"; }
    }
}
