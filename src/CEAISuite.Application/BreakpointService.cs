using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

public sealed record BreakpointOverview(
    string Id,
    string Address,
    string Type,
    string HitAction,
    bool IsEnabled,
    int HitCount,
    string Mode = "Hardware",
    string? Condition = null,
    int? ThreadFilter = null);

/// <summary>C3: Filter for hit log queries.</summary>
public sealed record HitLogFilter(
    int? ThreadId = null,
    DateTimeOffset? MinTimestamp = null,
    DateTimeOffset? MaxTimestamp = null);

/// <summary>C3: Aggregated statistics for a breakpoint's hit log.</summary>
public sealed record HitStatistics(
    int TotalHits,
    double HitsPerSecond,
    int UniqueThreads,
    IReadOnlyList<string> TopAddresses,
    DateTimeOffset? FirstHit,
    DateTimeOffset? LastHit);

/// <summary>C3: Export format for hit logs.</summary>
public enum HitLogExportFormat { Csv, Json }

/// <summary>B3: A named group of breakpoints for atomic enable/disable operations.</summary>
public sealed record BreakpointGroup(
    string GroupId,
    string Name,
    IReadOnlyList<string> BreakpointIds);

public sealed record BreakpointHitOverview(
    string BreakpointId,
    string Address,
    int ThreadId,
    string Timestamp,
    IReadOnlyDictionary<string, string> Registers);

/// <summary>
/// Application-level service wrapping IBreakpointEngine for the UI and AI operator.
/// Supports optional Lua callback registration for breakpoint hits.
/// </summary>
public sealed class BreakpointService(
    IBreakpointEngine? breakpointEngine,
    ILuaScriptEngine? luaScriptEngine = null,
    ILogger<BreakpointService>? logger = null)
{
    private bool IsAvailable => breakpointEngine is not null;

    private readonly ConcurrentDictionary<string, BreakpointLifecycleStatus> _lifecycleStatuses = new();
    private readonly ConcurrentDictionary<string, string> _luaCallbacks = new(); // bpId → luaFuncName
    private readonly ConcurrentDictionary<string, int> _lastProcessedHitCount = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveCallbackFailures = new(); // A6: per-BP failure counter
    private const int MaxConsecutiveCallbackFailures = 3;

    /// <summary>Register a Lua function to be called when a breakpoint is hit.</summary>
    public void RegisterLuaCallback(string breakpointId, string luaFunctionName)
    {
        _luaCallbacks[breakpointId] = luaFunctionName;
        _lastProcessedHitCount[breakpointId] = 0;
        luaScriptEngine?.RegisterBreakpointCallback(luaFunctionName);
    }

    /// <summary>Unregister a Lua callback for a breakpoint.</summary>
    public void UnregisterLuaCallback(string breakpointId)
    {
        _luaCallbacks.TryRemove(breakpointId, out _);
        _lastProcessedHitCount.TryRemove(breakpointId, out _);
    }

    /// <summary>Check if any breakpoints have pending Lua callbacks and invoke them.</summary>
    private async Task InvokePendingLuaCallbacksAsync(
        string breakpointId,
        IReadOnlyList<BreakpointHitEvent> hits,
        CancellationToken ct)
    {
        if (luaScriptEngine is null || !_luaCallbacks.TryGetValue(breakpointId, out var funcName))
            return;

        var lastProcessed = _lastProcessedHitCount.GetValueOrDefault(breakpointId, 0);
        var newHits = hits.Skip(lastProcessed).ToList();
        if (newHits.Count == 0) return;

        foreach (var hit in newHits)
        {
            try
            {
                await luaScriptEngine.InvokeBreakpointCallbackAsync(funcName, hit, ct).ConfigureAwait(false);
                _consecutiveCallbackFailures.AddOrUpdate(breakpointId, 0, (_, _) => 0); // atomic reset on success
            }
            catch (OperationCanceledException)
            {
                throw; // propagate cancellation
            }
            catch (Exception ex)
            {
                var failures = _consecutiveCallbackFailures.AddOrUpdate(breakpointId, 1, (_, v) => v + 1);
                logger?.LogWarning(ex, "Lua callback '{FuncName}' for BP {BreakpointId} threw (failure {Count}/{Max})",
                    funcName, breakpointId, failures, MaxConsecutiveCallbackFailures);

                if (failures >= MaxConsecutiveCallbackFailures)
                {
                    UnregisterLuaCallback(breakpointId);
                    logger?.LogError("Auto-unregistered Lua callback '{FuncName}' for BP {BreakpointId} after {Max} consecutive failures",
                        funcName, breakpointId, MaxConsecutiveCallbackFailures);
                    break; // stop processing hits for this BP
                }
            }
        }
        _lastProcessedHitCount[breakpointId] = hits.Count;
    }

    public void UpdateLifecycleStatus(string bpId, BreakpointLifecycleStatus status)
        => _lifecycleStatuses[bpId] = status;

    public BreakpointLifecycleStatus GetLifecycleStatus(string bpId)
        => _lifecycleStatuses.TryGetValue(bpId, out var status) ? status : BreakpointLifecycleStatus.Armed;

    public static IReadOnlyList<BreakpointModeCapabilities> GetModeCapabilities()=> new[]
    {
        new BreakpointModeCapabilities(BreakpointMode.Stealth,
            SupportsExecuteHook: true, SupportsDataWriteWatch: false,
            RequiresDebugger: false, UsesPageProtection: false, UsesThreadSuspend: false,
            StabilityTier: "Stable", Description: "Code cave JMP detour — no debugger, safest for execute hooks"),
        new BreakpointModeCapabilities(BreakpointMode.PageGuard,
            SupportsExecuteHook: true, SupportsDataWriteWatch: true,
            RequiresDebugger: true, UsesPageProtection: true, UsesThreadSuspend: false,
            StabilityTier: "Medium", Description: "PAGE_GUARD flag — catches memory access via guard page faults"),
        new BreakpointModeCapabilities(BreakpointMode.Hardware,
            SupportsExecuteHook: true, SupportsDataWriteWatch: true,
            RequiresDebugger: true, UsesPageProtection: false, UsesThreadSuspend: true,
            StabilityTier: "Medium", Description: "DR0-DR3 hardware debug registers — limited to 4 simultaneous"),
        new BreakpointModeCapabilities(BreakpointMode.Software,
            SupportsExecuteHook: true, SupportsDataWriteWatch: false,
            RequiresDebugger: true, UsesPageProtection: false, UsesThreadSuspend: false,
            StabilityTier: "Stable", Description: "INT3 byte patch — most compatible for code execution monitoring"),
        new BreakpointModeCapabilities(BreakpointMode.Auto,
            SupportsExecuteHook: true, SupportsDataWriteWatch: true,
            RequiresDebugger: true, UsesPageProtection: false, UsesThreadSuspend: true,
            StabilityTier: "Medium", Description: "Engine picks least intrusive mode for the request"),
    };

    public async Task<BreakpointOverview> SetBreakpointAsync(
        int processId,
        string addressText,
        BreakpointType type = BreakpointType.Software,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var address = ParseAddress(addressText);
        var bp = await breakpointEngine!.SetBreakpointAsync(processId, address, type, action, cancellationToken).ConfigureAwait(false);
        return ToOverview(bp);
    }

    public async Task<BreakpointOverview> SetBreakpointAsync(
        int processId,
        string addressText,
        BreakpointType type,
        BreakpointMode mode,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        bool singleHit = false,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var address = ParseAddress(addressText);
        var bp = await breakpointEngine!.SetBreakpointAsync(processId, address, type, mode, action, singleHit, cancellationToken).ConfigureAwait(false);
        if (singleHit)
            _singleHitBreakpoints.TryAdd(bp.Id, 0);
        return ToOverview(bp);
    }

    /// <summary>IDs of breakpoints that should auto-remove after first hit. Thread-safe.</summary>
    internal ConcurrentDictionary<string, byte> SingleHitBreakpoints => _singleHitBreakpoints;
    private readonly ConcurrentDictionary<string, byte> _singleHitBreakpoints = new();

    public async Task<bool> RemoveBreakpointAsync(
        int processId,
        string breakpointId,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        return await breakpointEngine!.RemoveBreakpointAsync(processId, breakpointId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BreakpointOverview>> ListBreakpointsAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var breakpoints = await breakpointEngine!.ListBreakpointsAsync(processId, cancellationToken).ConfigureAwait(false);
        return breakpoints.Select(ToOverview).ToArray();
    }

    public async Task<IReadOnlyList<BreakpointHitOverview>> GetHitLogAsync(
        string breakpointId,
        int maxEntries = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var hits = await breakpointEngine!.GetHitLogAsync(breakpointId, maxEntries, cancellationToken).ConfigureAwait(false);

        // Invoke pending Lua callbacks for new hits
        await InvokePendingLuaCallbacksAsync(breakpointId, hits, cancellationToken).ConfigureAwait(false);

        return hits.Select(h => new BreakpointHitOverview(
            h.BreakpointId,
            $"0x{h.Address:X}",
            h.ThreadId,
            h.TimestampUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            h.RegisterSnapshot)).ToArray();
    }

    // ── C3: Hit Log Enhancements ──

    /// <summary>Get filtered hit log for a breakpoint.</summary>
    public async Task<IReadOnlyList<BreakpointHitOverview>> GetFilteredHitLogAsync(
        string breakpointId,
        HitLogFilter filter,
        int maxEntries = 500,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var hits = await breakpointEngine!.GetHitLogAsync(breakpointId, maxEntries, cancellationToken).ConfigureAwait(false);

        IEnumerable<BreakpointHitEvent> filtered = hits;
        if (filter.ThreadId.HasValue)
            filtered = filtered.Where(h => h.ThreadId == filter.ThreadId.Value);
        if (filter.MinTimestamp.HasValue)
            filtered = filtered.Where(h => h.TimestampUtc >= filter.MinTimestamp.Value);
        if (filter.MaxTimestamp.HasValue)
            filtered = filtered.Where(h => h.TimestampUtc <= filter.MaxTimestamp.Value);

        return filtered.Select(h => new BreakpointHitOverview(
            h.BreakpointId, $"0x{h.Address:X}", h.ThreadId,
            h.TimestampUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            h.RegisterSnapshot)).ToArray();
    }

    /// <summary>Compute aggregated statistics for a breakpoint's hit log.</summary>
    public async Task<HitStatistics> GetHitStatisticsAsync(
        string breakpointId,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var hits = await breakpointEngine!.GetHitLogAsync(breakpointId, MaxHitLogEntries, cancellationToken).ConfigureAwait(false);

        if (hits.Count == 0)
            return new HitStatistics(0, 0, 0, [], null, null);

        var first = hits[0].TimestampUtc;
        var last = hits[^1].TimestampUtc;
        var duration = (last - first).TotalSeconds;
        var hitsPerSecond = duration > 0 ? hits.Count / duration : hits.Count;

        var topAddresses = hits
            .GroupBy(h => h.Address)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"0x{g.Key:X} ({g.Count()})")
            .ToList();

        return new HitStatistics(
            hits.Count,
            Math.Round(hitsPerSecond, 2),
            hits.Select(h => h.ThreadId).Distinct().Count(),
            topAddresses,
            first,
            last);
    }

    private const int MaxHitLogEntries = 500;
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    /// <summary>Export hit log to a file in CSV or JSON format.</summary>
    public async Task<string> ExportHitLogAsync(
        string breakpointId,
        string filePath,
        HitLogExportFormat format,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var hits = await breakpointEngine!.GetHitLogAsync(breakpointId, MaxHitLogEntries, cancellationToken).ConfigureAwait(false);

        if (format == HitLogExportFormat.Csv)
        {
            var sb = new StringBuilder();
            sb.AppendLine("BreakpointId,Address,ThreadId,Timestamp");
            foreach (var h in hits)
                sb.AppendLine(CultureInfo.InvariantCulture, $"{h.BreakpointId},0x{h.Address:X},{h.ThreadId},{h.TimestampUtc:O}");
            await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var data = hits.Select(h => new
            {
                h.BreakpointId,
                Address = $"0x{h.Address:X}",
                h.ThreadId,
                Timestamp = h.TimestampUtc.ToString("O"),
                Registers = h.RegisterSnapshot
            });
            var json = JsonSerializer.Serialize(data, IndentedJsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
        }

        return $"Exported {hits.Count} hits to {filePath}";
    }

    /// <summary>Set a conditional breakpoint with an expression and optional thread filter.</summary>
    public async Task<BreakpointOverview> SetConditionalBreakpointAsync(
        int processId,
        string addressText,
        BreakpointType type,
        BreakpointCondition condition,
        BreakpointMode mode = BreakpointMode.Auto,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        int? threadFilter = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var address = ParseAddress(addressText);
        var bp = await breakpointEngine!.SetConditionalBreakpointAsync(
            processId, address, type, condition, mode, action, threadFilter, cancellationToken).ConfigureAwait(false);
        return ToOverview(bp);
    }

    /// <summary>Trace execution from a breakpoint address, recording each instruction.</summary>
    public async Task<TraceResult> TraceFromBreakpointAsync(
        int processId,
        string addressText,
        int maxInstructions = 500,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var address = ParseAddress(addressText);
        return await breakpointEngine!.TraceFromBreakpointAsync(
            processId, address, maxInstructions, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Emergency: restore all page guard protections without locks. For crash recovery.</summary>
    public async Task<int> EmergencyRestorePageProtectionAsync(int processId)
    {
        EnsureAvailable();
        return await breakpointEngine!.EmergencyRestorePageProtectionAsync(processId).ConfigureAwait(false);
    }

    /// <summary>Force detach debugger and clean up. Nuclear option for hung processes.</summary>
    public async Task ForceDetachAndCleanupAsync(int processId)
    {
        EnsureAvailable();
        await breakpointEngine!.ForceDetachAndCleanupAsync(processId).ConfigureAwait(false);
    }

    // ── B3: Breakpoint Groups ──

    private readonly ConcurrentDictionary<string, BreakpointGroup> _groups = new();

    /// <summary>Create a named group of breakpoints for atomic enable/disable.</summary>
    public BreakpointGroup CreateGroup(string name, IEnumerable<string> breakpointIds)
    {
        var groupId = $"grp-{Guid.NewGuid().ToString("N")[..8]}";
        var group = new BreakpointGroup(groupId, name, breakpointIds.ToList());
        _groups[groupId] = group;
        return group;
    }

    /// <summary>Enable all breakpoints in a group by re-setting them.</summary>
    public async Task<int> EnableGroupAsync(int processId, string groupId, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (!_groups.TryGetValue(groupId, out var group)) return 0;

        var enabled = 0;
        var bps = await breakpointEngine!.ListBreakpointsAsync(processId, ct).ConfigureAwait(false);
        foreach (var bpId in group.BreakpointIds)
        {
            var bp = bps.FirstOrDefault(b => b.Id == bpId);
            if (bp is not null && !bp.IsEnabled)
            {
                // Re-set the breakpoint to re-enable it
                await breakpointEngine.SetBreakpointAsync(processId, bp.Address, bp.Type, bp.Mode,
                    bp.HitAction, false, ct).ConfigureAwait(false);
                enabled++;
            }
        }
        return enabled;
    }

    /// <summary>Disable all breakpoints in a group by removing them.</summary>
    public async Task<int> DisableGroupAsync(int processId, string groupId, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (!_groups.TryGetValue(groupId, out var group)) return 0;

        var disabled = 0;
        foreach (var bpId in group.BreakpointIds)
        {
            if (await breakpointEngine!.RemoveBreakpointAsync(processId, bpId, ct).ConfigureAwait(false))
                disabled++;
        }
        return disabled;
    }

    /// <summary>Remove a group definition (does not remove the breakpoints themselves).</summary>
    public bool RemoveGroup(string groupId) => _groups.TryRemove(groupId, out _);

    /// <summary>List all defined groups.</summary>
    public IReadOnlyList<BreakpointGroup> ListGroups() => _groups.Values.ToList();

    /// <summary>Get a specific group by ID.</summary>
    public BreakpointGroup? GetGroup(string groupId) =>
        _groups.TryGetValue(groupId, out var group) ? group : null;

    // ── B2: Region Breakpoints ──

    /// <summary>Set a breakpoint spanning a memory region. Auto-selects PAGE_GUARD for regions > 8 bytes.</summary>
    public async Task<IReadOnlyList<BreakpointOverview>> SetRegionBreakpointAsync(
        int processId,
        string startAddressText,
        int length,
        BreakpointHitAction action = BreakpointHitAction.LogAndContinue,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");
        if (length > 64 * 1024) throw new ArgumentOutOfRangeException(nameof(length), $"Region size {length} exceeds maximum of 65536 bytes.");
        var address = ParseAddress(startAddressText);
        var bps = await breakpointEngine!.SetRegionBreakpointAsync(processId, address, length, action, cancellationToken).ConfigureAwait(false);
        return bps.Select(ToOverview).ToArray();
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                "Breakpoint engine is not available. Ensure a process is attached and the debug engine is initialized.");
    }

    private static BreakpointOverview ToOverview(BreakpointDescriptor bp) =>
        new(bp.Id, $"0x{bp.Address:X}", bp.Type.ToString(), bp.HitAction.ToString(), bp.IsEnabled, bp.HitCount,
            bp.Mode.ToString(), bp.Condition?.Expression, bp.ThreadFilter);

    private static nuint ParseAddress(string addressText) => AddressTableService.ParseAddress(addressText);
}
