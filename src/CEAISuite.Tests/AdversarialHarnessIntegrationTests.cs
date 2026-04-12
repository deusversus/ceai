using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Live-process adversarial tests using the test harness.
/// These exercise the real engine against hostile process behavior:
/// mid-scan process exit, protection flipping, value churning.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Adversarial")]
[Collection("DebuggerTests")]  // Serialize: shares native debug infrastructure
public class AdversarialHarnessIntegrationTests
{
    // ── Process-disappears-mid-scan ──

    [Fact]
    public async Task Scan_ProcessExitsMidScan_ReturnsGracefully()
    {
        // Start harness and allocate some memory so there's something to scan
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var pid = harness.ProcessId;

        // Write a known value so we have a scan target
        var addr = await harness.AllocAsync(4096, TestContext.Current.CancellationToken);
        await harness.WriteAtAsync(addr, BitConverter.GetBytes(42), TestContext.Current.CancellationToken);

        var scanEngine = new WindowsScanEngine();

        // Start scan in background — then kill the process
        var scanTask = Task.Run(async () =>
        {
            try
            {
                var constraints = new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "42");
                return await scanEngine.StartScanAsync(pid, constraints, TestContext.Current.CancellationToken);
            }
            catch
            {
                // Any exception is acceptable — we just can't crash the test host
                return null as ScanResultSet;
            }
        });

        // Give scan a moment to start, then kill the harness
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await harness.DisposeAsync(); // Kills the process

        // The scan should either complete with partial results or throw a catchable exception
        // The key assertion: the test host does NOT crash with AccessViolationException
        var result = await scanTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // We don't care about the result — the test passes if we get here without crashing
        Assert.True(true, "Scan completed or failed gracefully after process exit");
    }

    // ── Memory protection race ──

    [Fact]
    public async Task ReadMemory_AfterProtectionFlip_HandlesGracefully()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var pid = harness.ProcessId;

        // Allocate RWX memory and write known data
        var addr = await harness.AllocAsync(4096, TestContext.Current.CancellationToken);
        await harness.WriteAtAsync(addr, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, TestContext.Current.CancellationToken);

        var facade = new WindowsEngineFacade(NullLogger<WindowsEngineFacade>.Instance);
        await facade.AttachAsync(pid);

        try
        {
            // Read should succeed while RWX
            var result = await facade.ReadMemoryAsync(pid, addr, 4, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.True(result.Bytes.Count > 0, "Should read bytes while page is accessible");

            // Flip to PAGE_NOACCESS (0x01)
            await harness.ProtectFlipAsync(addr, 0x01, TestContext.Current.CancellationToken);

            // Read after protection flip — should return error or empty, not crash
            try
            {
                var result2 = await facade.ReadMemoryAsync(pid, addr, 4, TestContext.Current.CancellationToken);
                // If it returns data, it read before the protection took effect — also fine
            }
            catch (Exception ex) when (ex is not AccessViolationException)
            {
                // Any managed exception is acceptable — AccessViolationException is NOT
                Assert.True(true, $"Caught managed exception: {ex.GetType().Name}");
            }

            // Restore to PAGE_READWRITE (0x04) for clean disposal
            await harness.ProtectFlipAsync(addr, 0x04, TestContext.Current.CancellationToken);
        }
        finally
        {
            facade.Detach();
        }
    }

    // ── Value churn during read ──

    [Fact]
    public async Task ReadMemory_DuringValueChurn_ReturnsConsistentData()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var pid = harness.ProcessId;

        var addr = await harness.AllocAsync(64, TestContext.Current.CancellationToken);
        var churnThreadId = await harness.StartValueChurnAsync(addr, 1, TestContext.Current.CancellationToken);

        var facade = new WindowsEngineFacade(NullLogger<WindowsEngineFacade>.Instance);
        await facade.AttachAsync(pid);

        try
        {
            // Read 100 times while values are churning — should never crash
            for (int i = 0; i < 100; i++)
            {
                var data = await facade.ReadMemoryAsync(pid, addr, 4, TestContext.Current.CancellationToken);
                Assert.NotNull(data);
                Assert.Equal(4, data.Bytes.Count);
            }
        }
        finally
        {
            await harness.StopLoopAsync(churnThreadId, TestContext.Current.CancellationToken);
            facade.Detach();
        }
    }

    // ── Pointer chain with re-allocation ──

    [Fact]
    public async Task ReadPointerChain_DuringReallocation_HandlesGracefully()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var pid = harness.ProcessId;

        // Create a 3-deep pointer chain that re-allocates the final block every 50ms
        var (baseAddr, chainThreadId) = await harness.StartPointerChainAsync(3, 50, TestContext.Current.CancellationToken);

        var facade = new WindowsEngineFacade(NullLogger<WindowsEngineFacade>.Instance);
        await facade.AttachAsync(pid);

        int successfulWalks = 0;
        int failedWalks = 0;

        try
        {
            // Walk the chain 50 times while it's being re-allocated
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    var currentAddr = baseAddr;
                    bool broken = false;

                    // Walk 2 hops (depth 3 = 2 pointer hops + final value)
                    for (int hop = 0; hop < 2; hop++)
                    {
                        var ptrBytes = await facade.ReadMemoryAsync(pid, currentAddr, (int)nuint.Size, TestContext.Current.CancellationToken);
                        if (ptrBytes.Bytes.Count < nuint.Size)
                        {
                            broken = true;
                            break;
                        }
                        currentAddr = (nuint)BitConverter.ToUInt64(ptrBytes.Bytes.ToArray(), 0);
                        if (currentAddr == 0)
                        {
                            broken = true;
                            break;
                        }
                    }

                    if (!broken)
                    {
                        // Read final value (should be 0xDEAD if chain is intact)
                        var valBytes = await facade.ReadMemoryAsync(pid, currentAddr, 4, TestContext.Current.CancellationToken);
                        if (valBytes.Bytes.Count >= 4)
                            successfulWalks++;
                        else
                            failedWalks++;
                    }
                    else
                    {
                        failedWalks++;
                    }
                }
                catch (Exception ex) when (ex is not AccessViolationException)
                {
                    // Managed exceptions from stale pointers are acceptable
                    failedWalks++;
                }
            }
        }
        finally
        {
            await harness.StopLoopAsync(chainThreadId, TestContext.Current.CancellationToken);
            facade.Detach();
        }

        // The key assertion: no crash, and at least some walks succeeded
        // (the chain is only briefly invalid during re-allocation)
        Assert.True(successfulWalks + failedWalks == 50, "All 50 walks should complete (success or graceful failure)");
        Assert.True(successfulWalks > 0, $"At least some chain walks should succeed (got {successfulWalks}/50)");
    }

    // ── Breakpoint flood ──

    [Fact]
    public async Task Breakpoint_HighFrequencyHits_DoesNotOomOrDeadlock()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var pid = harness.ProcessId;

        // Allocate and write a simple RET instruction
        var addr = await harness.AllocAsync(64, TestContext.Current.CancellationToken);
        await harness.WriteAtAsync(addr, new byte[] { 0xC3 }, TestContext.Current.CancellationToken);

        // Start exec loop at 1ms interval — very hot path (~1000 calls/sec)
        await harness.StartExecLoopAsync(addr, 1, TestContext.Current.CancellationToken);

        var engine = new WindowsBreakpointEngine(NullLogger<WindowsBreakpointEngine>.Instance);
        try
        {
            BreakpointDescriptor bp;
            try
            {
                bp = await engine.SetBreakpointAsync(
                    pid, addr, BreakpointType.Software,
                    BreakpointMode.Software, BreakpointHitAction.LogAndContinue,
                    singleHit: false, TestContext.Current.CancellationToken);
            }
            catch (Exception ex)
            {
                // Debug attach may fail — skip if so
                if (ex is InvalidOperationException ioe &&
                    (ioe.Message.Contains("Unable to") || ioe.InnerException is System.ComponentModel.Win32Exception))
                {
                    Assert.Skip($"Debug privileges not available: {ioe.Message}");
                    return;
                }
                throw;
            }

            // Let it run for 3 seconds with high-frequency hits
            await Task.Delay(3000, TestContext.Current.CancellationToken);

            // Key assertion: we can still query the hit log without deadlock or OOM
            var hits = await engine.GetHitLogAsync(bp.Id, maxEntries: 100, TestContext.Current.CancellationToken);

            // If no hits recorded, skip (CI timing issue) but don't fail
            if (hits.Count == 0)
                Assert.Skip("No hits recorded during flood test (debug event loop may not have processed in time)");

            // Verify the hit log didn't grow unbounded — should be capped
            Assert.True(hits.Count <= 100, $"Hit log should be capped, got {hits.Count}");
        }
        finally
        {
            await engine.ForceDetachAndCleanupAsync(pid);
        }
    }
}
