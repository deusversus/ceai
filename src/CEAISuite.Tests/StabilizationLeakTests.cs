using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase 10L: Memory and resource leak detection via attach/detach cycling.
/// Verifies that repeated attach/detach operations do not accumulate handles,
/// subscriptions, or managed objects beyond expected bounds.
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

        // Warmup: a few cycles to stabilize the GC
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

        // 100 attach/detach cycles with address table entries
        for (int i = 0; i < 100; i++)
        {
            await facade.AttachAsync(TestProcessId);

            // Simulate real usage: add entries, read values, then clear
            addressTable.AddEntry("0x2000", MemoryDataType.Int32, "42", $"Entry_{i}");
            addressTable.AddEntry("0x3000", MemoryDataType.Float, "1.5", $"Float_{i}");

            facade.Detach();
            addressTable.ClearAll();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        var finalMemory = GC.GetTotalMemory(forceFullCollection: true);

        // Allow up to 1MB growth (managed heap fragmentation, interned strings, etc.)
        var delta = finalMemory - baselineMemory;
        Assert.True(delta < 1_048_576,
            $"Managed memory grew by {delta:N0} bytes after 100 attach/detach cycles (threshold: 1MB). Possible leak.");
    }

    [Fact]
    public async Task AttachDetach_100Cycles_FacadeStateClean()
    {
        var facade = new StubEngineFacade();
        int attachCount = 0;
        int detachCount = 0;

        for (int i = 0; i < 100; i++)
        {
            await facade.AttachAsync(TestProcessId);
            attachCount++;
            Assert.True(facade.IsAttached);
            Assert.Equal(TestProcessId, facade.AttachedProcessId);

            facade.Detach();
            detachCount++;
            Assert.False(facade.IsAttached);
            Assert.Null(facade.AttachedProcessId);
        }

        // Verify attach/detach counts match exactly
        Assert.Equal(100, attachCount);
        Assert.Equal(100, detachCount);
    }

    [Fact]
    public async Task SpeedHackApplyRemove_100Cycles_NoResourceLeak()
    {
        var engine = new StubSpeedHackEngine();
        var service = new SpeedHackService(engine);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 100; i++)
        {
            var apply = await service.ApplyAsync(TestProcessId, 2.0);
            Assert.True(apply.Success, $"Apply failed on cycle {i}: {apply.ErrorMessage}");

            var state = service.GetState(TestProcessId);
            Assert.True(state.IsActive);

            var remove = await service.RemoveAsync(TestProcessId);
            Assert.True(remove.Success, $"Remove failed on cycle {i}: {remove.ErrorMessage}");

            state = service.GetState(TestProcessId);
            Assert.False(state.IsActive);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        var finalMemory = GC.GetTotalMemory(forceFullCollection: true);

        var delta = finalMemory - baselineMemory;
        Assert.True(delta < 1_048_576,
            $"Memory grew by {delta:N0} bytes after 100 speed hack cycles (threshold: 1MB). Possible leak.");
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
        Assert.True(delta < 1_048_576,
            $"Memory grew by {delta:N0} bytes after 100 ListProcesses cycles (threshold: 1MB).");
    }

    [Fact]
    public async Task AttachDetach_RapidCycling_NoExceptions()
    {
        var facade = new StubEngineFacade();
        var addressTable = new AddressTableService(facade);

        // Rapid-fire attach/detach with no delay — stress the state machine
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await facade.AttachAsync(TestProcessId);
                facade.Detach();
            }));
        }

        // Should not throw ConcurrentModificationException or similar
        await Task.WhenAll(tasks);
    }
}
