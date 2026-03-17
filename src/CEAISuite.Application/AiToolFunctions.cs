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
    PatchUndoService? patchUndoService = null)
{
    /// <summary>Queue of captured screenshots for injection into the AI conversation.</summary>
    public ConcurrentQueue<(string Description, byte[] PngData)> PendingImages { get; } = new();
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
        var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
        var probe = await dashboardService.ReadAddressAsync(processId, address, dt);
        return $"Read {dt} at {probe.Address}: {probe.DisplayValue}";
    }

    [Description("Write a value to process memory. Records original value for undo (Ctrl+Z). CAUTION: This modifies the target process.")]
    public async Task<string> WriteMemory(
        [Description("Process ID")] int processId,
        [Description("Memory address")] string address,
        [Description("Data type: Int32, Int64, Float, Double")] string dataType,
        [Description("Value to write")] string value)
    {
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

    [Description("Start a new memory scan for a value in a process. Returns number of results found.")]
    public async Task<string> StartScan(
        [Description("Process ID to scan")] int processId,
        [Description("Data type: Int32, Int64, Float, Double")] string dataType,
        [Description("Scan type: ExactValue, UnknownInitialValue, ArrayOfBytes")] string scanType,
        [Description("Value to search for. For ArrayOfBytes use hex pattern like '48 8B 05 ?? ?? ?? ??' where ?? is wildcard")] string? value)
    {
        var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
        var st = Enum.Parse<ScanType>(scanType, ignoreCase: true);
        scanService.ResetScan();
        var overview = await scanService.StartScanAsync(processId, dt, st, value ?? "");
        var topResults = overview.Results.Take(10)
            .Select(r => $"  {r.Address} = {r.CurrentValue}");
        return $"Scan complete: {overview.ResultCount:N0} results found.\n{string.Join('\n', topResults)}";
    }

    [Description("Refine the previous scan with a new constraint (e.g. value changed, increased, decreased, or new exact value).")]
    public async Task<string> RefineScan(
        [Description("Scan type: ExactValue, Increased, Decreased, Changed, Unchanged")] string scanType,
        [Description("Value to match (for ExactValue) or empty")] string? value)
    {
        var st = Enum.Parse<ScanType>(scanType, ignoreCase: true);
        var overview = await scanService.RefineScanAsync(st, value ?? "");
        var topResults = overview.Results.Take(10)
            .Select(r => $"  {r.Address} = {r.CurrentValue} (was {r.PreviousValue})");
        return $"Refinement complete: {overview.ResultCount:N0} results remaining.\n{string.Join('\n', topResults)}";
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

    [Description("Set a breakpoint at a memory address. Types: Software, HardwareExecute, HardwareWrite, HardwareReadWrite. Use to trace code execution or find what accesses/writes an address.")]
    public async Task<string> SetBreakpoint(
        [Description("Process ID")] int processId,
        [Description("Memory address (hex or decimal)")] string address,
        [Description("Breakpoint type: Software, HardwareExecute, HardwareWrite, HardwareReadWrite")] string type = "Software",
        [Description("Hit action: Break, Log, LogAndContinue")] string hitAction = "LogAndContinue")
    {
        if (breakpointService is null) return "Breakpoint engine not available.";
        var bpType = Enum.Parse<BreakpointType>(type, ignoreCase: true);
        var bpAction = Enum.Parse<BreakpointHitAction>(hitAction, ignoreCase: true);
        var bp = await breakpointService.SetBreakpointAsync(processId, address, bpType, bpAction);
        return $"Breakpoint {bp.Id} set at {bp.Address} (type: {bp.Type}, action: {bp.HitAction})";
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

        try
        {
            var result = await autoAssemblerEngine.EnableAsync(
                dashboard.CurrentInspection.ProcessId, node.AssemblerScript!);
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
            return $"Script error: {ex.Message}";
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
