using CEAISuite.Application.AgentLoop;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    // ─── Code Cave (Stealth Hook) Tools ─────────────────────────────────

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [InterruptBehavior(ToolInterruptMode.RequiresCleanup)]
    [Description("Install a stealth code cave hook. No debugger — game-safe. Captures registers and hit count.")]
    public async Task<string> InstallCodeCaveHook(
        [Description("Process ID")] int processId,
        [Description("Memory address to hook (hex or decimal)")] string address,
        [Description("Capture register snapshots (RAX-RDI, RSP) on each hit")] bool captureRegisters = true)
    {
        try
        {
            var pidError = ValidateDestructiveProcessId(processId);
            if (pidError is not null) return pidError;
            if (codeCaveEngine is null) return "Code cave engine not available.";
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);

            // ── Executable-memory safety gate ──
            if (memoryProtectionEngine is not null)
            {
                var region = await memoryProtectionEngine.QueryProtectionAsync(processId, addr).ConfigureAwait(false);
                if (!region.IsExecutable)
                {
                    return $"❌ Code cave hook REJECTED: Target address 0x{addr:X} is in a non-executable memory region " +
                           $"(R={region.IsReadable}, W={region.IsWritable}, X={region.IsExecutable}). " +
                           $"Code cave hooks inject a JMP detour and can only target executable code.\n" +
                           $"If you need to monitor writes to this data address, use SetBreakpoint with mode=Hardware and type=HardwareWrite instead.";
                }
            }

            if (watchdogService is not null && watchdogService.IsUnsafe(addr, "Stealth"))
            {
                // Still allow but warn
            }
            var result = await codeCaveEngine.InstallHookAsync(processId, addr, captureRegisters).ConfigureAwait(false);
            if (!result.Success) return $"Hook installation failed: {result.ErrorMessage}";
            var h = result.Hook!;
            var hookMsg = $"Stealth hook installed:\n  ID: {h.Id}\n  Target: 0x{h.OriginalAddress:X}\n  Cave: 0x{h.CaveAddress:X}\n  Stolen bytes: {h.OriginalBytesLength}\n  No debugger attached — completely game-safe.";
            if (watchdogService is not null)
            {
                var hookId = h.Id;
                watchdogService.StartMonitoring(processId, hookId, addr, "CodeCaveHook", "Stealth",
                    async () => await codeCaveEngine.RemoveHookAsync(processId, hookId).ConfigureAwait(false));
                if (watchdogService.IsUnsafe(addr, "Stealth"))
                    hookMsg += "\n⚠️ WARNING: This address+Stealth previously caused a process freeze. Watchdog is monitoring.";
                else
                    hookMsg += "\n🛡️ Watchdog monitoring active — will auto-rollback if process becomes unresponsive.";
            }
            operationJournal?.RecordOperation(
                h.Id, "CodeCaveHook", addr, "Stealth", groupId: null,
                async () => await codeCaveEngine.RemoveHookAsync(processId, h.Id).ConfigureAwait(false));
            return hookMsg;
        }
        catch (Exception ex) { return $"InstallCodeCaveHook failed: {ex.Message}"; }
    }

    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove a stealth code cave hook, restoring original bytes.")]
    public async Task<string> RemoveCodeCaveHook(
        [Description("Process ID")] int processId,
        [Description("Hook ID to remove")] string hookId)
    {
        var pidError = ValidateDestructiveProcessId(processId);
        if (pidError is not null) return pidError;
        if (codeCaveEngine is null) return "Code cave engine not available.";
        var removed = await codeCaveEngine.RemoveHookAsync(processId, hookId).ConfigureAwait(false);
        return removed ? $"Hook {hookId} removed, original bytes restored." : $"Hook {hookId} not found.";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all active stealth code cave hooks for a process.")]
    public async Task<string> ListCodeCaveHooks([Description("Process ID")] int processId)
    {
        if (codeCaveEngine is null) return "Code cave engine not available.";
        var hooks = await codeCaveEngine.ListHooksAsync(processId).ConfigureAwait(false);
        if (hooks.Count == 0) return ToJson(new { hooks = Array.Empty<object>(), count = 0 });
        return ToJson(new
        {
            hooks = hooks.Select(h => new { h.Id, originalAddress = $"0x{h.OriginalAddress:X}", caveAddress = $"0x{h.CaveAddress:X}", h.OriginalBytesLength, h.HitCount }),
            count = hooks.Count
        });
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Get register snapshots from a code cave hook. Returns captures with key registers, thread IDs, and timestamps.")]
    public async Task<string> GetCodeCaveHookHits(
        [Description("Hook ID")] string hookId,
        [Description("Maximum entries to return")] int maxEntries = 0,
        [Description("Process ID for register pointer dereferences (0=skip)")] int processId = 0,
        [Description("Include pointer dereferences for registers (costs extra reads)")] bool? dereference = null)
    {
        if (maxEntries <= 0) maxEntries = _limits.MaxHitLogEntries;
        dereference ??= _limits.DereferenceHookRegisters;
        if (codeCaveEngine is null) return "Code cave engine not available.";
        var hits = await codeCaveEngine.GetHookHitsAsync(hookId, maxEntries).ConfigureAwait(false);
        if (hits.Count == 0) return $"No hits recorded for hook {hookId}.";

        var hitResults = new List<object>();
        foreach (var h in hits)
        {
            var trimmedRegs = TrimRegisters(h.RegisterSnapshot);

            Dictionary<string, string>? dereferences = null;
            if (dereference == true && processId > 0 && trimmedRegs.Count > 0)
            {
                dereferences = await DereferenceRegistersAsync(processId, trimmedRegs).ConfigureAwait(false);
            }

            hitResults.Add(new
            {
                address = $"0x{h.Address:X}",
                threadId = h.ThreadId,
                timestamp = h.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
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
    private Dictionary<string, string> TrimRegisters(IReadOnlyDictionary<string, string>? registers)
    {
        if (registers is null || registers.Count == 0) return new();
        if (!_limits.FilterRegisters)
            return registers.ToDictionary(kv => kv.Key, kv => kv.Value);
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
                var mem = await engineFacade.ReadMemoryAsync(processId, (nuint)addr, 8).ConfigureAwait(false);
                if (mem.Bytes.Count == 8)
                {
                    var pointed = BitConverter.ToUInt64(mem.Bytes.ToArray(), 0);
                    result[regName] = $"0x{addr:X} (points to 0x{pointed:X})";
                }
            }
            catch (Exception ex)
            {
                // Address not readable — skip dereference
                System.Diagnostics.Trace.TraceWarning($"[AiToolFunctions] Dereference failed for {regName}: {ex.Message}");
            }
        }
        return result;
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
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
            sb.AppendLine(CultureInfo.InvariantCulture, $"## Dry-Run Hook Preview: 0x{addr:X}");

            bool canHook = true;

            // 1. Check executability
            if (memoryProtectionEngine is not null)
            {
                var region = await memoryProtectionEngine.QueryProtectionAsync(processId, addr).ConfigureAwait(false);
                if (!region.IsExecutable)
                {
                    sb.AppendLine("❌ Address is NOT executable — cannot install code cave hook");
                    canHook = false;
                }
                else
                {
                    string prot = region.IsWritable ? "RWX" : "RX";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"✅ Region is executable ({prot})");
                }
            }

            // 2. Disassemble at the target to determine stolen bytes
            var disasm = await disassemblyService.DisassembleAtAsync(processId, $"0x{addr:X}", 10).ConfigureAwait(false);

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
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Stolen Bytes: {stolenBytes} (minimum required: {minJmpSize})");
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
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {instr.Address}: [{instr.HexBytes}] {instr.Mnemonic} {instr.Operands}");
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
            var liveRead = await engineFacade.ReadMemoryAsync(processId, addr, stolenBytes).ConfigureAwait(false);
            var hexDump = string.Join(" ", liveRead.Bytes.Take(stolenBytes).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            sb.AppendLine();
            sb.AppendLine($"### Bytes to be overwritten:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {hexDump}");

            // 5. Trampoline layout estimate
            int trampolineSize =
                17 +          // push all registers (~17 bytes overhead for a minimal snapshot)
                stolenBytes + // relocated original instructions
                14;           // JMP back to original code
            int withCapture = trampolineSize + 128; // conservative estimate for full register capture

            sb.AppendLine();
            sb.AppendLine($"### Estimated Trampoline Size:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Without register capture: ~{trampolineSize} bytes");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  With register capture: ~{withCapture} bytes");

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
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {stolenInstructions.Count} instruction(s) will be relocated ({stolenBytes} bytes)");
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
}
