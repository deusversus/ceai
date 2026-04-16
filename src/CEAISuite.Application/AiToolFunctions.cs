using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;
using Iced.Intel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

/// <summary>
/// Exposes engine capabilities as AI-callable tools via Microsoft.Extensions.AI function calling.
/// Uses application-level services so the AI operates through the same layer as the UI.
/// </summary>
#pragma warning disable CS9113 // Parameter is unread — kept for DI constructor compatibility
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
    PointerScannerService? pointerScannerService = null,
    PointerRescanService? pointerRescanService = null,
    ICallStackEngine? callStackEngine = null,
    ICodeCaveEngine? codeCaveEngine = null,
    ProcessWatchdogService? watchdogService = null,
    OperationJournal? operationJournal = null,
    AiChatStore? chatStore = null,
    Func<IReadOnlyList<AiChatMessage>>? currentChatProvider = null,
    TokenLimits? tokenLimits = null,
    ToolResultStore? toolResultStore = null,
    ILuaScriptEngine? luaEngine = null,
    ISymbolEngine? symbolEngine = null,
    AgentLoop.PluginHost? pluginHost = null,
    IUiCommandBus? uiCommandBus = null,
    SpeedHackService? speedHackService = null,
    VehDebugService? vehDebugService = null,
    SteppingService? steppingService = null,
    AutorunScriptService? autorunService = null,
    AppSettingsService? appSettingsService = null,
    IMonoEngine? monoEngine = null,
    ILogger<AiToolFunctions>? logger = null)
{
    private readonly TokenLimits _limits = tokenLimits ?? TokenLimits.Balanced;

    private static readonly nuint PageMask = ~(nuint)0xFFF;
    private static readonly nuint PageSize = 0x1000;

    /// <summary>Store for large tool results that exceeded the context budget.</summary>
    internal ToolResultStore ToolResultStore { get; } = toolResultStore ?? new ToolResultStore();
    /// <summary>Queue of captured screenshots for injection into the AI conversation.</summary>
    public ConcurrentQueue<(string Description, byte[] PngData)> PendingImages { get; } = new();

    // M3: Process liveness / stale-state tracking
    private int _sessionGeneration;
    private int _lastAttachedPid;

    // WP1.5: Cache last pointer scan results so SavePointerMap doesn't re-scan
    private IReadOnlyList<PointerPath>? _lastPointerScanResults;
    private nuint _lastPointerScanTarget;

    // Phase 5A: short-TTL cache for process list
    private IReadOnlyList<ProcessDescriptor>? _processListCache;
    private DateTime _processListCacheExpiry;
    private static readonly TimeSpan ProcessListCacheTtl = TimeSpan.FromSeconds(2);

    private object GetProcessStatusJson(int processId)
    {
        bool alive = IsProcessAlive(processId);
        bool pidChanged = processId != _lastAttachedPid;
        if (pidChanged) { _sessionGeneration++; _lastAttachedPid = processId; }
        return new { processAlive = alive, processId, sessionGeneration = _sessionGeneration, pidChanged };
    }

    private bool IsProcessAlive(int processId)
    {
        try { return !System.Diagnostics.Process.GetProcessById(processId).HasExited; }
        catch (Exception ex) { if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(ex, "IsProcessAlive check failed for PID {ProcessId}", processId); return false; }
    }

    /// <summary>
    /// Validate that a destructive operation targets the currently attached process.
    /// Returns an error string if mismatched, or null if valid.
    /// </summary>
    private string? ValidateDestructiveProcessId(int processId)
    {
        if (!engineFacade.IsAttached)
            return "No process attached. Use AttachProcess first.";
        if (engineFacade.AttachedProcessId != processId)
            return $"Process ID mismatch: tool targets PID {processId} but attached to PID {engineFacade.AttachedProcessId}. Attach to the correct process first.";
        return null;
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List running processes. Returns PID, name, architecture.")]
    public async Task<string> ListProcesses()
    {
        if (_processListCache is null || DateTime.UtcNow >= _processListCacheExpiry)
        {
            _processListCache = await engineFacade.ListProcessesAsync().ConfigureAwait(false);
            _processListCacheExpiry = DateTime.UtcNow + ProcessListCacheTtl;
        }
        var processes = _processListCache;
        var cap = _limits.MaxListProcesses;
        var lines = processes.Take(cap).Select(p => $"PID {p.Id} | {p.Name} | {p.Architecture}");
        return $"Found {processes.Count} processes (showing {Math.Min(cap, processes.Count)}):\n{string.Join('\n', lines)}";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Inspect process by PID. Returns architecture, parent process, executable path, command line, window title, modules, and elevation status.")]
    public async Task<string> InspectProcess([Description("Process ID to inspect")] int processId)
    {
        var inspection = await dashboardService.InspectProcessAsync(processId).ConfigureAwait(false);
        var cap = _limits.MaxInspectModules;
        var modules = inspection.Modules.Take(cap)
            .Select(m => m.FullPath is not null
                ? $"  {m.Name} @ {m.BaseAddress} ({m.Size}, {m.FullPath})"
                : $"  {m.Name} @ {m.BaseAddress} ({m.Size})");
        var extra = inspection.Modules.Count > cap ? $"\n  ... and {inspection.Modules.Count - cap} more modules" : "";

        var sb = new System.Text.StringBuilder();
        sb.Append("Process: ").Append(inspection.ProcessName).Append(" (PID ").Append(inspection.ProcessId).Append(", ").Append(inspection.Architecture).AppendLine(")");
        if (inspection.ParentProcessId is { } ppid)
            sb.Append("Parent: ").Append(inspection.ParentProcessName ?? "unknown").Append(" (PID ").Append(ppid).AppendLine(")");
        if (inspection.ExecutablePath is not null)
            sb.Append("Path: ").AppendLine(inspection.ExecutablePath);
        if (inspection.CommandLine is not null)
            sb.Append("Command line: ").AppendLine(inspection.CommandLine);
        if (inspection.WindowTitle is not null)
            sb.Append("Window: ").AppendLine(inspection.WindowTitle);
        sb.Append("Elevated: ").AppendLine(inspection.IsElevated ? "Yes" : "No");
        sb.Append("Modules (").Append(inspection.Modules.Count).Append(" total, showing ").Append(Math.Min(cap, inspection.Modules.Count)).Append("):\n").Append(string.Join('\n', modules)).Append(extra);
        return sb.ToString();
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
            var resolvedAddress = await TryResolveToHex(processId, address).ConfigureAwait(false);
            var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
            var probe = await dashboardService.ReadAddressAsync(processId, resolvedAddress, dt).ConfigureAwait(false);
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
            var pidError = ValidateDestructiveProcessId(processId);
            if (pidError is not null) return pidError;
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var resolvedAddress = await TryResolveToHex(processId, address).ConfigureAwait(false);
            var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
            if (patchUndoService is not null)
            {
                var addr = AddressTableService.ParseAddress(resolvedAddress);
                var result = await patchUndoService.WriteWithUndoAsync(processId, addr, dt, value).ConfigureAwait(false);
                return result.BytesWritten > 0
                    ? $"Wrote '{value}' ({dt}) to 0x{addr:X}. {patchUndoService.UndoCount} patches in undo stack."
                    : $"Write failed at 0x{addr:X}.";
            }
            var message = await dashboardService.WriteAddressAsync(processId, resolvedAddress, dt, value).ConfigureAwait(false);
            return message;
        }
        catch (Exception ex)
        {
            return $"WriteMemory failed: {ex.Message}";
        }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Write multiple values in a single operation. Each entry is 'address|type|value' separated by semicolons.")]
    public async Task<string> BatchWrite(
        [Description("Process ID")] int processId,
        [Description("Semicolon-separated entries: 'addr|type|value;addr|type|value;...'")] string entries)
    {
        try
        {
            var pidError = ValidateDestructiveProcessId(processId);
            if (pidError is not null) return pidError;
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            var parts = entries.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int succeeded = 0, failed = 0;
            var errors = new List<string>();

            foreach (var part in parts)
            {
                var fields = part.Split('|', 3);
                if (fields.Length < 3) { failed++; errors.Add($"Invalid entry: '{part}'"); continue; }

                try
                {
                    var resolvedAddr = await TryResolveToHex(processId, fields[0].Trim()).ConfigureAwait(false);
                    var dt = Enum.Parse<MemoryDataType>(fields[1].Trim(), ignoreCase: true);
                    var addr = AddressTableService.ParseAddress(resolvedAddr);
                    if (patchUndoService is not null)
                        await patchUndoService.WriteWithUndoAsync(processId, addr, dt, fields[2].Trim()).ConfigureAwait(false);
                    else
                        await engineFacade.WriteValueAsync(processId, addr, dt, fields[2].Trim()).ConfigureAwait(false);
                    succeeded++;
                }
                catch (Exception ex) { failed++; errors.Add($"{fields[0]}: {ex.Message}"); }
            }

            return ToJson(new { succeeded, failed, total = parts.Length, errors = errors.Count > 0 ? errors : null });
        }
        catch (Exception ex) { return $"BatchWrite failed: {ex.Message}"; }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Fill a memory region with a repeating byte pattern.")]
    public async Task<string> FillMemory(
        [Description("Process ID")] int processId,
        [Description("Start address (hex or symbolic)")] string address,
        [Description("Number of bytes to fill")] int length,
        [Description("Byte pattern in hex (e.g. '90' for NOP, 'CC' for INT3, '00' for zero)")] string pattern = "00")
    {
        try
        {
            var pidError = ValidateDestructiveProcessId(processId);
            if (pidError is not null) return pidError;
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            if (length < 1 || length > 0x100000) return "Length must be 1..1048576 bytes.";

            var resolvedAddr = await TryResolveToHex(processId, address).ConfigureAwait(false);
            var addr = AddressTableService.ParseAddress(resolvedAddr);
            var patternBytes = Convert.FromHexString(pattern.Replace(" ", "", StringComparison.Ordinal));
            if (patternBytes.Length == 0) return "Pattern must not be empty.";

            // Build the fill buffer by repeating the pattern
            var fillBuffer = new byte[length];
            for (int i = 0; i < length; i++)
                fillBuffer[i] = patternBytes[i % patternBytes.Length];

            var written = await engineFacade.WriteBytesAsync(processId, addr, fillBuffer).ConfigureAwait(false);
            return $"Filled {written} bytes at 0x{(ulong)addr:X} with pattern {pattern}.";
        }
        catch (Exception ex) { return $"FillMemory failed: {ex.Message}"; }
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
            var overview = await scanService.StartScanAsync(processId, dt, st, value ?? "", options).ConfigureAwait(false);
            var topResults = overview.Results.Take(10)
                .Select(r => $"  {r.Address} = {r.CurrentValue}");
            return $"Scan complete: {overview.ResultCount:N0} results found.\n{string.Join('\n', topResults)}";
        }
        catch (Exception ex)
        {
            return $"StartScan failed: {ex.Message}";
        }
    }

    [ConcurrencySafe]
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
        [Description("Value to match (for ExactValue) or empty")] string? value,
        [Description("Scan alignment in bytes (0=auto, 1/2/4/8). Default: 0")] int alignment = 0,
        [Description("Float comparison tolerance. Default: null (exact match)")] float? floatEpsilon = null,
        [Description("Only scan writable regions. Default: true")] bool writableOnly = true)
    {
        try
        {
            var st = Enum.Parse<ScanType>(scanType, ignoreCase: true);
            var options = new ScanOptions(
                Alignment: alignment,
                FloatEpsilon: floatEpsilon,
                WritableOnly: writableOnly);
            var overview = await scanService.RefineScanAsync(st, value ?? "", options).ConfigureAwait(false);
            var topResults = overview.Results.Take(10)
                .Select(r => $"  {r.Address} = {r.CurrentValue} (was {r.PreviousValue})");
            return $"Refinement complete: {overview.ResultCount:N0} results remaining.\n{string.Join('\n', topResults)}";
        }
        catch (Exception ex)
        {
            return $"RefineScan failed: {ex.Message}";
        }
    }


    private static string FormatBytes(long bytes) => MemoryUtils.FormatBytes(bytes);

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
    [Description("List address table entries with pagination. Returns compact text by default; set compact=false for full JSON.")]
    public Task<string> ListAddressTable(
        [Description("Number of entries to skip (default 0). Use for pagination.")] int offset = 0,
        [Description("Max entries to return (default 50, max 100).")] int limit = 50,
        [Description("If true (default), return compact one-line-per-entry format. If false, return full JSON.")] bool compact = true)
    {
        var roots = addressTableService.Roots;
        var totalCount = CountNodes(roots);
        if (totalCount == 0)
            return Task.FromResult("Address table is empty (0 entries).");

        limit = Math.Clamp(limit, 1, 100);

        if (compact)
            return Task.FromResult(FormatCompactTable(roots, offset, limit, totalCount));

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

    private static string FormatCompactTable(IReadOnlyList<AddressTableNode> roots, int offset, int limit, int total)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        var endIdx = Math.Min(offset + limit, total);
        sb.AppendLine(ic, $"Address Table ({total} entries, showing {offset + 1}-{endIdx}):");

        int index = 0;
        FormatCompactNodes(roots, sb, 0, offset, limit, ref index);

        if (endIdx < total)
            sb.Append(ic, $"... {total - endIdx} more (offset={endIdx} for next page)");
        return sb.ToString();
    }

    private static void FormatCompactNodes(IEnumerable<AddressTableNode> nodes, System.Text.StringBuilder sb, int depth, int offset, int limit, ref int index)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var indent = new string(' ', depth * 2);
        foreach (var n in nodes)
        {
            if (index >= offset + limit) return;
            if (index >= offset)
            {
                if (n.IsGroup)
                    sb.AppendLine(ic, $"{indent}[G] {Truncate(n.Label, 35)} ({n.Children.Count} children)");
                else if (n.IsScriptEntry)
                    sb.AppendLine(ic, $"{indent}[S] {Truncate(n.Label, 35)} {n.Id} {(n.IsScriptEnabled ? "ON" : "OFF")}");
                else
                {
                    var addr = n.ResolvedAddress.HasValue ? $"0x{n.ResolvedAddress.Value:X}" : n.Address;
                    var locked = n.IsLocked ? " LOCKED" : "";
                    sb.AppendLine(ic, $"{indent}[V] {Truncate(n.Label, 25)} {addr} {n.DataType} {n.DisplayValue ?? "?"}{locked}");
                }
            }
            index++;
            if (n.IsGroup && n.Children.Count > 0)
                FormatCompactNodes(n.Children, sb, depth + 1, offset, limit, ref index);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

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
        return new
        {
            n.Id,
            n.Label,
            address = n.ResolvedAddress.HasValue ? $"0x{n.ResolvedAddress.Value:X}" : n.Address,
            symbolicAddress = n.IsOffset ? n.Address : null,
            n.DisplayValue,
            n.DataType,
            n.IsLocked
        };
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get a bird's-eye overview of the address table: group structure, entry counts by type, script status. Much cheaper than paging through ListAddressTable.")]
    public Task<string> SummarizeCheatTable()
    {
        var roots = addressTableService.Roots;
        if (roots.Count == 0) return Task.FromResult("Address table is empty.");

        var totalEntries = CountNodes(roots);
        var scriptCount = CountScriptsInNodes(roots);
        var leafCount = addressTableService.Entries.Count;
        var lockedCount = addressTableService.Entries.Count(e => e.IsLocked);

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ic, $"Address Table: {totalEntries} entries ({leafCount} values, {scriptCount} scripts, {lockedCount} locked)");
        sb.AppendLine();
        sb.AppendLine("Structure:");
        foreach (var root in roots)
        {
            if (root.IsGroup)
            {
                var cv = CountLeaves(root.Children);
                var cs = CountScriptsInNodes(root.Children);
                var cg = root.Children.Count(c => c.IsGroup);
                sb.AppendLine(ic, $"  [{root.Label}] {root.Children.Count} children ({cv} values, {cs} scripts, {cg} subgroups)");
            }
            else if (root.IsScriptEntry)
                sb.AppendLine(ic, $"  [Script] {root.Label} ({(root.IsScriptEnabled ? "ON" : "OFF")})");
            else
                sb.AppendLine(ic, $"  [Value] {root.Label} = {root.DisplayValue}");
        }

        // Data type distribution
        var byType = addressTableService.Entries
            .GroupBy(e => e.DataType)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}:{g.Count()}");
        sb.AppendLine(ic, $"\nTypes: {string.Join(", ", byType)}");

        return Task.FromResult(sb.ToString());
    }

    private static int CountLeaves(IEnumerable<AddressTableNode> nodes)
    {
        int count = 0;
        foreach (var n in nodes)
        {
            if (!n.IsGroup && !n.IsScriptEntry) count++;
            if (n.Children.Count > 0) count += CountLeaves(n.Children);
        }
        return count;
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Refresh address table values from process memory.")]
    public async Task<string> RefreshAddressTable([Description("Process ID")] int processId)
    {
        await addressTableService.RefreshAllAsync(processId).ConfigureAwait(false);
        var entries = addressTableService.Entries;
        var total = entries.Count;

        var changed = entries.Where(e =>
            e.CurrentValue != e.PreviousValue && e.PreviousValue is not null).ToList();
        var nonZeroCount = entries.Count(e =>
            e.CurrentValue is not null && e.CurrentValue != "0" && e.CurrentValue != "0.0");

        const int maxChangedShown = 20;
        var changedPreview = changed.Take(maxChangedShown)
            .Select(e => $"  {e.Label}: {e.PreviousValue} -> {e.CurrentValue}");

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ic, $"Refreshed {total} entries. {changed.Count} changed, {nonZeroCount} non-zero.");
        if (changed.Count > 0)
        {
            sb.AppendLine(string.Join('\n', changedPreview));
            if (changed.Count > maxChangedShown)
                sb.AppendLine(ic, $"  ... {changed.Count - maxChangedShown} more changes. Use ListAddressTable to see all values.");
        }
        return sb.ToString();
    }

    // ── Artifact generation tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Generate C# trainer script from locked entries.")]
    public Task<string> GenerateTrainerScript([Description("Process name for the trainer target")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries to generate trainer from. Lock some address table entries first.");
        var script = ScriptGenerationService.GenerateTrainerScript(locked, processName);
        return Task.FromResult($"Generated C# trainer script ({locked.Count} entries):\n\n{script}");
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Generate Auto Assembler script from locked entries.")]
    public Task<string> GenerateAutoAssemblerScript([Description("Process name")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries. Lock entries first.");
        var script = ScriptGenerationService.GenerateAutoAssemblerScript(locked, processName);
        return Task.FromResult($"Generated AA script ({locked.Count} entries):\n\n{script}");
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Generate Lua script from locked entries.")]
    public Task<string> GenerateLuaScript([Description("Process name")] string processName)
    {
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        if (locked.Count == 0) return Task.FromResult("No locked entries. Lock entries first.");
        var script = ScriptGenerationService.GenerateLuaScript(locked, processName);
        return Task.FromResult($"Generated Lua script ({locked.Count} entries):\n\n{script}");
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Summarize current investigation state.")]
    public async Task<string> SummarizeInvestigation(
        [Description("Process name")] string processName,
        [Description("Process ID")] int processId)
    {
        var dashboard = await dashboardService.BuildAsync(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CEAISuite", "workspace.db")).ConfigureAwait(false);
        var summary = ScriptGenerationService.SummarizeInvestigation(
            processName, processId, addressTableService.Entries.ToList(),
            scanService.LastScanResults is not null
                ? scanService.LastScanResults.Results.Take(10).Select(r =>
                    new ScanResultOverview($"0x{r.Address:X}", r.CurrentValue, r.PreviousValue,
                        Convert.ToHexString(r.RawBytes.ToArray()))).ToArray()
                : null,
            dashboard.Disassembly);
        return summary;
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Attach to a process by PID for memory operations.")]
    public async Task<string> AttachProcess([Description("Process ID")] int processId)
    {
        var inspection = await dashboardService.InspectProcessAsync(processId).ConfigureAwait(false);
        return $"Attached to {inspection.ProcessName} (PID {inspection.ProcessId}, {inspection.Architecture}). " +
               $"{inspection.Modules.Count} modules loaded.";
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Load a .CT (Cheat Table) file and import entries into the address table.")]
    public Task<string> LoadCheatTable([Description("Full file path to the .CT file")] string filePath)
    {
        filePath = System.IO.Path.GetFullPath(filePath);
        if (!filePath.EndsWith(".CT", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".ct", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult("Error: Only .CT files are supported.");
        if (filePath.Contains("..", StringComparison.Ordinal))
            return Task.FromResult("Error: Path traversal is not allowed.");
        if (!System.IO.File.Exists(filePath))
            return Task.FromResult($"File not found: {filePath}");

        var ctFile = CheatTableParser.ParseFile(filePath);
        var nodes = CheatTableParser.ToAddressTableNodes(ctFile);
        addressTableService.ImportNodes(nodes);

        var scriptCount = CountScriptsInNodes(nodes);
        var leafCount = addressTableService.Entries.Count;

        if (ctFile.LuaScript is not null)
            logger?.LogWarning("CT file {FileName} contains embedded Lua script ({Length} chars)", ctFile.FileName, ctFile.LuaScript.Length);

        return Task.FromResult(
            $"Loaded {ctFile.FileName}: {ctFile.TotalEntryCount} CT entries imported with hierarchy. " +
            $"{leafCount} address entries, {scriptCount} scripts, {nodes.Count} top-level nodes. " +
            $"Table version: {ctFile.TableVersion}" +
            (ctFile.LuaScript is not null ? $". \u26a0\ufe0f WARNING: Contains embedded Lua script ({ctFile.LuaScript.Length:#,0} chars). Review with ViewScript before executing." : "") +
            ". Use SummarizeCheatTable for a structural overview.");
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Save the current address table as a Cheat Engine .CT file.")]
    public Task<string> SaveCheatTable([Description("File path to save to")] string filePath)
    {
        filePath = System.IO.Path.GetFullPath(filePath);
        if (!filePath.EndsWith(".CT", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".ct", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult("Error: Only .CT files are supported.");
        if (filePath.Contains("..", StringComparison.Ordinal))
            return Task.FromResult("Error: Path traversal is not allowed.");
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
        [Description("Comma-separated module names to scan (e.g. 'game.dll,mono.dll'). Empty = all modules.")] string? moduleFilter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scanner = pointerScannerService ?? new PointerScannerService(engineFacade);
        var addr = AddressTableService.ParseAddress(targetAddress);
        IReadOnlyList<string>? filter = string.IsNullOrWhiteSpace(moduleFilter) ? null
            : moduleFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var paths = await scanner.ScanForPointersAsync(processId, addr, maxDepth, moduleFilter: filter, ct: cancellationToken).ConfigureAwait(false);

        // Cache results so SavePointerMap can use them without re-scanning
        _lastPointerScanResults = paths;
        _lastPointerScanTarget = addr;

        if (paths.Count == 0) return "No pointer paths found to the target address.";

        var lines = paths.Take(50).Select((p, i) => $"{i + 1}. {p.Display}");
        return $"Found {paths.Count} pointer path(s) to 0x{addr:X}:\n{string.Join('\n', lines)}";
    }

    // ── Phase 7E: Pointer Map I/O ──

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Save the last pointer scan results to a .PTR file for offline analysis.")]
    public async Task<string> SavePointerMap(
        [Description("Process ID (for process name metadata)")] int processId,
        [Description("Target address the scan was for (hex)")] string targetAddress,
        [Description("File path to save the .PTR file to")] string filePath)
    {
        try
        {
            var addr = AddressTableService.ParseAddress(targetAddress);

            // Use cached results from the most recent ScanForPointers call
            var paths = (_lastPointerScanResults is not null && _lastPointerScanTarget == addr)
                ? _lastPointerScanResults
                : null;

            if (paths is null || paths.Count == 0)
                return "No pointer paths cached. Run ScanForPointers first, then SavePointerMap.";

            var map = new PointerMapFile(
                $"process-{processId}", addr, DateTimeOffset.UtcNow, 2, 0x2000, paths);
            await PointerScannerService.SavePointerMapAsync(filePath, map).ConfigureAwait(false);
            return $"Saved {paths.Count} pointer paths to {filePath}";
        }
        catch (Exception ex) { return $"SavePointerMap failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Load a previously saved .PTR pointer map file.")]
    public static async Task<string> LoadPointerMap(
        [Description("File path to the .PTR file")] string filePath)
    {
        try
        {
            var map = await PointerScannerService.LoadPointerMapAsync(filePath).ConfigureAwait(false);
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
    public static async Task<string> ComparePointerMaps(
        [Description("Path to first .PTR file")] string filePathA,
        [Description("Path to second .PTR file")] string filePathB)
    {
        try
        {
            var mapA = await PointerScannerService.LoadPointerMapAsync(filePathA).ConfigureAwait(false);
            var mapB = await PointerScannerService.LoadPointerMapAsync(filePathB).ConfigureAwait(false);
            var result = PointerScannerService.CompareMaps(mapA, mapB);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Pointer map comparison: {result.OverlapRatio:P0} overlap");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Common: {result.CommonPaths.Count} | Only in A: {result.OnlyInFirst.Count} | Only in B: {result.OnlyInSecond.Count}");
            if (result.CommonPaths.Count > 0)
            {
                sb.AppendLine("\nCommon paths (most stable):");
                foreach (var p in result.CommonPaths.Take(20))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {p.Display}");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"ComparePointerMaps failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Resume a previously cancelled pointer scan from where it left off.")]
    public async Task<string> ResumePointerScan(
        [Description("Process ID to scan")] int processId)
    {
        if (pointerScannerService is null) return "Pointer scanner service not available.";
        try
        {
            var paths = await pointerScannerService.ResumeScanAsync(processId).ConfigureAwait(false);
            if (paths.Count == 0) return "No pointer paths found after resuming scan.";
            var lines = paths.Take(50).Select((p, i) => $"{i + 1}. {p.Display}");
            return $"Resumed scan found {paths.Count} pointer path(s):\n{string.Join('\n', lines)}";
        }
        catch (Exception ex)
        {
            return $"ResumePointerScan failed: {ex.Message}";
        }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Re-resolve all known pointer paths and rank by stability. Validates which paths still point to the correct address.")]
    public async Task<string> RescanAllPointerPaths(
        [Description("Process ID to validate against")] int processId,
        [Description("Semicolon-separated pointer paths in 'module+moduleOffset:off1,off2,...' format")] string paths,
        [Description("Expected target value (hex, optional)")] string? expectedValue = null)
    {
        if (pointerRescanService is null) return "Pointer rescan service not available.";
        try
        {
            var parsedPaths = ParsePointerPaths(paths);
            if (parsedPaths.Count == 0) return "No valid pointer paths provided.";
            nuint? expected = string.IsNullOrWhiteSpace(expectedValue) ? null : AddressTableService.ParseAddress(expectedValue);
            var results = await pointerRescanService.RescanAllAsync(processId, parsedPaths, expected).ConfigureAwait(false);
            if (results.Count == 0) return "No rescan results.";
            var lines = results.Select((r, i) =>
                $"{i + 1}. {r.OriginalPath.Display} — {r.Status} (stability: {r.StabilityScore:F2}, valid: {r.IsValid})");
            var validCount = results.Count(r => r.IsValid);
            return $"Rescanned {results.Count} path(s), {validCount} still valid:\n{string.Join('\n', lines)}";
        }
        catch (Exception ex)
        {
            return $"RescanAllPointerPaths failed: {ex.Message}";
        }
    }

    private static List<PointerPath> ParsePointerPaths(string input)
    {
        var result = new List<PointerPath>();
        foreach (var segment in input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIdx = segment.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;
            var basePart = segment[..colonIdx];
            var offsetsPart = segment[(colonIdx + 1)..];

            var plusIdx = basePart.IndexOf('+', StringComparison.Ordinal);
            if (plusIdx < 0) continue;
            var moduleName = basePart[..plusIdx].Trim();
            var moduleOffset = AddressTableService.ParseAddress(basePart[(plusIdx + 1)..].Trim());

            var offsets = offsetsPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(o => long.Parse(o, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray();

            result.Add(new PointerPath(moduleName, 0, (long)moduleOffset, offsets, 0));
        }
        return result;
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

            var results = await scanService.GroupedScanAsync(processId, parsedGroups, new ScanOptions()).ConfigureAwait(false);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Grouped scan: {results.Results.Count} total results across {parsedGroups.Count} groups");
            foreach (var group in parsedGroups)
            {
                var groupResults = results.Results.Where(r => r.GroupLabel == group.Label).Take(10).ToList();
                sb.AppendLine(CultureInfo.InvariantCulture, $"\n[{group.Label}] ({groupResults.Count} shown):");
                foreach (var r in groupResults)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{r.Address:X} = {r.CurrentValue}");
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
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
        length = Math.Clamp(length, 1, _limits.MaxBrowseMemoryBytes);
        var resolvedAddress = await TryResolveToHex(processId, address).ConfigureAwait(false);
        var addr = AddressTableService.ParseAddress(resolvedAddress);
        var result = await engineFacade.ReadMemoryAsync(processId, addr, length).ConfigureAwait(false);
        var bytes = result.Bytes.ToArray();

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < bytes.Length; i += 16)
        {
            var lineAddr = (nuint)((long)addr + i);
            sb.Append(CultureInfo.InvariantCulture, $"{lineAddr:X16}  ");
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
        var resolvedAddress = await TryResolveToHex(processId, address).ConfigureAwait(false);
        var addr = AddressTableService.ParseAddress(resolvedAddress);
        var (fields, clustersDetected) = await dissector.DissectAsync(processId, addr, regionSize, typeHint).ConfigureAwait(false);

        if (fields.Count == 0) return "No identifiable fields found in this region.";

        var cap = _limits.MaxDissectFields;
        var capped = fields.Take(cap).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Structure analysis at 0x{addr:X} ({fields.Count} fields, showing {capped.Count}, hint={typeHint}):");
        if (clustersDetected > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Detected {clustersDetected} integer cluster(s) — consecutive game-stat-like Int32 values.");
        sb.AppendLine("Offset  | Type     | Value                | Confidence");
        sb.AppendLine("--------|----------|----------------------|-----------");
        foreach (var f in capped)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"+0x{f.Offset:X4} | {f.ProbableType,-8} | {f.DisplayValue,-20} | {f.Confidence:P0}");
        }
        if (fields.Count > cap)
            sb.AppendLine(CultureInfo.InvariantCulture, $"... {fields.Count - cap} more fields omitted — reduce regionSize or increase limits.");
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
                            _ = autoAssemblerEngine.DisableAsync(pid, node.AssemblerScript!).ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                {
                                    logger?.LogWarning(t.Exception, "Hotkey script disable failed for {Label}", node.Label);
                                    node.ScriptStatus = $"FAILED: {t.Exception?.InnerException?.Message ?? "unknown"}";
                                    return;
                                }
                                node.IsScriptEnabled = false;
                                node.ScriptStatus = "Disabled (hotkey)";
                            }, TaskScheduler.Default);
                        }
                        else
                        {
                            _ = autoAssemblerEngine.EnableAsync(pid, node.AssemblerScript!).ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                {
                                    logger?.LogWarning(t.Exception, "Hotkey script enable failed for {Label}", node.Label);
                                    node.ScriptStatus = $"FAILED: {t.Exception?.InnerException?.Message ?? "unknown"}";
                                    return;
                                }
                                if (t.Result.Success)
                                {
                                    node.IsScriptEnabled = true;
                                    node.ScriptStatus = $"Enabled (hotkey, {t.Result.Patches.Count} patches)";
                                }
                                else
                                {
                                    node.ScriptStatus = $"FAILED: {t.Result.Error}";
                                }
                            }, TaskScheduler.Default);
                        }
                    }
                }
                else
                {
                    // No AA engine available — toggle is a simple flag flip.
                    // This is a no-op toggle (no actual script execution) so the
                    // state change is safe without async verification.
                    node.IsScriptEnabled = !node.IsScriptEnabled;
                    node.ScriptStatus = node.IsScriptEnabled ? "Enabled (hotkey, no AA engine)" : "Disabled (hotkey)";
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
            sb.AppendLine(CultureInfo.InvariantCulture, $"ID {b.Id}: {b.Description}");
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
        return await patchUndoService.UndoAsync().ConfigureAwait(false);
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Redo the last undone memory write operation.")]
    public async Task<string> RedoWrite()
    {
        if (patchUndoService is null) return "Undo service not available.";
        return await patchUndoService.RedoAsync().ConfigureAwait(false);
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"Undo: {patchUndoService.UndoCount} | Redo: {patchUndoService.RedoCount}");
        foreach (var p in patches)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{p.Address:X} [{p.DataType}] = '{p.NewValue}' @ {p.Timestamp:HH:mm:ss}");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    // ── State & control tools (agent needs these to act autonomously) ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Find a process by name (partial match). Returns PID.")]
    public async Task<string> FindProcess([Description("Process name or partial name")] string name)
    {
        var processes = await engineFacade.ListProcessesAsync().ConfigureAwait(false);
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
    [MaxResultSize(MaxResultSizeAttribute.Small)]
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
                var result = await autoAssemblerEngine.EnableAsync(processId, node.AssemblerScript!).ConfigureAwait(false);
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
                var result = await autoAssemblerEngine.DisableAsync(processId, node.AssemblerScript!).ConfigureAwait(false);
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"ID: {node.Id}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Label: {node.Label}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Type: {(node.IsGroup ? "Group" : node.IsScriptEntry ? "Script" : node.DataType.ToString())}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"symbolicAddress: {node.Address}");
        if (node.ResolvedAddress.HasValue)
            sb.AppendLine(CultureInfo.InvariantCulture, $"resolvedAddress: 0x{node.ResolvedAddress.Value:X}");
        else
            sb.AppendLine("resolvedAddress: (unresolved)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"isResolved: {node.ResolvedAddress.HasValue}");
        if (node.IsOffset && node.Parent is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"parentBase: \"{node.Parent.Label}\" ({node.Parent.Id})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"offset: {node.Address}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"Value: {node.CurrentValue}");
        if (node.IsLocked) sb.AppendLine(CultureInfo.InvariantCulture, $"FROZEN at: {node.LockedValue}");
        if (node.IsPointer)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Pointer chain: [{string.Join(", ", node.PointerOffsets.Select(o => $"0x{o:X}"))}]");
        if (node.IsOffset) sb.AppendLine("Is parent-relative offset");
        if (node.Children.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Children ({node.Children.Count}):");
            foreach (var child in node.Children.Take(20))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {child.Id}: {child.Label} = {child.CurrentValue} {(child.IsLocked ? "[FROZEN]" : "")}");
            if (node.Children.Count > 20)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ... and {node.Children.Count - 20} more");
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
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
        var addr = ParseAddress(address);
        var bytes = await engineFacade.ReadMemoryAsync(processId, addr, 8).ConfigureAwait(false);
        var raw = bytes.Bytes.ToArray();
        if (raw.Length < 8) return $"Could not read 8 bytes at {address}.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Memory probe at {address}:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Int16:  {BitConverter.ToInt16(raw, 0)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  UInt16: {BitConverter.ToUInt16(raw, 0)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Int32:  {BitConverter.ToInt32(raw, 0)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  UInt32: {BitConverter.ToUInt32(raw, 0)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Float:  {BitConverter.ToSingle(raw, 0):G9}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Int64:  {BitConverter.ToInt64(raw, 0)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  UInt64: {BitConverter.ToUInt64(raw, 0)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Double: {BitConverter.ToDouble(raw, 0):G17}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Hex:    {Convert.ToHexString(raw)}");
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
        var result = await screenCaptureEngine.CaptureWindowAsync(processId).ConfigureAwait(false);
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
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
        length = Math.Clamp(length, 1, _limits.MaxHexDumpBytes);
        var resolvedAddress = await TryResolveToHex(processId, address).ConfigureAwait(false);
        var addr = ParseAddress(resolvedAddress);
        var mem = await engineFacade.ReadMemoryAsync(processId, addr, length).ConfigureAwait(false);
        var raw = mem.Bytes.ToArray();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Hex dump at 0x{addr:X} ({raw.Length} bytes):");
        for (int i = 0; i < raw.Length; i += 16)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {addr + (nuint)i:X8}: ");
            var lineBytes = Math.Min(16, raw.Length - i);
            for (int j = 0; j < lineBytes; j++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{raw[i + j]:X2} ");
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

    // ── Address table extras ──

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("color", "highlight", "visual", "tag", "categorize")]
    [Description("Set a color for an address table entry for visual categorization. Use hex color like '#FF4444' or named: red, green, blue, yellow, purple, orange, cyan. Empty string clears.")]
    public Task<string> SetEntryColor(
        [Description("Node ID or label")] string nodeId,
        [Description("Color: hex '#FF4444', named 'red'/'green'/'blue'/'yellow'/'purple'/'orange'/'cyan', or '' to clear")] string color)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");

        var resolved = color.Trim().ToLowerInvariant() switch
        {
            "" or "none" or "clear" => (string?)null,
            "red" => "#FF4444",
            "green" => "#44AA44",
            "blue" => "#4488FF",
            "yellow" => "#DDAA00",
            "purple" => "#AA44CC",
            "orange" => "#FF8800",
            "cyan" => "#00AACC",
            _ when color.StartsWith('#') && (color.Length == 7 || color.Length == 9) => color,
            _ => "INVALID"
        };
        if (resolved == "INVALID")
            return Task.FromResult($"Invalid color '{color}'. Use hex (#RRGGBB) or named: red, green, blue, yellow, purple, orange, cyan, none.");

        node.UserColor = resolved;
        return Task.FromResult(resolved is null
            ? $"Color cleared on '{node.Label}'."
            : $"Color set to {resolved} on '{node.Label}'.");
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("duplicate", "copy", "clone", "entry")]
    [Description("Duplicate an address table entry, creating an independent copy with a new ID.")]
    public Task<string> DuplicateEntry([Description("Node ID or label to duplicate")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");

        var entry = addressTableService.AddEntry(node.Address, node.DataType, node.CurrentValue ?? "", node.Label + " (copy)");
        var copy = addressTableService.FindNode(entry.Id);
        if (copy is not null)
        {
            copy.AssemblerScript = node.AssemblerScript;
            copy.UserColor = node.UserColor;
            copy.ShowAsHex = node.ShowAsHex;
            copy.ShowAsSigned = node.ShowAsSigned;
            if (node.DropDownList is not null)
                copy.DropDownList = new Dictionary<int, string>(node.DropDownList);
        }
        return Task.FromResult($"Duplicated '{node.Label}' as '{entry.Label}' (ID: {entry.Id}).");
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("dropdown", "enum", "value list", "mapping", "item names")]
    [Description("Configure a dropdown value list for an address table entry. Maps integer values to display names (e.g., item IDs to names).")]
    public Task<string> ConfigureDropDown(
        [Description("Node ID or label")] string nodeId,
        [Description("JSON object mapping integer values to names, e.g. {\"0\":\"Off\",\"1\":\"On\",\"2\":\"Auto\"}")] string optionsJson)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(optionsJson);
            if (parsed is null || parsed.Count == 0)
                return Task.FromResult("Options JSON must be a non-empty object.");

            var dropdown = new Dictionary<int, string>();
            foreach (var (key, val) in parsed)
            {
                if (!int.TryParse(key, out var intKey))
                    return Task.FromResult($"Key '{key}' is not a valid integer.");
                dropdown[intKey] = val;
            }

            node.DropDownList = dropdown;
            return Task.FromResult($"Dropdown configured on '{node.Label}' with {dropdown.Count} options.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Task.FromResult($"Invalid JSON: {ex.Message}");
        }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [SearchHint("export", "json", "address table", "serialize")]
    [Description("Export the entire address table as JSON. Optionally write to a file.")]
    public Task<string> ExportAddressTableJson(
        [Description("File path to write JSON (optional, if omitted returns content)")] string? filePath = null)
    {
        var entries = addressTableService.Roots.ToList();
        if (entries.Count == 0) return Task.FromResult("Address table is empty.");

        var json = System.Text.Json.JsonSerializer.Serialize(
            entries.Select(SerializeNode), _jsonOptsIndented);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (filePath.Contains("..")) return Task.FromResult("Path traversal not allowed.");
            System.IO.File.WriteAllText(filePath, json);
            return Task.FromResult($"Address table exported to {filePath} ({entries.Count} root entries).");
        }
        return Task.FromResult(json.Length > _limits.MaxExportChars
            ? TokenLimits.Truncate(json, _limits.MaxExportChars)
            : json);
    }

    private static object SerializeNode(AddressTableNode node) => new
    {
        node.Id, node.Label, node.Address, type = node.DataType.ToString(),
        node.UserColor, node.ShowAsHex, node.ShowAsSigned,
        isGroup = node.IsGroup, isScript = node.IsScriptEntry,
        dropDown = node.DropDownList,
        children = node.Children.Select(SerializeNode).ToList()
    };

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("import", "json", "address table", "load entries")]
    [Description("Import address table entries from JSON. By default merges with existing entries; set merge=false to replace all.")]
    public Task<string> ImportAddressTableJson(
        [Description("JSON array of address table entries")] string json,
        [Description("Merge with existing entries (true) or replace all (false)")] bool merge = true)
    {
        try
        {
            var entries = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            if (entries.ValueKind != System.Text.Json.JsonValueKind.Array)
                return Task.FromResult("JSON must be an array of entries.");

            if (!merge)
                addressTableService.ClearAll();

            int imported = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                var label = entry.TryGetProperty("label", out var lbl) ? lbl.GetString() ?? "" : "";
                var address = entry.TryGetProperty("address", out var addr) ? addr.GetString() ?? "" : "";

                var dataType = MemoryDataType.Int32;
                if (entry.TryGetProperty("type", out var dt) && Enum.TryParse<MemoryDataType>(dt.GetString(), true, out var parsed))
                    dataType = parsed;

                var added = addressTableService.AddEntry(address, dataType, "", label);
                var node = addressTableService.FindNode(added.Id);
                if (node is not null)
                {
                    node.UserColor = entry.TryGetProperty("userColor", out var clr) ? clr.GetString() : null;
                    node.ShowAsHex = entry.TryGetProperty("showAsHex", out var hex) && hex.GetBoolean();
                    node.ShowAsSigned = entry.TryGetProperty("showAsSigned", out var sgn) && sgn.GetBoolean();
                }
                imported++;
            }

            return Task.FromResult($"Imported {imported} entries{(merge ? " (merged)" : " (replaced)")}.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Task.FromResult($"Invalid JSON: {ex.Message}");
        }
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
            new List<AiActionLogEntry>()).ConfigureAwait(false);
        return $"Session saved: {sessionId}";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List recent investigation sessions.")]
    public async Task<string> ListSessions([Description("Max sessions to list")] int limit = 10)
    {
        if (sessionService is null) return "Session service not available.";
        var sessions = await sessionService.ListSessionsAsync(limit).ConfigureAwait(false);
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
        var result = await sessionService.LoadSessionAsync(sessionId).ConfigureAwait(false);
        if (result is null) return $"Session '{sessionId}' not found.";
        var (entries, processName, processId) = (result.Value.Entries, result.Value.ProcessName, result.Value.ProcessId);
        addressTableService.ImportFlat(entries);
        return $"Loaded session '{sessionId}': {processName} (PID {processId}), {entries.Count} entries restored.";
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Delete a saved session by ID.")]
    public async Task<string> DeleteSession([Description("Session ID to delete")] string sessionId)
    {
        if (sessionService is null) return "Session service not available.";
        try
        {
            await sessionService.DeleteSessionAsync(sessionId).ConfigureAwait(false);
            return $"Session '{sessionId}' deleted.";
        }
        catch (Exception ex)
        {
            return $"DeleteSession failed: {ex.Message}";
        }
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
                ? AiChatStore.ListAll()
                : [AiChatStore.Load(scope)!];

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
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {results.Count} matches for \"{query}\" (showing {capped.Count}):");
        foreach (var (title, chatId, role, ts, snippet) in capped)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{chatId}] {title} — {role} @ {ts:yyyy-MM-dd HH:mm}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    ...{snippet}...");
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
            var sig = await signatureService.GenerateAsync(processId, addr, length).ConfigureAwait(false);
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
            var count = await signatureService.TestUniquenessAsync(processId, moduleName, pattern).ConfigureAwait(false);
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
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Change memory page protection for a region.")]
    public async Task<string> ChangeMemoryProtection(
        [Description("Process ID")] int processId,
        [Description("Memory address as hex (e.g. 0x7FF6A000)")] string address,
        [Description("Region size in bytes")] int size,
        [Description("Protection: ReadWrite, ExecuteReadWrite, ReadOnly, etc.")] string protection)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (memoryProtectionEngine is null) return "Memory protection engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            var prot = Enum.Parse<MemoryProtection>(protection, ignoreCase: true);
            var result = await memoryProtectionEngine.ChangeProtectionAsync(processId, addr, size, prot).ConfigureAwait(false);
            return $"Protection changed at 0x{result.Address:X}: {result.OldProtection} → {result.NewProtection} ({result.Size} bytes)";
        }
        catch (Exception ex) { return $"ChangeMemoryProtection failed: {ex.Message}"; }
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Allocate memory in the target process.")]
    public async Task<string> AllocateMemory(
        [Description("Process ID")] int processId,
        [Description("Size in bytes to allocate")] int size,
        [Description("Protection: ExecuteReadWrite (default), ReadWrite, etc.")] string protection = "ExecuteReadWrite",
        [Description("Preferred address as hex, or 0 for any")] string preferredAddress = "0")
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (memoryProtectionEngine is null) return "Memory protection engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var prot = Enum.Parse<MemoryProtection>(protection, ignoreCase: true);
            var preferred = ParseAddress(preferredAddress);
            var result = await memoryProtectionEngine.AllocateAsync(processId, size, prot, preferred).ConfigureAwait(false);
            return $"Allocated {result.Size} bytes at 0x{result.BaseAddress:X} with {result.Protection}";
        }
        catch (Exception ex) { return $"AllocateMemory failed: {ex.Message}"; }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Free previously allocated memory in the target process.")]
    public async Task<string> FreeMemory(
        [Description("Process ID")] int processId,
        [Description("Address of allocated block as hex")] string address)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (memoryProtectionEngine is null) return "Memory protection engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            var success = await memoryProtectionEngine.FreeAsync(processId, addr).ConfigureAwait(false);
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
            var region = await memoryProtectionEngine.QueryProtectionAsync(processId, addr).ConfigureAwait(false);
            return ToJson(new
            {
                address = $"0x{addr:X}",
                regionBase = $"0x{region.BaseAddress:X}",
                regionSize = region.RegionSize,
                isReadable = region.IsReadable,
                isWritable = region.IsWritable,
                isExecutable = region.IsExecutable,
                pageBase = $"0x{(addr & PageMask):X}"
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

    /// <summary>
    /// Returns true for modules that are OS/runtime/driver DLLs unlikely to contain game logic.
    /// Used to deprioritize system modules in code search results.
    /// </summary>
    private static bool IsSystemModule(string moduleName)
    {
        var name = moduleName.ToLowerInvariant();
        // Windows system directories
        if (name.StartsWith("ntdll", StringComparison.Ordinal) ||
            name.StartsWith("kernel32", StringComparison.Ordinal) ||
            name.StartsWith("kernelbase", StringComparison.Ordinal) ||
            name.StartsWith("user32", StringComparison.Ordinal) ||
            name.StartsWith("advapi32", StringComparison.Ordinal) ||
            name.StartsWith("msvcrt", StringComparison.Ordinal) ||
            name.StartsWith("ucrtbase", StringComparison.Ordinal) ||
            name.StartsWith("ws2_32", StringComparison.Ordinal) ||
            name.StartsWith("combase", StringComparison.Ordinal) ||
            name.StartsWith("rpcrt4", StringComparison.Ordinal) ||
            name.StartsWith("sechost", StringComparison.Ordinal) ||
            name.StartsWith("bcrypt", StringComparison.Ordinal) ||
            name.StartsWith("crypt32", StringComparison.Ordinal) ||
            name.StartsWith("ole32", StringComparison.Ordinal) ||
            name.StartsWith("gdi32", StringComparison.Ordinal) ||
            name.StartsWith("shell32", StringComparison.Ordinal) ||
            name.StartsWith("mswsock", StringComparison.Ordinal) ||
            name.StartsWith("clr", StringComparison.Ordinal))
            return true;
        // GPU drivers
        if (name.StartsWith("nvwgf", StringComparison.Ordinal) ||
            name.StartsWith("nvapi", StringComparison.Ordinal) ||
            name.StartsWith("nvgpu", StringComparison.Ordinal) ||
            name.StartsWith("amdxc", StringComparison.Ordinal) ||
            name.StartsWith("atiuxp", StringComparison.Ordinal) ||
            name.StartsWith("d3d", StringComparison.Ordinal) ||
            name.StartsWith("dxgi", StringComparison.Ordinal) ||
            name.StartsWith("vulkan", StringComparison.Ordinal))
            return true;
        // .NET runtime
        if (name.StartsWith("coreclr", StringComparison.Ordinal) ||
            name.StartsWith("hostfxr", StringComparison.Ordinal) ||
            name.StartsWith("hostpolicy", StringComparison.Ordinal) ||
            name.StartsWith("system.private", StringComparison.Ordinal) ||
            name.StartsWith("system.runtime", StringComparison.Ordinal))
            return true;
        return false;
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
                var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
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
            var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
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

    private static nuint ParseAddress(string address) => AddressTableService.ParseAddress(address);

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
            var entryPage = node.ResolvedAddress.Value & PageMask;
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
                var read = await engineFacade.ReadMemoryAsync(processId, addr, byteCount).ConfigureAwait(false);
                var bytes = read.Bytes.ToArray();
                var hex = string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

                bool changed = prevBytes is not null && !bytes.SequenceEqual(prevBytes);
                string? prevHex = prevBytes is not null ? string.Join(" ", prevBytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) : null;

                snapshots.Add(new
                {
                    sample = i + 1,
                    timestamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    hex,
                    changed,
                    previousHex = changed ? prevHex : null,
                    int32Value = byteCount >= 4 ? BitConverter.ToInt32(bytes.Take(4).ToArray()) : (int?)null,
                    floatValue = byteCount >= 4 ? BitConverter.ToSingle(bytes.Take(4).ToArray()) : (float?)null
                });

                prevBytes = bytes;

                if (i < sampleCount - 1)
                    await Task.Delay(sampleDelayMs).ConfigureAwait(false);
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
    public static string BeginTransaction([Description("Name for this transaction group")] string name = "auto")
    {
        var groupId = $"txn-{name}-{Guid.NewGuid():N}"[..24];
        return JsonSerializer.Serialize(new { groupId, status = "open", message = $"Transaction group '{groupId}' created. Pass this groupId to subsequent BP/hook operations." }, _jsonOpts);
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Rollback all operations in a transaction group, restoring original state in reverse order.")]
    public async Task<string> RollbackTransaction([Description("Transaction group ID")] string groupId)
    {
        if (operationJournal is null) return "Operation journal not available.";
        var result = await operationJournal.RollbackGroupAsync(groupId).ConfigureAwait(false);
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
                e.Mode, e.GroupId, status = e.Status.ToString(), timestamp = e.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
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
                var hooks = await codeCaveEngine.ListHooksAsync(processId).ConfigureAwait(false);
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
                var bps = await breakpointService.ListBreakpointsAsync(processId).ConfigureAwait(false);
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

    private static readonly JsonSerializerOptions _jsonOptsIndented = new()
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
            storedAt = r.StoredAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        }));
    }

    // ── Symbol resolution tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Load debug symbols (PDB/exports) for a module. Must be called before ResolveAddressToSymbol can resolve addresses in that module.")]
    public async Task<string> LoadSymbolsForModule(
        [Description("Process ID")] int processId,
        [Description("Module name (e.g. 'GameAssembly.dll')")] string moduleName)
    {
        if (symbolEngine is null) return "Symbol engine not available.";
        try
        {
            var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
            var mod = attachment.Modules.FirstOrDefault(m =>
                m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            if (mod is null)
            {
                var available = string.Join(", ", attachment.Modules.Select(m => m.Name).Take(10));
                return $"Module '{moduleName}' not found. Loaded modules: {available}";
            }

            var loaded = await symbolEngine.LoadSymbolsForModuleAsync(processId, mod.Name, mod.BaseAddress, mod.SizeBytes).ConfigureAwait(false);
            return loaded
                ? $"Symbols loaded for {mod.Name} (base 0x{mod.BaseAddress:X}, size {mod.SizeBytes})."
                : $"No symbols found for {mod.Name} (base 0x{mod.BaseAddress:X}).";
        }
        catch (Exception ex)
        {
            return $"LoadSymbolsForModule failed: {ex.Message}";
        }
    }

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Resolve a memory address to its symbol name (e.g. 'GameAssembly.dll!Player::TakeDamage+0x1A'). " +
        "Requires LoadSymbolsForModule to have been called first for the relevant module.")]
    public string ResolveAddressToSymbol(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex string)")] string address)
    {
        if (symbolEngine is null) return "Symbol engine not available.";
        try
        {
            var addr = ParseAddress(address);
            var info = symbolEngine.ResolveAddress(addr);
            return info is not null
                ? info.DisplayName
                : $"No symbol found for address 0x{addr:X}";
        }
        catch (Exception ex)
        {
            return $"ResolveAddressToSymbol failed: {ex.Message}";
        }
    }

    // ── WP10: New AI Tools for UI Parity ──

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Modify an existing address table entry's address, data type, value, or display flags.")]
    public Task<string> ModifyAddressTableEntry(
        [Description("Node ID or label")] string nodeId,
        [Description("New address (hex). Leave null to keep current.")] string? newAddress = null,
        [Description("New data type (Int32, Float, Int64, Double, Byte, Int16, String). Leave null to keep current.")] string? newType = null,
        [Description("New value to write. Leave null to keep current.")] string? newValue = null,
        [Description("Show value as hex (true/false). Leave null to keep current.")] bool? showAsHex = null,
        [Description("Show value as signed (true/false). Leave null to keep current.")] bool? showAsSigned = null)
    {
        var node = ResolveNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");

        var changes = new List<string>();
        if (newAddress is not null) { node.Address = newAddress; changes.Add($"address→{newAddress}"); }
        if (newType is not null && Enum.TryParse<MemoryDataType>(newType, true, out var dt))
        {
            node.DataType = dt; changes.Add($"type→{dt}");
        }
        else if (newType is not null) return Task.FromResult($"Unknown data type '{newType}'. Use: Int32, Float, Int64, Double, Byte, Int16, String.");
        if (newValue is not null) { node.CurrentValue = newValue; changes.Add($"value→{newValue}"); }
        if (showAsHex.HasValue) { node.ShowAsHex = showAsHex.Value; changes.Add($"hex→{showAsHex.Value}"); }
        if (showAsSigned.HasValue) { node.ShowAsSigned = showAsSigned.Value; changes.Add($"signed→{showAsSigned.Value}"); }

        return Task.FromResult(changes.Count > 0
            ? $"Modified '{node.Label}': {string.Join(", ", changes)}"
            : "No changes specified.");
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Assemble an instruction and write the bytes at the given address. Uses Keystone assembler.")]
    public async Task<string> AssembleInstruction(
        [Description("Process ID")] int processId,
        [Description("Address to write the instruction at (hex)")] string address,
        [Description("Assembly instruction in MASM/Intel syntax (e.g., 'mov eax, 1' or 'nop')")] string instruction)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (autoAssemblerEngine is null) return "Auto assembler engine not available.";
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

        var script = $"[ENABLE]\n{address}:\n{instruction}\n[DISABLE]\n";
        var result = await autoAssemblerEngine.EnableAsync(processId, script).ConfigureAwait(false);
        return result.Success
            ? $"Assembled and wrote at {address}: {instruction} ({result.Patches.Count} patch(es))"
            : $"Assembly failed: {result.Error}";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove all active breakpoints at once.")]
    public async Task<string> RemoveAllBreakpoints(
        [Description("Process ID")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (breakpointService is null) return "Breakpoint service not available.";

        var bps = await breakpointService.ListBreakpointsAsync(processId).ConfigureAwait(false);
        if (bps.Count == 0) return "No active breakpoints to remove.";

        int removed = 0;
        foreach (var bp in bps)
        {
            if (await breakpointService.RemoveBreakpointAsync(processId, bp.Id).ConfigureAwait(false))
                removed++;
        }
        return $"Removed {removed}/{bps.Count} breakpoints.";
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Reset scan state, clearing all results and the undo stack. Use before starting a completely new scan.")]
    public Task<string> ResetScan()
    {
        scanService.ResetScan();
        return Task.FromResult("Scan state reset. Ready for a new scan.");
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Reset the Lua script engine, clearing all global state and registered functions.")]
    public Task<string> ResetLuaEngine()
    {
        if (luaEngine is null) return Task.FromResult("Lua engine is not available.");
        luaEngine.Reset();
        return Task.FromResult("Lua engine reset. All global state cleared.");
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Fill a memory region with NOP (0x90) bytes. Useful for patching out instructions.")]
    public async Task<string> NopRegion(
        [Description("Process ID")] int processId,
        [Description("Start address (hex)")] string address,
        [Description("Number of bytes to NOP (1-64)")] int length)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
        length = Math.Clamp(length, 1, 64);

        var addr = ParseAddress(address);
        var nops = new byte[length];
        Array.Fill(nops, (byte)0x90);
        var written = await engineFacade.WriteBytesAsync(processId, addr, nops).ConfigureAwait(false);
        return written > 0
            ? $"Wrote {written} NOP bytes at 0x{addr:X}."
            : $"Failed to write NOP bytes at 0x{addr:X}.";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Increment or decrement an address table entry's numeric value by a delta. Reads current value, adds delta, writes back.")]
    public async Task<string> AdjustValue(
        [Description("Process ID")] int processId,
        [Description("Node ID or label")] string nodeId,
        [Description("Amount to add (positive) or subtract (negative)")] double delta)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
        var node = ResolveNode(nodeId);
        if (node is null) return $"Node '{nodeId}' not found.";

        var addr = ParseAddress(node.Address);
        var mem = await engineFacade.ReadMemoryAsync(processId, addr, 8).ConfigureAwait(false);
        var raw = mem.Bytes is byte[] arr ? arr : mem.Bytes.ToArray();
        if (raw.Length < 4) return "Could not read current value.";

        byte[] newBytes;
        string displayOld, displayNew;
        switch (node.DataType)
        {
            case MemoryDataType.Float:
                var fVal = BitConverter.ToSingle(raw, 0);
                var fNew = fVal + (float)delta;
                displayOld = fVal.ToString("F2", CultureInfo.InvariantCulture); displayNew = fNew.ToString("F2", CultureInfo.InvariantCulture);
                newBytes = BitConverter.GetBytes(fNew);
                break;
            case MemoryDataType.Double:
                var dVal = BitConverter.ToDouble(raw, 0);
                var dNew = dVal + delta;
                displayOld = dVal.ToString("F2", CultureInfo.InvariantCulture); displayNew = dNew.ToString("F2", CultureInfo.InvariantCulture);
                newBytes = BitConverter.GetBytes(dNew);
                break;
            case MemoryDataType.Int64:
                var lVal = BitConverter.ToInt64(raw, 0);
                var lNew = lVal + (long)delta;
                displayOld = lVal.ToString(CultureInfo.InvariantCulture); displayNew = lNew.ToString(CultureInfo.InvariantCulture);
                newBytes = BitConverter.GetBytes(lNew);
                break;
            default: // Int32, Byte, Int16 — treat as Int32
                var iVal = BitConverter.ToInt32(raw, 0);
                var iNew = iVal + (int)delta;
                displayOld = iVal.ToString(CultureInfo.InvariantCulture); displayNew = iNew.ToString(CultureInfo.InvariantCulture);
                newBytes = BitConverter.GetBytes(iNew);
                break;
        }

        var written = await engineFacade.WriteBytesAsync(processId, addr, newBytes).ConfigureAwait(false);
        return written > 0
            ? $"Adjusted '{node.Label}': {displayOld} → {displayNew} (delta: {delta:+0;-0})"
            : $"Failed to write adjusted value at 0x{addr:X}.";
    }
}
