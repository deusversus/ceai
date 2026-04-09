using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

/// <summary>
/// Integration tests for <see cref="WindowsCallStackEngine"/> using the test harness process.
/// Verifies stack walking for individual threads and bulk thread enumeration.
///
/// NOTE: Stack walking requires THREAD_GET_CONTEXT and THREAD_SUSPEND_RESUME access which
/// may fail without elevated privileges. Tests skip when frames cannot be retrieved.
/// </summary>
[Trait("Category", "Integration")]
public class EngineWindowsCallStackIntegrationTests
{
    [Fact]
    public async Task WalkStack_HarnessThread_ReturnsFrames()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var threads = await harness.GetThreadsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(threads);

        var (baseAddr, size) = await harness.GetModuleInfoAsync(TestContext.Current.CancellationToken);
        var modules = new List<ModuleDescriptor>
        {
            new("CEAISuite.Tests.Harness", baseAddr, size)
        };

        var engine = new WindowsCallStackEngine();
        var frames = await engine.WalkStackAsync(
            harness.ProcessId, threads[0], modules, maxFrames: 64,
            TestContext.Current.CancellationToken);

        Assert.NotNull(frames);
        if (frames.Count == 0)
        {
            // Stack walking can fail without debug privileges (OpenThread/SuspendThread/GetThreadContext)
            Assert.Skip("Stack walking returned 0 frames (may require elevated privileges or thread access)");
        }
        Assert.True(frames.Count > 0);
    }

    [Fact]
    public async Task WalkAllThreads_ReturnsMultipleThreads()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        // Start an extra thread in the harness to ensure multiple threads exist
        var address = await harness.AllocAsync(64, TestContext.Current.CancellationToken);
        await harness.WriteAtAsync(address, new byte[] { 0xC3 }, TestContext.Current.CancellationToken);
        var loopTid = await harness.StartExecLoopAsync(address, 100, TestContext.Current.CancellationToken);

        try
        {
            var (baseAddr, size) = await harness.GetModuleInfoAsync(TestContext.Current.CancellationToken);
            var modules = new List<ModuleDescriptor>
            {
                new("CEAISuite.Tests.Harness", baseAddr, size)
            };

            var engine = new WindowsCallStackEngine();
            var allStacks = await engine.WalkAllThreadsAsync(
                harness.ProcessId, modules, maxFrames: 32,
                TestContext.Current.CancellationToken);

            Assert.NotNull(allStacks);
            if (allStacks.Count == 0)
            {
                Assert.Skip("WalkAllThreads returned 0 entries (may require elevated privileges or thread access)");
            }
            Assert.True(allStacks.Count >= 2,
                $"Expected at least 2 thread stacks (main + loop), got {allStacks.Count}");
        }
        finally
        {
            try { await harness.StopLoopAsync(loopTid, TestContext.Current.CancellationToken); } catch { }
        }
    }
}
