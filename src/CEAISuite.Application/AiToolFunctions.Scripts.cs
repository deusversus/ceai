using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    // ── Script tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all script entries with name, status, and type.")]
    public Task<string> ListScripts()
    {
        var scripts = new List<string>();
        CollectScripts(addressTableService.Roots, scripts, "");
        if (scripts.Count == 0) return Task.FromResult("No scripts in the address table.");
        return Task.FromResult($"Found {scripts.Count} scripts:\n{string.Join('\n', scripts.Take(30))}");
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

    private static void CollectScriptIds(IEnumerable<AddressTableNode> nodes, List<string> results)
    {
        foreach (var node in nodes)
        {
            if (node.IsScriptEntry)
                results.Add($"{node.Id} (\"{node.Label}\")");
            if (node.Children.Count > 0)
                CollectScriptIds(node.Children, results);
        }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("View source code of a script entry by ID or label.")]
    public Task<string> ViewScript([Description("Node ID or label of the script entry")] string nodeId)
    {
        var node = ResolveNode(nodeId);
        if (node is null)
        {
            // List available scripts to help the operator find the right one
            var available = new List<string>();
            CollectScriptIds(addressTableService.Roots, available);
            var hint = available.Count > 0
                ? $" Available scripts: {string.Join(", ", available.Take(10))}"
                : " No scripts in address table.";
            return Task.FromResult($"Node '{nodeId}' not found.{hint}");
        }
        if (node.AssemblerScript is null) return Task.FromResult($"Node '{nodeId}' is not a script entry (it's a {(node.IsGroup ? "group" : "value entry")}).");

        var type = node.AssemblerScript.Contains("LuaCall") ? "LuaCall" : "Auto Assembler";
        var status = node.IsScriptEnabled ? "✅ Enabled" : "❌ Disabled";
        return Task.FromResult(
            $"Script: {node.Label}\nType: {type}\nStatus: {status}\n" +
            $"──────────────────────\n{node.AssemblerScript}");
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Validate script syntax without executing.")]
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

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Deep-validate script against live process state.")]
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Deep Validation: {node.Label}");

        int passed = 0, failed = 0, warnings = 0;

        // Step 1: Parse validation
        var parseResult = autoAssemblerEngine.Parse(node.AssemblerScript);
        if (!parseResult.IsValid)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"❌ PARSE FAILED: {string.Join("; ", parseResult.Errors)}");
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

                        var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
                        var mod = attachment.Modules.FirstOrDefault(m =>
                            m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));

                        if (mod is null)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"❌ Assert at {addrStr}: module '{modName}' not found");
                            failed++;
                            continue;
                        }

                        var offsetVal = ulong.Parse(offsetStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        assertAddr = (nuint)((ulong)mod.BaseAddress + offsetVal);
                    }
                    else
                    {
                        assertAddr = ParseAddress(addrStr);
                    }

                    var expectedBytes = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                        .ToArray();

                    var liveRead = await engineFacade.ReadMemoryAsync(processId, assertAddr, expectedBytes.Length).ConfigureAwait(false);
                    var liveBytes = liveRead.Bytes.ToArray();

                    if (liveBytes.SequenceEqual(expectedBytes))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"✅ Assert 0x{assertAddr:X}: live bytes match ({expectedHex})");
                        passed++;
                    }
                    else
                    {
                        var liveHex = string.Join(" ", liveBytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                        sb.AppendLine(CultureInfo.InvariantCulture, $"❌ Assert 0x{assertAddr:X}: MISMATCH");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"   Expected: {expectedHex}");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"   Live:     {liveHex}");
                        sb.AppendLine($"   ⚠️ Script may be incompatible with current game version");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"❌ Assert {addrStr}: verification failed ({ex.Message})");
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
                        var attachment = await engineFacade.AttachAsync(processId).ConfigureAwait(false);
                        var mod = attachment.Modules.FirstOrDefault(m =>
                            m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
                        if (mod is null) continue;
                        hookAddr = (nuint)((ulong)mod.BaseAddress + ulong.Parse(offsetStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        hookAddr = ParseAddress(match.Groups[3].Value);
                    }

                    var region = await memoryProtectionEngine.QueryProtectionAsync(processId, hookAddr).ConfigureAwait(false);
                    if (region.IsExecutable)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"✅ Hook target 0x{hookAddr:X}: executable ({(region.IsWritable ? "RWX" : "RX")})");
                        passed++;
                    }
                    else
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"⚠️ Hook target 0x{hookAddr:X}: NOT executable — hook may fail or crash");
                        warnings++;
                    }
                }
                catch (Exception ex) { logger?.LogDebug(ex, "ValidateScriptDeep address resolve failed"); }
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Summary: {passed} passed, {failed} failed, {warnings} warnings");
        if (failed > 0)
            sb.AppendLine("🛑 DO NOT ENABLE — script has critical issues that may crash or corrupt the game");
        else if (warnings > 0)
            sb.AppendLine("⚠️ Script may work but has potential issues — proceed with caution");
        else
            sb.AppendLine("✅ All checks passed — script appears safe to enable");

        return sb.ToString();
    }

    // ── Phase 7C: Registered Symbols ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all symbols registered by Auto Assembler scripts via registersymbol().")]
    public Task<string> ListRegisteredSymbols()
    {
        if (autoAssemblerEngine is null) return Task.FromResult("Auto Assembler engine not available.");

        var symbols = autoAssemblerEngine.GetRegisteredSymbols();
        if (symbols.Count == 0)
            return Task.FromResult("No symbols registered. Execute a script with registersymbol() first.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{symbols.Count} registered symbol(s):");
        foreach (var sym in symbols)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {sym.Name} = 0x{sym.Address:X}");
        return Task.FromResult(sb.ToString());
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Resolve a registered symbol name to its address.")]
    public Task<string> ResolveRegisteredSymbol(
        [Description("Symbol name to resolve")] string name)
    {
        if (autoAssemblerEngine is null) return Task.FromResult("Auto Assembler engine not available.");

        var addr = autoAssemblerEngine.ResolveSymbol(name);
        if (addr is null)
            return Task.FromResult($"Symbol '{name}' not found. Use ListRegisteredSymbols to see available symbols.");
        return Task.FromResult($"{name} = 0x{addr.Value:X}");
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Execute an Auto Assembler script directly without creating an address table entry. Runs the [ENABLE] section. Use DisableAutoAssemblerScript to run the [DISABLE] section.")]
    public async Task<string> ExecuteAutoAssemblerScript(
        [Description("Process ID to execute the script against")] int processId,
        [Description("Full Auto Assembler script text")] string script)
    {
        if (autoAssemblerEngine is null)
            return "Auto Assembler engine not available.";
        if (!IsProcessAlive(processId))
            return $"Process {processId} is no longer running.";

        var parseResult = autoAssemblerEngine.Parse(script);
        if (!parseResult.IsValid)
            return $"Script parse failed:\nErrors: {string.Join("; ", parseResult.Errors)}\nWarnings: {string.Join("; ", parseResult.Warnings)}";

        try
        {
            var result = await autoAssemblerEngine.EnableAsync(processId, script).ConfigureAwait(false);
            if (result.Success)
                return $"Script ENABLED successfully. {result.Allocations.Count} allocations, {result.Patches.Count} patches applied.";
            else
                return $"Script FAILED to enable: {result.Error}";
        }
        catch (Exception ex)
        {
            return $"Script execution error: {ex.Message}";
        }
    }

    [Destructive]
    [InterruptBehavior(ToolInterruptMode.MustComplete)]
    [Description("Execute the [DISABLE] section of an Auto Assembler script to undo its changes.")]
    public async Task<string> DisableAutoAssemblerScript(
        [Description("Process ID to execute the script against")] int processId,
        [Description("Full Auto Assembler script text (must match the previously enabled script)")] string script)
    {
        if (autoAssemblerEngine is null)
            return "Auto Assembler engine not available.";
        if (!IsProcessAlive(processId))
            return $"Process {processId} is no longer running.";

        var parseResult = autoAssemblerEngine.Parse(script);
        if (!parseResult.IsValid)
            return $"Script parse failed:\nErrors: {string.Join("; ", parseResult.Errors)}\nWarnings: {string.Join("; ", parseResult.Warnings)}";

        try
        {
            var result = await autoAssemblerEngine.DisableAsync(processId, script).ConfigureAwait(false);
            if (result.Success)
                return $"Script DISABLED successfully. {result.Patches.Count} patches restored.";
            else
                return $"Script FAILED to disable: {result.Error}";
        }
        catch (Exception ex)
        {
            return $"Script disable error: {ex.Message}";
        }
    }
}
