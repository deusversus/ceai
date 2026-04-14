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
    // ── Breakpoint tools ──

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [Description("Set a breakpoint. Modes: Auto, Stealth, PageGuard, Hardware, Software.")]
    public async Task<string> SetBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex or decimal)")] string address,
        [Description("Type: Software, HardwareExecute, HardwareWrite, HardwareReadWrite")] string type = "Software",
        [Description("Hit action: Break, Log, LogAndContinue")] string hitAction = "LogAndContinue",
        [Description("Mode: Auto, Stealth, PageGuard, Hardware, Software")] string mode = "Auto",
        [Description("Auto-remove after first hit")] bool singleHit = false,
        [Description("Condition expression (e.g., 'RAX == 0x100', '> 10' for hit count). Omit for unconditional.")] string? condition = null,
        [Description("Condition type: RegisterCompare, MemoryCompare, HitCount (default RegisterCompare)")] string? conditionType = null,
        [Description("Thread ID filter — only break on this thread (0 or omit for all threads)")] int? threadFilter = null)
    {
        try
        {
            // If a condition or threadFilter is provided, delegate to conditional breakpoint path
            if (!string.IsNullOrWhiteSpace(condition) || (threadFilter.HasValue && threadFilter.Value != 0))
            {
                return await SetConditionalBreakpoint(
                    processId, address, type,
                    condition ?? "",
                    conditionType ?? "RegisterCompare",
                    mode, threadFilter ?? 0).ConfigureAwait(false);
            }

            var pidError = ValidateDestructiveProcessId(processId);
            if (pidError is not null) return pidError;
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

                // ── Executable-memory safety gate ──
                // Stealth hooks inject a JMP detour into the target address.
                // If the target is in a non-executable region (heap data, stack, etc.)
                // the detour overwrites live data and crashes/freezes the process.
                if (memoryProtectionEngine is not null)
                {
                    var region = await memoryProtectionEngine.QueryProtectionAsync(processId, stealthAddr).ConfigureAwait(false);
                    if (!region.IsExecutable)
                    {
                        return $"❌ Stealth hook REJECTED: Target address 0x{stealthAddr:X} is in a non-executable memory region " +
                               $"(R={region.IsReadable}, W={region.IsWritable}, X={region.IsExecutable}). " +
                               $"Stealth (code cave) hooks inject a JMP detour and can only target executable code.\n" +
                               $"Recommended alternatives:\n" +
                               $"  1. Use mode=Hardware with type=HardwareWrite to watch data writes via CPU debug registers\n" +
                               $"  2. Use mode=PageGuard for page-level write monitoring\n" +
                               $"  3. Use FindWritersToOffset or TraceFieldWriters to find the code that writes to this data, then hook that code instead";
                    }
                }

                var result = await codeCaveEngine.InstallHookAsync(processId, stealthAddr).ConfigureAwait(false);
                if (!result.Success) return $"Stealth hook failed: {result.ErrorMessage}";
                var stealthMsg = $"Stealth code cave hook installed at 0x{result.Hook!.OriginalAddress:X} (ID: {result.Hook.Id}, cave at 0x{result.Hook.CaveAddress:X}). No debugger attached — game-safe.";
                if (watchdogService is not null)
                {
                    var hookId = result.Hook.Id;
                    watchdogService.StartMonitoring(processId, hookId, stealthAddr, "CodeCaveHook", "Stealth",
                        async () => await codeCaveEngine.RemoveHookAsync(processId, hookId).ConfigureAwait(false));
                    if (watchdogService.IsUnsafe(stealthAddr, "Stealth"))
                        stealthMsg += "\n⚠️ WARNING: This address+Stealth previously caused a process freeze. Watchdog is monitoring.";
                    else
                        stealthMsg += "\n🛡️ Watchdog monitoring active — will auto-rollback if process becomes unresponsive.";
                }
                operationJournal?.RecordOperation(
                    result.Hook.Id, "CodeCaveHook", stealthAddr, "Stealth", groupId: null,
                    async () => await codeCaveEngine.RemoveHookAsync(processId, result.Hook.Id).ConfigureAwait(false));
                return stealthMsg;
            }

            var parsedAddr = ParseAddress(address);
            var modeStr = bpMode.ToString();

            // ── PageGuard co-tenancy gate ──
            // Hard-reject PageGuard on pages shared with many address table entries.
            // Hot heap pages cause guard-page storms that can wedge the target process.
            const int CoTenancyThreshold = 10;
            if (bpMode == BreakpointMode.PageGuard)
            {
                var targetPage = parsedAddr & PageMask;
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

            // For PageGuard mode, use transactional install with rollback (guard storms can wedge the target).
            // Hardware mode uses CPU debug registers — safe at OS level, no memory/page modifications.
            // Hardware BPs generate frequent debug events on hot addresses which can make the process
            // *appear* unresponsive to the watchdog (WM_NULL timeouts, stalled CPU time samples)
            // even though it's running fine. So we skip the transactional verify for Hardware mode.
            if (watchdogService is not null && bpMode is BreakpointMode.PageGuard)
            {
                BreakpointOverview? txBp = null;
                var txResult = await watchdogService.InstallWithTransactionAsync(
                    processId, $"bp-{Guid.NewGuid():N}", parsedAddr, "Breakpoint", modeStr,
                    installAction: async () =>
                    {
                        txBp = await breakpointService.SetBreakpointAsync(processId, address, bpType, bpMode, bpAction, singleHit: singleHit).ConfigureAwait(false);
                    },
                    rollbackAction: async () => txBp is not null && await breakpointService.RemoveBreakpointAsync(processId, txBp.Id).ConfigureAwait(false)).ConfigureAwait(false);

                if (!txResult.Success)
                {
                    // If rollback failed, the debug session may still be attached — force-detach
                    // so future breakpoint attempts don't fail with "Unable to attach debugger".
                    if (txResult.Message.Contains("FAILED", StringComparison.Ordinal))
                    {
                        try { await breakpointService.ForceDetachAndCleanupAsync(processId).ConfigureAwait(false); }
                        catch (Exception detachEx) { logger?.LogWarning(detachEx, "Force-detach after failed rollback also failed for PID {ProcessId}", processId); }
                    }
                    return $"⚠️ Transactional install failed at {txResult.Phase}: {txResult.Message}";
                }

                var msg = $"Breakpoint {txBp!.Id} set at {txBp.Address} (type: {txBp.Type}, mode: {bpMode}, action: {txBp.HitAction})";
                if (singleHit) msg += " [SINGLE-HIT: will auto-remove after first trigger]";
                if (wasDowngraded) msg += "\n⚠️ Mode was auto-downgraded from Stealth→PageGuard. Stealth (code cave) only works on executable code, not data write targets.";
                if (watchdogService.IsUnsafe(parsedAddr, modeStr))
                    msg += $"\n⚠️ WARNING: This address+{modeStr} previously caused a process freeze. Watchdog is monitoring.";
                msg += "\n✅ Transaction committed. Watchdog monitoring active.";
                operationJournal?.RecordOperation(
                    txBp!.Id, "Breakpoint", parsedAddr, modeStr, groupId: null,
                    async () => await breakpointService.RemoveBreakpointAsync(processId, txBp.Id).ConfigureAwait(false));
                return msg;
            }

            var bp = await breakpointService.SetBreakpointAsync(processId, address, bpType, bpMode, bpAction, singleHit: singleHit).ConfigureAwait(false);
            var msg2 = $"Breakpoint {bp.Id} set at {bp.Address} (type: {bp.Type}, mode: {bpMode}, action: {bp.HitAction})";
            if (singleHit) msg2 += " [SINGLE-HIT: will auto-remove after first trigger]";
            if (wasDowngraded) msg2 += "\n⚠️ Mode was auto-downgraded from Stealth→PageGuard. Stealth (code cave) only works on executable code, not data write targets.";
            if (watchdogService is not null)
            {
                var bpId = bp.Id;
                watchdogService.StartMonitoring(processId, bpId, parsedAddr, "Breakpoint", modeStr,
                    async () => await breakpointService.RemoveBreakpointAsync(processId, bpId).ConfigureAwait(false));
                if (watchdogService.IsUnsafe(parsedAddr, modeStr))
                    msg2 += $"\n⚠️ WARNING: This address+{modeStr} previously caused a process freeze. Watchdog is monitoring.";
                else
                    msg2 += "\n🛡️ Watchdog monitoring active — will auto-rollback if process becomes unresponsive.";
            }
            operationJournal?.RecordOperation(
                bp.Id, "Breakpoint", parsedAddr, modeStr, groupId: null,
                async () => await breakpointService.RemoveBreakpointAsync(processId, bp.Id).ConfigureAwait(false));
            return msg2;
        }
        catch (Exception ex)
        {
            return $"SetBreakpoint failed: {ex.Message}";
        }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove a breakpoint by its ID.")]
    public async Task<string> RemoveBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Breakpoint ID to remove")] string breakpointId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (breakpointService is null) return "Breakpoint engine not available.";
        var removed = await breakpointService.RemoveBreakpointAsync(processId, breakpointId).ConfigureAwait(false);
        return removed ? $"Breakpoint {breakpointId} removed." : $"Breakpoint {breakpointId} not found.";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("EMERGENCY: Restore page guard protections for hung target.")]
    public async Task<string> EmergencyRestorePageProtection(
        [Description("Process ID of the hung process")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (breakpointService is null) return "Breakpoint engine not available.";
        var restored = await breakpointService.EmergencyRestorePageProtectionAsync(processId).ConfigureAwait(false);
        return restored > 0
            ? $"✅ Emergency restore complete: {restored} page guard protection(s) restored. Target process should recover."
            : "No active page guard breakpoints found to restore.";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("EMERGENCY: Force detach debugger, clean up all BPs.")]
    public async Task<string> ForceDetachAndCleanup(
        [Description("Process ID of the hung process")] int processId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (breakpointService is null) return "Breakpoint engine not available.";
        await breakpointService.ForceDetachAndCleanupAsync(processId).ConfigureAwait(false);
        return $"✅ Force detach complete for process {processId}. Page guards restored, debugger detached, session torn down.";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all active breakpoints for a process.")]
    public async Task<string> ListBreakpoints([Description("Process ID")] int processId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var bps = await breakpointService.ListBreakpointsAsync(processId).ConfigureAwait(false);
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
                lifecycleStatus = breakpointService.GetLifecycleStatus(b.Id).ToString(),
                condition = b.Condition,
                threadFilter = b.ThreadFilter
            }),
            count = bps.Count
        });
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Get breakpoint hit log with registers and thread info.")]
    public async Task<string> GetBreakpointHitLog(
        [Description("Breakpoint ID")] string breakpointId,
        [Description("Maximum entries to return")] int maxEntries = 0)
    {
        if (maxEntries <= 0) maxEntries = _limits.MaxHitLogEntries;
        if (breakpointService is null) return "Breakpoint engine not available.";
        var hits = await breakpointService.GetHitLogAsync(breakpointId, maxEntries).ConfigureAwait(false);
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Get breakpoint health and lifecycle status.")]
    public async Task<string> GetBreakpointHealth(
        [Description("Breakpoint ID")] string breakpointId,
        [Description("Process ID")] int processId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var bps = await breakpointService.ListBreakpointsAsync(processId).ConfigureAwait(false);
        var bp = bps.FirstOrDefault(b => string.Equals(b.Id, breakpointId, StringComparison.Ordinal));
        if (bp is null) return $"Breakpoint {breakpointId} not found on process {processId}.";

        var lifecycle = breakpointService.GetLifecycleStatus(breakpointId);
        var hits = await breakpointService.GetHitLogAsync(breakpointId, 1).ConfigureAwait(false);
        var lastHit = hits.Count > 0 ? hits[0].Timestamp : "none";

        // Page co-tenancy for PageGuard breakpoints
        int coTenants = 0;
        bool isPageGuard = string.Equals(bp.Mode, "PageGuard", StringComparison.OrdinalIgnoreCase);
        if (isPageGuard)
        {
            var addr = ParseAddress(bp.Address);
            var pageBase = addr & PageMask;
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Get breakpoint mode capability matrix.")]
    public static string GetBreakpointModeCapabilities()
    {
        var caps = BreakpointService.GetModeCapabilities();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Breakpoint Mode Capabilities");
        foreach (var c in caps)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"\n### {c.Mode} [{c.StabilityTier}]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {c.Description}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Execute hooks: {(c.SupportsExecuteHook ? "✓" : "✗")} | Data write watch: {(c.SupportsDataWriteWatch ? "✓" : "✗")}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Debugger: {(c.RequiresDebugger ? "required" : "none")} | Page protection: {(c.UsesPageProtection ? "yes" : "no")} | Thread suspend: {(c.UsesThreadSuspend ? "yes" : "no")}");
        }
        return sb.ToString();
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Probe address risk level and recommended BP modes.")]
    public async Task<string> ProbeTargetRisk(
        [Description("Process ID")] int processId,
        [Description("Memory address to probe (hex or decimal)")] string address)
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);

            if (memoryProtectionEngine is null) return "Memory protection engine not available.";
            var region = await memoryProtectionEngine.QueryProtectionAsync(processId, addr).ConfigureAwait(false);

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
                var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
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
            catch (Exception ex) { logger?.LogDebug(ex, "ProbeTargetRisk module lookup failed"); }

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

                nuint pageBase = addr & PageMask;
                nuint pageEnd = pageBase + PageSize;
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
            sb.AppendLine(CultureInfo.InvariantCulture, $"## Probe: 0x{addr:X}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Region: {regionKind}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Module: {moduleName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Protection: {protectionStr}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Executable: {isExecutable} | Writable: {isWritable} | Readable: {isReadable}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Risk level: {riskLevel}");
            sb.AppendLine();
            sb.AppendLine("Recommended modes:");
            foreach (var r in recommended) sb.AppendLine(CultureInfo.InvariantCulture, $"  ✓ {r}");
            sb.AppendLine("Avoid:");
            foreach (var a in avoid) sb.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {a}");
            if (warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (var w in warnings) sb.AppendLine(CultureInfo.InvariantCulture, $"  ⚠️ {w}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"ProbeTargetRisk failed: {ex.Message}";
        }
    }

    // ── Phase 7B: Conditional Breakpoints ──

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Set a conditional breakpoint that only triggers when an expression is true.")]
    public async Task<string> SetConditionalBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Address (hex) to set breakpoint")] string address,
        [Description("Breakpoint type: HardwareExecute, HardwareWrite, HardwareReadWrite")] string type,
        [Description("Condition expression, e.g. 'RAX == 0x1000' or '[RBX+0x10] > 100' or 'hitcount >= 5'")] string expression,
        [Description("Condition type: RegisterCompare, MemoryCompare, HitCount")] string conditionType = "RegisterCompare",
        [Description("Breakpoint mode: Auto, Stealth, PageGuard, Hardware, Software")] string mode = "Auto",
        [Description("Thread ID to filter (null = all threads)")] int? threadFilter = null)
    {
        try
        {
            var pidError = ValidateDestructiveProcessId(processId);
            if (pidError is not null) return pidError;
            if (breakpointService is null) return "Breakpoint engine not available.";
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            var bpType = Enum.Parse<BreakpointType>(type, ignoreCase: true);
            var bpMode = Enum.Parse<BreakpointMode>(mode, ignoreCase: true);
            var ct = Enum.Parse<BreakpointConditionType>(conditionType, ignoreCase: true);
            var condition = new BreakpointCondition(expression, ct);

            var bp = await breakpointService.SetConditionalBreakpointAsync(
                processId, address, bpType, condition, bpMode, threadFilter: threadFilter).ConfigureAwait(false);

            return $"Conditional breakpoint set: {bp.Id} at {bp.Address} ({bp.Mode})\n" +
                   $"Condition: {expression} ({conditionType})" +
                   (threadFilter.HasValue ? $"\nThread filter: {threadFilter}" : "");
        }
        catch (Exception ex)
        {
            return $"SetConditionalBreakpoint failed: {ex.Message}";
        }
    }

    // ── Phase 7B: Break-and-Trace ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Trace instruction execution from an address, recording each instruction in the linear path.")]
    public async Task<string> TraceFromAddress(
        [Description("Process ID")] int processId,
        [Description("Address (hex) to start tracing from")] string address,
        [Description("Maximum instructions to trace (default 500)")] int maxInstructions = 500,
        [Description("Timeout in milliseconds (default 5000)")] int timeoutMs = 5000)
    {
        try
        {
            if (breakpointService is null) return "Breakpoint engine not available.";
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

            var result = await breakpointService.TraceFromBreakpointAsync(
                processId, address, maxInstructions, timeoutMs).ConfigureAwait(false);

            if (result.Entries.Count == 0)
                return $"Trace from {address}: no instructions decoded (address may be invalid or unreadable).";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Trace from {address}: {result.Entries.Count} instructions");
            if (result.MaxDepthReached) sb.AppendLine("⚠ Max instruction limit reached.");
            if (result.WasTruncated) sb.AppendLine("⚠ Trace was truncated.");

            foreach (var entry in result.Entries.Take(50))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{entry.InstructionAddress:X}: {entry.Disassembly}");

            if (result.Entries.Count > 50)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({result.Entries.Count - 50} more)");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"TraceFromAddress failed: {ex.Message}";
        }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Register a Lua function to be called when a breakpoint is hit. The Lua function receives the register snapshot as a table.")]
    public string RegisterBreakpointLuaCallback(
        [Description("Breakpoint ID")] string breakpointId,
        [Description("Lua function name to call on hit")] string luaFunctionName)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        breakpointService.RegisterLuaCallback(breakpointId, luaFunctionName);
        return $"Lua callback '{luaFunctionName}' registered for breakpoint {breakpointId}.";
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove a previously registered Lua callback from a breakpoint.")]
    public string UnregisterBreakpointLuaCallback(
        [Description("Breakpoint ID")] string breakpointId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        breakpointService.UnregisterLuaCallback(breakpointId);
        return $"Lua callback unregistered from breakpoint {breakpointId}.";
    }

    // ── C3: Hit Log Enhancements ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Get aggregated hit statistics for a breakpoint: total hits, hits/sec, unique threads, top addresses.")]
    public async Task<string> GetBreakpointHitStatistics(
        [Description("Breakpoint ID")] string breakpointId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        try
        {
            var stats = await breakpointService.GetHitStatisticsAsync(breakpointId).ConfigureAwait(false);
            return ToJson(new
            {
                stats.TotalHits,
                stats.HitsPerSecond,
                stats.UniqueThreads,
                stats.TopAddresses,
                FirstHit = stats.FirstHit?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                LastHit = stats.LastHit?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
            });
        }
        catch (Exception ex)
        {
            return $"Failed to get statistics: {ex.Message}";
        }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Export a breakpoint's hit log to a file. Supports CSV and JSON formats.")]
    public async Task<string> ExportBreakpointHitLog(
        [Description("Breakpoint ID")] string breakpointId,
        [Description("Output file path")] string filePath,
        [Description("Format: Csv or Json")] string format = "Csv")
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        if (!Enum.TryParse<HitLogExportFormat>(format, ignoreCase: true, out var fmt))
            return $"Invalid format '{format}'. Use 'Csv' or 'Json'.";
        // Path traversal check per SECURITY.md
        if (filePath.Contains(".."))
            return "Path traversal rejected: '..' not allowed in file paths.";
        try
        {
            return await breakpointService.ExportHitLogAsync(breakpointId, filePath, fmt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Export failed: {ex.Message}";
        }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Get filtered hit log for a breakpoint. Filter by thread ID and/or time range.")]
    public async Task<string> GetFilteredBreakpointHitLog(
        [Description("Breakpoint ID")] string breakpointId,
        [Description("Optional: filter to this thread ID only")] int? threadId = null,
        [Description("Max entries to return")] int maxEntries = 50)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        try
        {
            var filter = new HitLogFilter(ThreadId: threadId);
            var hits = await breakpointService.GetFilteredHitLogAsync(breakpointId, filter, maxEntries).ConfigureAwait(false);
            if (hits.Count == 0) return "No hits matching filter.";
            return ToJson(hits.Select(h => new { h.Address, h.ThreadId, h.Timestamp }).Take(50));
        }
        catch (Exception ex)
        {
            return $"Failed to get filtered hits: {ex.Message}";
        }
    }

    // ── C4: Breakpoint Persistence ──

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Save current breakpoints to a named profile XML file.")]
    public async Task<string> SaveBreakpointProfile(
        [Description("Process ID")] int processId,
        [Description("Profile name")] string profileName,
        [Description("Output file path")] string filePath)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (filePath.Contains("..")) return "Path traversal rejected.";
        try
        {
            return await breakpointService.SaveProfileAsync(processId, profileName, filePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Save failed: {ex.Message}";
        }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Load breakpoints from a profile XML file and re-create them.")]
    public async Task<string> LoadBreakpointProfile(
        [Description("Process ID")] int processId,
        [Description("Profile file path")] string filePath)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (filePath.Contains("..")) return "Path traversal rejected.";
        try
        {
            return await breakpointService.LoadProfileAsync(processId, filePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Load failed: {ex.Message}";
        }
    }

    // ── B2: Region Breakpoint ──

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Set a breakpoint spanning a memory region. Uses hardware BP for ≤8 bytes, PAGE_GUARD chaining for larger regions (max 64KB).")]
    public async Task<string> SetRegionBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Start address (hex)")] string startAddress,
        [Description("Region size in bytes")] int length)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        try
        {
            var bps = await breakpointService.SetRegionBreakpointAsync(processId, startAddress, length).ConfigureAwait(false);
            if (bps.Count == 1)
                return $"Region breakpoint set: {bps[0].Id} at {bps[0].Address} ({bps[0].Mode} mode).";
            return $"Region breakpoint set across {bps.Count} pages (PAGE_GUARD mode). IDs: {string.Join(", ", bps.Select(b => b.Id))}";
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return $"Invalid region: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Region breakpoint failed: {ex.Message}";
        }
    }

    // ── B3: Breakpoint Group tools ──

    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Create a named breakpoint group for atomic enable/disable. Returns the group ID.")]
    public string CreateBreakpointGroup(
        [Description("Group name")] string name,
        [Description("Comma-separated breakpoint IDs to include")] string breakpointIds)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var ids = breakpointIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return "No breakpoint IDs provided.";
        var group = breakpointService.CreateGroup(name, ids);
        return $"Created group '{name}' (ID: {group.GroupId}) with {ids.Length} breakpoints.";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Enable all breakpoints in a group atomically.")]
    public async Task<string> EnableBreakpointGroup(
        [Description("Process ID")] int processId,
        [Description("Group ID")] string groupId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        var count = await breakpointService.EnableGroupAsync(processId, groupId).ConfigureAwait(false);
        return count > 0 ? $"Enabled {count} breakpoints in group {groupId}." : $"No breakpoints enabled (group not found or all already active).";
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Disable all breakpoints in a group atomically.")]
    public async Task<string> DisableBreakpointGroup(
        [Description("Process ID")] int processId,
        [Description("Group ID")] string groupId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        var count = await breakpointService.DisableGroupAsync(processId, groupId).ConfigureAwait(false);
        return count > 0 ? $"Disabled {count} breakpoints in group {groupId}." : $"No breakpoints disabled (group not found or none active).";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all defined breakpoint groups.")]
    public string ListBreakpointGroups()
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var groups = breakpointService.ListGroups();
        if (groups.Count == 0) return "No breakpoint groups defined.";
        return string.Join("\n", groups.Select(g => $"[{g.GroupId}] {g.Name}: {string.Join(", ", g.BreakpointIds)}"));
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove a breakpoint group definition (does not remove the breakpoints themselves).")]
    public string RemoveBreakpointGroup([Description("Group ID")] string groupId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        return breakpointService.RemoveGroup(groupId)
            ? $"Removed group {groupId}."
            : $"Group {groupId} not found.";
    }

}
