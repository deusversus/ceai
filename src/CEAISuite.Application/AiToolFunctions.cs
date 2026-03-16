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
    DisassemblyService disassemblyService)
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
        [Description("Scan type: ExactValue, UnknownInitialValue")] string scanType,
        [Description("Value to search for (leave empty for unknown scan)")] string? value)
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
}
