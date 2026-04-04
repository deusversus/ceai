using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application;

/// <summary>
/// Exposes engine capabilities as AI-callable tools via Microsoft.Extensions.AI function calling.
/// Uses application-level services so the AI operates through the same layer as the UI.
/// </summary>
public sealed partial class AiToolFunctions(
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
    OperationJournal? operationJournal = null,
    AiChatStore? chatStore = null,
    Func<IReadOnlyList<AiChatMessage>>? currentChatProvider = null,
    TokenLimits? tokenLimits = null,
    ToolResultStore? toolResultStore = null)
{
    private readonly TokenLimits _limits = tokenLimits ?? TokenLimits.Balanced;

    /// <summary>Store for large tool results that exceeded the context budget.</summary>
    internal ToolResultStore ToolResultStore { get; } = toolResultStore ?? new ToolResultStore();
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
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[IsProcessAlive] PID {processId}: {ex.Message}"); return false; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List running processes. Returns PID, name, architecture.")]
    public async Task<string> ListProcesses()
    {
        var processes = await engineFacade.ListProcessesAsync();
        var cap = _limits.MaxListProcesses;
        var lines = processes.Take(cap).Select(p => $"PID {p.Id} | {p.Name} | {p.Architecture}");
        return $"Found {processes.Count} processes (showing {Math.Min(cap, processes.Count)}):\n{string.Join('\n', lines)}";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Inspect process by PID. Returns modules and architecture.")]
    public async Task<string> InspectProcess([Description("Process ID to inspect")] int processId)
    {
        var inspection = await dashboardService.InspectProcessAsync(processId);
        var cap = _limits.MaxInspectModules;
        var modules = inspection.Modules.Take(cap)
            .Select(m => $"  {m.Name} @ {m.BaseAddress} ({m.Size})");
        var extra = inspection.Modules.Count > cap ? $"\n  ... and {inspection.Modules.Count - cap} more modules" : "";
        return $"Process: {inspection.ProcessName} (PID {inspection.ProcessId})\n" +
               $"Modules ({inspection.Modules.Count} total, showing {Math.Min(cap, inspection.Modules.Count)}):\n{string.Join('\n', modules)}{extra}";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Read a typed value from process memory.")]
    public async Task<string> ReadMemory(
        [Description("Process ID")] int processId,
        [Description("Address: hex (0x...), decimal, or symbolic (module+offset)")] string address,
        [Description("Data type: Int32, Int64, Float, Double, or Pointer")] string dataType)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var resolvedAddress = await TryResolveToHex(processId, address);
            var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
            var probe = await dashboardService.ReadAddressAsync(processId, resolvedAddress, dt);
            return $"Read {dt} at {probe.Address}: {probe.DisplayValue}";
        }
        catch (Exception ex)
        {
            return $"ReadMemory failed: {ex.Message}";
        }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Write a value to process memory. Records original for undo.")]
    public async Task<string> WriteMemory(
        [Description("Process ID")] int processId,
        [Description("Address: hex, decimal, or symbolic (module+offset)")] string address,
        [Description("Data type: Int32, Int64, Float, Double")] string dataType,
        [Description("Value to write")] string value)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var resolvedAddress = await TryResolveToHex(processId, address);
            var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
            if (patchUndoService is not null)
            {
                var addr = AddressTableService.ParseAddress(resolvedAddress);
                var result = await patchUndoService.WriteWithUndoAsync(processId, addr, dt, value);
                return result.BytesWritten > 0
                    ? $"Wrote '{value}' ({dt}) to 0x{addr:X}. {patchUndoService.UndoCount} patches in undo stack."
                    : $"Write failed at 0x{addr:X}.";
            }
            var message = await dashboardService.WriteAddressAsync(processId, resolvedAddress, dt, value);
            return message;
        }
        catch (Exception ex)
        {
            return $"WriteMemory failed: {ex.Message}";
        }
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Start a new memory scan. Returns result count.")]
    public async Task<string> StartScan(
        [Description("Process ID to scan")] int processId,
        [Description("Data type: Int32, Int64, Float, Double")] string dataType,
        [Description("Scan type: ExactValue, UnknownInitialValue, ArrayOfBytes, BitChanged")] string scanType,
        [Description("Value to search for. ArrayOfBytes: '48 8B ?? ??' with ?? wildcards")] string? value,
        [Description("Scan alignment in bytes (0=auto, 1/2/4/8). Default: 0")] int alignment = 0,
        [Description("Float comparison tolerance. Default: null (exact match)")] float? floatEpsilon = null,
        [Description("Only scan writable regions. Default: true")] bool writableOnly = true,
        [Description("Suspend process during scan for consistency. Default: false")] bool pauseProcess = false)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
            var st = Enum.Parse<ScanType>(scanType, ignoreCase: true);
            scanService.ResetScan();
            var options = new ScanOptions(
                Alignment: alignment,
                FloatEpsilon: floatEpsilon,
                WritableOnly: writableOnly,
                SuspendProcess: pauseProcess);
            var overview = await scanService.StartScanAsync(processId, dt, st, value ?? "", options);
            var topResults = overview.Results.Take(10)
                .Select(r => $"  {r.Address} = {r.CurrentValue}");
            return $"Scan complete: {overview.ResultCount:N0} results found.\n{string.Join('\n', topResults)}";
        }
        catch (Exception ex)
        {
            return $"StartScan failed: {ex.Message}";
        }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Undo the last scan refinement, restoring the previous result set.")]
    public Task<string> UndoScan()
    {
        var overview = scanService.UndoScan();
        if (overview is null)
            return Task.FromResult("No scan to undo. The undo history is empty.");
        return Task.FromResult($"Undo complete: {overview.ResultCount:N0} results restored (scan type: {overview.ScanType}).");
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Refine previous scan with a new constraint.")]
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


    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List address table entries with pagination.")]
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Refresh address table values from process memory.")]
    public async Task<string> RefreshAddressTable([Description("Process ID")] int processId)
    {
        await addressTableService.RefreshAllAsync(processId);
        var entries = addressTableService.Entries;
        var total = entries.Count;

        // Only report entries with non-zero values or that changed
        var changed = entries.Where(e =>
            e.CurrentValue != e.PreviousValue && e.PreviousValue is not null).ToList();
        var nonZero = entries.Where(e =>
            e.CurrentValue is not null && e.CurrentValue != "0" && e.CurrentValue != "0.0").ToList();

        var summary = new
        {
            totalRefreshed = total,
            changedCount = changed.Count,
            nonZeroCount = nonZero.Count,
            changed = changed.Select(e => new { e.Label, e.Address, current = e.CurrentValue, previous = e.PreviousValue }),
        };

        return ToJson(summary);
    }

    // ── Artifact generation tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Generate C# trainer script from locked entries.")]
    public Task<string> GenerateTrainerScript([Description("Process name for the trainer target")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries to generate trainer from. Lock some address table entries first.");
        var script = scriptGenerationService.GenerateTrainerScript(locked, processName);
        return Task.FromResult($"Generated C# trainer script ({locked.Count} entries):\n\n{script}");
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Generate Auto Assembler script from locked entries.")]
    public Task<string> GenerateAutoAssemblerScript([Description("Process name")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries. Lock entries first.");
        var script = scriptGenerationService.GenerateAutoAssemblerScript(locked, processName);
        return Task.FromResult($"Generated AA script ({locked.Count} entries):\n\n{script}");
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Generate Lua script from locked entries.")]
    public Task<string> GenerateLuaScript([Description("Process name")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries. Lock entries first.");
        var script = scriptGenerationService.GenerateLuaScript(locked, processName);
        return Task.FromResult($"Generated Lua script ({locked.Count} entries):\n\n{script}");
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Summarize current investigation state.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Attach to a process by PID for memory operations.")]
    public async Task<string> AttachProcess([Description("Process ID")] int processId)
    {
        var inspection = await dashboardService.InspectProcessAsync(processId);
        return $"Attached to {inspection.ProcessName} (PID {inspection.ProcessId}, {inspection.Architecture}). " +
               $"{inspection.Modules.Count} modules loaded.";
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
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


    // ── Pointer Scanner tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Scan for pointer chains to a target address.")]
    public async Task<string> ScanForPointers(
        [Description("Process ID to scan")] int processId,
        [Description("Target address (hex)")] string targetAddress,
        [Description("Maximum pointer chain depth (1-3, default 2)")] int maxDepth = 2,
        [Description("Comma-separated module names to scan (e.g. 'game.dll,mono.dll'). Empty = all modules.")] string? moduleFilter = null)
    {
        var scanner = new PointerScannerService(engineFacade);
        var addr = AddressTableService.ParseAddress(targetAddress);
        IReadOnlyList<string>? filter = string.IsNullOrWhiteSpace(moduleFilter) ? null
            : moduleFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var paths = await scanner.ScanForPointersAsync(processId, addr, maxDepth, moduleFilter: filter);

        if (paths.Count == 0) return "No pointer paths found to the target address.";

        var lines = paths.Take(50).Select((p, i) => $"{i + 1}. {p.Display}");
        return $"Found {paths.Count} pointer path(s) to 0x{addr:X}:\n{string.Join('\n', lines)}";
    }

    // ── Phase 7E: Pointer Map I/O ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Save the last pointer scan results to a .PTR file for offline analysis.")]
    public async Task<string> SavePointerMap(
        [Description("Process ID (for process name metadata)")] int processId,
        [Description("Target address the scan was for (hex)")] string targetAddress,
        [Description("File path to save the .PTR file to")] string filePath)
    {
        try
        {
            var scanner = new PointerScannerService(engineFacade);
            var addr = AddressTableService.ParseAddress(targetAddress);

            // Re-scan to get fresh paths (the service doesn't persist scan state)
            var paths = await scanner.ScanForPointersAsync(processId, addr, 2);
            if (paths.Count == 0) return "No pointer paths found. Run ScanForPointers first.";

            var map = new PointerMapFile(
                $"process-{processId}", addr, DateTimeOffset.UtcNow, 2, 0x2000, paths);
            await PointerScannerService.SavePointerMapAsync(filePath, map);
            return $"Saved {paths.Count} pointer paths to {filePath}";
        }
        catch (Exception ex) { return $"SavePointerMap failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Load a previously saved .PTR pointer map file.")]
    public async Task<string> LoadPointerMap(
        [Description("File path to the .PTR file")] string filePath)
    {
        try
        {
            var map = await PointerScannerService.LoadPointerMapAsync(filePath);
            var lines = map.Paths.Take(50).Select((p, i) => $"{i + 1}. {p.Display}");
            return $"Loaded {map.Paths.Count} pointer paths from {System.IO.Path.GetFileName(filePath)}\n" +
                   $"Target: 0x{map.OriginalTargetAddress:X} | Scanned: {map.ScanTimestamp:g}\n" +
                   string.Join('\n', lines) +
                   (map.Paths.Count > 50 ? $"\n... ({map.Paths.Count - 50} more)" : "");
        }
        catch (Exception ex) { return $"LoadPointerMap failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Compare two pointer map (.PTR) files to find common paths that survive across restarts.")]
    public async Task<string> ComparePointerMaps(
        [Description("Path to first .PTR file")] string filePathA,
        [Description("Path to second .PTR file")] string filePathB)
    {
        try
        {
            var mapA = await PointerScannerService.LoadPointerMapAsync(filePathA);
            var mapB = await PointerScannerService.LoadPointerMapAsync(filePathB);
            var result = PointerScannerService.CompareMaps(mapA, mapB);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Pointer map comparison: {result.OverlapRatio:P0} overlap");
            sb.AppendLine($"  Common: {result.CommonPaths.Count} | Only in A: {result.OnlyInFirst.Count} | Only in B: {result.OnlyInSecond.Count}");
            if (result.CommonPaths.Count > 0)
            {
                sb.AppendLine("\nCommon paths (most stable):");
                foreach (var p in result.CommonPaths.Take(20))
                    sb.AppendLine($"  {p.Display}");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"ComparePointerMaps failed: {ex.Message}"; }
    }

    // ── Phase 7A: Grouped Scan ──

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Scan for multiple values simultaneously in one pass (e.g. health AND mana). Each group gets a label.")]
    public async Task<string> GroupedScan(
        [Description("Process ID to scan")] int processId,
        [Description("Semicolon-separated groups: 'label:type:scanType:value'. Example: 'Health:Int32:ExactValue:100;Mana:Int32:ExactValue:50'")] string groups)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            var parsedGroups = new List<GroupedScanConstraint>();
            foreach (var groupStr in groups.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = groupStr.Split(':', 4);
                if (parts.Length < 4) return $"Invalid group format: '{groupStr}'. Expected 'label:dataType:scanType:value'.";
                var label = parts[0].Trim();
                var dt = Enum.Parse<MemoryDataType>(parts[1], ignoreCase: true);
                var st = Enum.Parse<ScanType>(parts[2], ignoreCase: true);
                var val = parts[3].Trim();
                parsedGroups.Add(new GroupedScanConstraint(label, new ScanConstraints(dt, st, val)));
            }

            if (parsedGroups.Count == 0) return "No valid groups provided.";

            var results = await scanService.GroupedScanAsync(processId, parsedGroups, new ScanOptions());

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Grouped scan: {results.Results.Count} total results across {parsedGroups.Count} groups");
            foreach (var group in parsedGroups)
            {
                var groupResults = results.Results.Where(r => r.GroupLabel == group.Label).Take(10).ToList();
                sb.AppendLine($"\n[{group.Label}] ({groupResults.Count} shown):");
                foreach (var r in groupResults)
                    sb.AppendLine($"  0x{r.Address:X} = {r.CurrentValue}");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"GroupedScan failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Browse raw memory as hex dump with ASCII.")]
    public async Task<string> BrowseMemory(
        [Description("Process ID")] int processId,
        [Description("Start address (hex, decimal, or symbolic like 'GameAssembly.dll+9A18E8')")] string address,
        [Description("Number of bytes to read")] int length = 128)
    {
        length = Math.Clamp(length, 1, _limits.MaxBrowseMemoryBytes);
        var resolvedAddress = await TryResolveToHex(processId, address);
        var addr = AddressTableService.ParseAddress(resolvedAddress);
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Dissect memory structure, identify field types and values.")]
    public async Task<string> DissectStructure(
        [Description("Process ID")] int processId,
        [Description("Base address (hex, decimal, or symbolic like 'module.dll+offset')")] string address,
        [Description("Region size in bytes (default 256)")] int regionSize = 256,
        [Description("Hint: auto, int32, float, or pointers")] string typeHint = "auto")
    {
        var dissector = new StructureDissectorService(engineFacade);
        var resolvedAddress = await TryResolveToHex(processId, address);
        var addr = AddressTableService.ParseAddress(resolvedAddress);
        var (fields, clustersDetected) = await dissector.DissectAsync(processId, addr, regionSize, typeHint);

        if (fields.Count == 0) return "No identifiable fields found in this region.";

        var cap = _limits.MaxDissectFields;
        var capped = fields.Take(cap).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Structure analysis at 0x{addr:X} ({fields.Count} fields, showing {capped.Count}, hint={typeHint}):");
        if (clustersDetected > 0)
            sb.AppendLine($"  Detected {clustersDetected} integer cluster(s) — consecutive game-stat-like Int32 values.");
        sb.AppendLine("Offset  | Type     | Value                | Confidence");
        sb.AppendLine("--------|----------|----------------------|-----------");
        foreach (var f in capped)
        {
            sb.AppendLine($"+0x{f.Offset:X4} | {f.ProbableType,-8} | {f.DisplayValue,-20} | {f.Confidence:P0}");
        }
        if (fields.Count > cap)
            sb.AppendLine($"... {fields.Count - cap} more fields omitted — reduce regionSize or increase limits.");
        return sb.ToString();
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Register a global hotkey to toggle freeze/script.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove a registered global hotkey by binding ID.")]
    public Task<string> RemoveHotkey([Description("Binding ID to remove")] int bindingId)
    {
        if (hotkeyService is null) return Task.FromResult("Hotkey service not available.");
        return Task.FromResult(hotkeyService.Unregister(bindingId)
            ? $"Hotkey binding {bindingId} removed."
            : $"Binding {bindingId} not found.");
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Undo the last memory write operation, restoring original bytes.")]
    public async Task<string> UndoWrite()
    {
        if (patchUndoService is null) return "Undo service not available.";
        return await patchUndoService.UndoAsync();
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Redo the last undone memory write operation.")]
    public async Task<string> RedoWrite()
    {
        if (patchUndoService is null) return "Undo service not available.";
        return await patchUndoService.RedoAsync();
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Find a process by name (partial match). Returns PID.")]
    public async Task<string> FindProcess([Description("Process name or partial name")] string name)
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Freeze or unfreeze an address table entry's value.")]
    public Task<string> FreezeAddress(
        [Description("Node ID or label")] string nodeId,
        [Description("'freeze', 'unfreeze', or 'set' (freeze at specific value)")] string action = "freeze",
        [Description("Value for action='set'")] string? value = null)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (node.IsGroup) return Task.FromResult("Cannot freeze a group.");
        if (node.IsScriptEntry) return Task.FromResult("Use ToggleScript for script entries.");

        switch (action.ToLowerInvariant())
        {
            case "unfreeze":
                if (!node.IsLocked) return Task.FromResult($"'{node.Label}' is not frozen.");
                node.IsLocked = false;
                node.LockedValue = null;
                return Task.FromResult($"Unfrozen '{node.Label}'. Value can now change freely.");

            case "set":
                if (string.IsNullOrEmpty(value)) return Task.FromResult("Value required for action='set'.");
                node.IsLocked = true;
                node.LockedValue = value;
                return Task.FromResult($"Frozen '{node.Label}' at value {value}.");

            default: // "freeze"
                if (node.IsLocked) return Task.FromResult($"'{node.Label}' is already frozen at {node.LockedValue}.");
                node.IsLocked = true;
                node.LockedValue = node.CurrentValue;
                return Task.FromResult($"Frozen '{node.Label}' at current value {node.CurrentValue}.");
        }
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Toggle a script entry on/off via the AA engine.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get detailed info about an address table node.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Get current scan results (top N).")]
    public Task<string> GetScanResults(
        [Description("Maximum results to return")] int maxResults = 0)
    {
        if (maxResults <= 0) maxResults = _limits.MaxSearchResults;
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Get current context: process, table summary, scan state.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Check if process is alive and session state freshness.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Probe address as multiple types (Int32, Float, Int64, etc).")]
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Replace script content of an existing entry.")]
    public Task<string> EditScript(
        [Description("Node ID or label of the script entry")] string nodeId,
        [Description("New script content with [ENABLE]/[DISABLE] sections")] string newScript)
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Create a new script entry in the address table.")]
    public Task<string> CreateScriptEntry(
        [Description("Label/name for the script entry")] string label,
        [Description("Script content with [ENABLE]/[DISABLE] sections")] string script,
        [Description("Parent group ID (optional, omit for top-level)")] string? parentGroupId = null)
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

    // EnableScript and DisableScript removed — use ToggleScript instead.

    // ── Screen capture tool ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Capture screenshot of attached process window.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Hex dump of raw bytes at an address.")]
    public async Task<string> HexDump(
        [Description("Process ID")] int processId,
        [Description("Start address (hex, decimal, or symbolic like 'module.dll+offset')")] string address,
        [Description("Number of bytes to read (default 64)")] int length = 64)
    {
        length = Math.Clamp(length, 1, _limits.MaxHexDumpBytes);
        var resolvedAddress = await TryResolveToHex(processId, address);
        var addr = ParseAddress(resolvedAddress);
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove an entry from the address table by its node ID.")]
    public Task<string> RemoveFromAddressTable([Description("Node ID or label to remove")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        var label = node.Label;
        addressTableService.RemoveEntry(nodeId);
        return Task.FromResult($"Removed '{label}' (ID: {nodeId}) from address table.");
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Save current session (address table + log) to disk.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List recent investigation sessions.")]
    public async Task<string> ListSessions([Description("Max sessions to list")] int limit = 10)
    {
        if (sessionService is null) return "Session service not available.";
        var sessions = await sessionService.ListSessionsAsync(limit);
        if (sessions.Count == 0) return "No saved sessions.";
        var lines = sessions.Select(s => $"  [{s.Id}] {s.ProcessName} — {s.CreatedAtUtc:g} ({s.AddressEntryCount} entries)");
        return $"Sessions ({sessions.Count}):\n{string.Join('\n', lines)}";
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Load a saved session by ID, restoring address table.")]
    public async Task<string> LoadSession([Description("Session ID to load")] string sessionId)
    {
        if (sessionService is null) return "Session service not available.";
        var result = await sessionService.LoadSessionAsync(sessionId);
        if (result is null) return $"Session '{sessionId}' not found.";
        var (entries, processName, processId) = (result.Value.Entries, result.Value.ProcessName, result.Value.ProcessId);
        addressTableService.ImportFlat(entries);
        return $"Loaded session '{sessionId}': {processName} (PID {processId}), {entries.Count} entries restored.";
    }

    // ── Chat History Search ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Search chat transcripts for a keyword or phrase.")]
    public string SearchChatHistory(
        [Description("Search query (case-insensitive substring match)")] string query,
        [Description("Max results to return")] int maxResults = 0,
        [Description("Scope: all, current, or a chat ID")] string scope = "all")
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Please provide a search query.";

        if (maxResults <= 0) maxResults = _limits.MaxChatSearchResults;

        var results = new List<(string chatTitle, string chatId, string role, DateTimeOffset timestamp, string snippet)>();
        const int snippetRadius = 120;

        // Search current in-memory chat first
        if (scope is "all" or "current" && currentChatProvider is not null)
        {
            foreach (var msg in currentChatProvider())
                SearchMessage(msg, query, "(current chat)", "current", snippetRadius, results);
        }

        // Search saved chats
        if (scope is not "current" && chatStore is not null)
        {
            var chats = scope == "all"
                ? chatStore.ListAll()
                : [chatStore.Load(scope)!];

            foreach (var chat in chats)
            {
                if (chat is null) continue;
                foreach (var msg in chat.Messages)
                    SearchMessage(msg, query, chat.Title, chat.Id, snippetRadius, results);
            }
        }

        if (results.Count == 0)
            return $"No matches found for \"{query}\".";

        // Most recent first, capped
        var capped = results.OrderByDescending(r => r.timestamp).Take(maxResults).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} matches for \"{query}\" (showing {capped.Count}):");
        foreach (var (title, chatId, role, ts, snippet) in capped)
        {
            sb.AppendLine($"  [{chatId}] {title} — {role} @ {ts:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"    ...{snippet}...");
        }
        return sb.ToString();
    }

    private static void SearchMessage(AiChatMessage msg, string query, string chatTitle, string chatId,
        int snippetRadius, List<(string, string, string, DateTimeOffset, string)> results)
    {
        // Search message content
        var idx = msg.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = Math.Max(0, idx - snippetRadius);
            var end = Math.Min(msg.Content.Length, idx + query.Length + snippetRadius);
            var snippet = msg.Content[start..end].Replace('\n', ' ').Replace('\r', ' ');
            results.Add((chatTitle, chatId, msg.Role, msg.Timestamp, snippet));
        }

        // Search tool results too (often contain addresses, values, findings)
        if (msg.ToolResults is not null)
        {
            foreach (var tr in msg.ToolResults)
            {
                if (tr.Result is null) continue;
                var trIdx = tr.Result.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (trIdx >= 0)
                {
                    var start = Math.Max(0, trIdx - snippetRadius);
                    var end = Math.Min(tr.Result.Length, trIdx + query.Length + snippetRadius);
                    var snippet = $"[{tr.Name}] {tr.Result[start..end].Replace('\n', ' ').Replace('\r', ' ')}";
                    results.Add((chatTitle, chatId, "tool:" + tr.Name, msg.Timestamp, snippet));
                }
            }
        }
    }

    // ── Signature / AOB tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Generate AOB signature at a code address.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Test AOB signature uniqueness within a module.")]
    public async Task<string> TestSignatureUniqueness(
        [Description("Process ID")] int processId,
        [Description("Module name to search (e.g. GameAssembly.dll)")] string moduleName,
        [Description("AOB pattern with ?? wildcards")] string pattern)
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

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [Description("Change memory page protection for a region.")]
    public async Task<string> ChangeMemoryProtection(
        [Description("Process ID")] int processId,
        [Description("Memory address as hex (e.g. 0x7FF6A000)")] string address,
        [Description("Region size in bytes")] int size,
        [Description("Protection: ReadWrite, ExecuteReadWrite, ReadOnly, etc.")] string protection)
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

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [Description("Allocate memory in the target process.")]
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

    [Destructive]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Query memory protection flags at an address.")]
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

    /// <summary>
    /// Resolve a symbolic address (module+offset or bare module name) to a raw hex string.
    /// If already hex, returns as-is. Throws if a symbolic pattern is detected but can't resolve.
    /// </summary>
    private async Task<string> TryResolveToHex(int processId, string address)
    {
        var normalized = address.Trim();

        // Already a raw hex address
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return normalized;

        // Check for module+offset pattern
        var plusIdx = normalized.IndexOf('+');
        if (plusIdx > 0)
        {
            var modulePart = normalized[..plusIdx].Trim();
            var offsetPart = normalized[(plusIdx + 1)..].Trim();
            if (offsetPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                offsetPart = offsetPart[2..];

            if (ulong.TryParse(offsetPart, NumberStyles.HexNumber, null, out var offset))
            {
                var attachment = await engineFacade.AttachAsync(processId);
                var mod = attachment.Modules.FirstOrDefault(m =>
                    m.Name.Equals(modulePart, StringComparison.OrdinalIgnoreCase));
                if (mod is not null)
                    return $"0x{(ulong)mod.BaseAddress + offset:X}";

                var available = string.Join(", ", attachment.Modules.Select(m => m.Name).Take(10));
                throw new InvalidOperationException(
                    $"Module '{modulePart}' not found (may not be loaded yet). Loaded modules: {available}");
            }
        }

        // Bare module name (contains '.')
        if (normalized.Contains('.') && !ulong.TryParse(normalized, NumberStyles.HexNumber, null, out _))
        {
            var attachment = await engineFacade.AttachAsync(processId);
            var mod = attachment.Modules.FirstOrDefault(m =>
                m.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (mod is not null)
                return $"0x{(ulong)mod.BaseAddress:X}";

            var available = string.Join(", ", attachment.Modules.Select(m => m.Name).Take(10));
            throw new InvalidOperationException(
                $"Module '{normalized}' not found (may not be loaded yet). Loaded modules: {available}");
        }

        // Might be raw decimal or plain hex without prefix — pass through
        return normalized;
    }

    private static nuint ParseAddress(string address)
    {
        var addr = address.Trim();
        if (addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            addr = addr[2..];
        return (nuint)ulong.Parse(addr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Find which module owns a memory region by checking if the region overlaps with any module's address range.</summary>
    private static string? FindOwningModule(nuint regionBase, long regionSize, IReadOnlyList<ModuleDescriptor>? modules)
    {
        if (modules is null) return null;
        var regionEnd = (ulong)regionBase + (ulong)regionSize;
        foreach (var m in modules)
        {
            var modBase = (ulong)m.BaseAddress;
            var modEnd = modBase + (ulong)m.SizeBytes;
            if ((ulong)regionBase >= modBase && (ulong)regionBase < modEnd)
                return m.Name;
        }
        return null;
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
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

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Begin a named transaction group for compound breakpoint/hook operations. All operations in the group can be rolled back together. Returns the group ID.")]
    public string BeginTransaction([Description("Name for this transaction group")] string name = "auto")
    {
        var groupId = $"txn-{name}-{Guid.NewGuid():N}"[..24];
        return JsonSerializer.Serialize(new { groupId, status = "open", message = $"Transaction group '{groupId}' created. Pass this groupId to subsequent BP/hook operations." }, _jsonOpts);
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Rollback all operations in a transaction group, restoring original state in reverse order.")]
    public async Task<string> RollbackTransaction([Description("Transaction group ID")] string groupId)
    {
        if (operationJournal is null) return "Operation journal not available.";
        var result = await operationJournal.RollbackGroupAsync(groupId);
        return JsonSerializer.Serialize(new { result.Success, result.TotalOperations, result.SucceededRollbacks, result.Message }, _jsonOpts);
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all recorded operations in the journal. Shows operation type, address, mode, status, and group membership.")]
    public string ListJournalEntries()
    {
        if (operationJournal is null) return "Operation journal not available.";
        var entries = operationJournal.GetEntries();
        return JsonSerializer.Serialize(new
        {
            entries = entries.Take(50).Select(e => new
            {
                e.OperationId, e.OperationType, address = $"0x{e.Address:X}",
                e.Mode, e.GroupId, status = e.Status.ToString(), timestamp = e.Timestamp.ToString("HH:mm:ss")
            }),
            count = entries.Count
        }, _jsonOpts);
    }

    // ── Hook/script coexistence (L4) ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all addresses marked unsafe by the watchdog due to prior freeze incidents.")]
    public string ListUnsafeAddresses()
    {
        if (watchdogService is null) return "Watchdog not available.";
        var entries = watchdogService.GetUnsafeAddresses();
        if (entries.Count == 0) return "No addresses marked unsafe.";
        var lines = entries.Take(50).Select(e =>
            $"  0x{e.Address:X} | {e.Mode} | {e.OperationType} | frozen: {e.FreezeDetectedUtc:HH:mm:ss} | rollback: {(e.RollbackSucceeded ? "OK" : "FAILED")}");
        return $"Unsafe addresses ({entries.Count}):\n{string.Join('\n', lines)}";
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
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
        WriteIndented = false,
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

    // ── Spilled Result Retrieval ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Unbounded)]
    [Description("Retrieve a page of a large tool result that was spilled to storage. " +
        "When a tool result is too large for the context window, it is stored and you receive a " +
        "summary with a result_id. Use this tool to page through the full data.")]
    public string RetrieveToolResult(
        [Description("The result handle ID (e.g. 'tr_0001') from the spill notice")] string resultId,
        [Description("Character offset to start reading from (0-based)")] int offset = 0,
        [Description("Maximum characters to return in this page")] int maxChars = 0)
    {
        if (maxChars <= 0) maxChars = _limits.MaxToolResultChars;
        var page = ToolResultStore.Retrieve(resultId, offset, maxChars);
        if (page is null)
            return $"No stored result found for '{resultId}'. Use ListStoredResults to see available handles.";
        return page;
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all large tool results currently stored. Each entry shows the result ID, " +
        "source tool name, total size, and when it was stored.")]
    public string ListStoredResults()
    {
        var items = ToolResultStore.ListAll();
        if (items.Count == 0)
            return "No stored results. Results are stored automatically when a tool returns data " +
                   "exceeding the context budget.";

        return ToJson(items.Take(20).Select(r => new
        {
            r.Id,
            r.ToolName,
            totalChars = r.TotalChars,
            totalLines = r.TotalLines,
            storedAt = r.StoredAt.ToString("HH:mm:ss"),
        }));
    }
}
