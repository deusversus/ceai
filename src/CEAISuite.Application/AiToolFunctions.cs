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
    BreakpointService? breakpointService = null)
{
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

    [Description("Write a value to process memory. CAUTION: This modifies the target process.")]
    public async Task<string> WriteMemory(
        [Description("Process ID")] int processId,
        [Description("Memory address")] string address,
        [Description("Data type: Int32, Int64, Float, Double")] string dataType,
        [Description("Value to write")] string value)
    {
        var dt = Enum.Parse<MemoryDataType>(dataType, ignoreCase: true);
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

    [Description("Load a Cheat Engine .CT (Cheat Table) file and import its entries into the address table. Provide the full file path.")]
    public Task<string> LoadCheatTable([Description("Full file path to the .CT file")] string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            return Task.FromResult($"File not found: {filePath}");

        var parser = new CheatTableParser();
        var ctFile = parser.ParseFile(filePath);
        var entries = parser.ToAddressTableEntries(ctFile);

        foreach (var entry in entries)
        {
            addressTableService.AddEntry(entry.Address, entry.DataType, entry.CurrentValue, entry.Label);
        }

        var pointerCount = entries.Count(e => e.Notes?.StartsWith("Pointer") == true);
        return Task.FromResult(
            $"Loaded {ctFile.FileName}: {ctFile.TotalEntryCount} CT entries, " +
            $"{entries.Count} addresses imported ({pointerCount} pointers). " +
            $"Table version: {ctFile.TableVersion}" +
            (ctFile.LuaScript is not null ? ". Contains embedded Lua script." : ""));
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
}
