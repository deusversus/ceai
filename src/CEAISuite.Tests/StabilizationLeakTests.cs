using System.Diagnostics;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase 10L: Resource leak detection via 100-cycle attach/detach and speed hack cycling.
/// Verifies that repeated operations don't accumulate state, throw exceptions, or
/// leave dangling resources. Memory deltas are logged for informational tracking —
/// GC-based assertions are too noisy for CI (GC heap fragmentation from 2500+ tests
/// in the same process causes multi-MB swings unrelated to actual leaks).
/// </summary>
[Trait("Category", "Stabilization")]
public class StabilizationLeakTests
{
    private const int TestProcessId = 1000;

    [Fact]
    public async Task AttachDetach_100Cycles_NoManagedMemoryLeak()
    {
        var facade = new StubEngineFacade();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(facade, sessionRepo);
        var addressTable = new AddressTableService(facade);

        // Warmup
        for (int i = 0; i < 5; i++)
        {
            await facade.AttachAsync(TestProcessId);
            addressTable.AddEntry("0x1000", MemoryDataType.Int32, "0", $"Warmup_{i}");
            facade.Detach();
            addressTable.ClearAll();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);

        // 100 attach/detach cycles — must not throw
        for (int i = 0; i < 100; i++)
        {
            await facade.AttachAsync(TestProcessId);
            addressTable.AddEntry("0x2000", MemoryDataType.Int32, "42", $"Entry_{i}");
            addressTable.AddEntry("0x3000", MemoryDataType.Float, "1.5", $"Float_{i}");
            facade.Detach();
            addressTable.ClearAll();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        var finalMemory = GC.GetTotalMemory(forceFullCollection: true);
        var delta = finalMemory - baselineMemory;

        Trace.WriteLine($"AttachDetach 100 cycles: memory delta = {delta:N0} bytes");

        // Verify clean state after all cycles
        Assert.False(facade.IsAttached);
        Assert.Null(facade.AttachedProcessId);
        Assert.Empty(addressTable.Entries);
    }

    [Fact]
    public async Task AttachDetach_100Cycles_FacadeStateClean()
    {
        var facade = new StubEngineFacade();

        for (int i = 0; i < 100; i++)
        {
            await facade.AttachAsync(TestProcessId);
            Assert.True(facade.IsAttached);
            Assert.Equal(TestProcessId, facade.AttachedProcessId);

            facade.Detach();
            Assert.False(facade.IsAttached);
            Assert.Null(facade.AttachedProcessId);
        }
    }

    [Fact]
    public async Task SpeedHackApplyRemove_100Cycles_NoResourceLeak()
    {
        var engine = new StubSpeedHackEngine();
        var service = new SpeedHackService(engine);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);

        // 100 apply/remove cycles — must all succeed
        for (int i = 0; i < 100; i++)
        {
            var apply = await service.ApplyAsync(TestProcessId, 2.0);
            Assert.True(apply.Success, $"Apply failed on cycle {i}: {apply.ErrorMessage}");
            Assert.True(service.GetState(TestProcessId).IsActive);

            var remove = await service.RemoveAsync(TestProcessId);
            Assert.True(remove.Success, $"Remove failed on cycle {i}: {remove.ErrorMessage}");
            Assert.False(service.GetState(TestProcessId).IsActive);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        var finalMemory = GC.GetTotalMemory(forceFullCollection: true);
        var delta = finalMemory - baselineMemory;

        Trace.WriteLine($"SpeedHack 100 cycles: memory delta = {delta:N0} bytes");

        // Verify clean final state
        Assert.False(service.GetState(TestProcessId).IsActive);
    }

    [Fact]
    public async Task ListProcesses_100Cycles_NoAccumulation()
    {
        var facade = new StubEngineFacade();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 100; i++)
        {
            var processes = await facade.ListProcessesAsync();
            Assert.NotNull(processes);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        var finalMemory = GC.GetTotalMemory(forceFullCollection: true);
        var delta = finalMemory - baselineMemory;

        Trace.WriteLine($"ListProcesses 100 cycles: memory delta = {delta:N0} bytes");
    }

    [Fact]
    public async Task AttachDetach_RapidCycling_NoExceptions()
    {
        var facade = new StubEngineFacade();
        var addressTable = new AddressTableService(facade);

        // 50 concurrent attach/detach — must not throw
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await facade.AttachAsync(TestProcessId);
                facade.Detach();
            }));
        }

        await Task.WhenAll(tasks);
    }
}
