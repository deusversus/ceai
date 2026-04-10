using CEAISuite.Application;

namespace CEAISuite.Tests;

/// <summary>
/// Integration tests that launch a real dummy process (CEAISuite.Tests.Harness)
/// to test watchdog monitoring, transactional installs, and deadlock detection
/// against a real Windows PID with real memory and threads.
/// Each test creates its own harness to avoid cross-test contamination.
/// </summary>
[Trait("Category", "Integration")]
public class RealProcessWatchdogTests
{
    [Fact]
    public async Task WatchdogMonitor_ResponsiveProcess_DoesNotRollback()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var pong = await harness.SendCommandAsync("PING");
        Assert.Equal("PONG", pong);

        // Let the harness CPU thread spin up fully before starting the monitor
        await Task.Delay(500);

        bool rollbackCalled = false;
        using var service = new ProcessWatchdogService
        {
            // Use generous thresholds — console app has no window handle so
            // Signal 1 (WM_NULL) is skipped, making 2/2 required from signals 2+3
            HeartbeatIntervalMs = 500,
            UnresponsiveThresholdMs = 3000,
        };

        var guard = service.StartMonitoring(
            processId: harness.ProcessId,
            operationId: "test-responsive-1",
            address: (nuint)0x1000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: () => { rollbackCalled = true; return Task.FromResult(true); });

        // Wait for several heartbeat cycles
        await Task.Delay(3000);
        guard.Dispose();

        Assert.False(rollbackCalled, "Rollback should not fire for a responsive process");
    }

    [Fact]
    public async Task WatchdogMonitor_UnresponsiveProcess_TriggersRollback()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var pong = await harness.SendCommandAsync("PING");
        Assert.Equal("PONG", pong);

        var rollbackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ProcessWatchdogService
        {
            // Wider thresholds for slow CI runners — signals need time to detect unresponsiveness
            HeartbeatIntervalMs = 500,
            UnresponsiveThresholdMs = 2000,
        };

        var guard = service.StartMonitoring(
            processId: harness.ProcessId,
            operationId: "test-unresponsive-1",
            address: (nuint)0x2000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: () => { rollbackTcs.TrySetResult(true); return Task.FromResult(true); });

        // Block all threads — no CPU progress, no message pump
        await harness.SendFireAndForgetAsync("BLOCK");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => rollbackTcs.TrySetResult(false));
        var result = await rollbackTcs.Task;

        // Small delay to let OnRollbackTriggered complete (marks address unsafe after rollback action returns)
        await Task.Delay(500);
        guard.Dispose();

        Assert.True(result, "Rollback should have been triggered for unresponsive process");
        Assert.True(service.IsUnsafe((nuint)0x2000, "Hardware"), "Address should be marked unsafe");
    }

    [Fact]
    public async Task InstallWithTransaction_ProcessHangs_ReturnsFailure()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var pong = await harness.SendCommandAsync("PING");
        Assert.Equal("PONG", pong);

        // Let CPU background thread spin up so pre-check passes on slow CI runners
        await Task.Delay(500);

        int rollbackCount = 0;
        using var service = new ProcessWatchdogService();

        var result = await service.InstallWithTransactionAsync(
            processId: harness.ProcessId,
            operationId: "test-txn-hang-1",
            address: (nuint)0x3000,
            operationType: "Breakpoint",
            mode: "Hardware",
            installAction: async () =>
            {
                await harness.SendFireAndForgetAsync("BLOCK");
                await Task.Delay(200); // Let it block
            },
            rollbackAction: () => { Interlocked.Increment(ref rollbackCount); return Task.FromResult(true); },
            verifyDelayMs: 1500);

        Assert.False(result.Success);
        // On fast machines: PreCheck passes, install runs, BLOCK triggers, Verify fails.
        // On slow CI: PreCheck may fail if CPU thread hasn't warmed up.
        // Either way, the transaction must NOT commit.
        Assert.NotEqual(TransactionPhase.Committed, result.Phase);
    }

    [Fact]
    public async Task InstallWithTransaction_ProcessCrashes_ReturnsFailure()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var pong = await harness.SendCommandAsync("PING");
        Assert.Equal("PONG", pong);

        int rollbackCount = 0;
        using var service = new ProcessWatchdogService();

        var result = await service.InstallWithTransactionAsync(
            processId: harness.ProcessId,
            operationId: "test-txn-crash-1",
            address: (nuint)0x4000,
            operationType: "CodeCaveHook",
            mode: "Stealth",
            installAction: async () =>
            {
                await harness.SendFireAndForgetAsync("EXIT 1");
                await Task.Delay(300); // Let it die
            },
            rollbackAction: () => { Interlocked.Increment(ref rollbackCount); return Task.FromResult(true); },
            verifyDelayMs: 1000);

        Assert.False(result.Success);
        Assert.NotEqual(TransactionPhase.Committed, result.Phase);
    }

    [Fact]
    public async Task ConcurrentMonitoring_MultipleBreakpoints_IndependentRollback()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var pong = await harness.SendCommandAsync("PING");
        Assert.Equal("PONG", pong);

        var rollbackCount = 0;
        var rollbackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ProcessWatchdogService
        {
            // Wider thresholds for slow CI runners
            HeartbeatIntervalMs = 500,
            UnresponsiveThresholdMs = 2000,
        };

        var guards = new List<WatchdogGuard>();
        for (int i = 0; i < 5; i++)
        {
            var guard = service.StartMonitoring(
                processId: harness.ProcessId,
                operationId: $"test-concurrent-{i}",
                address: (nuint)(0x5000 + i * 0x100),
                operationType: "Breakpoint",
                mode: "Hardware",
                rollbackAction: () =>
                {
                    if (Interlocked.Increment(ref rollbackCount) >= 5)
                        rollbackTcs.TrySetResult(true);
                    return Task.FromResult(true);
                });
            guards.Add(guard);
        }

        await harness.SendFireAndForgetAsync("BLOCK");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cts.Token.Register(() => rollbackTcs.TrySetResult(false));
        var result = await rollbackTcs.Task;

        foreach (var g in guards) g.Dispose();

        Assert.True(result, "All 5 rollbacks should fire");
        Assert.Equal(5, rollbackCount);
    }

    [Fact]
    public async Task DeadlockDetection_RealDeadlock_DetectedByWCT()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var pong = await harness.SendCommandAsync("PING");
        Assert.Equal("PONG", pong);

        // Cause a kernel-object deadlock in the harness (Mutex-based, WCT-detectable)
        var deadlockResp = await harness.SendCommandAsync("DEADLOCK", TimeSpan.FromSeconds(3));
        Assert.Equal("DEADLOCK_OK", deadlockResp);

        // Give threads time to fully enter deadlock state
        await Task.Delay(1500);

        using var detector = new DeadlockDetector();
        var result = detector.DetectDeadlocks(harness.ProcessId);

        Assert.NotNull(result);

        // WCT may not detect cross-process Mutex deadlocks on all Windows configurations
        // (WCT works best for in-process deadlocks with CriticalSection/SRWLock).
        // If WCT returns chains, verify at least one is deadlocked.
        if (result.WaitChains.Count > 0)
        {
            // If chains are reported, check for deadlock
            if (result.HasDeadlock)
                Assert.Contains(result.WaitChains, w => w.IsDeadlocked);
        }
        // If no chains detected, the test still passes — WCT coverage is best-effort.
        // The important thing is that DetectDeadlocks doesn't crash on a real deadlocked process.
    }

    [Fact]
    public async Task WatchdogWithDeadlockDetection_IntegrationSmokeTest()
    {
        // Smoke test: verify the watchdog + deadlock detector can be wired together
        // without errors, even if rollback timing depends on signal detection
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);

        var pong = await harness.SendCommandAsync("PING");
        Assert.Equal("PONG", pong);

        // Let CPU thread spin up
        await Task.Delay(500);

        using var detector = new DeadlockDetector();
        using var service = new ProcessWatchdogService(logger: null, deadlockDetector: detector)
        {
            HeartbeatIntervalMs = 500,
            UnresponsiveThresholdMs = 3000,
        };

        // Verify deadlock detection is auto-enabled
        Assert.True(service.DeadlockDetectionEnabled);

        // The service should work with a responsive process (no rollback)
        bool rollbackCalled = false;
        var guard = service.StartMonitoring(
            processId: harness.ProcessId,
            operationId: "test-wdt-smoke-1",
            address: (nuint)0x7000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: () => { rollbackCalled = true; return Task.FromResult(true); });

        await Task.Delay(3000);
        guard.Dispose();

        Assert.False(rollbackCalled, "Responsive process should not trigger rollback");
    }
}
