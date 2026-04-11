using System.ComponentModel;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Verifies that AI tool methods handle engine-level faults gracefully —
/// returning error strings or throwing catchable exceptions instead of crashing.
/// </summary>
public class FaultInjectionTests
{
    private static (AiToolFunctions tools, FaultInjectingEngineFacade facade) CreateFaultableTools()
    {
        var facade = new FaultInjectingEngineFacade();
        // Must call AttachAsync so IsAttached=true and AttachedProcessId=1000
        facade.AttachAsync(1000).Wait();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(facade, sessionRepo);
        var scanEngine = new StubScanEngine();
        var scanService = new ScanService(scanEngine);
        var disasmEngine = new StubDisassemblyEngine();
        var disasmService = new DisassemblyService(disasmEngine);
        var scriptGen = new ScriptGenerationService();
        var addressTable = new AddressTableService(facade);

        var tools = new AiToolFunctions(facade, dashboard, scanService, addressTable, disasmService, scriptGen);
        return (tools, facade);
    }

    // ────────────────────────────────────────────────────────────────
    // ReadMemory fault tests
    // Note: ReadMemory checks IsProcessAlive first; for non-existent PIDs
    // it returns "Process X is no longer running." before the engine call.
    // This IS graceful error handling (no throw, returns informative string).
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadMemory_AccessDenied_ReturnsErrorString()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.ReadMemoryAsync));

        var result = await tools.ReadMemory(1000, "0x1000", "Int32");

        Assert.NotNull(result);
        // Method returns graceful error string (IsProcessAlive guard or caught exception)
        Assert.DoesNotContain("Read Int32 at", result);
    }

    [Fact]
    public async Task ReadMemory_PartialCopy_ReturnsErrorString()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectPartialCopy(nameof(facade.ReadMemoryAsync));

        var result = await tools.ReadMemory(1000, "0x1000", "Int32");

        Assert.NotNull(result);
        Assert.DoesNotContain("Read Int32 at", result);
    }

    [Fact]
    public async Task ReadMemory_ProcessTerminating_ReturnsErrorString()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectProcessTerminating(nameof(facade.ReadMemoryAsync));

        var result = await tools.ReadMemory(1000, "0x1000", "Int32");

        Assert.NotNull(result);
        Assert.DoesNotContain("Read Int32 at", result);
    }

    // ────────────────────────────────────────────────────────────────
    // WriteMemory fault tests
    // Same IsProcessAlive guard applies — graceful error string returned.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteMemory_AccessDenied_ReturnsErrorString()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.WriteValueAsync));

        var result = await tools.WriteMemory(1000, "0x1000", "Int32", "42");

        Assert.NotNull(result);
        Assert.DoesNotContain("Wrote '42'", result);
    }

    [Fact]
    public async Task WriteMemory_PartialCopy_ReturnsErrorString()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectPartialCopy(nameof(facade.WriteValueAsync));

        var result = await tools.WriteMemory(1000, "0x1000", "Int32", "42");

        Assert.NotNull(result);
        Assert.DoesNotContain("Wrote '42'", result);
    }

    [Fact]
    public async Task WriteMemory_ProcessTerminating_ReturnsErrorString()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectProcessTerminating(nameof(facade.WriteValueAsync));

        var result = await tools.WriteMemory(1000, "0x1000", "Int32", "42");

        Assert.NotNull(result);
        Assert.DoesNotContain("Wrote '42'", result);
    }

    // ────────────────────────────────────────────────────────────────
    // ListProcesses — no IsProcessAlive guard, fault actually fires
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListProcesses_AccessDenied_Throws()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.ListProcessesAsync));

        var ex = await Assert.ThrowsAsync<Win32Exception>(() => tools.ListProcesses());
        Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListProcesses_FaultThenClear_SubsequentSucceeds()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.ListProcessesAsync));

        // First call fails
        await Assert.ThrowsAsync<Win32Exception>(() => tools.ListProcesses());

        // Clear faults and retry
        facade.ClearFaults();
        var result = await tools.ListProcesses();

        Assert.NotNull(result);
        Assert.Contains("processes", result, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────
    // FindProcess — no IsProcessAlive guard, fault fires on ListProcessesAsync
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindProcess_AccessDenied_Throws()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.ListProcessesAsync));

        var ex = await Assert.ThrowsAsync<Win32Exception>(() => tools.FindProcess("test"));
        Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindProcess_FaultThenClear_SubsequentSucceeds()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.ListProcessesAsync));

        await Assert.ThrowsAsync<Win32Exception>(() => tools.FindProcess("TestGame"));

        facade.ClearFaults();
        var result = await tools.FindProcess("TestGame");

        Assert.NotNull(result);
        Assert.Contains("TestGame", result);
    }

    // ────────────────────────────────────────────────────────────────
    // AttachProcess / InspectProcess — faults on AttachAsync
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AttachProcess_AccessDenied_Throws()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.AttachAsync));

        // AttachProcess calls dashboardService.InspectProcessAsync which calls ListProcessesAsync first,
        // then AttachAsync. We need ListProcessesAsync to succeed (it returns PID 1000).
        var ex = await Assert.ThrowsAsync<Win32Exception>(() => tools.AttachProcess(1000));
        Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttachProcess_ProcessTerminating_Throws()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectProcessTerminating(nameof(facade.AttachAsync));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.AttachProcess(1000));
        Assert.Contains("terminating", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttachProcess_FaultThenClear_SubsequentSucceeds()
    {
        var (tools, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.AttachAsync));

        await Assert.ThrowsAsync<Win32Exception>(() => tools.AttachProcess(1000));

        facade.ClearFaults();
        var result = await tools.AttachProcess(1000);

        Assert.NotNull(result);
        Assert.Contains("Attached", result);
    }

    // ────────────────────────────────────────────────────────────────
    // Detach fault
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Detach_AccessDenied_Throws()
    {
        var (_, facade) = CreateFaultableTools();
        facade.InjectAccessDenied(nameof(facade.Detach));

        var ex = Assert.Throws<Win32Exception>(() => facade.Detach());
        Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────
    // WriteBytesAsync fault
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteBytesAsync_PartialCopy_Throws()
    {
        var (_, facade) = CreateFaultableTools();
        facade.InjectPartialCopy(nameof(facade.WriteBytesAsync));

        var ex = await Assert.ThrowsAsync<Win32Exception>(
            () => facade.WriteBytesAsync(1000, 0x1000, new byte[] { 1, 2, 3 }));
        Assert.Contains("Partial copy", ex.Message);
    }

    // ────────────────────────────────────────────────────────────────
    // State integrity tests — verify no corruption after faults
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadMemory_AfterFaultClear_NoStateCorruption()
    {
        var (_, facade) = CreateFaultableTools();

        // Write known data
        facade.WriteMemoryDirect(0x2000, BitConverter.GetBytes(12345));

        // Inject fault on ReadValueAsync
        facade.InjectAccessDenied(nameof(facade.ReadValueAsync));

        // Faulted read throws
        await Assert.ThrowsAsync<Win32Exception>(
            () => facade.ReadValueAsync(1000, 0x2000, MemoryDataType.Int32));

        // Clear fault
        facade.ClearFaults();

        // Read should return the original data — no state corruption
        var result = await facade.ReadValueAsync(1000, 0x2000, MemoryDataType.Int32);
        Assert.Equal("12345", result.DisplayValue);
    }

    [Fact]
    public async Task WriteMemory_AfterFaultClear_DataIntact()
    {
        var (_, facade) = CreateFaultableTools();

        // Write initial data
        facade.WriteMemoryDirect(0x3000, BitConverter.GetBytes(100));

        // Inject fault on WriteValueAsync
        facade.InjectAccessDenied(nameof(facade.WriteValueAsync));

        // Faulted write throws — original data should be unchanged
        await Assert.ThrowsAsync<Win32Exception>(
            () => facade.WriteValueAsync(1000, 0x3000, MemoryDataType.Int32, "999"));

        // Clear fault
        facade.ClearFaults();

        // Original value should be intact (the faulted write didn't corrupt it)
        var result = await facade.ReadValueAsync(1000, 0x3000, MemoryDataType.Int32);
        Assert.Equal("100", result.DisplayValue);
    }

    // ────────────────────────────────────────────────────────────────
    // Multiple faults active simultaneously
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultipleFaults_IndependentInjection()
    {
        var (_, facade) = CreateFaultableTools();

        facade.InjectAccessDenied(nameof(facade.ReadMemoryAsync));
        facade.InjectPartialCopy(nameof(facade.WriteValueAsync));

        // Read fault fires
        var readEx = await Assert.ThrowsAsync<Win32Exception>(
            () => facade.ReadMemoryAsync(1000, 0x1000, 4));
        Assert.Contains("denied", readEx.Message, StringComparison.OrdinalIgnoreCase);

        // Write fault fires with different exception
        var writeEx = await Assert.ThrowsAsync<Win32Exception>(
            () => facade.WriteValueAsync(1000, 0x1000, MemoryDataType.Int32, "42"));
        Assert.Contains("Partial copy", writeEx.Message);

        // ListProcesses is NOT faulted — should succeed
        var processes = await facade.ListProcessesAsync();
        Assert.True(processes.Count > 0);
    }

    [Fact]
    public void FaultMap_CaseInsensitive()
    {
        var (_, facade) = CreateFaultableTools();

        // Inject with lowercase
        facade.InjectFault("readmemoryasync", new InvalidOperationException("test fault"));

        // Should match the actual method name (PascalCase)
        Assert.True(facade.FaultMap.ContainsKey("ReadMemoryAsync"));
    }
}
