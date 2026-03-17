using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using CEAISuite.Engine.Abstractions;
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
    ICodeCaveEngine? codeCaveEngine = null)
{
    /// <summary>Queue of captured screenshots for injection into the AI conversation.</summary>
    public ConcurrentQueue<(string Description, byte[] PngData)> PendingImages { get; } = new();

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

    [Description("Disassemble machine code at an address in a process. Shows assembly instructions.")]
    public async Task<string> Disassemble(
        [Description("Process ID")] int processId,
        [Description("Memory address to start disassembling")] string address)
    {
        var overview = await disassemblyService.DisassembleAtAsync(processId, address);
        var lines = overview.Lines.Select(i => $"  {i.Address}  {i.HexBytes,-24}  {i.Mnemonic,-8} {i.Operands}");
        return $"Disassembly ({overview.Lines.Count} instructions):\n{string.Join('\n', lines)}";
    }

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

    [Description("List all entries currently in the address table.")]
    public Task<string> ListAddressTable()
    {
        var entries = addressTableService.Entries;
        if (entries.Count == 0)
            return Task.FromResult("Address table is empty.");

        var lines = entries.Select(e => $"  {e.Label}: {e.Address} = {e.CurrentValue} ({e.DataType}){(e.IsLocked ? " [LOCKED]" : "")}");
        return Task.FromResult($"Address table ({entries.Count} entries):\n{string.Join('\n', lines)}");
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

    [Description("Load a Cheat Engine .CT (Cheat Table) file and import its entries into the address table with hierarchy and scripts preserved. Provide the full file path.")]
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

    [Description("Set a breakpoint at a memory address. Supports multiple intrusiveness modes: Auto (engine picks), Stealth (code cave, no debugger), PageGuard (less intrusive), Hardware (DR registers), Software (INT3). Use Stealth or Auto for anti-debug-sensitive targets.")]
    public async Task<string> SetBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex or decimal)")] string address,
        [Description("Breakpoint type: Software, HardwareExecute, HardwareWrite, HardwareReadWrite")] string type = "Software",
        [Description("Hit action: Break, Log, LogAndContinue")] string hitAction = "LogAndContinue",
        [Description("Intrusiveness mode: Auto, Stealth, PageGuard, Hardware, Software")] string mode = "Auto")
    {
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            if (breakpointService is null) return "Breakpoint engine not available.";
            var bpType = Enum.Parse<BreakpointType>(type, ignoreCase: true);
            var bpAction = Enum.Parse<BreakpointHitAction>(hitAction, ignoreCase: true);
            var bpMode = Enum.Parse<BreakpointMode>(mode, ignoreCase: true);

            // For Stealth mode with execute BPs, redirect to code cave engine
            if (bpMode == BreakpointMode.Stealth && bpType is BreakpointType.HardwareExecute or BreakpointType.Software)
            {
                if (codeCaveEngine is null) return "Code cave engine not available.";
                var result = await codeCaveEngine.InstallHookAsync(processId, ParseAddress(address));
                if (!result.Success) return $"Stealth hook failed: {result.ErrorMessage}";
                return $"Stealth code cave hook installed at 0x{result.Hook!.OriginalAddress:X} (ID: {result.Hook.Id}, cave at 0x{result.Hook.CaveAddress:X}). No debugger attached — game-safe.";
            }

            var bp = await breakpointService.SetBreakpointAsync(processId, address, bpType, bpMode, bpAction);
            return $"Breakpoint {bp.Id} set at {bp.Address} (type: {bp.Type}, mode: {bpMode}, action: {bp.HitAction})";
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

    [Description("List all active breakpoints for a process.")]
    public async Task<string> ListBreakpoints([Description("Process ID")] int processId)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var bps = await breakpointService.ListBreakpointsAsync(processId);
        if (bps.Count == 0) return "No active breakpoints.";
        var lines = bps.Select(b => $"  [{b.Id}] {b.Address} ({b.Type}) hits={b.HitCount} {(b.IsEnabled ? "enabled" : "disabled")}");
        return $"Active breakpoints ({bps.Count}):\n{string.Join('\n', lines)}";
    }

    [Description("Get the hit log for a breakpoint. Shows when it was triggered, register state, and thread info.")]
    public async Task<string> GetBreakpointHitLog(
        [Description("Breakpoint ID")] string breakpointId,
        [Description("Maximum entries to return")] int maxEntries = 20)
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var hits = await breakpointService.GetHitLogAsync(breakpointId, maxEntries);
        if (hits.Count == 0) return $"No hits recorded for breakpoint {breakpointId}.";
        var lines = hits.Select(h =>
        {
            var regs = string.Join(", ", h.Registers.Take(4).Select(r => $"{r.Key}={r.Value}"));
            return $"  [{h.Timestamp}] thread={h.ThreadId} @ {h.Address} | {regs}";
        });
        return $"Hit log ({hits.Count} entries):\n{string.Join('\n', lines)}";
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
    public Task<string> ViewScript([Description("Node ID of the script entry")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
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
        [Description("Node ID of the script entry")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
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
        [Description("Region size in bytes (default 256)")] int regionSize = 256)
    {
        var dissector = new StructureDissectorService(engineFacade);
        var addr = AddressTableService.ParseAddress(address);
        var fields = await dissector.DissectAsync(processId, addr, regionSize);

        if (fields.Count == 0) return "No identifiable fields found in this region.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Structure analysis at 0x{addr:X} ({fields.Count} fields):");
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
        [Description("Node ID of the address table entry")] string nodeId,
        [Description("Hotkey combination like 'Ctrl+F1' or 'Alt+Shift+G'")] string hotkey)
    {
        var node = addressTableService.FindNode(nodeId);
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
    public Task<string> FreezeAddress([Description("Node ID of the address table entry")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (node.IsGroup) return Task.FromResult("Cannot freeze a group.");
        if (node.IsScriptEntry) return Task.FromResult("Use ToggleScript for script entries.");
        if (node.IsLocked) return Task.FromResult($"'{node.Label}' is already frozen at {node.LockedValue}.");

        node.IsLocked = true;
        node.LockedValue = node.CurrentValue;
        return Task.FromResult($"Frozen '{node.Label}' at value {node.CurrentValue}. It will be continuously written back.");
    }

    [Description("Unfreeze (unlock) an address table entry so it can change naturally again.")]
    public Task<string> UnfreezeAddress([Description("Node ID of the address table entry")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (!node.IsLocked) return Task.FromResult($"'{node.Label}' is not frozen.");

        node.IsLocked = false;
        node.LockedValue = null;
        return Task.FromResult($"Unfrozen '{node.Label}'. Value can now change freely.");
    }

    [Description("Freeze an address at a specific value (not just its current value). Useful for setting health to 9999, gold to max, etc.")]
    public Task<string> FreezeAddressAtValue(
        [Description("Node ID of the address table entry")] string nodeId,
        [Description("Value to freeze at")] string value)
    {
        var node = addressTableService.FindNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        if (node.IsGroup || node.IsScriptEntry) return Task.FromResult("Can only freeze value entries.");

        node.IsLocked = true;
        node.LockedValue = value;
        return Task.FromResult($"Frozen '{node.Label}' at value {value}. Will continuously write {value}.");
    }

    [Description("Enable or disable a script (Auto Assembler) entry in the address table. Actually executes the AA engine. Returns the execution result.")]
    public async Task<string> ToggleScript([Description("Node ID of the script entry")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
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

    [Description("Get detailed info about a specific address table node by its ID. Shows address, type, value, pointer chain, locked state, and children.")]
    public Task<string> GetAddressTableNode([Description("Node ID")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ID: {node.Id}");
        sb.AppendLine($"Label: {node.Label}");
        sb.AppendLine($"Type: {(node.IsGroup ? "Group" : node.IsScriptEntry ? "Script" : node.DataType.ToString())}");
        sb.AppendLine($"Address: {node.Address}");
        if (node.ResolvedAddress.HasValue)
            sb.AppendLine($"Resolved: 0x{node.ResolvedAddress.Value:X}");
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
        var lines = results.Results.Take(maxResults)
            .Select(r => $"  0x{r.Address:X} = {r.CurrentValue} (was {r.PreviousValue})");
        return Task.FromResult(
            $"Scan: {count:N0} results ({results.Constraints.DataType})\n{string.Join('\n', lines)}" +
            (count > maxResults ? $"\n  ... {count - maxResults:N0} more" : ""));
    }

    [Description("Get current context: attached process, address table summary, scan state. Use this to orient yourself before taking action.")]
    public Task<string> GetCurrentContext()
    {
        var sb = new System.Text.StringBuilder();

        // Process
        var dashboard = dashboardService.CurrentDashboard;
        if (dashboard?.CurrentInspection is not null)
        {
            var p = dashboard.CurrentInspection;
            sb.AppendLine($"Attached: {p.ProcessName} (PID {p.ProcessId}, {p.Architecture})");
            sb.AppendLine($"Modules: {p.Modules.Count}");
        }
        else
        {
            sb.AppendLine("No process attached.");
        }

        // Address table
        var roots = addressTableService.Roots;
        var totalEntries = CountNodes(roots);
        var frozenCount = CountNodes(roots, n => n.IsLocked);
        var scriptCount = CountNodes(roots, n => n.IsScriptEntry);
        sb.AppendLine($"Address table: {totalEntries} entries, {frozenCount} frozen, {scriptCount} scripts");

        // Scan
        if (scanService.LastScanResults is not null)
        {
            var s = scanService.LastScanResults;
            sb.AppendLine($"Active scan: {s.Results.Count:N0} results ({s.Constraints.DataType})");
        }
        else
        {
            sb.AppendLine("No active scan.");
        }

        return Task.FromResult(sb.ToString());
    }

    [Description("Read memory at an address as multiple data types at once. Useful when you don't know the type — shows Int32, UInt32, Float, Int64, Double interpretations.")]
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
        [Description("Node ID of the script entry")] string nodeId,
        [Description("New complete script content (must include [ENABLE] and [DISABLE] sections)")] string newScript)
    {
        var node = addressTableService.FindNode(nodeId);
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

        var nodeId = Guid.NewGuid().ToString("N")[..12];
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
    public async Task<string> EnableScript([Description("Node ID of the script entry")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
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
    public async Task<string> DisableScript([Description("Node ID of the script entry")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
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
    public Task<string> RemoveFromAddressTable([Description("Node ID to remove")] string nodeId)
    {
        var node = addressTableService.FindNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        var label = node.Label;
        addressTableService.RemoveEntry(nodeId);
        return Task.FromResult($"Removed '{label}' (ID: {nodeId}) from address table.");
    }

    [Description("Rename an address table entry's label/description.")]
    public Task<string> RenameAddressTableEntry(
        [Description("Node ID to rename")] string nodeId,
        [Description("New label/description")] string newLabel)
    {
        var node = addressTableService.FindNode(nodeId);
        if (node is null) return Task.FromResult($"Node '{nodeId}' not found.");
        var oldLabel = node.Label;
        addressTableService.UpdateLabel(nodeId, newLabel);
        return Task.FromResult($"Renamed '{oldLabel}' → '{newLabel}'.");
    }

    [Description("Set or update notes/annotations on an address table entry.")]
    public Task<string> SetEntryNotes(
        [Description("Node ID")] string nodeId,
        [Description("Notes text (or empty to clear)")] string notes)
    {
        var node = addressTableService.FindNode(nodeId);
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
        [Description("Node ID of entry to move")] string nodeId,
        [Description("Target group ID (or empty for top level)")] string? groupId = null)
    {
        var node = addressTableService.FindNode(nodeId);
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
        var (entries, processName, processId) = result.Value;
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

    [Description("Query memory page protection for an address.")]
    public async Task<string> QueryMemoryProtection(
        [Description("Process ID")] int processId,
        [Description("Memory address as hex")] string address)
    {
        if (memoryProtectionEngine is null) return "Memory protection engine not available.";
        try
        {
            if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
            var addr = ParseAddress(address);
            var info = await memoryProtectionEngine.QueryProtectionAsync(processId, addr);
            return $"Region at 0x{info.BaseAddress:X}: {info.RegionSize} bytes | " +
                   $"R={info.IsReadable} W={info.IsWritable} X={info.IsExecutable}";
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
        [Description("Maximum frames per thread")] int maxFrames = 16)
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
            foreach (var (tid, frames) in allStacks.OrderByDescending(kv => kv.Value.Count).Take(8))
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

    [Description("Install a stealth code cave hook at an address. No debugger attachment — game-safe. Redirects execution through an allocated trampoline that captures registers and counts hits. Use this for anti-debug-sensitive targets.")]
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
            var result = await codeCaveEngine.InstallHookAsync(processId, addr, captureRegisters);
            if (!result.Success) return $"Hook installation failed: {result.ErrorMessage}";
            var h = result.Hook!;
            return $"Stealth hook installed:\n  ID: {h.Id}\n  Target: 0x{h.OriginalAddress:X}\n  Cave: 0x{h.CaveAddress:X}\n  Stolen bytes: {h.OriginalBytesLength}\n  No debugger attached — completely game-safe.";
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
        if (hooks.Count == 0) return "No active code cave hooks.";
        var lines = hooks.Select(h => $"  [{h.Id}] 0x{h.OriginalAddress:X} → 0x{h.CaveAddress:X} ({h.OriginalBytesLength}B stolen, hits={h.HitCount})");
        return $"Active stealth hooks ({hooks.Count}):\n{string.Join('\n', lines)}";
    }

    [Description("Get register snapshots captured by a code cave hook. Returns the most recent captures with RAX, RBX, RCX, RDX, RSI, RDI, RSP values.")]
    public async Task<string> GetCodeCaveHookHits(
        [Description("Hook ID")] string hookId,
        [Description("Maximum entries to return")] int maxEntries = 20)
    {
        if (codeCaveEngine is null) return "Code cave engine not available.";
        var hits = await codeCaveEngine.GetHookHitsAsync(hookId, maxEntries);
        if (hits.Count == 0) return $"No hits recorded for hook {hookId}.";
        var lines = hits.Select(h =>
        {
            var regs = string.Join(", ", h.RegisterSnapshot.Take(4).Select(r => $"{r.Key}={r.Value}"));
            return $"  @ 0x{h.Address:X} | {regs}";
        });
        return $"Hook hits ({hits.Count} entries):\n{string.Join('\n', lines)}";
    }

    // ── Helpers ──

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
}
