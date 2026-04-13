using System.Collections.Concurrent;
using System.Globalization;
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
            _singleHitBreakpoints.Add(bp.Id);
        return ToOverview(bp);
    }

    /// <summary>IDs of breakpoints that should auto-remove after first hit.</summary>
    internal HashSet<string> SingleHitBreakpoints => _singleHitBreakpoints;
    private readonly HashSet<string> _singleHitBreakpoints = new();

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
