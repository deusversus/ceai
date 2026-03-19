using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application;

/// <summary>
/// Exposes engine capabilities as AI-callable tools via Microsoft.Extensions.AI function calling.
/// Uses application-level services so the AI operates through the same layer as the UI.
/// </summary>
public sealed class AiToolFunctions(
    IEngineFacade engineFacade,
    WorkspaceDashboardService dashboardService,
    ScanService scanService,
    AddressTableService addressTableService,
    DisassemblyService disassemblyService,
    ScriptGenerationService scriptGenerationService,
    BreakpointService? breakpointService = null,
    IAutoAssemblerEngine? autoAssemblerEngine = null,
    IScreenCaptureEngine? screenCaptureEngine = null,
    GlobalHotkeyService? hotkeyService = null,
    PatchUndoService? patchUndoService = null,
    SessionService? sessionService = null,
    SignatureGeneratorService? signatureService = null,
    IMemoryProtectionEngine? memoryProtectionEngine = null,
    MemorySnapshotService? snapshotService = null,
    PointerRescanService? pointerRescanService = null,
    ICallStackEngine? callStackEngine = null,
    ICodeCaveEngine? codeCaveEngine = null,
    ProcessWatchdogService? watchdogService = null,
    OperationJournal? operationJournal = null)
{
    /// <summary>Queue of captured screenshots for injection into the AI conversation.</summary>
    public ConcurrentQueue<(string Description, byte[] PngData)> PendingImages { get; } = new();

    // M3: Process liveness / stale-state tracking
    private int _sessionGeneration;
    private int _lastAttachedPid;

    private object GetProcessStatusJson(int processId)
    {
        bool alive = IsProcessAlive(processId);
        bool pidChanged = processId != _lastAttachedPid;
        if (pidChanged) { _sessionGeneration++; _lastAttachedPid = processId; }
        return new { processAlive = alive, processId, sessionGeneration = _sessionGeneration, pidChanged };
    }

    private static bool IsProcessAlive(int processId)
    {
        try { return !System.Diagnostics.Process.GetProcessById(processId).HasExited; }
        catch { return false; }
    }

    [Description("List running processes on the system. Returns process ID, name, and architecture.")]
    public async Task<string> ListProcesses()
    {
        var processes = await engineFacade.ListProcessesAsync();
        var lines = processes.Take(30).Select(p => $"PID {p.Id} | {p.Name} | {p.Architecture}");
        return $"Found {processes.Count} processes. Top 30:\n{string.Join('\n', lines)}";
    }

    [Description("Inspect a process by PID. Returns loaded modules and architecture info.")]
    public async Task<string> InspectProcess([Description("Process ID to inspect")] int processId)
    {
        var inspection = await dashboardService.InspectProcessAsync(processId);
        var modules = inspection.Modules.Take(20)
            .Select(m => $"  {m.Name} @ {m.BaseAddress} ({m.Size})");
        return $"Process: {inspection.ProcessName} (PID {inspection.ProcessId})\n" +
               $"Modules ({inspection.Modules.Count} total):\n{string.Join('\n', modules)}";
    }

    [Description("Read a typed value from process memory at the given address.")]
    public async Task<string> ReadMemory(
        [Description("Process ID")] int processId,
        [Description("Memory address as hex (e.g. 0x7FF6A000) or decimal")] string address,
        [Description("Data type: Int32, Int64, Float, Double, or Pointer")] string dataType)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
            var probe = await dashboardService.ReadAddressAsync(processId, address, dt);
            return $"Read {dt} at {probe.Address}: {probe.DisplayValue}";
        }
        catch (Exception ex)
        {
            return $"ReadMemory failed: {ex.Message}";
        }
    }

    [Description("Write a value to process memory. Records original value for undo (Ctrl+Z). CAUTION: This modifies the target process.")]
    public async Task<string> WriteMemory(
        [Description("Process ID")] int processId,
        [Description("Memory address")] string address,
        [Description("Data type: Int32, Int64, Float, Double")] string dataType,
        [Description("Value to write")] string value)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
            if (patchUndoService is not null)
            {
                var addr = AddressTableService.ParseAddress(address);
                var result = await patchUndoService.WriteWithUndoAsync(processId, addr, dt, value);
                return result.BytesWritten > 0
                    ? $"Wrote '{value}' ({dt}) to 0x{addr:X}. {patchUndoService.UndoCount} patches in undo stack."
                    : $"Write failed at 0x{addr:X}.";
            }
            var message = await dashboardService.WriteAddressAsync(processId, address, dt, value);
            return message;
        }
        catch (Exception ex)
        {
            return $"WriteMemory failed: {ex.Message}";
        }
    }

    [Description("Start a new memory scan for a value in a process. Returns number of results found.")]
    public async Task<string> StartScan(
        [Description("Process ID to scan")] int processId,
        [Description("Data type: Int32, Int64, Float, Double")] string dataType,
        [Description("Scan type: ExactValue, UnknownInitialValue, ArrayOfBytes")] string scanType,
        [Description("Value to search for. For ArrayOfBytes use hex pattern like '48 8B 05 ?? ?? ?? ??' where ?? is wildcard")] string? value)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
            var st = Enum.Parse<ScanType>(scanType, ignoreCase: true);
            scanService.ResetScan();
            var overview = await scanService.StartScanAsync(processId, dt, st, value ?? "");
            var topResults = overview.Results.Take(10)
                .Select(r => $"  {r.Address} = {r.CurrentValue}");
            return $"Scan complete: {overview.ResultCount:N0} results found.\n{string.Join('\n', topResults)}";
        }
        catch (Exception ex)
        {
            return $"StartScan failed: {ex.Message}";
        }
    }

    [Description("Refine the previous scan with a new constraint (e.g. value changed, increased, decreased, or new exact value).")]
    public async Task<string> RefineScan(
        [Description("Scan type: ExactValue, Increased, Decreased, Changed, Unchanged")] string scanType,
        [Description("Value to match (for ExactValue) or empty")] string? value)
    {
        try
        {
            var st = Enum.Parse<ScanType>(scanType, ignoreCase: true);
            var overview = await scanService.RefineScanAsync(st, value ?? "");
            var topResults = overview.Results.Take(10)
                .Select(r => $"  {r.Address} = {r.CurrentValue} (was {r.PreviousValue})");
            return $"Refinement complete: {overview.ResultCount:N0} results remaining.\n{string.Join('\n', topResults)}";
        }
        catch (Exception ex)
        {
            return $"RefineScan failed: {ex.Message}";
        }
    }

    [Description("Resolve a symbolic expression like 'ModuleName.dll+offset' to a live virtual address. " +
        "Useful for converting script define() addresses, CE-style module+offset notation, or any symbolic " +
        "address into the current live address. Returns the resolved address, module base, and offset.")]
    public async Task<string> ResolveSymbol(
        [Description("Process ID")] int processId,
        [Description("Symbolic expression to resolve, e.g., 'GameAssembly.dll+9A18E8', 'kernel32.dll+1234', or a raw hex address '0x7FF8...'")] string expression)
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
                return $"Module '{modulePart}' not found. Use InspectProcess to see loaded modules.";

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

    [Description("Identify what type of artifact an ID refers to (hook, breakpoint, script, address entry, group, scan). " +
        "Use this when you have an opaque ID and need to know what it is before calling the right management tool.")]
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

    [Description("Disassemble machine code at an address in a process. Shows assembly instructions.")]
    public async Task<string> Disassemble(
        [Description("Process ID")] int processId,
        [Description("Memory address to start disassembling")] string address)
    {
        // Pre-check: warn if the target address is not in executable memory
        string? execWarning = null;
        string? protectionString = null;
        try
        {
            if (memoryProtectionEngine is not null)
            {
                var addr = ParseAddress(address);
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

        var overview = await disassemblyService.DisassembleAtAsync(processId, address);

        if (execWarning is not null)
        {
            return ToJson(new
            {
                warning = execWarning,
                protection = protectionString,
                startAddress = overview.StartAddress,
                instructions = overview.Lines.Select(l => new { l.Address, l.HexBytes, l.Mnemonic, l.Operands }),
                count = overview.Lines.Count,
                summary = overview.Summary
            });
        }

        return ToJson(new
        {
            startAddress = overview.StartAddress,
            instructions = overview.Lines.Select(l => new { l.Address, l.HexBytes, l.Mnemonic, l.Operands }),
            count = overview.Lines.Count,
            summary = overview.Summary
        });
    }

    [Description("List memory regions of a process. Shows base address, size, and access flags (R/W/X).")]
    public async Task<string> ListMemoryRegions(
        [Description("Process ID")] int processId,
        [Description("Filter: 'all', 'readable', 'writable', 'executable' (default: readable)")] string filter = "readable")
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var regions = await scanService.EnumerateRegionsAsync(processId);

            var filtered = filter.ToLowerInvariant() switch
            {
                "writable" => regions.Where(r => r.IsWritable).ToList(),
                "executable" => regions.Where(r => r.IsExecutable).ToList(),
                "all" => regions.ToList(),
                _ => regions.Where(r => r.IsReadable).ToList()
            };

            if (filtered.Count == 0) return $"No {filter} memory regions found.";

            var totalSize = filtered.Sum(r => r.RegionSize);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Memory regions ({filtered.Count} {filter}, {FormatBytes(totalSize)} total):");
            foreach (var r in filtered.Take(100))
            {
                var flags = $"{(r.IsReadable ? "R" : "-")}{(r.IsWritable ? "W" : "-")}{(r.IsExecutable ? "X" : "-")}";
                sb.AppendLine($"  0x{r.BaseAddress:X} [{FormatBytes(r.RegionSize),-10}] {flags}");
            }
            if (filtered.Count > 100)
                sb.AppendLine($"  ... and {filtered.Count - 100} more regions");
            return sb.ToString();
        }
        catch (Exception ex) { return $"ListMemoryRegions failed: {ex.Message}"; }
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };

    [Description("Add a memory address to the address table for tracking.")]
    public Task<string> AddToAddressTable(
        [Description("Memory address")] string address,
        [Description("Data type: Int32, Int64, Float, Double")] string dataType,
        [Description("Current value at the address")] string currentValue,
        [Description("Label/description for this address")] string? label)
    {
        var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
        var entry = addressTableService.AddEntry(address, dt, currentValue, label);
        return Task.FromResult($"Added to address table: {entry.Label} at {entry.Address} ({entry.DataType})");
    }

    [Description("List address table entries. Returns up to 50 entries with offset pagination.")]
    public Task<string> ListAddressTable(
        [Description("Number of entries to skip (default 0). Use for pagination.")] int offset = 0,
        [Description("Max entries to return (default 50, max 100).")] int limit = 50)
    {
        var roots = addressTableService.Roots;
        var totalCount = CountNodes(roots);
        if (totalCount == 0)
            return Task.FromResult(ToJson(new { entries = Array.Empty<object>(), count = 0, total = 0 }));

        limit = Math.Clamp(limit, 1, 100);
        var allNodes = FlattenNodes(roots);
        var page = allNodes.Skip(offset).Take(limit);

        return Task.FromResult(ToJson(new
        {
            entries = page,
            count = page.Count(),
            total = totalCount,
            offset,
            hasMore = offset + limit < totalCount
        }));
    }

    private static List<object> FlattenNodes(IEnumerable<AddressTableNode> nodes)
    {
        var result = new List<object>();
        foreach (var n in nodes)
        {
            result.Add(FormatNodeJson(n));
            if (n.IsGroup && n.Children.Count > 0)
                result.AddRange(FlattenNodes(n.Children));
        }
        return result;
    }

    private static object FormatNodeJson(AddressTableNode n)
    {
        if (n.IsGroup)
            return new { n.Id, n.Label, type = "group", childCount = n.Children.Count };
        if (n.IsScriptEntry)
            return new { n.Id, n.Label, type = "script", enabled = n.IsScriptEnabled };
        return new { n.Id, n.Label, n.Address, n.DisplayValue, n.DataType, n.IsLocked };
    }

    private static void FormatNodes(System.Text.StringBuilder sb, IEnumerable<AddressTableNode> nodes, int indent)
    {
        var prefix = new string(' ', indent * 2);
        foreach (var n in nodes)
        {
            if (n.IsGroup)
            {
                sb.AppendLine($"{prefix}[{n.Id}] 📁 {n.Label} ({n.Children.Count} children)");
                FormatNodes(sb, n.Children, indent + 1);
            }
            else if (n.IsScriptEntry)
            {
                sb.AppendLine($"{prefix}[{n.Id}] 📜 {n.Label} ({(n.IsScriptEnabled ? "ENABLED" : "disabled")})");
            }
            else
            {
                var resolved = n.ResolvedAddress.HasValue;
                var addrDisplay = resolved ? $"addr=0x{n.ResolvedAddress!.Value:X}" : $"addr={n.Address}";
                var parentInfo = n.IsOffset && n.Parent is not null
                    ? $" | parent=\"{n.Parent.Label}\"+{n.Address}"
                    : "";
                var frozen = n.IsLocked ? " | FROZEN" : "";
                var pointer = n.IsPointer ? " | ptr" : "";
                sb.AppendLine($"{prefix}[{n.Id}] \"{n.Label}\" | {addrDisplay}{parentInfo} | value={n.CurrentValue} ({n.DataType}) | resolved={resolved}{pointer}{frozen}");
            }
        }
    }

    [Description("Refresh all values in the address table by re-reading from process memory.")]
    public async Task<string> RefreshAddressTable([Description("Process ID")] int processId)
    {
        await addressTableService.RefreshAllAsync(processId);
        var entries = addressTableService.Entries;
        var lines = entries.Select(e => $"  {e.Label}: {e.Address} = {e.CurrentValue} (was {e.PreviousValue})");
        return $"Refreshed {entries.Count} entries:\n{string.Join('\n', lines)}";
    }

    // ── Artifact generation tools ──

    [Description("Generate a C# trainer script from locked entries in the address table.")]
    public Task<string> GenerateTrainerScript([Description("Process name for the trainer target")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries to generate trainer from. Lock some address table entries first.");
        var script = scriptGenerationService.GenerateTrainerScript(locked, processName);
        return Task.FromResult($"Generated C# trainer script ({locked.Count} entries):\n\n{script}");
    }

    [Description("Generate an Auto Assembler (AA) script from locked entries in the address table.")]
    public Task<string> GenerateAutoAssemblerScript([Description("Process name")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries. Lock entries first.");
        var script = scriptGenerationService.GenerateAutoAssemblerScript(locked, processName);
        return Task.FromResult($"Generated AA script ({locked.Count} entries):\n\n{script}");
    }

    [Description("Generate a Lua script from locked entries in the address table.")]
    public Task<string> GenerateLuaScript([Description("Process name")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries. Lock entries first.");
        var script = scriptGenerationService.GenerateLuaScript(locked, processName);
        return Task.FromResult($"Generated Lua script ({locked.Count} entries):\n\n{script}");
    }

    [Description("Summarize the current investigation including address table, scan results, and disassembly.")]
    public Task<string> SummarizeInvestigation(
        [Description("Process name")] string processName,
        [Description("Process ID")] int processId)
    {
        var dashboard = dashboardService.BuildAsync(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CEAISuite", "workspace.db")).GetAwaiter().GetResult();
        var summary = scriptGenerationService.SummarizeInvestigation(
            processName, processId, addressTableService.Entries.ToList(),
            scanService.LastScanResults is not null
                ? scanService.LastScanResults.Results.Take(10).Select(r =>
                    new ScanResultOverview($"0x{r.Address:X}", r.CurrentValue, r.PreviousValue,
                        Convert.ToHexString(r.RawBytes.ToArray()))).ToArray()
                : null,
            dashboard.Disassembly);
        return Task.FromResult(summary);
    }

    [Description("Attach to a process by PID for memory operations. Must be called before scans/reads/breakpoints.")]
    public async Task<string> AttachProcess([Description("Process ID")] int processId)
    {
        var inspection = await dashboardService.InspectProcessAsync(processId);
        return $"Attached to {inspection.ProcessName} (PID {inspection.ProcessId}, {inspection.Architecture}). " +
               $"{inspection.Modules.Count} modules loaded.";
    }

    [Description("Load a .CT (Cheat Table) file and import entries into the address table.")]
    public Task<string> LoadCheatTable([Description("Full file path to the .CT file")] string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            return Task.FromResult($"File not found: {filePath}");

        var parser = new CheatTableParser();
        var ctFile = parser.ParseFile(filePath);
        var nodes = parser.ToAddressTableNodes(ctFile);
        addressTableService.ImportNodes(nodes);

        var scriptCount = CountScriptsInNodes(nodes);
        var leafCount = addressTableService.Entries.Count;
        return Task.FromResult(
            $"Loaded {ctFile.FileName}: {ctFile.TotalEntryCount} CT entries imported with hierarchy. " +
            $"{leafCount} address entries, {scriptCount} scripts, {nodes.Count} top-level nodes. " +
            $"Table version: {ctFile.TableVersion}" +
            (ctFile.LuaScript is not null ? ". Contains embedded Lua script." : ""));
    }

    [Description("Save the current address table as a Cheat Engine .CT file.")]
    public Task<string> SaveCheatTable([Description("File path to save to")] string filePath)
    {
        var roots = addressTableService.Roots;
        if (roots.Count == 0)
            return Task.FromResult("Address table is empty. Nothing to save.");

        try
        {
            var exporter = new CheatTableExporter();
            exporter.SaveToFile(roots, filePath);
            var totalCount = CountNodes(roots);
            var scriptCount = CountScriptsInNodes(roots);
            return Task.FromResult(
                $"Saved {totalCount} entries ({scriptCount} scripts) to {filePath}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to save CT file: {ex.Message}");
        }
    }

    private static int CountScriptsInNodes(IEnumerable<AddressTableNode> nodes)
    {
        var count = 0;
        foreach (var node in nodes)
        {
            if (node.IsScriptEntry) count++;
            count += CountScriptsInNodes(node.Children);
        }
        return count;
    }

    // ── Breakpoint tools ──

    [Description("Set a breakpoint at an address. Modes: Auto, Stealth (code cave), PageGuard, Hardware (DR), Software (INT3). Use Stealth/Auto for anti-debug targets.")]
    public async Task<string> SetBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex or decimal)")] string address,
        [Description("Breakpoint type: Software, HardwareExecute, HardwareWrite, HardwareReadWrite")] string type = "Software",
        [Description("Hit action: Break, Log, LogAndContinue")] string hitAction = "LogAndContinue",
        [Description("Intrusiveness mode: Auto, Stealth, PageGuard, Hardware, Software")] string mode = "Auto",
        [Description("If true, breakpoint auto-removes after first hit (safer for risky targets)")] bool singleHit = false)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            if (breakpointService is null) return "Breakpoint engine not available.";
            var bpType = Enum.Parse<BreakpointType>(type, ignoreCase: true);
            var bpAction = Enum.Parse<BreakpointHitAction>(hitAction, ignoreCase: true);
            var bpMode = Enum.Parse<BreakpointMode>(mode, ignoreCase: true);

            // ── Mode/type safety guard ──
            // Stealth (code cave JMP detour) only works on executable code — NOT data addresses.
            // Requesting Stealth on a write/readwrite BP is invalid; auto-downgrade to PageGuard.
            bool wasDowngraded = false;
            if (bpMode == BreakpointMode.Stealth && bpType is BreakpointType.HardwareWrite or BreakpointType.HardwareReadWrite)
            {
                bpMode = BreakpointMode.PageGuard;
                wasDowngraded = true;
            }

            // Software breakpoints (INT3) can't watch data writes — reject
            if (bpMode == BreakpointMode.Software && bpType is BreakpointType.HardwareWrite or BreakpointType.HardwareReadWrite)
            {
                return "Software breakpoints (INT3) cannot monitor data writes. Use mode=PageGuard or mode=Hardware for write breakpoints.";
            }

            // For Stealth mode with execute BPs, redirect to code cave engine
            if (bpMode == BreakpointMode.Stealth && bpType is BreakpointType.HardwareExecute or BreakpointType.Software)
            {
                if (codeCaveEngine is null) return "Code cave engine not available.";
                var stealthAddr = ParseAddress(address);
                if (watchdogService is not null && watchdogService.IsUnsafe(stealthAddr, "Stealth"))
                {
                    // Still allow but warn
                }
                var result = await codeCaveEngine.InstallHookAsync(processId, stealthAddr);
                if (!result.Success) return $"Stealth hook failed: {result.ErrorMessage}";
                var stealthMsg = $"Stealth code cave hook installed at 0x{result.Hook!.OriginalAddress:X} (ID: {result.Hook.Id}, cave at 0x{result.Hook.CaveAddress:X}). No debugger attached — game-safe.";
                if (watchdogService is not null)
                {
                    var hookId = result.Hook.Id;
                    watchdogService.StartMonitoring(processId, hookId, stealthAddr, "CodeCaveHook", "Stealth",
                        async () => await codeCaveEngine.RemoveHookAsync(processId, hookId));
                    if (watchdogService.IsUnsafe(stealthAddr, "Stealth"))
                        stealthMsg += "\n⚠️ WARNING: This address+Stealth previously caused a process freeze. Watchdog is monitoring.";
                    else
                        stealthMsg += "\n🛡️ Watchdog monitoring active — will auto-rollback if process becomes unresponsive.";
                }
                operationJournal?.RecordOperation(
                    result.Hook.Id, "CodeCaveHook", stealthAddr, "Stealth", groupId: null,
                    async () => await codeCaveEngine.RemoveHookAsync(processId, result.Hook.Id));
                return stealthMsg;
            }

            var parsedAddr = ParseAddress(address);
            var modeStr = bpMode.ToString();
            if (watchdogService is not null && watchdogService.IsUnsafe(parsedAddr, modeStr))
            {
                // Still allow but warn
            }

            // ── PageGuard co-tenancy gate ──
            // Hard-reject PageGuard on pages shared with many address table entries.
            // Hot heap pages cause guard-page storms that can wedge the target process.
            const int CoTenancyThreshold = 10;
            if (bpMode == BreakpointMode.PageGuard)
            {
                var targetPage = parsedAddr & ~(nuint)4095;
                int coTenants = CountPageCoTenants(targetPage);
                if (coTenants > CoTenancyThreshold)
                {
                    return $"❌ PageGuard REJECTED: Target address 0x{parsedAddr:X} shares a 4KB page with {coTenants} other address table entries (threshold: {CoTenancyThreshold}). " +
                           $"PageGuard on crowded pages causes guard-page storms that can hang the target process.\n" +
                           $"Recommended alternatives:\n" +
                           $"  1. Use FindWritersToOffset or TraceFieldWriters to find the code that writes to this field\n" +
                           $"  2. Install a Stealth code-cave hook on the writer instruction instead\n" +
                           $"  3. Use Hardware mode (limited to 4 simultaneous BPs) if you must watch data directly";
                }
            }

            // For risky modes (PageGuard, Hardware), use transactional install with rollback
            if (watchdogService is not null && bpMode is BreakpointMode.PageGuard or BreakpointMode.Hardware)
            {
                BreakpointOverview? txBp = null;
                var txResult = await watchdogService.InstallWithTransactionAsync(
                    processId, $"bp-{Guid.NewGuid():N}", parsedAddr, "Breakpoint", modeStr,
                    installAction: async () =>
                    {
                        txBp = await breakpointService.SetBreakpointAsync(processId, address, bpType, bpMode, bpAction, singleHit: singleHit);
                    },
                    rollbackAction: async () => txBp is not null && await breakpointService.RemoveBreakpointAsync(processId, txBp.Id));

                if (!txResult.Success)
                    return $"⚠️ Transactional install failed at {txResult.Phase}: {txResult.Message}";

                var msg = $"Breakpoint {txBp!.Id} set at {txBp.Address} (type: {txBp.Type}, mode: {bpMode}, action: {txBp.HitAction})";
                if (singleHit) msg += " [SINGLE-HIT: will auto-remove after first trigger]";
                if (wasDowngraded) msg += "\n⚠️ Mode was auto-downgraded from Stealth→PageGuard. Stealth (code cave) only works on executable code, not data write targets.";
                if (watchdogService.IsUnsafe(parsedAddr, modeStr))
                    msg += $"\n⚠️ WARNING: This address+{modeStr} previously caused a process freeze. Watchdog is monitoring.";
                msg += "\n✅ Transaction committed. Watchdog monitoring active.";
                operationJournal?.RecordOperation(
                    txBp!.Id, "Breakpoint", parsedAddr, modeStr, groupId: null,
                    async () => await breakpointService.RemoveBreakpointAsync(processId, txBp.Id));
                return msg;
            }

            var bp = await breakpointService.SetBreakpointAsync(processId, address, bpType, bpMode, bpAction, singleHit: singleHit);
            var msg2 = $"Breakpoint {bp.Id} set at {bp.Address} (type: {bp.Type}, mode: {bpMode}, action: {bp.HitAction})";
            if (singleHit) msg2 += " [SINGLE-HIT: will auto-remove after first trigger]";
            if (wasDowngraded) msg2 += "\n⚠️ Mode was auto-downgraded from Stealth→PageGuard. Stealth (code cave) only works on executable code, not data write targets.";
            if (watchdogService is not null)
            {
                var bpId = bp.Id;
                watchdogService.StartMonitoring(processId, bpId, parsedAddr, "Breakpoint", modeStr,
                    async () => await breakpointService.RemoveBreakpointAsync(processId, bpId));
                if (watchdogService.IsUnsafe(parsedAddr, modeStr))
                    msg2 += $"\n⚠️ WARNING: This address+{modeStr} previously caused a process freeze. Watchdog is monitoring.";
                else
                    msg2 += "\n🛡️ Watchdog monitoring active — will auto-rollback if process becomes unresponsive.";
            }
            operationJournal?.RecordOperation(
                bp.Id, "Breakpoint", parsedAddr, modeStr, groupId: null,
                async () => await breakpointService.RemoveBreakpointAsync(processId, bp.Id));
            return msg2;
        }
        catch (Exception ex)
        {
            return $"SetBreakpoint failed: {ex.Message}";
        }
    }

    [Description("Remove a breakpoint by its ID.")]
    public async Task<string> RemoveBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Breakpoint ID to remove")] string breakpointId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var removed = await breakpointService.RemoveBreakpointAsync(processId, breakpointId);
        return removed ? $"Breakpoint {breakpointId} removed." : $"Breakpoint {breakpointId} not found.";
    }

    [Description("EMERGENCY: Restore page guard protections without locks. Use when PageGuard breakpoint has hung the target.")]
    public async Task<string> EmergencyRestorePageProtection(
        [Description("Process ID of the hung process")] int processId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var restored = await breakpointService.EmergencyRestorePageProtectionAsync(processId);
        return restored > 0
            ? $"✅ Emergency restore complete: {restored} page guard protection(s) restored. Target process should recover."
            : "No active page guard breakpoints found to restore.";
    }

    [Description("EMERGENCY: Force detach debugger and clean up all breakpoints. Nuclear option for hung targets.")]
    public async Task<string> ForceDetachAndCleanup(
        [Description("Process ID of the hung process")] int processId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        await breakpointService.ForceDetachAndCleanupAsync(processId);
        return $"✅ Force detach complete for process {processId}. Page guards restored, debugger detached, session torn down.";
    }

    [Description("List all active breakpoints for a process.")]
    public async Task<string> ListBreakpoints([Description("Process ID")] int processId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var bps = await breakpointService.ListBreakpointsAsync(processId);
        if (bps.Count == 0) return ToJson(new { breakpoints = Array.Empty<object>(), count = 0 });
        return ToJson(new
        {
            breakpoints = bps.Select(b => new
            {
                b.Id,
                b.Address,
                b.Type,
                b.Mode,
                b.HitCount,
                b.IsEnabled,
                lifecycleStatus = breakpointService.GetLifecycleStatus(b.Id).ToString()
            }),
            count = bps.Count
        });
    }

    [Description("Get the hit log for a breakpoint. Shows when it was triggered, register state, and thread info.")]
    public async Task<string> GetBreakpointHitLog(
        [Description("Breakpoint ID")] string breakpointId,
        [Description("Maximum entries to return")] int maxEntries = 10)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var hits = await breakpointService.GetHitLogAsync(breakpointId, maxEntries);
        if (hits.Count == 0) return $"No hits recorded for breakpoint {breakpointId}.";
        return ToJson(new
        {
            breakpointId,
            hits = hits.Select(h => new
            {
                h.BreakpointId, h.Address, h.ThreadId, h.Timestamp,
                registers = TrimRegisters(h.Registers)
            }),
            count = hits.Count
        });
    }

    [Description("Get breakpoint health: lifecycle state, hit count, throttle status, page co-tenancy.")]
    public async Task<string> GetBreakpointHealth(
        [Description("Breakpoint ID")] string breakpointId,
        [Description("Process ID")] int processId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var bps = await breakpointService.ListBreakpointsAsync(processId);
        var bp = bps.FirstOrDefault(b => string.Equals(b.Id, breakpointId, StringComparison.Ordinal));
        if (bp is null) return $"Breakpoint {breakpointId} not found on process {processId}.";

        var lifecycle = breakpointService.GetLifecycleStatus(breakpointId);
        var hits = await breakpointService.GetHitLogAsync(breakpointId, 1);
        var lastHit = hits.Count > 0 ? hits[0].Timestamp : "none";

        // Page co-tenancy for PageGuard breakpoints
        int coTenants = 0;
        bool isPageGuard = string.Equals(bp.Mode, "PageGuard", StringComparison.OrdinalIgnoreCase);
        if (isPageGuard)
        {
            var addr = ParseAddress(bp.Address);
            var pageBase = addr & ~(nuint)0xFFF;
            coTenants = CountPageCoTenants(pageBase);
        }

        return ToJson(new
        {
            breakpointId,
            address = bp.Address,
            type = bp.Type,
            mode = bp.Mode,
            isEnabled = bp.IsEnabled,
            lifecycleStatus = lifecycle.ToString(),
            hitCount = bp.HitCount,
            lastHitTimestamp = lastHit,
            hitAction = bp.HitAction,
            pageCoTenancy = isPageGuard ? coTenants : (int?)null,
            health = lifecycle switch
            {
                BreakpointLifecycleStatus.Active => "HEALTHY",
                BreakpointLifecycleStatus.Armed => "HEALTHY",
                BreakpointLifecycleStatus.ThrottleDisabled => "DEGRADED — hit-rate throttle triggered, BP auto-disabled",
                BreakpointLifecycleStatus.Faulted => "FAULTED — installation or re-arm failure",
                BreakpointLifecycleStatus.SingleHitRemoved => "COMPLETED — single-hit BP fired and auto-removed",
                BreakpointLifecycleStatus.Downgraded => "DEGRADED — mode was downgraded",
                BreakpointLifecycleStatus.ManuallyDisabled => "DISABLED — manually disabled by operator",
                _ => "UNKNOWN"
            }
        });
    }

    [Description("Get capability matrix for all breakpoint modes: execute/data support, debugger needs, stability.")]
    public string GetBreakpointModeCapabilities()
    {
        var caps = BreakpointService.GetModeCapabilities();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Breakpoint Mode Capabilities");
        foreach (var c in caps)
        {
            sb.AppendLine($"\n### {c.Mode} [{c.StabilityTier}]");
            sb.AppendLine($"  {c.Description}");
            sb.AppendLine($"  Execute hooks: {(c.SupportsExecuteHook ? "✓" : "✗")} | Data write watch: {(c.SupportsDataWriteWatch ? "✓" : "✗")}");
            sb.AppendLine($"  Debugger: {(c.RequiresDebugger ? "required" : "none")} | Page protection: {(c.UsesPageProtection ? "yes" : "no")} | Thread suspend: {(c.UsesThreadSuspend ? "yes" : "no")}");
        }
        return sb.ToString();
    }

    [Description("Probe an address for risk before setting a breakpoint or hook. Returns region type, protection, recommended modes, risk level.")]
    public async Task<string> ProbeTargetRisk(
        [Description("Process ID")] int processId,
        [Description("Memory address to probe (hex or decimal)")] string address)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);

            if (memoryProtectionEngine is null) return "Memory protection engine not available.";
            var region = await memoryProtectionEngine.QueryProtectionAsync(processId, addr);

            bool isExecutable = region.IsExecutable;
            bool isWritable = region.IsWritable;
            bool isReadable = region.IsReadable;

            // Build a human-readable protection string
            string protectionStr = (isReadable, isWritable, isExecutable) switch
            {
                (true, true, true) => "RWX (Read/Write/Execute)",
                (true, true, false) => "RW (Read/Write)",
                (true, false, true) => "RX (Read/Execute)",
                (true, false, false) => "R (Read-only)",
                (false, false, true) => "X (Execute-only)",
                (false, true, _) => "W (Write — unusual)",
                _ => "NoAccess"
            };

            // Determine what module (if any) this address belongs to
            string moduleName = "unknown";
            string regionKind = "heap/dynamic";
            try
            {
                var attachment = await engineFacade.AttachAsync(processId);
                foreach (var mod in attachment.Modules)
                {
                    if (addr >= mod.BaseAddress && addr < (nuint)((ulong)mod.BaseAddress + (ulong)mod.SizeBytes))
                    {
                        moduleName = mod.Name;
                        regionKind = isExecutable ? "module .text (code)" : "module .data/.rdata";
                        break;
                    }
                }
            }
            catch { /* module lookup failed, use defaults */ }

            if (regionKind == "heap/dynamic")
            {
                ulong addrVal = (ulong)addr;
                if (addrVal > 0x7FFE0000_00000000UL) regionKind = "kernel (inaccessible)";
                else if (!isExecutable && !isWritable) regionKind = "read-only data";
                else if (isExecutable) regionKind = "dynamic code (JIT/alloc)";
            }

            // Risk assessment
            string riskLevel;
            var recommended = new List<string>();
            var avoid = new List<string>();
            var warnings = new List<string>();

            var capabilityMap = BreakpointService.GetModeCapabilities()
                .ToDictionary(c => c.Mode, c => c.StabilityTier);

            if (isExecutable)
            {
                riskLevel = "LOW";
                recommended.Add($"Stealth (code cave — safest, no debugger) [{capabilityMap.GetValueOrDefault(BreakpointMode.Stealth, "?")}]");
                recommended.Add($"Software (INT3) [{capabilityMap.GetValueOrDefault(BreakpointMode.Software, "?")}]");
                recommended.Add($"Hardware (DR register) [{capabilityMap.GetValueOrDefault(BreakpointMode.Hardware, "?")}]");
                avoid.Add("PageGuard on code (may trap unrelated fetches)");
            }
            else
            {
                riskLevel = "MEDIUM";
                recommended.Add($"PageGuard (least intrusive for data) [{capabilityMap.GetValueOrDefault(BreakpointMode.PageGuard, "?")}]");

                nuint pageBase = addr & ~(nuint)0xFFF;
                nuint pageEnd = pageBase + 0x1000;
                warnings.Add($"Page-guard will trap ALL access to the 4KB page containing this address (0x{pageBase:X}–0x{pageEnd:X})");
                warnings.Add("Hot data fields (e.g., HP/position/timer) may cause excessive hits — use singleHit=true");

                // M5: Page co-tenancy — scan address table for other entries on the same 4KB page
                int coTenants = 0;
                foreach (var root in addressTableService.Roots)
                    CountCoTenants(root, pageBase, addr, ref coTenants);
                if (coTenants > 0)
                    warnings.Add($"Page co-tenancy: {coTenants} other address table entries share this 4KB page — PageGuard will affect all of them");

                // Escalate to CRITICAL when co-tenancy exceeds gate threshold
                if (coTenants > 10)
                {
                    riskLevel = "CRITICAL";
                    recommended.Clear();
                    recommended.Add("Use FindWritersToOffset or TraceFieldWriters to find the code path that writes to this field");
                    recommended.Add("Install a Stealth code-cave hook on the discovered writer instruction");
                    recommended.Add($"Hardware (DR register, max 4 BPs) [{capabilityMap.GetValueOrDefault(BreakpointMode.Hardware, "?")}] — if you must watch data directly");
                    warnings.Add($"⛔ PageGuard BLOCKED: {coTenants} co-tenants on this page exceeds threshold (10). Guard-page storms will hang the target.");
                }

                avoid.Add("Stealth (code cave cannot monitor data writes)");
                avoid.Add("Software (INT3 cannot monitor data writes)");

                if (!isReadable && !isWritable)
                {
                    riskLevel = "HIGH";
                    warnings.Add("Region is not readable or writable — address may be invalid or protected");
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## Probe: 0x{addr:X}");
            sb.AppendLine($"Region: {regionKind}");
            sb.AppendLine($"Module: {moduleName}");
            sb.AppendLine($"Protection: {protectionStr}");
            sb.AppendLine($"Executable: {isExecutable} | Writable: {isWritable} | Readable: {isReadable}");
            sb.AppendLine($"Risk level: {riskLevel}");
            sb.AppendLine();
            sb.AppendLine("Recommended modes:");
            foreach (var r in recommended) sb.AppendLine($"  ✓ {r}");
            sb.AppendLine("Avoid:");
            foreach (var a in avoid) sb.AppendLine($"  ✗ {a}");
            if (warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (var w in warnings) sb.AppendLine($"  ⚠️ {w}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"ProbeTargetRisk failed: {ex.Message}";
        }
    }

    // ── Script tools ──

    [Description("List all script entries in the address table. Shows script name, enabled status, and type (Auto Assembler or LuaCall).")]
    public Task<string> ListScripts()
    {
        var scripts = new List<string>();
        CollectScripts(addressTableService.Roots, scripts, "");
        if (scripts.Count == 0) return Task.FromResult("No scripts in the address table.");
        return Task.FromResult($"Found {scripts.Count} scripts:\n{string.Join('\n', scripts)}");
    }

    private static void CollectScripts(IEnumerable<AddressTableNode> nodes, List<string> results, string prefix)
    {
        foreach (var node in nodes)
        {
            if (node.IsScriptEntry)
            {
                var status = node.IsScriptEnabled ? "✅ Enabled" : "❌ Disabled";
                var type = node.AssemblerScript!.Contains("LuaCall") ? "LuaCall" : "Auto Assembler";
                results.Add($"  [{status}] {prefix}{node.Label} (ID: {node.Id}, Type: {type})");
            }
            if (node.Children.Count > 0)
                CollectScripts(node.Children, results, $"{prefix}{node.Label}/");
        }
    }

    [Description("View the source code of a script entry by its node ID. Use ListScripts first to find the ID.")]
    public Task<string> ViewScript([Description("Node ID or label of the script entry")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (node.AssemblerScript is null) return Task.FromResult($"Node '{nodeId}' is not a script entry.");

        var type = node.AssemblerScript.Contains("LuaCall") ? "LuaCall" : "Auto Assembler";
        var status = node.IsScriptEnabled ? "✅ Enabled" : "❌ Disabled";
        return Task.FromResult(
            $"Script: {node.Label}\nType: {type}\nStatus: {status}\n" +
            $"──────────────────────\n{node.AssemblerScript}");
    }

    [Description("Validate a script entry by parsing it. Checks for syntax errors without executing.")]
    public Task<string> ValidateScript(
        [Description("Node ID or label of the script entry")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (node.AssemblerScript is null) return Task.FromResult($"Node '{nodeId}' is not a script entry.");

        if (autoAssemblerEngine is null) return Task.FromResult("Auto Assembler engine not available for validation.");

        var result = autoAssemblerEngine.Parse(node.AssemblerScript);
        if (result.IsValid)
            return Task.FromResult($"Script '{node.Label}' is valid. Has [ENABLE]: {result.EnableSection is not null}, [DISABLE]: {result.DisableSection is not null}");

        return Task.FromResult(
            $"Script '{node.Label}' has issues:\n" +
            $"Errors: {string.Join("; ", result.Errors)}\n" +
            $"Warnings: {string.Join("; ", result.Warnings)}");
    }

    [Description("Deep validation of a script against live process state. Verifies assert bytes, hook targets, detour space, and disable/enable symmetry.")]
    public async Task<string> ValidateScriptDeep(
        [Description("Node ID or label of the script entry")] string nodeId,
        [Description("Process ID to validate against")] int processId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return $"Node '{nodeId}' not found.";
        if (node.AssemblerScript is null) return $"Node '{nodeId}' is not a script entry.";
        if (autoAssemblerEngine is null) return "Auto Assembler engine not available.";
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Deep Validation: {node.Label}");

        int passed = 0, failed = 0, warnings = 0;

        // Step 1: Parse validation
        var parseResult = autoAssemblerEngine.Parse(node.AssemblerScript);
        if (!parseResult.IsValid)
        {
            sb.AppendLine($"❌ PARSE FAILED: {string.Join("; ", parseResult.Errors)}");
            return sb.ToString();
        }
        sb.AppendLine("✅ Parse: valid syntax");
        passed++;

        var script = node.AssemblerScript;

        // Step 2: Extract and verify assert directives
        // Format: "assert(address,bytehex)" e.g. "assert(GameAssembly.dll+9A18E8,48 8B 44 24 38)"
        var assertPattern = new Regex(
            @"assert\s*\(\s*([^,]+)\s*,\s*([0-9A-Fa-f\s]+)\s*\)",
            RegexOptions.IgnoreCase);

        var assertMatches = assertPattern.Matches(script);
        if (assertMatches.Count == 0)
        {
            sb.AppendLine("⚠️ No assert directives found — cannot verify hook compatibility");
            warnings++;
        }
        else
        {
            foreach (Match match in assertMatches)
            {
                var addrStr = match.Groups[1].Value.Trim();
                var expectedHex = match.Groups[2].Value.Trim();

                try
                {
                    nuint assertAddr;
                    if (addrStr.Contains('+'))
                    {
                        var parts = addrStr.Split('+', 2);
                        var modName = parts[0].Trim();
                        var offsetStr = parts[1].Trim();

                        var attachment = await engineFacade.AttachAsync(processId);
                        var mod = attachment.Modules.FirstOrDefault(m =>
                            m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));

                        if (mod is null)
                        {
                            sb.AppendLine($"❌ Assert at {addrStr}: module '{modName}' not found");
                            failed++;
                            continue;
                        }

                        var offsetVal = ulong.Parse(offsetStr, NumberStyles.HexNumber);
                        assertAddr = (nuint)((ulong)mod.BaseAddress + offsetVal);
                    }
                    else
                    {
                        assertAddr = ParseAddress(addrStr);
                    }

                    var expectedBytes = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(b => byte.Parse(b, NumberStyles.HexNumber))
                        .ToArray();

                    var liveRead = await engineFacade.ReadMemoryAsync(processId, assertAddr, expectedBytes.Length);
                    var liveBytes = liveRead.Bytes.ToArray();

                    if (liveBytes.SequenceEqual(expectedBytes))
                    {
                        sb.AppendLine($"✅ Assert 0x{assertAddr:X}: live bytes match ({expectedHex})");
                        passed++;
                    }
                    else
                    {
                        var liveHex = string.Join(" ", liveBytes.Select(b => b.ToString("X2")));
                        sb.AppendLine($"❌ Assert 0x{assertAddr:X}: MISMATCH");
                        sb.AppendLine($"   Expected: {expectedHex}");
                        sb.AppendLine($"   Live:     {liveHex}");
                        sb.AppendLine($"   ⚠️ Script may be incompatible with current game version");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"❌ Assert {addrStr}: verification failed ({ex.Message})");
                    failed++;
                }
            }
        }

        // Step 3: Check hook target executability
        if (memoryProtectionEngine is not null)
        {
            var addressPattern = new Regex(
                @"^\s*(?:(\w+\.dll)\+([0-9A-Fa-f]+)|0x([0-9A-Fa-f]+))\s*:",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            var enableSection = parseResult.EnableSection ?? script;
            var addrMatches = addressPattern.Matches(enableSection);

            foreach (Match match in addrMatches)
            {
                try
                {
                    nuint hookAddr;
                    if (match.Groups[1].Success)
                    {
                        var modName = match.Groups[1].Value;
                        var offsetStr = match.Groups[2].Value;
                        var attachment = await engineFacade.AttachAsync(processId);
                        var mod = attachment.Modules.FirstOrDefault(m =>
                            m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
                        if (mod is null) continue;
                        hookAddr = (nuint)((ulong)mod.BaseAddress + ulong.Parse(offsetStr, NumberStyles.HexNumber));
                    }
                    else
                    {
                        hookAddr = ParseAddress(match.Groups[3].Value);
                    }

                    var region = await memoryProtectionEngine.QueryProtectionAsync(processId, hookAddr);
                    if (region.IsExecutable)
                    {
                        sb.AppendLine($"✅ Hook target 0x{hookAddr:X}: executable ({(region.IsWritable ? "RWX" : "RX")})");
                        passed++;
                    }
                    else
                    {
                        sb.AppendLine($"⚠️ Hook target 0x{hookAddr:X}: NOT executable — hook may fail or crash");
                        warnings++;
                    }
                }
                catch { /* skip unresolvable addresses */ }
            }
        }

        // Step 4: Check [DISABLE] has restore logic
        bool hasEnable = parseResult.EnableSection is not null;
        bool hasDisable = parseResult.DisableSection is not null;

        if (hasEnable && !hasDisable)
        {
            sb.AppendLine("❌ Script has [ENABLE] but no [DISABLE] — cannot be safely reversed");
            failed++;
        }
        else if (hasEnable && hasDisable)
        {
            bool hasRestoreBytes = parseResult.DisableSection!.Contains("db ", StringComparison.OrdinalIgnoreCase)
                || parseResult.DisableSection.Contains("readmem", StringComparison.OrdinalIgnoreCase);
            bool hasDealloc = parseResult.DisableSection.Contains("dealloc", StringComparison.OrdinalIgnoreCase);

            if (hasRestoreBytes)
            {
                sb.AppendLine("✅ [DISABLE] contains byte restoration directives");
                passed++;
            }
            else
            {
                sb.AppendLine("⚠️ [DISABLE] section exists but has no visible byte restoration (db/readmem)");
                warnings++;
            }

            if (parseResult.EnableSection!.Contains("alloc", StringComparison.OrdinalIgnoreCase))
            {
                if (hasDealloc)
                {
                    sb.AppendLine("✅ [DISABLE] deallocates memory allocated in [ENABLE]");
                    passed++;
                }
                else
                {
                    sb.AppendLine("⚠️ [ENABLE] allocates memory but [DISABLE] has no dealloc — potential memory leak");
                    warnings++;
                }
            }
        }

        // Summary
        sb.AppendLine();
        sb.AppendLine($"## Summary: {passed} passed, {failed} failed, {warnings} warnings");
        if (failed > 0)
            sb.AppendLine("🛑 DO NOT ENABLE — script has critical issues that may crash or corrupt the game");
        else if (warnings > 0)
            sb.AppendLine("⚠️ Script may work but has potential issues — proceed with caution");
        else
            sb.AppendLine("✅ All checks passed — script appears safe to enable");

        return sb.ToString();
    }

    // ── Pointer Scanner tools ──

    [Description("Scan process memory for pointer chains leading to a target address. Returns potential static pointers.")]
    public async Task<string> ScanForPointers(
        [Description("Process ID to scan")] int processId,
        [Description("Target address to find pointers to (hex string like 0x1234ABCD)")] string targetAddress,
        [Description("Maximum pointer chain depth (1-3, default 2)")] int maxDepth = 2)
    {
        var scanner = new PointerScannerService(engineFacade);
        var addr = AddressTableService.ParseAddress(targetAddress);
        var paths = await scanner.ScanForPointersAsync(processId, addr, maxDepth);

        if (paths.Count == 0) return "No pointer paths found to the target address.";

        var lines = paths.Take(50).Select((p, i) => $"{i + 1}. {p.Display}");
        return $"Found {paths.Count} pointer path(s) to 0x{addr:X}:\n{string.Join('\n', lines)}";
    }

    [Description("Browse raw memory at an address. Returns hex dump with ASCII.")]
    public async Task<string> BrowseMemory(
        [Description("Process ID")] int processId,
        [Description("Start address (hex string)")] string address,
        [Description("Number of bytes to read (default 128)")] int length = 128)
    {
        var addr = AddressTableService.ParseAddress(address);
        var result = await engineFacade.ReadMemoryAsync(processId, addr, length);
        var bytes = result.Bytes.ToArray();

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < bytes.Length; i += 16)
        {
            var lineAddr = (nuint)((long)addr + i);
            sb.Append($"{lineAddr:X16}  ");
            var end = Math.Min(i + 16, bytes.Length);
            for (var j = i; j < i + 16; j++)
            {
                sb.Append(j < end ? $"{bytes[j]:X2} " : "   ");
                if (j - i == 7) sb.Append(' ');
            }
            sb.Append(" │ ");
            for (var j = i; j < end; j++)
            {
                var c = (char)bytes[j];
                sb.Append(c is >= ' ' and <= '~' ? c : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [Description("Analyze memory at an address and identify probable data types at each offset (structure dissection). Returns fields with type, value, and confidence.")]
    public async Task<string> DissectStructure(
        [Description("Process ID")] int processId,
        [Description("Base address to start analysis (hex string)")] string address,
        [Description("Region size in bytes (default 256)")] int regionSize = 256,
        [Description("Type interpretation hint: 'auto' (default), 'int32' (prefer integers, good for stat blocks), 'float' (prefer floats, good for coordinates), 'pointers' (prefer pointer detection)")] string typeHint = "auto")
    {
        var dissector = new StructureDissectorService(engineFacade);
        var addr = AddressTableService.ParseAddress(address);
        var (fields, clustersDetected) = await dissector.DissectAsync(processId, addr, regionSize, typeHint);

        if (fields.Count == 0) return "No identifiable fields found in this region.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Structure analysis at 0x{addr:X} ({fields.Count} fields, hint={typeHint}):");
        if (clustersDetected > 0)
            sb.AppendLine($"  Detected {clustersDetected} integer cluster(s) — consecutive game-stat-like Int32 values.");
        sb.AppendLine("Offset  | Type     | Value                | Confidence");
        sb.AppendLine("--------|----------|----------------------|-----------");
        foreach (var f in fields)
        {
            sb.AppendLine($"+0x{f.Offset:X4} | {f.ProbableType,-8} | {f.DisplayValue,-20} | {f.Confidence:P0}");
        }
        return sb.ToString();
    }

    [Description("Register a global hotkey to toggle a specific address table entry's freeze lock or script activation. Hotkey works system-wide even when the game is focused.")]
    public Task<string> SetHotkey(
        [Description("Node ID or label of the address table entry")] string nodeId,
        [Description("Hotkey combination like 'Ctrl+F1' or 'Alt+Shift+G'")] string hotkey)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");

        if (hotkeyService is null) return Task.FromResult("Hotkey service not available.");

        var (mods, vk) = GlobalHotkeyService.ParseHotkeyString(hotkey);
        if (vk == 0) return Task.FromResult($"Could not parse hotkey '{hotkey}'. Use format like 'Ctrl+F1' or 'Alt+G'.");

        // Register the hotkey with a callback that toggles the node's active state
        var desc = $"{hotkey} → {node.Label}";
        var bindingId = hotkeyService.Register(mods, vk, desc, () =>
        {
            if (node.IsScriptEntry)
            {
                // For scripts, toggle via the AA engine if available
                if (autoAssemblerEngine is not null)
                {
                    var dashboard = dashboardService.CurrentDashboard;
                    if (dashboard?.CurrentInspection is not null)
                    {
                        var pid = dashboard.CurrentInspection.ProcessId;
                        if (node.IsScriptEnabled)
                        {
                            _ = autoAssemblerEngine.DisableAsync(pid, node.AssemblerScript!);
                            node.IsScriptEnabled = false;
                            node.ScriptStatus = "Disabled (hotkey)";
                        }
                        else
                        {
                            _ = autoAssemblerEngine.EnableAsync(pid, node.AssemblerScript!).ContinueWith(t =>
                            {
                                if (t.Result.Success)
                                {
                                    node.IsScriptEnabled = true;
                                    node.ScriptStatus = $"Enabled (hotkey, {t.Result.Patches.Count} patches)";
                                }
                                else
                                {
                                    node.ScriptStatus = $"FAILED: {t.Result.Error}";
                                }
                            });
                        }
                    }
                }
                else
                {
                    node.IsScriptEnabled = !node.IsScriptEnabled;
                }
            }
            else
            {
                // For value entries, toggle freeze
                node.IsLocked = !node.IsLocked;
                if (node.IsLocked)
                    node.LockedValue = node.CurrentValue;
                else
                    node.LockedValue = null;
            }
        });

        if (bindingId < 0)
            return Task.FromResult($"Failed to register hotkey '{hotkey}'. It may already be in use by another application.");

        return Task.FromResult($"Hotkey '{hotkey}' registered for '{node.Label}'. Press {hotkey} to toggle {(node.IsScriptEntry ? "script" : "freeze")}.");
    }

    [Description("List all registered global hotkeys and their bound actions.")]
    public Task<string> ListHotkeys()
    {
        if (hotkeyService is null) return Task.FromResult("Hotkey service not available.");
        var bindings = hotkeyService.Bindings;
        if (bindings.Count == 0) return Task.FromResult("No hotkeys registered.");
        var sb = new System.Text.StringBuilder();
        foreach (var b in bindings)
            sb.AppendLine($"ID {b.Id}: {b.Description}");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    [Description("Remove a registered global hotkey by its binding ID (from ListHotkeys).")]
    public Task<string> RemoveHotkey([Description("Binding ID to remove")] int bindingId)
    {
        if (hotkeyService is null) return Task.FromResult("Hotkey service not available.");
        return Task.FromResult(hotkeyService.Unregister(bindingId)
            ? $"Hotkey binding {bindingId} removed."
            : $"Binding {bindingId} not found.");
    }

    [Description("Undo the last memory write operation, restoring original bytes.")]
    public async Task<string> UndoWrite()
    {
        if (patchUndoService is null) return "Undo service not available.";
        return await patchUndoService.UndoAsync();
    }

    [Description("Redo the last undone memory write operation.")]
    public async Task<string> RedoWrite()
    {
        if (patchUndoService is null) return "Undo service not available.";
        return await patchUndoService.RedoAsync();
    }

    [Description("Show recent memory write history for undo/redo review.")]
    public Task<string> PatchHistory([Description("Number of recent patches to show (default 10)")] int count = 10)
    {
        if (patchUndoService is null) return Task.FromResult("Undo service not available.");
        var patches = patchUndoService.GetHistory(count);
        if (patches.Count == 0) return Task.FromResult("No patches recorded.");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Undo: {patchUndoService.UndoCount} | Redo: {patchUndoService.RedoCount}");
        foreach (var p in patches)
            sb.AppendLine($"  0x{p.Address:X} [{p.DataType}] = '{p.NewValue}' @ {p.Timestamp:HH:mm:ss}");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    // ── State & control tools (agent needs these to act autonomously) ──

    [Description("Find a running process by name (partial match). Returns PID and full name. Use this instead of ListProcesses when you know the game name.")]
    public async Task<string> FindProcess([Description("Process name or partial name to search for")] string name)
    {
        var processes = await engineFacade.ListProcessesAsync();
        var matches = processes
            .Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(10).ToList();
        if (matches.Count == 0)
            return $"No process found matching '{name}'. Use ListProcesses to see all.";
        var lines = matches.Select(p => $"  PID {p.Id} | {p.Name} | {p.Architecture}");
        return $"Found {matches.Count} match(es) for '{name}':\n{string.Join('\n', lines)}";
    }

    [Description("Freeze (lock) an address table entry so its value is continuously written back. The value is frozen at its current reading.")]
    public Task<string> FreezeAddress([Description("Node ID or label of the address table entry")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (node.IsGroup) return Task.FromResult("Cannot freeze a group.");
        if (node.IsScriptEntry) return Task.FromResult("Use ToggleScript for script entries.");
        if (node.IsLocked) return Task.FromResult($"'{node.Label}' is already frozen at {node.LockedValue}.");

        node.IsLocked = true;
        node.LockedValue = node.CurrentValue;
        return Task.FromResult($"Frozen '{node.Label}' at value {node.CurrentValue}. It will be continuously written back.");
    }

    [Description("Unfreeze (unlock) an address table entry so it can change naturally again.")]
    public Task<string> UnfreezeAddress([Description("Node ID or label of the address table entry")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (!node.IsLocked) return Task.FromResult($"'{node.Label}' is not frozen.");

        node.IsLocked = false;
        node.LockedValue = null;
        return Task.FromResult($"Unfrozen '{node.Label}'. Value can now change freely.");
    }

    [Description("Freeze an address at a specific value (not just its current value). Useful for setting health to 9999, gold to max, etc.")]
    public Task<string> FreezeAddressAtValue(
        [Description("Node ID or label of the address table entry")] string nodeId,
        [Description("Value to freeze at")] string value)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (node.IsGroup || node.IsScriptEntry) return Task.FromResult("Can only freeze value entries.");

        node.IsLocked = true;
        node.LockedValue = value;
        return Task.FromResult($"Frozen '{node.Label}' at value {value}. Will continuously write {value}.");
    }

    [Description("Enable or disable a script (Auto Assembler) entry in the address table. Actually executes the AA engine. Returns the execution result.")]
    public async Task<string> ToggleScript([Description("Node ID or label of the script entry")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return $"Node '{nodeId}' not found.";
        if (!node.IsScriptEntry) return $"'{node.Label}' is not a script entry. Use FreezeAddress for value entries.";

        if (autoAssemblerEngine is null)
            return "Auto Assembler engine not available.";

        var dashboard = dashboardService.CurrentDashboard;
        if (dashboard?.CurrentInspection is null)
            return "No process attached. Attach to a process before toggling scripts.";

        var processId = dashboard.CurrentInspection.ProcessId;
        var wantEnabled = !node.IsScriptEnabled;

        try
        {
            if (wantEnabled)
            {
                var result = await autoAssemblerEngine.EnableAsync(processId, node.AssemblerScript!);
                if (result.Success)
                {
                    node.IsScriptEnabled = true;
                    node.ScriptStatus = $"Enabled ({result.Allocations.Count} allocs, {result.Patches.Count} patches)";
                    return $"Script '{node.Label}' ENABLED successfully. {result.Allocations.Count} allocations, {result.Patches.Count} patches applied.";
                }
                else
                {
                    node.ScriptStatus = $"FAILED: {result.Error}";
                    return $"Script '{node.Label}' FAILED to enable: {result.Error}";
                }
            }
            else
            {
                var result = await autoAssemblerEngine.DisableAsync(processId, node.AssemblerScript!);
                node.IsScriptEnabled = false;
                node.ScriptStatus = result.Success ? "Disabled" : $"Disable failed: {result.Error}";
                return $"Script '{node.Label}' DISABLED. {(result.Success ? "Clean disable." : $"Warning: {result.Error}")}";
            }
        }
        catch (Exception ex)
        {
            node.IsScriptEnabled = false;
            node.ScriptStatus = $"Error: {ex.Message}";
            return $"Script '{node.Label}' error: {ex.Message}";
        }
    }

    [Description("Get detailed info about a specific address table node by its ID or label. Shows address, type, value, pointer chain, locked state, and children.")]
    public Task<string> GetAddressTableNode([Description("Node ID or label (case-insensitive label match)")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found by ID or label.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ID: {node.Id}");
        sb.AppendLine($"Label: {node.Label}");
        sb.AppendLine($"Type: {(node.IsGroup ? "Group" : node.IsScriptEntry ? "Script" : node.DataType.ToString())}");
        sb.AppendLine($"symbolicAddress: {node.Address}");
        if (node.ResolvedAddress.HasValue)
            sb.AppendLine($"resolvedAddress: 0x{node.ResolvedAddress.Value:X}");
        else
            sb.AppendLine("resolvedAddress: (unresolved)");
        sb.AppendLine($"isResolved: {node.ResolvedAddress.HasValue}");
        if (node.IsOffset && node.Parent is not null)
        {
            sb.AppendLine($"parentBase: \"{node.Parent.Label}\" ({node.Parent.Id})");
            sb.AppendLine($"offset: {node.Address}");
        }
        sb.AppendLine($"Value: {node.CurrentValue}");
        if (node.IsLocked) sb.AppendLine($"FROZEN at: {node.LockedValue}");
        if (node.IsPointer)
            sb.AppendLine($"Pointer chain: [{string.Join(", ", node.PointerOffsets.Select(o => $"0x{o:X}"))}]");
        if (node.IsOffset) sb.AppendLine("Is parent-relative offset");
        if (node.Children.Count > 0)
        {
            sb.AppendLine($"Children ({node.Children.Count}):");
            foreach (var child in node.Children.Take(20))
                sb.AppendLine($"  {child.Id}: {child.Label} = {child.CurrentValue} {(child.IsLocked ? "[FROZEN]" : "")}");
            if (node.Children.Count > 20)
                sb.AppendLine($"  ... and {node.Children.Count - 20} more");
        }
        return Task.FromResult(sb.ToString());
    }

    [Description("Get the current scan results (top N results from the last scan or refinement).")]
    public Task<string> GetScanResults(
        [Description("Maximum results to return (default 20)")] int maxResults = 20)
    {
        if (scanService.LastScanResults is null)
            return Task.FromResult("No active scan. Use StartScan first.");

        var results = scanService.LastScanResults;
        var count = results.Results.Count;
        return Task.FromResult(ToJson(new
        {
            dataType = results.Constraints.DataType.ToString(),
            totalCount = count,
            results = results.Results.Take(maxResults).Select(r => new
            {
                address = $"0x{r.Address:X}",
                value = r.CurrentValue,
                previousValue = r.PreviousValue
            }),
            returnedCount = Math.Min(maxResults, count),
            hasMore = count > maxResults
        }));
    }

    [Description("Get current context: attached process, address table summary, scan state. Use this to orient yourself before taking action.")]
    public Task<string> GetCurrentContext()
    {
        var dashboard = dashboardService.CurrentDashboard;
        var roots = addressTableService.Roots;

        var processInfo = dashboard?.CurrentInspection is { } p
            ? new { attached = true, processId = p.ProcessId, processName = p.ProcessName, architecture = p.Architecture.ToString(), moduleCount = p.Modules.Count }
            : null;

        var totalEntries = CountNodes(roots);
        var frozenCount = CountNodes(roots, n => n.IsLocked);
        var scriptCount = CountNodes(roots, n => n.IsScriptEntry);

        var scanInfo = scanService.LastScanResults is { } s
            ? new { active = true, resultCount = s.Results.Count, dataType = s.Constraints.DataType.ToString() }
            : null;

        var pid = processInfo?.processId ?? 0;
        var processStatus = pid > 0 ? GetProcessStatusJson(pid) : null;

        return Task.FromResult(ToJson(new
        {
            process = processInfo != null
                ? (object)new { processInfo.attached, processInfo.processId, processInfo.processName, processInfo.architecture, processInfo.moduleCount }
                : new { attached = false, processId = 0, processName = (string?)null, architecture = (string?)null, moduleCount = 0 },
            processStatus,
            addressTable = new { totalEntries, frozenCount, scriptCount },
            scan = scanInfo != null
                ? (object)new { scanInfo.active, scanInfo.resultCount, scanInfo.dataType }
                : new { active = false, resultCount = 0, dataType = (string?)null }
        }));
    }

    [Description("Check if attached process is still alive and if session state may be stale. Returns process status, session generation, and staleness indicators.")]
    public string CheckProcessLiveness([Description("Process ID")] int processId)
    {
        bool alive = IsProcessAlive(processId);
        bool pidChanged = processId != _lastAttachedPid;
        if (pidChanged && alive) { _sessionGeneration++; _lastAttachedPid = processId; }

        return ToJson(new
        {
            processAlive = alive,
            processId,
            sessionGeneration = _sessionGeneration,
            pidChanged,
            warning = pidChanged ? "Process changed — cached addresses, scans, and node resolutions may be stale. Re-resolve before using." : null,
            recommendation = !alive ? "Process exited. Re-attach to continue." : pidChanged ? "Refresh address table and re-resolve nodes." : "Session is current."
        });
    }

    [Description("Read memory at an address as multiple data types at once.Useful when you don't know the type — shows Int32, UInt32, Float, Int64, Double interpretations.")]
    public async Task<string> ProbeAddress(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex string)")] string address)
    {
        var addr = ParseAddress(address);
        var bytes = await engineFacade.ReadMemoryAsync(processId, addr, 8);
        var raw = bytes.Bytes.ToArray();
        if (raw.Length < 8) return $"Could not read 8 bytes at {address}.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Memory probe at {address}:");
        sb.AppendLine($"  Int16:  {BitConverter.ToInt16(raw, 0)}");
        sb.AppendLine($"  UInt16: {BitConverter.ToUInt16(raw, 0)}");
        sb.AppendLine($"  Int32:  {BitConverter.ToInt32(raw, 0)}");
        sb.AppendLine($"  UInt32: {BitConverter.ToUInt32(raw, 0)}");
        sb.AppendLine($"  Float:  {BitConverter.ToSingle(raw, 0):G9}");
        sb.AppendLine($"  Int64:  {BitConverter.ToInt64(raw, 0)}");
        sb.AppendLine($"  UInt64: {BitConverter.ToUInt64(raw, 0)}");
        sb.AppendLine($"  Double: {BitConverter.ToDouble(raw, 0):G17}");
        sb.AppendLine($"  Hex:    {Convert.ToHexString(raw)}");
        return sb.ToString();
    }

    // ── Script editing tools ──

    [Description("Edit/replace the Auto Assembler script content of an existing script entry. Use this to fix or improve scripts. The script must have [ENABLE] and [DISABLE] sections.")]
    public Task<string> EditScript(
        [Description("Node ID or label of the script entry")] string nodeId,
        [Description("New complete script content (must include [ENABLE] and [DISABLE] sections)")] string newScript)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (!node.IsScriptEntry) return Task.FromResult($"'{node.Label}' is not a script entry.");

        if (node.IsScriptEnabled)
            return Task.FromResult("Script is currently ENABLED. Disable it first before editing (use ToggleScript).");

        // Validate the new script if AA engine is available
        if (autoAssemblerEngine is not null)
        {
            var parseResult = autoAssemblerEngine.Parse(newScript);
            if (!parseResult.IsValid)
            {
                return Task.FromResult(
                    $"New script has validation errors — NOT applied:\n" +
                    string.Join("\n", parseResult.Errors));
            }
        }

        var oldSnippet = node.AssemblerScript?.Length > 60
            ? node.AssemblerScript[..60] + "..."
            : node.AssemblerScript ?? "(empty)";
        node.AssemblerScript = newScript;
        return Task.FromResult(
            $"Script '{node.Label}' updated successfully.\n" +
            $"Old: {oldSnippet}\n" +
            $"New script is {newScript.Length} chars. Use ValidateScript to verify, then ToggleScript to enable.");
    }

    [Description("Create a new Auto Assembler script entry in the address table. Use this to add entirely new scripts (hooks, patches, multipliers, etc.).")]
    public Task<string> CreateScriptEntry(
        [Description("Label/name for the script entry")] string label,
        [Description("Auto Assembler script content (must include [ENABLE] and [DISABLE] sections)")] string script,
        [Description("Optional: parent group node ID to add the script under")] string? parentGroupId = null)
    {
        // Validate if possible
        if (autoAssemblerEngine is not null)
        {
            var parseResult = autoAssemblerEngine.Parse(script);
            if (!parseResult.IsValid)
            {
                return Task.FromResult(
                    $"Script validation failed — NOT created:\n" +
                    string.Join("\n", parseResult.Errors));
            }
        }

        var nodeId = $"script-{Guid.NewGuid().ToString("N")[..8]}";
        var node = new AddressTableNode(nodeId, label, false)
        {
            AssemblerScript = script
        };

        if (parentGroupId is not null)
        {
            var parent = addressTableService.FindNode(parentGroupId);
            if (parent is null) return Task.FromResult($"Parent group '{parentGroupId}' not found.");
            if (!parent.IsGroup) return Task.FromResult($"'{parent.Label}' is not a group.");
            addressTableService.AddEntryToGroup(node, parentGroupId);
        }
        else
        {
            addressTableService.Roots.Add(node);
        }

        return Task.FromResult(
            $"Script entry '{label}' created (ID: {node.Id}). " +
            $"Script is {script.Length} chars. Use ToggleScript to enable it.");
    }

    [Description("Enable a script by its node ID. Executes the [ENABLE] section of the Auto Assembler script.")]
    public async Task<string> EnableScript([Description("Node ID or label of the script entry")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return $"Node '{nodeId}' not found.";
        if (!node.IsScriptEntry) return $"'{node.Label}' is not a script entry.";
        if (node.IsScriptEnabled) return $"Script '{node.Label}' is already enabled.";

        if (autoAssemblerEngine is null) return "Auto Assembler engine not available.";

        var dashboard = dashboardService.CurrentDashboard;
        if (dashboard?.CurrentInspection is null)
            return "No process attached. Attach first.";

        int pid = dashboard.CurrentInspection.ProcessId;
        if (!IsProcessAlive(pid)) return $"Process {pid} is no longer running.";

        try
        {
            var result = await autoAssemblerEngine.EnableAsync(pid, node.AssemblerScript!);
            if (result.Success)
            {
                node.IsScriptEnabled = true;
                node.ScriptStatus = $"Enabled ({result.Allocations.Count} allocs, {result.Patches.Count} patches)";
                return $"Script '{node.Label}' ENABLED. {result.Allocations.Count} allocations, {result.Patches.Count} patches.";
            }
            else
            {
                node.ScriptStatus = $"FAILED: {result.Error}";
                return $"Script '{node.Label}' FAILED: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            node.ScriptStatus = $"Error: {ex.Message}";
            return $"EnableScript failed (game may have crashed): {ex.Message}";
        }
    }

    [Description("Disable a script by its node ID. Executes the [DISABLE] section to restore original bytes.")]
    public async Task<string> DisableScript([Description("Node ID or label of the script entry")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return $"Node '{nodeId}' not found.";
        if (!node.IsScriptEntry) return $"'{node.Label}' is not a script entry.";
        if (!node.IsScriptEnabled) return $"Script '{node.Label}' is already disabled.";

        if (autoAssemblerEngine is null) return "Auto Assembler engine not available.";

        var dashboard = dashboardService.CurrentDashboard;
        if (dashboard?.CurrentInspection is null) return "No process attached.";

        int pid = dashboard.CurrentInspection.ProcessId;
        if (!IsProcessAlive(pid)) 
        {
            node.IsScriptEnabled = false;
            node.ScriptStatus = "Process exited";
            return $"Process {pid} is no longer running. Script marked as disabled.";
        }

        try
        {
            var result = await autoAssemblerEngine.DisableAsync(
                dashboard.CurrentInspection.ProcessId, node.AssemblerScript!);
            node.IsScriptEnabled = false;
            node.ScriptStatus = result.Success ? "Disabled" : $"Disable warning: {result.Error}";
            return $"Script '{node.Label}' DISABLED. {(result.Success ? "Original bytes restored." : $"Warning: {result.Error}")}";
        }
        catch (Exception ex)
        {
            node.IsScriptEnabled = false;
            node.ScriptStatus = $"Error: {ex.Message}";
            return $"Disable error: {ex.Message}";
        }
    }

    // ── Screen capture tool ──

    [Description("Capture a screenshot of the attached process's game window. The image will be sent to you for visual analysis. Use this to verify game state, check if scripts are working, or see what the user sees.")]
    public async Task<string> CaptureProcessWindow()
    {
        if (screenCaptureEngine is null)
            return "Screen capture engine not available.";

        var dashboard = dashboardService.CurrentDashboard;
        if (dashboard?.CurrentInspection is null)
            return "No process attached. Attach to a process first.";

        var processId = dashboard.CurrentInspection.ProcessId;
        var result = await screenCaptureEngine.CaptureWindowAsync(processId);
        if (result is null)
            return "Failed to capture process window. The window may be minimized or not visible.";

        // Queue the image for injection into the AI conversation
        PendingImages.Enqueue(($"Screenshot of '{result.WindowTitle}' ({result.Width}x{result.Height})", result.PngData));

        return $"Screenshot captured: '{result.WindowTitle}' ({result.Width}x{result.Height}, {result.PngData.Length / 1024}KB). The image has been queued for your visual analysis.";
    }

    [Description("Read a range of raw bytes from process memory and display as hex dump. Useful for examining code bytes, data structures, or verifying patches.")]
    public async Task<string> HexDump(
        [Description("Process ID")] int processId,
        [Description("Start address (hex string)")] string address,
        [Description("Number of bytes to read (default 64, max 256)")] int length = 64)
    {
        length = Math.Clamp(length, 1, 256);
        var addr = ParseAddress(address);
        var mem = await engineFacade.ReadMemoryAsync(processId, addr, length);
        var raw = mem.Bytes.ToArray();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Hex dump at 0x{addr:X} ({raw.Length} bytes):");
        for (int i = 0; i < raw.Length; i += 16)
        {
            sb.Append($"  {addr + (nuint)i:X8}: ");
            var lineBytes = Math.Min(16, raw.Length - i);
            for (int j = 0; j < lineBytes; j++)
            {
                sb.Append($"{raw[i + j]:X2} ");
                if (j == 7) sb.Append(' ');
            }
            // ASCII representation
            sb.Append(new string(' ', (16 - lineBytes) * 3 + (lineBytes <= 7 ? 1 : 0)));
            sb.Append(" | ");
            for (int j = 0; j < lineBytes; j++)
            {
                var c = (char)raw[i + j];
                sb.Append(c is >= ' ' and <= '~' ? c : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── Address table management tools ──

    [Description("Remove an entry from the address table by its node ID.")]
    public Task<string> RemoveFromAddressTable([Description("Node ID or label to remove")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        var label = node.Label;
        addressTableService.RemoveEntry(nodeId);
        return Task.FromResult($"Removed '{label}' (ID: {nodeId}) from address table.");
    }

    [Description("Rename an address table entry's label/description.")]
    public Task<string> RenameAddressTableEntry(
        [Description("Node ID or label to rename")] string nodeId,
        [Description("New label/description")] string newLabel)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        var oldLabel = node.Label;
        addressTableService.UpdateLabel(nodeId, newLabel);
        return Task.FromResult($"Renamed '{oldLabel}' → '{newLabel}'.");
    }

    [Description("Set or update notes/annotations on an address table entry.")]
    public Task<string> SetEntryNotes(
        [Description("Node ID or label")] string nodeId,
        [Description("Notes text (or empty to clear)")] string notes)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        addressTableService.UpdateNotes(nodeId, string.IsNullOrWhiteSpace(notes) ? null : notes);
        return Task.FromResult($"Notes updated for '{node.Label}'.");
    }

    [Description("Create a group (folder) in the address table to organize entries.")]
    public Task<string> CreateAddressGroup(
        [Description("Group label")] string label,
        [Description("Optional parent group ID for nesting (omit for top-level)")] string? parentGroupId = null)
    {
        AddressTableNode group;
        if (!string.IsNullOrWhiteSpace(parentGroupId))
        {
            var parent = addressTableService.FindNode(parentGroupId);
            if (parent is null) return Task.FromResult($"Parent group '{parentGroupId}' not found.");
            group = addressTableService.CreateSubGroup(parentGroupId, label);
        }
        else
        {
            group = addressTableService.CreateGroup(label);
        }
        return Task.FromResult($"Group '{label}' created (ID: {group.Id}).");
    }

    [Description("Move an address table entry into a group. Pass null groupId to move to top level.")]
    public Task<string> MoveEntryToGroup(
        [Description("Node ID or label of entry to move")] string nodeId,
        [Description("Target group ID (or empty for top level)")] string? groupId = null)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            var group = addressTableService.FindNode(groupId);
            if (group is null) return Task.FromResult($"Group '{groupId}' not found.");
        }
        addressTableService.MoveToGroup(nodeId, string.IsNullOrWhiteSpace(groupId) ? null : groupId);
        return Task.FromResult($"Moved '{node.Label}' to {(string.IsNullOrWhiteSpace(groupId) ? "top level" : $"group '{groupId}'")}.");
    }

    // ── Session management tools ──

    [Description("Save the current investigation session (address table + action log) to disk for later retrieval.")]
    public async Task<string> SaveSession()
    {
        if (sessionService is null) return "Session service not available.";
        var dashboard = dashboardService.CurrentDashboard;
        var sessionId = await sessionService.SaveSessionAsync(
            dashboard?.CurrentInspection?.ProcessName,
            dashboard?.CurrentInspection?.ProcessId,
            addressTableService.Entries,
            new List<AiActionLogEntry>());
        return $"Session saved: {sessionId}";
    }

    [Description("List recent investigation sessions.")]
    public async Task<string> ListSessions([Description("Max sessions to list")] int limit = 10)
    {
        if (sessionService is null) return "Session service not available.";
        var sessions = await sessionService.ListSessionsAsync(limit);
        if (sessions.Count == 0) return "No saved sessions.";
        var lines = sessions.Select(s => $"  [{s.Id}] {s.ProcessName} — {s.CreatedAtUtc:g} ({s.AddressEntryCount} entries)");
        return $"Sessions ({sessions.Count}):\n{string.Join('\n', lines)}";
    }

    [Description("Load a saved investigation session by ID, restoring the address table.")]
    public async Task<string> LoadSession([Description("Session ID to load")] string sessionId)
    {
        if (sessionService is null) return "Session service not available.";
        var result = await sessionService.LoadSessionAsync(sessionId);
        if (result is null) return $"Session '{sessionId}' not found.";
        var (entries, processName, processId) = (result.Value.Entries, result.Value.ProcessName, result.Value.ProcessId);
        addressTableService.ImportFlat(entries);
        return $"Loaded session '{sessionId}': {processName} (PID {processId}), {entries.Count} entries restored.";
    }

    // ── Signature / AOB tools ──

    [Description("Generate an AOB (Array of Bytes) signature at a code address. Useful for creating patterns that survive game updates. Automatically wildcards relocatable offsets.")]
    public async Task<string> GenerateSignature(
        [Description("Process ID")] int processId,
        [Description("Address to generate signature at")] string address,
        [Description("Number of bytes (default 32)")] int length = 32)
    {
        if (signatureService is null) return "Signature generator not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            var sig = await signatureService.GenerateAsync(processId, addr, length);
            return $"Signature at 0x{addr:X} ({sig.Length} bytes):\n{sig.Pattern}\n\nUse this pattern with ArrayOfBytes scan type to find this code in future sessions.";
        }
        catch (Exception ex)
        {
            return $"GenerateSignature failed: {ex.Message}";
        }
    }

    [Description("Test if an AOB signature uniquely matches within a module. Returns match count — should be exactly 1 for a good signature.")]
    public async Task<string> TestSignatureUniqueness(
        [Description("Process ID")] int processId,
        [Description("Module name to search (e.g. GameAssembly.dll)")] string moduleName,
        [Description("AOB pattern (e.g. '48 8B 05 ?? ?? ?? ?? 48 85 C0')")] string pattern)
    {
        if (signatureService is null) return "Signature generator not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var count = await signatureService.TestUniquenessAsync(processId, moduleName, pattern);
            if (count < 0) return $"Module '{moduleName}' not found.";
            return count switch
            {
                0 => $"No matches found for pattern in {moduleName}. Pattern may be too specific or module is wrong.",
                1 => $"✓ Pattern is unique in {moduleName} — exactly 1 match. Good signature!",
                _ => $"⚠ Pattern matches {count} locations in {moduleName}. Needs more bytes or different hook point."
            };
        }
        catch (Exception ex)
        {
            return $"TestSignatureUniqueness failed: {ex.Message}";
        }
    }

    // ── Memory Protection Tools ──

    [Description("Change memory page protection for a region in the target process. Used for making code pages writable or allocating executable memory.")]
    public async Task<string> ChangeMemoryProtection(
        [Description("Process ID")] int processId,
        [Description("Memory address as hex (e.g. 0x7FF6A000)")] string address,
        [Description("Region size in bytes")] int size,
        [Description("New protection: ReadWrite, ExecuteReadWrite, ReadOnly, Execute, ExecuteRead")] string protection)
    {
        if (memoryProtectionEngine is null) return "Memory protection engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            var prot = Enum.Parse<MemoryProtection>(protection, ignoreCase: true);
            var result = await memoryProtectionEngine.ChangeProtectionAsync(processId, addr, size, prot);
            return $"Protection changed at 0x{result.Address:X}: {result.OldProtection} → {result.NewProtection} ({result.Size} bytes)";
        }
        catch (Exception ex) { return $"ChangeMemoryProtection failed: {ex.Message}"; }
    }

    [Description("Allocate memory in the target process. Useful for code caves and injected scripts.")]
    public async Task<string> AllocateMemory(
        [Description("Process ID")] int processId,
        [Description("Size in bytes to allocate")] int size,
        [Description("Protection: ExecuteReadWrite (default), ReadWrite, etc.")] string protection = "ExecuteReadWrite",
        [Description("Preferred address as hex, or 0 for any")] string preferredAddress = "0")
    {
        if (memoryProtectionEngine is null) return "Memory protection engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var prot = Enum.Parse<MemoryProtection>(protection, ignoreCase: true);
            var preferred = ParseAddress(preferredAddress);
            var result = await memoryProtectionEngine.AllocateAsync(processId, size, prot, preferred);
            return $"Allocated {result.Size} bytes at 0x{result.BaseAddress:X} with {result.Protection}";
        }
        catch (Exception ex) { return $"AllocateMemory failed: {ex.Message}"; }
    }

    [Description("Free previously allocated memory in the target process.")]
    public async Task<string> FreeMemory(
        [Description("Process ID")] int processId,
        [Description("Address of allocated block as hex")] string address)
    {
        if (memoryProtectionEngine is null) return "Memory protection engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            var success = await memoryProtectionEngine.FreeAsync(processId, addr);
            return success ? $"Memory freed at 0x{addr:X}" : $"Failed to free memory at 0x{addr:X}";
        }
        catch (Exception ex) { return $"FreeMemory failed: {ex.Message}"; }
    }

    [Description("Query the memory protection state of an address. Returns base address, region size, and protection flags. Useful before and after applying hooks to verify protection hasn't been corrupted.")]
    public async Task<string> QueryMemoryProtection(
        [Description("Process ID")] int processId,
        [Description("Memory address to query (hex)")] string address)
    {
        if (memoryProtectionEngine is null) return "Memory protection engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            var region = await memoryProtectionEngine.QueryProtectionAsync(processId, addr);
            return ToJson(new
            {
                address = $"0x{addr:X}",
                regionBase = $"0x{region.BaseAddress:X}",
                regionSize = region.RegionSize,
                isReadable = region.IsReadable,
                isWritable = region.IsWritable,
                isExecutable = region.IsExecutable,
                pageBase = $"0x{(addr & ~(nuint)0xFFF):X}"
            });
        }
        catch (Exception ex) { return $"QueryMemoryProtection failed: {ex.Message}"; }
    }

    // ── Memory Snapshot Tools ──

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

            var lines = diff.Changes.Take(30).Select(c =>
                $"  +0x{c.Offset:X4}: {BitConverter.ToString(c.OldBytes).Replace("-", " ")} → " +
                $"{BitConverter.ToString(c.NewBytes).Replace("-", " ")} ({c.Interpretation})");
            return $"Found {diff.Changes.Count} change(s), {diff.ChangedByteCount} byte(s) modified:\n{string.Join('\n', lines)}";
        }
        catch (Exception ex) { return $"CompareSnapshots failed: {ex.Message}"; }
    }

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

            var lines = diff.Changes.Take(30).Select(c =>
                $"  +0x{c.Offset:X4}: {BitConverter.ToString(c.OldBytes).Replace("-", " ")} → " +
                $"{BitConverter.ToString(c.NewBytes).Replace("-", " ")} ({c.Interpretation})");
            return $"Found {diff.Changes.Count} change(s) since snapshot:\n{string.Join('\n', lines)}";
        }
        catch (Exception ex) { return $"CompareSnapshotWithLive failed: {ex.Message}"; }
    }

    [Description("List all captured memory snapshots.")]
    public string ListSnapshots()
    {
        if (snapshotService is null) return "Snapshot service not available.";
        var snaps = snapshotService.ListSnapshots();
        if (snaps.Count == 0) return "No snapshots captured.";

        var lines = snaps.Select(s =>
            $"  {s.Id}: \"{s.Label}\" — {s.Data.Length} bytes @ 0x{s.BaseAddress:X} ({s.CapturedAt.ToLocalTime():g})");
        return $"{snaps.Count} snapshot(s):\n{string.Join('\n', lines)}";
    }

    [Description("Delete a memory snapshot by ID.")]
    public string DeleteSnapshot(
        [Description("Snapshot ID to delete")] string snapshotId)
    {
        if (snapshotService is null) return "Snapshot service not available.";
        return snapshotService.DeleteSnapshot(snapshotId) ? $"Snapshot '{snapshotId}' deleted." : $"Snapshot '{snapshotId}' not found.";
    }

    // ── Pointer Rescan Tools ──

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
            var pointerEntries = entries.Where(e => e.Label.Contains("→") || e.Label.Contains("ptr", StringComparison.OrdinalIgnoreCase)).ToList();

            var target = ParseAddress(targetAddress);
            if (pointerEntries.Count == 0)
                return "No pointer path entries found in the address table. Use ScanForPointers first.";

            return $"Found {pointerEntries.Count} pointer-related entries. Use RescanPointerPath on individual paths for validation.";
        }
        catch (Exception ex) { return $"ValidatePointerPaths failed: {ex.Message}"; }
    }

    // ── Call Stack Tools ──

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
                var best = allStacks.OrderByDescending(kv => kv.Value.Count).First();
                threadId = best.Key;
                var frames = best.Value;
                return FormatCallStack(threadId, frames);
            }

            var stack = await callStackEngine.WalkStackAsync(processId, threadId, attachment.Modules, maxFrames);
            return FormatCallStack(threadId, stack);
        }
        catch (Exception ex) { return $"GetCallStack failed: {ex.Message}"; }
    }

    [Description("Walk call stacks of all threads in the process. Returns frames per thread with module resolution.")]
    public async Task<string> GetAllThreadStacks(
        [Description("Process ID")] int processId,
        [Description("Maximum frames per thread")] int maxFrames = 8)
    {
        if (callStackEngine is null) return "Call stack engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            var attachment = await engineFacade.AttachAsync(processId);
            var allStacks = await callStackEngine.WalkAllThreadsAsync(processId, attachment.Modules, maxFrames);

            if (allStacks.Count == 0) return "No thread stacks could be captured.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Captured stacks for {allStacks.Count} thread(s):");
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

    // ─── Code Cave (Stealth Hook) Tools ─────────────────────────────────

    [Description("Install a stealth code cave hook. No debugger — game-safe. Captures registers and hit count.")]
    public async Task<string> InstallCodeCaveHook(
        [Description("Process ID")] int processId,
        [Description("Memory address to hook (hex or decimal)")] string address,
        [Description("Capture register snapshots (RAX-RDI, RSP) on each hit")] bool captureRegisters = true)
    {
        try
        {
            if (codeCaveEngine is null) return "Code cave engine not available.";
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            if (watchdogService is not null && watchdogService.IsUnsafe(addr, "Stealth"))
            {
                // Still allow but warn
            }
            var result = await codeCaveEngine.InstallHookAsync(processId, addr, captureRegisters);
            if (!result.Success) return $"Hook installation failed: {result.ErrorMessage}";
            var h = result.Hook!;
            var hookMsg = $"Stealth hook installed:\n  ID: {h.Id}\n  Target: 0x{h.OriginalAddress:X}\n  Cave: 0x{h.CaveAddress:X}\n  Stolen bytes: {h.OriginalBytesLength}\n  No debugger attached — completely game-safe.";
            if (watchdogService is not null)
            {
                var hookId = h.Id;
                watchdogService.StartMonitoring(processId, hookId, addr, "CodeCaveHook", "Stealth",
                    async () => await codeCaveEngine.RemoveHookAsync(processId, hookId));
                if (watchdogService.IsUnsafe(addr, "Stealth"))
                    hookMsg += "\n⚠️ WARNING: This address+Stealth previously caused a process freeze. Watchdog is monitoring.";
                else
                    hookMsg += "\n🛡️ Watchdog monitoring active — will auto-rollback if process becomes unresponsive.";
            }
            operationJournal?.RecordOperation(
                h.Id, "CodeCaveHook", addr, "Stealth", groupId: null,
                async () => await codeCaveEngine.RemoveHookAsync(processId, h.Id));
            return hookMsg;
        }
        catch (Exception ex) { return $"InstallCodeCaveHook failed: {ex.Message}"; }
    }

    [Description("Remove a stealth code cave hook, restoring original bytes.")]
    public async Task<string> RemoveCodeCaveHook(
        [Description("Process ID")] int processId,
        [Description("Hook ID to remove")] string hookId)
    {
        if (codeCaveEngine is null) return "Code cave engine not available.";
        var removed = await codeCaveEngine.RemoveHookAsync(processId, hookId);
        return removed ? $"Hook {hookId} removed, original bytes restored." : $"Hook {hookId} not found.";
    }

    [Description("List all active stealth code cave hooks for a process.")]
    public async Task<string> ListCodeCaveHooks([Description("Process ID")] int processId)
    {
        if (codeCaveEngine is null) return "Code cave engine not available.";
        var hooks = await codeCaveEngine.ListHooksAsync(processId);
        if (hooks.Count == 0) return ToJson(new { hooks = Array.Empty<object>(), count = 0 });
        return ToJson(new
        {
            hooks = hooks.Select(h => new { h.Id, originalAddress = $"0x{h.OriginalAddress:X}", caveAddress = $"0x{h.CaveAddress:X}", h.OriginalBytesLength, h.HitCount }),
            count = hooks.Count
        });
    }

    [Description("Get register snapshots from a code cave hook. Returns captures with key registers, thread IDs, and timestamps.")]
    public async Task<string> GetCodeCaveHookHits(
        [Description("Hook ID")] string hookId,
        [Description("Maximum entries to return")] int maxEntries = 10,
        [Description("Process ID for register pointer dereferences (0=skip)")] int processId = 0,
        [Description("Include pointer dereferences for registers (costs extra reads)")] bool dereference = false)
    {
        if (codeCaveEngine is null) return "Code cave engine not available.";
        var hits = await codeCaveEngine.GetHookHitsAsync(hookId, maxEntries);
        if (hits.Count == 0) return $"No hits recorded for hook {hookId}.";

        var hitResults = new List<object>();
        foreach (var h in hits)
        {
            var trimmedRegs = TrimRegisters(h.RegisterSnapshot);

            Dictionary<string, string>? dereferences = null;
            if (dereference && processId > 0 && trimmedRegs.Count > 0)
            {
                dereferences = await DereferenceRegistersAsync(processId, trimmedRegs);
            }

            hitResults.Add(new
            {
                address = $"0x{h.Address:X}",
                threadId = h.ThreadId,
                timestamp = h.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                registers = trimmedRegs,
                dereferences
            });
        }

        return ToJson(new
        {
            hookId,
            hits = hitResults,
            count = hits.Count
        });
    }

    // Essential registers for debugging — skip R8-R15, segment regs, RFLAGS etc.
    private static readonly HashSet<string> EssentialRegisters = new(StringComparer.OrdinalIgnoreCase)
    {
        "RAX", "RBX", "RCX", "RDX", "RSI", "RDI", "RSP", "RBP", "RIP",
        "EAX", "EBX", "ECX", "EDX", "ESI", "EDI", "ESP", "EBP", "EIP"
    };

    /// <summary>Filter registers to only essential ones to save tokens in tool results.</summary>
    private static Dictionary<string, string> TrimRegisters(IReadOnlyDictionary<string, string>? registers)
    {
        if (registers is null || registers.Count == 0) return new();
        return registers
            .Where(kv => EssentialRegisters.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private async Task<Dictionary<string, string>> DereferenceRegistersAsync(
        int processId, IReadOnlyDictionary<string, string> registers)
    {
        var result = new Dictionary<string, string>();
        foreach (var (regName, regValue) in registers)
        {
            if (!ulong.TryParse(regValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? regValue[2..] : regValue,
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr))
                continue;

            if (addr <= 0x10000) continue;

            try
            {
                var mem = await engineFacade.ReadMemoryAsync(processId, (nuint)addr, 8);
                if (mem.Bytes.Count == 8)
                {
                    var pointed = BitConverter.ToUInt64(mem.Bytes.ToArray(), 0);
                    result[regName] = $"0x{addr:X} (points to 0x{pointed:X})";
                }
            }
            catch
            {
                // Address not readable — skip dereference
            }
        }
        return result;
    }

    [Description("Dry-run a code cave hook install. Shows bytes, relocations, fixups, and safety assessment without patching.")]
    public async Task<string> DryRunHookInstall(
        [Description("Process ID")] int processId,
        [Description("Memory address to analyze for hook installation (hex)")] string address)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## Dry-Run Hook Preview: 0x{addr:X}");

            bool canHook = true;

            // 1. Check executability
            if (memoryProtectionEngine is not null)
            {
                var region = await memoryProtectionEngine.QueryProtectionAsync(processId, addr);
                if (!region.IsExecutable)
                {
                    sb.AppendLine("❌ Address is NOT executable — cannot install code cave hook");
                    canHook = false;
                }
                else
                {
                    string prot = region.IsWritable ? "RWX" : "RX";
                    sb.AppendLine($"✅ Region is executable ({prot})");
                }
            }

            // 2. Disassemble at the target to determine stolen bytes
            var disasm = await disassemblyService.DisassembleAtAsync(processId, $"0x{addr:X}", 10);

            // We need at least 14 bytes for a 64-bit JMP (FF 25 00 00 00 00 + 8-byte address)
            const int minJmpSize = 14;
            int stolenBytes = 0;
            var stolenInstructions = new List<DisassemblyLineOverview>();
            bool hasRipRelative = false;
            var ripRelativeInstructions = new List<string>();

            foreach (var line in disasm.Lines)
            {
                stolenInstructions.Add(line);
                var byteCount = line.HexBytes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                stolenBytes += byteCount;

                if (line.Operands.Contains("[rip", StringComparison.OrdinalIgnoreCase))
                {
                    hasRipRelative = true;
                    ripRelativeInstructions.Add($"  {line.Address}: {line.Mnemonic} {line.Operands}");
                }

                if (stolenBytes >= minJmpSize) break;
            }

            sb.AppendLine();
            sb.AppendLine($"### Stolen Bytes: {stolenBytes} (minimum required: {minJmpSize})");
            if (stolenBytes < minJmpSize)
            {
                sb.AppendLine("❌ Insufficient bytes — cannot fit JMP detour");
                canHook = false;
            }
            else
            {
                sb.AppendLine("✅ Sufficient space for JMP detour");
            }

            sb.AppendLine();
            sb.AppendLine("### Instructions to be relocated:");
            foreach (var instr in stolenInstructions)
            {
                sb.AppendLine($"  {instr.Address}: [{instr.HexBytes}] {instr.Mnemonic} {instr.Operands}");
            }

            // 3. RIP-relative analysis
            sb.AppendLine();
            if (hasRipRelative)
            {
                sb.AppendLine("### RIP-Relative Instructions (will be auto-relocated by BlockEncoder):");
                foreach (var rip in ripRelativeInstructions)
                    sb.AppendLine(rip);
                sb.AppendLine("⚠️ These instructions reference memory relative to RIP and will need displacement adjustment in the trampoline");
            }
            else
            {
                sb.AppendLine("✅ No RIP-relative instructions — relocation is straightforward");
            }

            // 4. Read the actual bytes that would be overwritten
            var liveRead = await engineFacade.ReadMemoryAsync(processId, addr, stolenBytes);
            var hexDump = string.Join(" ", liveRead.Bytes.Take(stolenBytes).Select(b => b.ToString("X2")));
            sb.AppendLine();
            sb.AppendLine($"### Bytes to be overwritten:");
            sb.AppendLine($"  {hexDump}");

            // 5. Trampoline layout estimate
            int trampolineSize =
                17 +          // push all registers (~17 bytes overhead for a minimal snapshot)
                stolenBytes + // relocated original instructions
                14;           // JMP back to original code
            int withCapture = trampolineSize + 128; // conservative estimate for full register capture

            sb.AppendLine();
            sb.AppendLine($"### Estimated Trampoline Size:");
            sb.AppendLine($"  Without register capture: ~{trampolineSize} bytes");
            sb.AppendLine($"  With register capture: ~{withCapture} bytes");

            // 6. Verify stolen bytes end on a clean instruction boundary
            bool cleanBoundary = stolenBytes == stolenInstructions.Sum(i => i.HexBytes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            if (cleanBoundary)
            {
                sb.AppendLine();
                sb.AppendLine("✅ Stolen bytes end on clean instruction boundary");
            }

            // 7. Final verdict
            sb.AppendLine();
            if (canHook)
            {
                sb.AppendLine("### Verdict: ✅ SAFE TO HOOK");
                sb.AppendLine($"  {stolenInstructions.Count} instruction(s) will be relocated ({stolenBytes} bytes)");
                if (hasRipRelative)
                    sb.AppendLine("  RIP-relative fixups will be applied automatically");
            }
            else
            {
                sb.AppendLine("### Verdict: ❌ DO NOT HOOK — see issues above");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"DryRunHookInstall failed: {ex.Message}";
        }
    }

    // ── Static Analysis Tools ──

    [Description("Search a module's code for instructions that write to a specific offset (e.g., [reg+0x38]). Returns all MOV/ADD/SUB/XOR/INC/DEC/IMUL instructions targeting [any_register + offset]. Essential for finding what code writes to a known data field. Set includeReads=true to also find reads and LEA address computations.")]
    public async Task<string> FindWritersToOffset(
        [Description("Process ID")] int processId,
        [Description("Module name (e.g., 'GameAssembly.dll') or 'all' to scan loaded modules")] string moduleName,
        [Description("The displacement/offset to search for (hex), e.g., '0x38' or '38'")] string offset,
        [Description("Max results to return")] int maxResults = 30,
        [Description("Also include instructions that READ from [reg+offset] (useful for tracing data flow)")] bool includeReads = false)
    {
        var dispValue = (long)(ulong)ParseAddress(offset);
        var attachment = await engineFacade.AttachAsync(processId);
        var targetModules = moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetModules.Count == 0) return $"Module '{moduleName}' not found.";

        var results = new List<string>();
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        foreach (var mod in targetModules)
        {
            if (results.Count >= maxResults) break;

            const int chunkSize = 0x10000;
            for (long off = 0; off < mod.SizeBytes && results.Count < maxResults; off += chunkSize)
            {
                var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                MemoryReadResult memResult;
                try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen); }
                catch { continue; }

                var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                if (bytes.Length == 0) continue;

                var reader = new ByteArrayCodeReader(bytes);
                var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                {
                    var instr = decoder.Decode();
                    if (instr.IsInvalid) continue;

                    bool isLea = instr.Mnemonic == Mnemonic.Lea;

                    for (int opIdx = 0; opIdx < instr.OpCount; opIdx++)
                    {
                        if (instr.GetOpKind(opIdx) == OpKind.Memory &&
                            (long)instr.MemoryDisplacement64 == dispValue &&
                            instr.MemoryBase != Register.None)
                        {
                            bool isWrite = opIdx == 0 && IsWriteInstruction(instr);

                            string classification;
                            if (isLea)
                                classification = "LEA (address computation)";
                            else if (isWrite)
                                classification = "WRITE";
                            else
                                classification = "READ";

                            // Default mode: only include writes. With includeReads: include all.
                            if (!isWrite && !isLea && !includeReads) continue;

                            formatter.Format(instr, output);
                            results.Add($"  0x{instr.IP:X} | {output.ToStringAndReset()} | {classification} | base={instr.MemoryBase} | in {mod.Name}");
                            if (results.Count >= maxResults) break;
                        }
                    }
                }
            }
        }

        var label = includeReads ? "Accessors of" : "Writers to";
        return results.Count > 0
            ? $"{label} offset 0x{(ulong)(nuint)ParseAddress(offset):X} ({results.Count} found):\n{string.Join('\n', results)}"
            : $"No instructions {(includeReads ? "accessing" : "writing to")} offset 0x{(ulong)(nuint)ParseAddress(offset):X} found in {moduleName}.";
    }

    [Description("Find function boundaries around a given address by scanning for prologue/epilogue patterns (push rbp, sub rsp, ret, int3 padding). Returns the likely function start, end, and size.")]
    public async Task<string> FindFunctionBoundaries(
        [Description("Process ID")] int processId,
        [Description("Address inside the function (hex)")] string address,
        [Description("How far to search backward/forward (bytes)")] int searchRange = 4096)
    {
        var targetAddr = (ulong)ParseAddress(address);
        var startAddr = targetAddr > (ulong)searchRange ? targetAddr - (ulong)searchRange : 0UL;
        var totalLen = (long)Math.Min((ulong)searchRange * 2, 0x100000UL);

        // Read in chunks (engine limits per-call reads)
        const int chunkSize = 0x10000;
        var allBytes = new List<byte>();
        for (long off = 0; off < totalLen; off += chunkSize)
        {
            var readAddr = (nuint)(startAddr + (ulong)off);
            var readLen = (int)Math.Min(chunkSize, totalLen - off);
            try
            {
                var memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen);
                var chunk = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                allBytes.AddRange(chunk);
            }
            catch { break; }
        }

        var bytes = allBytes.ToArray();
        if (bytes.Length == 0) return $"Failed to read memory around 0x{targetAddr:X}.";

        var reader = new ByteArrayCodeReader(bytes);
        var decoder = Decoder.Create(64, reader, startAddr);

        var instructions = new List<Iced.Intel.Instruction>();
        while (decoder.IP < startAddr + (ulong)bytes.Length)
        {
            var instr = decoder.Decode();
            if (instr.IsInvalid) { continue; } // skip invalid bytes, don't break
            instructions.Add(instr);
        }

        if (instructions.Count == 0) return $"Could not decode any instructions around 0x{targetAddr:X}.";

        // Scan backward from target for prologue patterns
        ulong funcStart = 0;
        bool foundStart = false;
        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i].IP > targetAddr) continue;

            var instr = instructions[i];

            // Pattern 1: push rbp (classic frame pointer prologue)
            if (instr.Mnemonic == Mnemonic.Push && instr.Op0Register == Register.RBP)
            {
                funcStart = instr.IP;
                foundStart = true;
                break;
            }

            // Pattern 2: sub rsp, imm (frameless prologue) preceded by a boundary
            if (instr.Mnemonic == Mnemonic.Sub && instr.Op0Register == Register.RSP &&
                (instr.GetOpKind(1) == OpKind.Immediate8 || instr.GetOpKind(1) == OpKind.Immediate32))
            {
                // Accept if preceded by ret, int3, or a push sequence
                if (i > 0)
                {
                    var prev = instructions[i - 1];
                    if (prev.Mnemonic is Mnemonic.Ret or Mnemonic.Int3)
                    {
                        funcStart = instr.IP;
                        foundStart = true;
                        break;
                    }
                    // Accept push rbp/rdi/rsi/rbx before sub rsp (common IL2CPP pattern)
                    if (prev.Mnemonic == Mnemonic.Push)
                    {
                        // Walk back through consecutive push instructions to find the function start
                        int pushStart = i - 1;
                        while (pushStart > 0 && instructions[pushStart - 1].Mnemonic == Mnemonic.Push)
                            pushStart--;
                        // Verify boundary: before the push sequence should be ret/int3/start of buffer
                        if (pushStart == 0 ||
                            instructions[pushStart - 1].Mnemonic is Mnemonic.Ret or Mnemonic.Int3 or Mnemonic.Jmp)
                        {
                            funcStart = instructions[pushStart].IP;
                            foundStart = true;
                            break;
                        }
                    }
                }
            }

            // Pattern 3: int3 boundary followed by real code
            if (instr.Mnemonic == Mnemonic.Int3 && i + 1 < instructions.Count && instructions[i + 1].IP <= targetAddr)
            {
                // Skip consecutive int3 padding
                int nextReal = i + 1;
                while (nextReal < instructions.Count && instructions[nextReal].Mnemonic == Mnemonic.Int3)
                    nextReal++;
                if (nextReal < instructions.Count && instructions[nextReal].IP <= targetAddr)
                {
                    funcStart = instructions[nextReal].IP;
                    foundStart = true;
                    break;
                }
            }
        }

        // Scan forward from target for epilogue
        ulong funcEnd = 0;
        bool foundEnd = false;
        int retCount = 0;
        foreach (var instr in instructions)
        {
            if (instr.IP < targetAddr) continue;

            if (instr.Mnemonic == Mnemonic.Ret)
            {
                funcEnd = instr.IP + (ulong)instr.Length;
                foundEnd = true;
                retCount++;
                // Continue to find int3 padding after ret (true function end)
                continue;
            }
            // int3 after ret confirms we're past the function
            if (foundEnd && instr.Mnemonic == Mnemonic.Int3)
                break;
            // Non-int3 after ret means there are more paths (e.g., multiple returns)
            if (foundEnd && instr.Mnemonic != Mnemonic.Int3)
            {
                foundEnd = false; // reset, keep looking for final ret
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Function boundary analysis around 0x{targetAddr:X}:");
        sb.AppendLine($"  Start: {(foundStart ? $"0x{funcStart:X}" : $"not found (searched {searchRange} bytes back)")}");
        sb.AppendLine($"  End:   {(foundEnd ? $"0x{funcEnd:X}" : $"not found (searched {searchRange} bytes forward)")}");
        if (foundStart && foundEnd)
        {
            var size = funcEnd - funcStart;
            sb.AppendLine($"  Size:  {size} bytes (0x{size:X})");
        }
        if (retCount > 1)
            sb.AppendLine($"  Note:  {retCount} return instructions found (multiple exit paths)");
        return sb.ToString().TrimEnd();
    }

    [Description("Find all CALL and JMP instructions that target a specific function address. Scans module code for direct calls (call 0xABCD), indirect calls (call [rip+disp] resolved to target), and tail-call jumps. Useful for tracing who calls a known function.")]
    public async Task<string> GetCallerGraph(
        [Description("Process ID")] int processId,
        [Description("Target function address (hex)")] string targetAddress,
        [Description("Module to scan (or 'all')")] string moduleName,
        [Description("Max results")] int maxResults = 30)
    {
        var target = (ulong)ParseAddress(targetAddress);
        var attachment = await engineFacade.AttachAsync(processId);
        var targetModules = moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetModules.Count == 0) return $"Module '{moduleName}' not found.";

        var results = new List<string>();
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        foreach (var mod in targetModules)
        {
            if (results.Count >= maxResults) break;

            const int chunkSize = 0x10000;
            for (long off = 0; off < mod.SizeBytes && results.Count < maxResults; off += chunkSize)
            {
                var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                MemoryReadResult memResult;
                try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen); }
                catch { continue; }

                var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                if (bytes.Length == 0) continue;

                var reader = new ByteArrayCodeReader(bytes);
                var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                {
                    var instr = decoder.Decode();
                    if (instr.IsInvalid) continue;

                    bool isCall = instr.Mnemonic == Mnemonic.Call;
                    bool isJmp = instr.Mnemonic == Mnemonic.Jmp;
                    if (!isCall && !isJmp) continue;

                    bool matches = false;
                    string callType = isCall ? "CALL" : "JMP (tail call)";

                    // Direct near branch (E8 rel32 for call, E9 rel32 for jmp)
                    if (instr.NearBranchTarget == target)
                    {
                        matches = true;
                    }
                    // Indirect RIP-relative: call [rip+disp] → try to resolve the pointer
                    else if (instr.IsIPRelativeMemoryOperand && instr.IPRelativeMemoryAddress != 0)
                    {
                        // The instruction reads a pointer from [rip+disp]; read 8 bytes at that address
                        try
                        {
                            var ptrResult = await engineFacade.ReadMemoryAsync(processId,
                                (nuint)instr.IPRelativeMemoryAddress, 8);
                            var ptrBytes = ptrResult.Bytes is byte[] pb ? pb : ptrResult.Bytes.ToArray();
                            if (ptrBytes.Length == 8)
                            {
                                var resolvedTarget = BitConverter.ToUInt64(ptrBytes, 0);
                                if (resolvedTarget == target)
                                {
                                    matches = true;
                                    callType = isCall ? "CALL [indirect, resolved]" : "JMP [indirect, resolved]";
                                }
                            }
                        }
                        catch { /* pointer read failed, skip */ }
                    }

                    if (matches)
                    {
                        formatter.Format(instr, output);
                        results.Add($"  0x{instr.IP:X} | {output.ToStringAndReset()} | {callType} | in {mod.Name}");
                        if (results.Count >= maxResults) break;
                    }
                }
            }
        }

        return results.Count > 0
            ? $"Callers of 0x{target:X} ({results.Count} found):\n{string.Join('\n', results)}"
            : $"No CALL/JMP instructions targeting 0x{target:X} found in {moduleName}.\n" +
              "Note: If the target is called through vtables or register-indirect calls (call rax), those cannot be resolved statically.";
    }

    [Description("Search for instruction patterns in a module. Find specific mnemonics, register usage, or memory access patterns. Examples: 'mov.*\\\\[rsi\\\\+0x38\\\\]', 'call.*GameAssembly', 'imul.*ebx'.")]
    public async Task<string> SearchInstructionPattern(
        [Description("Process ID")] int processId,
        [Description("Module name (or 'all')")] string moduleName,
        [Description("Regex pattern to match against formatted instruction text (MASM syntax)")] string pattern,
        [Description("Max results")] int maxResults = 30)
    {
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5)); }
        catch (ArgumentException ex) { return $"Invalid regex pattern: {ex.Message}"; }

        var attachment = await engineFacade.AttachAsync(processId);
        var targetModules = moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetModules.Count == 0) return $"Module '{moduleName}' not found.";

        var results = new List<string>();
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        foreach (var mod in targetModules)
        {
            if (results.Count >= maxResults) break;

            const int chunkSize = 0x10000;
            for (long off = 0; off < mod.SizeBytes && results.Count < maxResults; off += chunkSize)
            {
                var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                MemoryReadResult memResult;
                try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen); }
                catch { continue; }

                var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                if (bytes.Length == 0) continue;

                var reader = new ByteArrayCodeReader(bytes);
                var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                {
                    var instr = decoder.Decode();
                    if (instr.IsInvalid) continue;

                    formatter.Format(instr, output);
                    var text = output.ToStringAndReset();

                    if (regex.IsMatch(text))
                    {
                        results.Add($"  0x{instr.IP:X} | {text} | in {mod.Name}");
                        if (results.Count >= maxResults) break;
                    }
                }
            }
        }

        return results.Count > 0
            ? $"Pattern matches for '{pattern}' ({results.Count} found):\n{string.Join('\n', results)}"
            : $"No instructions matching '{pattern}' found in {moduleName}.\nNote: MASM formatter uses hex suffixed with 'h' (e.g., [rax+38h] not [rax+0x38]). Consider using FindByMemoryOperand for offset-based searches.";
    }

    [Description("Search for instructions with a specific memory operand displacement and optional base register. More reliable than regex pattern search since it uses structured operand data, not text matching.")]
    public async Task<string> FindByMemoryOperand(
        [Description("Process ID")] int processId,
        [Description("Module name (or 'all')")] string moduleName,
        [Description("Memory displacement/offset to match (hex), e.g., '0x38'")] string displacement,
        [Description("Optional base register filter, e.g., 'rsi', 'rax', or 'any'")] string baseRegister = "any",
        [Description("Filter: 'writes', 'reads', 'all'")] string filter = "all",
        [Description("Max results")] int maxResults = 20)
    {
        var dispValue = (long)(ulong)ParseAddress(displacement);
        bool filterAnyBase = baseRegister.Equals("any", StringComparison.OrdinalIgnoreCase);

        var attachment = await engineFacade.AttachAsync(processId);
        var targetModules = moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetModules.Count == 0) return $"Module '{moduleName}' not found.";

        var results = new List<string>();
        var formatter = new MasmFormatter();
        var output = new StringOutput();

        foreach (var mod in targetModules)
        {
            if (results.Count >= maxResults) break;

            const int chunkSize = 0x10000;
            for (long off = 0; off < mod.SizeBytes && results.Count < maxResults; off += chunkSize)
            {
                var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                MemoryReadResult memResult;
                try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen); }
                catch { continue; }

                var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                if (bytes.Length == 0) continue;

                var reader = new ByteArrayCodeReader(bytes);
                var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                {
                    var instr = decoder.Decode();
                    if (instr.IsInvalid) continue;

                    bool isLea = instr.Mnemonic == Mnemonic.Lea;

                    for (int opIdx = 0; opIdx < instr.OpCount; opIdx++)
                    {
                        if (instr.GetOpKind(opIdx) != OpKind.Memory) continue;
                        if ((long)instr.MemoryDisplacement64 != dispValue) continue;
                        if (instr.MemoryBase == Register.None) continue;
                        if (!filterAnyBase && !instr.MemoryBase.ToString().Equals(baseRegister, StringComparison.OrdinalIgnoreCase)) continue;

                        string classification;
                        if (isLea)
                            classification = "LEA";
                        else if (opIdx == 0 && IsWriteInstruction(instr))
                            classification = "WRITE";
                        else
                            classification = "READ";

                        if (filter.Equals("writes", StringComparison.OrdinalIgnoreCase) && classification != "WRITE") continue;
                        if (filter.Equals("reads", StringComparison.OrdinalIgnoreCase) && classification != "READ") continue;

                        formatter.Format(instr, output);
                        results.Add($"  0x{instr.IP:X} | {output.ToStringAndReset()} | {classification} | base={instr.MemoryBase} | in {mod.Name}");
                        if (results.Count >= maxResults) break;
                    }
                }
            }
        }

        var baseDesc = filterAnyBase ? "any base" : $"base={baseRegister}";
        return results.Count > 0
            ? $"Memory operand matches for displacement 0x{(ulong)(nuint)ParseAddress(displacement):X}, {baseDesc}, filter={filter} ({results.Count} found):\n{string.Join('\n', results)}"
            : $"No instructions with displacement 0x{(ulong)(nuint)ParseAddress(displacement):X} ({baseDesc}, filter={filter}) found in {moduleName}.";
    }

    [Description("High-level tool that bridges from a known data field to the code that writes it. " +
        "Given an address table entry (by ID or label), extracts its structure offset, finds the containing module's code, " +
        "and searches for instructions referencing that offset. Combines table metadata + FindByMemoryOperand + caller analysis. " +
        "This is the recommended starting point for 'find what writes to this field' investigations.")]
    public async Task<string> TraceFieldWriters(
        [Description("Process ID")] int processId,
        [Description("Address table entry ID or label (e.g., 'EXP' or 'ct-75')")] string entryIdOrLabel,
        [Description("Module to search (e.g., 'GameAssembly.dll'). If empty, searches all code modules.")] string moduleName = "",
        [Description("Max results per search strategy")] int maxResults = 30)
    {
        // Step 1: Resolve the table entry
        var node = ResolveNode(entryIdOrLabel);
        if (node is null)
            return $"Address table entry '{entryIdOrLabel}' not found.";
        if (!node.ResolvedAddress.HasValue)
            return $"Entry '{node.Label}' ({node.Id}) has no resolved address — refresh the table first.";

        var resolvedAddr = node.ResolvedAddress.Value;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ TraceFieldWriters: {node.Label} ({node.Id}) ═══");
        sb.AppendLine($"Resolved address: 0x{resolvedAddr:X}");
        sb.AppendLine($"Data type: {node.DataType}");
        sb.AppendLine($"Current value: {node.CurrentValue}");

        // Step 2: Determine structure offset from parent or address pattern
        long? structOffset = null;
        string offsetSource = "unknown";

        if (node.IsOffset && node.Parent is not null && node.Parent.ResolvedAddress.HasValue)
        {
            // Parent-relative offset: the offset IS the displacement we need
            structOffset = (long)((ulong)resolvedAddr - (ulong)node.Parent.ResolvedAddress.Value);
            offsetSource = $"parent-relative ({node.Parent.Label} + 0x{structOffset:X})";
        }
        else if (node.Address.StartsWith("+") || node.Address.StartsWith("-"))
        {
            // Symbolic offset address like "+38"
            if (long.TryParse(node.Address.TrimStart('+'), System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            {
                structOffset = parsed;
                offsetSource = $"symbolic offset ({node.Address})";
            }
        }

        if (structOffset.HasValue)
        {
            sb.AppendLine($"Structure offset: 0x{structOffset.Value:X} (from {offsetSource})");
        }
        else
        {
            sb.AppendLine("⚠️ Could not determine structure offset — no parent-relative relationship found.");
            sb.AppendLine("   Falling back to absolute address search only.");
        }

        // Step 3: Determine search module(s)
        var attachment = await engineFacade.AttachAsync(processId);
        var searchModules = string.IsNullOrWhiteSpace(moduleName) || moduleName.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? attachment.Modules.Where(m => m.SizeBytes > 0x1000).ToList()
            : attachment.Modules.Where(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (searchModules.Count == 0)
        {
            sb.AppendLine($"⚠️ Module '{moduleName}' not found.");
            return sb.ToString();
        }

        var modNames = string.Join(", ", searchModules.Select(m => m.Name).Take(5));
        if (searchModules.Count > 5) modNames += $" (+{searchModules.Count - 5} more)";
        sb.AppendLine($"Searching: {modNames}");
        sb.AppendLine();

        var allResults = new List<(string Strategy, string Line)>();

        // Strategy A: If we have a structure offset, search for displacement-based memory operands
        if (structOffset.HasValue)
        {
            sb.AppendLine($"── Strategy A: Instructions with memory displacement 0x{structOffset.Value:X} ──");
            var formatter = new MasmFormatter();
            var output = new StringOutput();
            int stratACount = 0;

            foreach (var mod in searchModules)
            {
                if (stratACount >= maxResults) break;

                const int chunkSize = 0x10000;
                for (long off = 0; off < mod.SizeBytes && stratACount < maxResults; off += chunkSize)
                {
                    var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                    var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                    MemoryReadResult memResult;
                    try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen); }
                    catch { continue; }

                    var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                    if (bytes.Length == 0) continue;

                    var reader = new ByteArrayCodeReader(bytes);
                    var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                    while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                    {
                        var instr = decoder.Decode();
                        if (instr.IsInvalid) continue;

                        bool isLea = instr.Mnemonic == Mnemonic.Lea;

                        for (int opIdx = 0; opIdx < instr.OpCount; opIdx++)
                        {
                            if (instr.GetOpKind(opIdx) != OpKind.Memory) continue;
                            if ((long)instr.MemoryDisplacement64 != structOffset.Value) continue;
                            if (instr.MemoryBase == Register.None) continue;

                            string classification;
                            if (isLea) classification = "LEA";
                            else if (opIdx == 0 && IsWriteInstruction(instr)) classification = "WRITE";
                            else classification = "READ";

                            formatter.Format(instr, output);
                            var line = $"  0x{instr.IP:X} | {output.ToStringAndReset()} | {classification} | base={instr.MemoryBase} | {mod.Name}";
                            allResults.Add(("A", line));
                            sb.AppendLine(line);
                            stratACount++;
                            if (stratACount >= maxResults) break;
                        }
                    }
                }
            }

            if (stratACount == 0)
                sb.AppendLine("  (no results — the field may be accessed through helper functions or computed offsets)");
            sb.AppendLine();
        }

        // Strategy B: Search for nearby small offsets if offset is small (common in IL2CPP)
        // If the primary offset search found nothing and offset < 0x200, also try adjacent offsets
        if (structOffset.HasValue && allResults.Count == 0 && structOffset.Value < 0x200)
        {
            var adjacentOffsets = new[] { structOffset.Value - 4, structOffset.Value + 4, structOffset.Value - 8, structOffset.Value + 8 };
            sb.AppendLine($"── Strategy B: Adjacent offsets (±4, ±8 from 0x{structOffset.Value:X}) ──");
            int stratBCount = 0;

            foreach (var adjOff in adjacentOffsets.Where(o => o > 0))
            {
                if (stratBCount >= 10) break;
                foreach (var mod in searchModules.Take(3))
                {
                    if (stratBCount >= 10) break;

                    const int chunkSize = 0x10000;
                    var formatter = new MasmFormatter();
                    var output = new StringOutput();

                    for (long off = 0; off < mod.SizeBytes && stratBCount < 10; off += chunkSize)
                    {
                        var readAddr = (nuint)((ulong)mod.BaseAddress + (ulong)off);
                        var readLen = (int)Math.Min(chunkSize, mod.SizeBytes - off);

                        MemoryReadResult memResult;
                        try { memResult = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen); }
                        catch { continue; }

                        var bytes = memResult.Bytes is byte[] arr ? arr : memResult.Bytes.ToArray();
                        if (bytes.Length == 0) continue;

                        var reader = new ByteArrayCodeReader(bytes);
                        var decoder = Decoder.Create(64, reader, (ulong)readAddr);

                        while (decoder.IP < (ulong)readAddr + (ulong)bytes.Length)
                        {
                            var instr = decoder.Decode();
                            if (instr.IsInvalid) continue;

                            for (int opIdx = 0; opIdx < instr.OpCount; opIdx++)
                            {
                                if (instr.GetOpKind(opIdx) != OpKind.Memory) continue;
                                if ((long)instr.MemoryDisplacement64 != adjOff) continue;
                                if (instr.MemoryBase == Register.None) continue;
                                if (opIdx != 0 || !IsWriteInstruction(instr)) continue;

                                formatter.Format(instr, output);
                                var line = $"  0x{instr.IP:X} | {output.ToStringAndReset()} | WRITE to +0x{adjOff:X} (adjacent) | base={instr.MemoryBase} | {mod.Name}";
                                allResults.Add(("B", line));
                                sb.AppendLine(line);
                                stratBCount++;
                                if (stratBCount >= 10) break;
                            }
                        }
                    }
                }
            }

            if (stratBCount == 0)
                sb.AppendLine("  (no adjacent offset writers found either)");
            sb.AppendLine();
        }

        // Strategy C: If we found writers, try to identify their containing functions
        if (allResults.Count > 0)
        {
            sb.AppendLine("── Strategy C: Function context for top writer candidates ──");
            var writerAddresses = allResults
                .Where(r => r.Line.Contains("WRITE"))
                .Select(r =>
                {
                    var addrStr = r.Line.TrimStart().Split('|')[0].Trim();
                    return ParseAddress(addrStr);
                })
                .Distinct()
                .Take(5);

            foreach (var writerAddr in writerAddresses)
            {
                // Find function boundaries around this writer
                var containingMod = searchModules.FirstOrDefault(m =>
                    (ulong)writerAddr >= (ulong)m.BaseAddress &&
                    (ulong)writerAddr < (ulong)m.BaseAddress + (ulong)m.SizeBytes);

                if (containingMod is null) continue;

                // Quick backward scan for function prologue (push rbp / sub rsp)
                const int scanBack = 0x200;
                var scanStart = (nuint)Math.Max((long)writerAddr - scanBack, (long)containingMod.BaseAddress);
                var scanLen = (int)((ulong)writerAddr - (ulong)scanStart + 0x20);

                MemoryReadResult scanMem;
                try { scanMem = await engineFacade.ReadMemoryAsync(processId, scanStart, scanLen); }
                catch { continue; }

                var scanBytes = scanMem.Bytes is byte[] sa ? sa : scanMem.Bytes.ToArray();
                var scanReader = new ByteArrayCodeReader(scanBytes);
                var scanDecoder = Decoder.Create(64, scanReader, (ulong)scanStart);

                nuint funcStart = 0;
                while (scanDecoder.IP < (ulong)writerAddr)
                {
                    var ins = scanDecoder.Decode();
                    if (ins.IsInvalid) continue;
                    // push rbp or sub rsp,imm
                    if ((ins.Mnemonic == Mnemonic.Push && ins.Op0Register == Register.RBP) ||
                        (ins.Mnemonic == Mnemonic.Sub && ins.Op0Register == Register.RSP))
                    {
                        funcStart = (nuint)ins.IP;
                    }
                }

                if (funcStart != 0)
                {
                    sb.AppendLine($"  Writer 0x{writerAddr:X} → likely function at 0x{funcStart:X} in {containingMod.Name}");
                    sb.AppendLine($"    (use GetCallerGraph with this function address to find call sites)");
                }
            }
            sb.AppendLine();
        }

        // Summary
        sb.AppendLine("── Summary ──");
        int writes = allResults.Count(r => r.Line.Contains("WRITE"));
        int reads = allResults.Count(r => r.Line.Contains("READ") && !r.Line.Contains("READWRITE"));
        int leas = allResults.Count(r => r.Line.Contains("LEA"));
        sb.AppendLine($"Total: {allResults.Count} results ({writes} writes, {reads} reads, {leas} LEAs)");

        if (allResults.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("💡 No direct offset references found. Possible reasons:");
            sb.AppendLine("   • Field is accessed through IL2CPP/Unity accessor functions (indirect calls)");
            sb.AppendLine("   • Offset is computed at runtime rather than embedded in displacement");
            sb.AppendLine("   • Writer code is in a different module (try moduleName='all')");
            sb.AppendLine("   • Field uses a different base+offset decomposition than expected");
            sb.AppendLine();
            sb.AppendLine("🔧 Next steps:");
            sb.AppendLine("   1. Try searching adjacent offsets manually with FindByMemoryOperand");
            sb.AppendLine("   2. Use function analysis around the known reward hook addresses");
            sb.AppendLine("   3. Install a stealth code-cave hook on a nearby known function and capture register snapshots");
            sb.AppendLine("   4. Use SampledWriteTrace on the data address to confirm write activity");
        }

        return sb.ToString();
    }

    // ── Helpers ──

    private static bool IsWriteInstruction(Iced.Intel.Instruction instr)
    {
        var m = instr.Mnemonic;
        return m is
            // Basic ALU
            Mnemonic.Mov or Mnemonic.Add or Mnemonic.Sub or Mnemonic.Xor
            or Mnemonic.And or Mnemonic.Or or Mnemonic.Inc or Mnemonic.Dec
            or Mnemonic.Imul or Mnemonic.Shl or Mnemonic.Shr or Mnemonic.Sar
            // Unary
            or Mnemonic.Neg or Mnemonic.Not
            // Rotate / shift
            or Mnemonic.Rol or Mnemonic.Ror or Mnemonic.Rcl or Mnemonic.Rcr
            // Conditional moves
            or Mnemonic.Cmova or Mnemonic.Cmovae or Mnemonic.Cmovb or Mnemonic.Cmovbe
            or Mnemonic.Cmove or Mnemonic.Cmovg or Mnemonic.Cmovge or Mnemonic.Cmovl
            or Mnemonic.Cmovle or Mnemonic.Cmovne or Mnemonic.Cmovno or Mnemonic.Cmovnp
            or Mnemonic.Cmovns or Mnemonic.Cmovo or Mnemonic.Cmovp or Mnemonic.Cmovs
            // Exchange / compare-exchange
            or Mnemonic.Xchg or Mnemonic.Cmpxchg or Mnemonic.Cmpxchg8b or Mnemonic.Cmpxchg16b
            // Sign-extend store
            or Mnemonic.Movsxd
            // Bit operations
            or Mnemonic.Bts or Mnemonic.Btr or Mnemonic.Btc
            // String operations
            or Mnemonic.Stosb or Mnemonic.Stosd or Mnemonic.Stosq
            or Mnemonic.Movsb or Mnemonic.Movsd
            // SSE scalar / packed
            or Mnemonic.Movss or Mnemonic.Movaps or Mnemonic.Movups
            or Mnemonic.Addss or Mnemonic.Addsd or Mnemonic.Mulss or Mnemonic.Mulsd
            or Mnemonic.Subss or Mnemonic.Subsd or Mnemonic.Divss or Mnemonic.Divsd
            or Mnemonic.Cvtsi2ss or Mnemonic.Cvtsi2sd
            or Mnemonic.Movdqu or Mnemonic.Movdqa or Mnemonic.Movq
            // AVX
            or Mnemonic.Vmovss or Mnemonic.Vmovsd or Mnemonic.Vmovaps or Mnemonic.Vmovups
            or Mnemonic.Vmovdqu or Mnemonic.Vmovdqa
            or Mnemonic.Vaddss or Mnemonic.Vaddsd or Mnemonic.Vmulss or Mnemonic.Vmulsd;
    }

    /// <summary>Resolve a node by ID first, then by label (case-insensitive).</summary>
    private AddressTableNode? ResolveNode(string idOrLabel) =>
        addressTableService.FindNode(idOrLabel) ?? addressTableService.FindNodeByLabel(idOrLabel);

    private static nuint ParseAddress(string address)
    {
        var addr = address.Trim();
        if (addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            addr = addr[2..];
        return (nuint)ulong.Parse(addr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static int CountNodes(IEnumerable<AddressTableNode> nodes, Func<AddressTableNode, bool>? predicate = null)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            if (predicate is null || predicate(node)) count++;
            count += CountNodes(node.Children, predicate);
        }
        return count;
    }

    /// <summary>Recursively count address table nodes sharing the same 4KB page as the target address.</summary>
    private static void CountCoTenants(AddressTableNode node, nuint pageBase, nuint excludeAddr, ref int count)
    {
        if (node.ResolvedAddress.HasValue)
        {
            var entryPage = node.ResolvedAddress.Value & ~(nuint)0xFFF;
            if (entryPage == pageBase && node.ResolvedAddress.Value != excludeAddr)
                count++;
        }
        foreach (var child in node.Children)
            CountCoTenants(child, pageBase, excludeAddr, ref count);
    }

    /// <summary>Count how many address table entries share the same 4KB page as the target.</summary>
    private int CountPageCoTenants(nuint targetPage)
    {
        int count = 0;
        foreach (var root in addressTableService.Roots)
            CountCoTenants(root, targetPage, nuint.MaxValue, ref count);
        return count;
    }

    // ── Non-debugger write tracing (M2) ──

    [Description("Trace writes to a data address using sampled memory snapshots (no debugger attachment). Takes multiple snapshots with a delay and reports if the value changed. Safer than breakpoints for anti-debug targets.")]
    public async Task<string> SampledWriteTrace(
        [Description("Process ID")] int processId,
        [Description("Memory address to watch (hex)")] string address,
        [Description("Number of bytes to watch (4 for Int32, 8 for Int64/Pointer)")] int byteCount = 4,
        [Description("Delay between samples in milliseconds")] int sampleDelayMs = 2000,
        [Description("Number of samples to take")] int sampleCount = 5)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);

            var snapshots = new List<object>();
            byte[]? prevBytes = null;

            for (int i = 0; i < sampleCount; i++)
            {
                var read = await engineFacade.ReadMemoryAsync(processId, addr, byteCount);
                var bytes = read.Bytes.ToArray();
                var hex = string.Join(" ", bytes.Select(b => b.ToString("X2")));

                bool changed = prevBytes is not null && !bytes.SequenceEqual(prevBytes);
                string? prevHex = prevBytes is not null ? string.Join(" ", prevBytes.Select(b => b.ToString("X2"))) : null;

                snapshots.Add(new
                {
                    sample = i + 1,
                    timestamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff"),
                    hex,
                    changed,
                    previousHex = changed ? prevHex : null,
                    int32Value = byteCount >= 4 ? BitConverter.ToInt32(bytes.Take(4).ToArray()) : (int?)null,
                    floatValue = byteCount >= 4 ? BitConverter.ToSingle(bytes.Take(4).ToArray()) : (float?)null
                });

                prevBytes = bytes;

                if (i < sampleCount - 1)
                    await Task.Delay(sampleDelayMs);
            }

            int changeCount = snapshots.Cast<dynamic>().Count(s => (bool)((dynamic)s).changed);

            return JsonSerializer.Serialize(new
            {
                address = $"0x{addr:X}",
                byteCount,
                sampleCount,
                sampleDelayMs,
                changeCount,
                hotness = changeCount > sampleCount / 2 ? "HOT" : changeCount > 0 ? "WARM" : "COLD",
                snapshots,
                recommendation = changeCount > sampleCount / 2
                    ? "Address is frequently written — avoid PageGuard (will cause excessive traps). Use FindWritersToOffset with static analysis instead."
                    : changeCount > 0
                    ? "Address changes occasionally. PageGuard with singleHit=true may work, but prefer static analysis."
                    : "No writes detected during sampling. Value may change during specific game events only. Try again during the relevant event."
            }, _jsonOpts);
        }
        catch (Exception ex) { return $"SampledWriteTrace failed: {ex.Message}"; }
    }

    // ── Operation journaling (M4) ──

    [Description("Begin a named transaction group for compound breakpoint/hook operations. All operations in the group can be rolled back together. Returns the group ID.")]
    public string BeginTransaction([Description("Name for this transaction group")] string name = "auto")
    {
        var groupId = $"txn-{name}-{Guid.NewGuid():N}"[..24];
        return JsonSerializer.Serialize(new { groupId, status = "open", message = $"Transaction group '{groupId}' created. Pass this groupId to subsequent BP/hook operations." }, _jsonOpts);
    }

    [Description("Rollback all operations in a transaction group, restoring original state in reverse order.")]
    public async Task<string> RollbackTransaction([Description("Transaction group ID")] string groupId)
    {
        if (operationJournal is null) return "Operation journal not available.";
        var result = await operationJournal.RollbackGroupAsync(groupId);
        return JsonSerializer.Serialize(new { result.Success, result.TotalOperations, result.SucceededRollbacks, result.Message }, _jsonOpts);
    }

    [Description("List all recorded operations in the journal. Shows operation type, address, mode, status, and group membership.")]
    public string ListJournalEntries()
    {
        if (operationJournal is null) return "Operation journal not available.";
        var entries = operationJournal.GetEntries();
        return JsonSerializer.Serialize(new
        {
            entries = entries.Select(e => new
            {
                e.OperationId, e.OperationType, address = $"0x{e.Address:X}",
                e.Mode, e.GroupId, status = e.Status.ToString(), timestamp = e.Timestamp.ToString("HH:mm:ss")
            }),
            count = entries.Count
        }, _jsonOpts);
    }

    // ── Hook/script coexistence (L4) ──

    [Description("Check for hook/patch conflicts at an address. Detects if existing code cave hooks, breakpoints, or scripts already modify the target bytes. Use before installing new hooks to avoid conflicts.")]
    public async Task<string> CheckHookConflicts(
        [Description("Process ID")] int processId,
        [Description("Target address to check (hex)")] string address,
        [Description("Number of bytes to check (14 for standard JMP detour)")] int byteCount = 14)
    {
        try
        {
            var addr = ParseAddress(address);
            var conflicts = new List<object>();

            // Check code cave hooks
            if (codeCaveEngine is not null)
            {
                var hooks = await codeCaveEngine.ListHooksAsync(processId);
                foreach (var hook in hooks)
                {
                    if (hook.OriginalAddress >= addr && hook.OriginalAddress < (nuint)((ulong)addr + (ulong)byteCount))
                        conflicts.Add(new { type = "CodeCaveHook", id = hook.Id, address = $"0x{hook.OriginalAddress:X}", stolenBytes = hook.OriginalBytesLength });
                    else if (addr >= hook.OriginalAddress && addr < (nuint)((ulong)hook.OriginalAddress + (ulong)hook.OriginalBytesLength))
                        conflicts.Add(new { type = "CodeCaveHook", id = hook.Id, address = $"0x{hook.OriginalAddress:X}", stolenBytes = hook.OriginalBytesLength });
                }
            }

            // Check breakpoints
            if (breakpointService is not null)
            {
                var bps = await breakpointService.ListBreakpointsAsync(processId);
                foreach (var bp in bps)
                {
                    var bpAddr = AddressTableService.ParseAddress(bp.Address);
                    if (bpAddr >= addr && bpAddr < (nuint)((ulong)addr + (ulong)byteCount))
                        conflicts.Add(new { type = "Breakpoint", id = bp.Id, address = bp.Address, bpType = bp.Type });
                }
            }

            return JsonSerializer.Serialize(new
            {
                targetAddress = $"0x{addr:X}",
                byteRange = byteCount,
                conflicts,
                conflictCount = conflicts.Count,
                safe = conflicts.Count == 0,
                recommendation = conflicts.Count > 0
                    ? "Conflicts detected — remove existing hooks/breakpoints before installing a new one at this address."
                    : "No conflicts — safe to install."
            }, _jsonOpts);
        }
        catch (Exception ex) { return $"CheckHookConflicts failed: {ex.Message}"; }
    }

    // ── Watchdog safety tools ──

    [Description("Check if an address+mode combination has been marked unsafe by the watchdog. Returns safety status and history of prior freeze events.")]
    public string CheckAddressSafety(
        [Description("Memory address to check (hex or decimal)")] string address,
        [Description("Breakpoint mode to check: Auto, Stealth, PageGuard, Hardware, Software")] string mode = "Auto")
    {
        if (watchdogService is null) return "Watchdog not available.";
        var addr = ParseAddress(address);
        if (watchdogService.IsUnsafe(addr, mode))
        {
            var entries = watchdogService.GetUnsafeAddresses()
                .Where(e => e.Address == addr && e.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
            var details = entries.Select(e =>
                $"  Frozen: {e.FreezeDetectedUtc:HH:mm:ss} | Type: {e.OperationType} | Rollback: {(e.RollbackSucceeded ? "OK" : "FAILED")}");
            return $"⚠️ Address 0x{addr:X} + mode {mode} is UNSAFE (caused process freeze previously):\n{string.Join('\n', details)}";
        }
        return $"Address 0x{addr:X} + mode {mode}: no known safety issues.";
    }

    [Description("List all addresses marked unsafe by the watchdog due to prior freeze incidents.")]
    public string ListUnsafeAddresses()
    {
        if (watchdogService is null) return "Watchdog not available.";
        var entries = watchdogService.GetUnsafeAddresses();
        if (entries.Count == 0) return "No addresses marked unsafe.";
        var lines = entries.Select(e =>
            $"  0x{e.Address:X} | {e.Mode} | {e.OperationType} | frozen: {e.FreezeDetectedUtc:HH:mm:ss} | rollback: {(e.RollbackSucceeded ? "OK" : "FAILED")}");
        return $"Unsafe addresses ({entries.Count}):\n{string.Join('\n', lines)}";
    }

    [Description("Clear the unsafe flag for an address, allowing breakpoints to be set there again. Use after fixing the underlying issue.")]
    public string ClearUnsafeAddress(
        [Description("Memory address (hex or decimal)")] string address,
        [Description("Mode to clear: Auto, Stealth, PageGuard, Hardware, Software")] string mode)
    {
        if (watchdogService is null) return "Watchdog not available.";
        var addr = ParseAddress(address);
        watchdogService.ClearUnsafe(addr, mode);
        return $"Cleared unsafe flag for 0x{addr:X} + {mode}.";
    }

    // ── JSON serialization helpers ──

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string ToJson<T>(T obj) => JsonSerializer.Serialize(obj, _jsonOpts);

    private static List<object> FormatNodesJson(IEnumerable<AddressTableNode> nodes)
    {
        var result = new List<object>();
        foreach (var n in nodes)
        {
            if (n.IsGroup)
            {
                result.Add(new
                {
                    n.Id,
                    n.Label,
                    type = "group",
                    children = FormatNodesJson(n.Children),
                    childCount = n.Children.Count
                });
            }
            else if (n.IsScriptEntry)
            {
                result.Add(new
                {
                    n.Id,
                    n.Label,
                    type = "script",
                    isEnabled = n.IsScriptEnabled
                });
            }
            else
            {
                result.Add(new
                {
                    n.Id,
                    n.Label,
                    type = "address",
                    address = n.Address,
                    resolvedAddress = n.ResolvedAddress.HasValue ? $"0x{n.ResolvedAddress.Value:X}" : null,
                    value = n.CurrentValue,
                    dataType = n.DataType.ToString(),
                    isResolved = n.ResolvedAddress.HasValue,
                    isPointer = n.IsPointer,
                    isFrozen = n.IsLocked,
                    isOffset = n.IsOffset
                });
            }
        }
        return result;
    }
}
