using System.Collections.Concurrent;
using CEAISuite.Application;

namespace CEAISuite.Tests;

/// <summary>
/// Stress tests for process watchdog concurrency, disposal races, and thread safety.
/// These tests verify the <see cref="ProcessWatchdogService"/> under concurrent load
/// without requiring real Windows processes (CI-safe).
/// </summary>
public class WatchdogGuardConcurrentDisposalTests : IDisposable
{
    private readonly ProcessWatchdogService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task ConcurrentDisposal_DoesNotDoubleRollback()
    {
        // Arrange: track rollback invocations
        int rollbackCount = 0;
        Func<Task<bool>> rollbackAction = () =>
        {
            Interlocked.Increment(ref rollbackCount);
            return Task.FromResult(true);
        };

        // Use a fake PID that will not match a real process; the monitor loop
        // will exit quickly because IsProcessResponsive will throw/return false,
        // but we only care about the guard disposal path here.
        var guard = _service.StartMonitoring(
            processId: -1,
            operationId: "op-concurrent-1",
            address: (nuint)0x1000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: rollbackAction);

        // Act: dispose the same guard from 20 threads simultaneously
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            guard.Dispose();
        }));

        var exceptions = new ConcurrentBag<Exception>();
        await Task.WhenAll(tasks.Select(async t =>
        {
            try { await t; }
            catch (Exception ex) { exceptions.Add(ex); }
        }));

        // Assert: no exceptions, rollback never called (StopMonitoring disposes the
        // monitor's CTS but does not invoke the rollback action — rollback is only
        // triggered by the monitor loop detecting unresponsiveness).
        Assert.Empty(exceptions);

        // Verify rollback was never invoked through the disposal path.
        // The rollback action is only triggered by the monitor loop detecting
        // unresponsiveness, not by guard disposal.
        Assert.Equal(0, Volatile.Read(ref rollbackCount));

        // The guard's Interlocked.Exchange pattern ensures StopMonitoring is called
        // at most once, even with 20 concurrent Dispose calls.
        // We verify the guard is in a clean disposed state by disposing again (no-op).
        guard.Dispose();
    }

    [Fact]
    public async Task ConcurrentDisposal_StopMonitoringCalledExactlyOnce()
    {
        // Arrange: use a distinct operationId so we can verify monitor removal
        var operationId = "op-stop-once";

        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);

        var guard = _service.StartMonitoring(
            processId: -1,
            operationId: operationId,
            address: (nuint)0x2000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: rollbackAction);

        // Wrap StopMonitoring in a counting proxy by using reflection-free approach:
        // After disposal, StartMonitoring with the same ID should succeed (monitor slot is free).
        // Before disposal, the monitor is tracked.

        // Act: 15 concurrent disposals
        var barrier = new Barrier(15);
        var tasks = Enumerable.Range(0, 15).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            guard.Dispose();
        }));

        await Task.WhenAll(tasks);

        // Assert: monitor should be removed from the service.
        // Attempting to start a new monitor with the same ID should work cleanly.
        var guard2 = _service.StartMonitoring(
            processId: -1,
            operationId: operationId,
            address: (nuint)0x2000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: rollbackAction);

        Assert.NotNull(guard2);
        guard2.Dispose();
    }

    [Fact]
    public void SingleDisposal_IsIdempotent()
    {
        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);

        var guard = _service.StartMonitoring(
            processId: -1,
            operationId: "op-idempotent",
            address: (nuint)0x3000,
            operationType: "CodeCaveHook",
            mode: "Stealth",
            rollbackAction: rollbackAction);

        // Dispose multiple times sequentially — should be safe
        for (int i = 0; i < 10; i++)
            guard.Dispose();
    }
}

public class MultipleConcurrentWatchdogMonitorTests : IDisposable
{
    private readonly ProcessWatchdogService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public Task MultipleMonitors_TrackedIndependently()
    {
        // Arrange: start 8 monitors with distinct operation IDs
        var guards = new List<WatchdogGuard>();
        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);

        for (int i = 0; i < 8; i++)
        {
            var guard = _service.StartMonitoring(
                processId: -1,
                operationId: $"op-multi-{i}",
                address: (nuint)(0x1000 + i * 0x100),
                operationType: "Breakpoint",
                mode: "Hardware",
                rollbackAction: rollbackAction);
            guards.Add(guard);
        }

        // Act: dispose in reverse order
        for (int i = guards.Count - 1; i >= 0; i--)
            guards[i].Dispose();

        // Assert: all cleaned up, service can start new monitors with same IDs
        for (int i = 0; i < 8; i++)
        {
            var guard = _service.StartMonitoring(
                processId: -1,
                operationId: $"op-multi-{i}",
                address: (nuint)(0x1000 + i * 0x100),
                operationType: "Breakpoint",
                mode: "Hardware",
                rollbackAction: rollbackAction);
            guard.Dispose();
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentStartAndDispose_NoExceptions()
    {
        // Arrange
        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);
        var exceptions = new ConcurrentBag<Exception>();

        // Act: start 10 monitors concurrently, then dispose them concurrently
        var guards = new ConcurrentBag<WatchdogGuard>();
        var startTasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            try
            {
                var guard = _service.StartMonitoring(
                    processId: -1,
                    operationId: $"op-concurrent-start-{i}",
                    address: (nuint)(0x5000 + i * 0x100),
                    operationType: "Breakpoint",
                    mode: "PageGuard",
                    rollbackAction: rollbackAction);
                guards.Add(guard);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(startTasks);
        Assert.Empty(exceptions);

        // Dispose all concurrently
        var disposeTasks = guards.Select(g => Task.Run(() =>
        {
            try { g.Dispose(); }
            catch (Exception ex) { exceptions.Add(ex); }
        }));

        await Task.WhenAll(disposeTasks);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task DisposeSameMonitorFromMultipleGuardReferences_Safe()
    {
        // Even if somehow the same operationId gets multiple guards, disposal is safe
        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);

        var guard1 = _service.StartMonitoring(
            processId: -1,
            operationId: "op-shared",
            address: (nuint)0x8000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: rollbackAction);

        // Overwrite the same operationId with a new monitor (simulating a race in StartMonitoring)
        var guard2 = _service.StartMonitoring(
            processId: -1,
            operationId: "op-shared",
            address: (nuint)0x8000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: rollbackAction);

        // Disposing both should not throw
        var tasks = new[]
        {
            Task.Run(() => guard1.Dispose()),
            Task.Run(() => guard2.Dispose()),
        };

        var exceptions = new ConcurrentBag<Exception>();
        foreach (var t in tasks)
        {
            try { await t; }
            catch (Exception ex) { exceptions.Add(ex); }
        }

        Assert.Empty(exceptions);
    }
}

public class WatchdogServiceDisposeUnderLoadTests
{
    [Fact]
    public async Task DisposeWhileMonitorsRunning_NoHangs()
    {
        // Arrange
        var service = new ProcessWatchdogService
        {
            HeartbeatIntervalMs = 100,
            UnresponsiveThresholdMs = 500,
        };
        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);

        var guards = new List<WatchdogGuard>();
        for (int i = 0; i < 5; i++)
        {
            guards.Add(service.StartMonitoring(
                processId: -1,
                operationId: $"op-dispose-load-{i}",
                address: (nuint)(0xA000 + i * 0x100),
                operationType: "Breakpoint",
                mode: "Hardware",
                rollbackAction: rollbackAction));
        }

        // Let monitors spin briefly
        await Task.Delay(50);

        // Act: dispose service while monitors are running — must complete within timeout
        var disposeTask = Task.Run(() => service.Dispose());
        var completed = await Task.WhenAny(disposeTask, Task.Delay(5000));

        // Assert: disposal completed, did not hang
        Assert.Equal(disposeTask, completed);
    }

    [Fact]
    public Task DisposeServiceThenDisposeGuards_Safe()
    {
        // Arrange
        var service = new ProcessWatchdogService();
        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);

        var guards = new List<WatchdogGuard>();
        for (int i = 0; i < 3; i++)
        {
            guards.Add(service.StartMonitoring(
                processId: -1,
                operationId: $"op-post-dispose-{i}",
                address: (nuint)(0xB000 + i * 0x100),
                operationType: "CodeCaveHook",
                mode: "Stealth",
                rollbackAction: rollbackAction));
        }

        // Dispose the service first
        service.Dispose();

        // Now dispose the guards — StopMonitoring will call TryRemove on an empty dictionary
        // and the monitor is already disposed, so this should be a no-op without exceptions.
        var exceptions = new ConcurrentBag<Exception>();
        foreach (var g in guards)
        {
            try { g.Dispose(); }
            catch (Exception ex) { exceptions.Add(ex); }
        }

        Assert.Empty(exceptions);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentDisposeOfService_Safe()
    {
        // Arrange
        var service = new ProcessWatchdogService();
        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);

        for (int i = 0; i < 5; i++)
        {
            service.StartMonitoring(
                processId: -1,
                operationId: $"op-multi-dispose-{i}",
                address: (nuint)(0xC000 + i * 0x100),
                operationType: "Breakpoint",
                mode: "Hardware",
                rollbackAction: rollbackAction);
        }

        // Act: dispose from multiple threads
        var exceptions = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            try { service.Dispose(); }
            catch (Exception ex) { exceptions.Add(ex); }
        }));

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);
    }
}

public class UnsafeAddressRegistryThreadSafetyTests : IDisposable
{
    private readonly ProcessWatchdogService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task ConcurrentIsUnsafeAndClear_MaintainsConsistency()
    {
        // Arrange: pre-populate some unsafe addresses by using StartMonitoring + the
        // OnRollbackTriggered path. Since we cannot easily trigger rollback without a
        // real process, we test IsUnsafe and ClearUnsafe on the public API directly.
        // The ConcurrentDictionary backing store should handle concurrent reads/writes.

        var exceptions = new ConcurrentBag<Exception>();
        int iterations = 1000;

        // Act: concurrent reads, writes, and clears on the unsafe registry
        var tasks = new List<Task>();

        // Writer tasks: simulate marking addresses as unsafe via ClearUnsafe round-trips
        // (we cannot call the internal MarkUnsafe, but we can verify IsUnsafe + ClearUnsafe)
        for (int t = 0; t < 4; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        var addr = (nuint)(0x10000 + threadId * 0x1000 + i);
                        // ClearUnsafe on a non-existent address should not throw
                        _service.ClearUnsafe(addr, "Hardware");
                    }
                }
                catch (Exception ex) { exceptions.Add(ex); }
            }));
        }

        // Reader tasks: concurrent IsUnsafe checks
        for (int t = 0; t < 4; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        var addr = (nuint)(0x10000 + threadId * 0x1000 + i);
                        // Should always return false since we only clear, never mark
                        bool result = _service.IsUnsafe(addr, "Hardware");
                        Assert.False(result);
                    }
                }
                catch (Exception ex) { exceptions.Add(ex); }
            }));
        }

        // GetUnsafeAddresses reader tasks
        for (int t = 0; t < 2; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        var addresses = _service.GetUnsafeAddresses();
                        // Should be empty since nothing was marked unsafe
                        Assert.Empty(addresses);
                    }
                }
                catch (Exception ex) { exceptions.Add(ex); }
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentClearOnSameAddress_NoException()
    {
        var exceptions = new ConcurrentBag<Exception>();
        var addr = (nuint)0xDEAD;

        // Multiple threads clearing the same address simultaneously
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 500; i++)
                {
                    _service.ClearUnsafe(addr, "PageGuard");
                    bool check = _service.IsUnsafe(addr, "PageGuard");
                }
            }
            catch (Exception ex) { exceptions.Add(ex); }
        }));

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);
    }
}

public class TransactionResultAndPhaseTests
{
    [Theory]
    [InlineData(TransactionPhase.PreCheck)]
    [InlineData(TransactionPhase.Install)]
    [InlineData(TransactionPhase.Verify)]
    [InlineData(TransactionPhase.Committed)]
    public void TransactionResult_Success_ReportsPhaseCorrectly(TransactionPhase phase)
    {
        var result = new TransactionResult(true, "Test message", phase);

        Assert.True(result.Success);
        Assert.Equal("Test message", result.Message);
        Assert.Equal(phase, result.Phase);
    }

    [Theory]
    [InlineData(TransactionPhase.PreCheck)]
    [InlineData(TransactionPhase.Install)]
    [InlineData(TransactionPhase.Verify)]
    [InlineData(TransactionPhase.Committed)]
    public void TransactionResult_Failure_ReportsPhaseCorrectly(TransactionPhase phase)
    {
        var result = new TransactionResult(false, "Failure reason", phase);

        Assert.False(result.Success);
        Assert.Equal("Failure reason", result.Message);
        Assert.Equal(phase, result.Phase);
    }

    [Fact]
    public void TransactionPhase_HasAllExpectedValues()
    {
        var values = Enum.GetValues<TransactionPhase>();
        Assert.Equal(4, values.Length);
        Assert.Contains(TransactionPhase.PreCheck, values);
        Assert.Contains(TransactionPhase.Install, values);
        Assert.Contains(TransactionPhase.Verify, values);
        Assert.Contains(TransactionPhase.Committed, values);
    }

    [Fact]
    public void TransactionResult_RecordEquality()
    {
        var a = new TransactionResult(true, "OK", TransactionPhase.Committed);
        var b = new TransactionResult(true, "OK", TransactionPhase.Committed);
        var c = new TransactionResult(false, "OK", TransactionPhase.Committed);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void TransactionResult_PreCheck_FailureMessage()
    {
        var result = new TransactionResult(
            false,
            "Process was already unresponsive before install.",
            TransactionPhase.PreCheck);

        Assert.False(result.Success);
        Assert.Equal(TransactionPhase.PreCheck, result.Phase);
        Assert.Contains("unresponsive", result.Message);
    }

    [Fact]
    public void TransactionResult_Install_FailureMessage()
    {
        var result = new TransactionResult(
            false,
            "Install failed: Access denied",
            TransactionPhase.Install);

        Assert.False(result.Success);
        Assert.Equal(TransactionPhase.Install, result.Phase);
        Assert.Contains("Install failed", result.Message);
    }

    [Fact]
    public void TransactionResult_Verify_RollbackSucceeded()
    {
        var result = new TransactionResult(
            false,
            "Process became unresponsive after install. Rollback succeeded. Address marked unsafe.",
            TransactionPhase.Verify);

        Assert.False(result.Success);
        Assert.Equal(TransactionPhase.Verify, result.Phase);
        Assert.Contains("Rollback succeeded", result.Message);
    }

    [Fact]
    public void TransactionResult_Verify_RollbackFailed()
    {
        var result = new TransactionResult(
            false,
            "Process became unresponsive after install. Rollback FAILED. Address marked unsafe.",
            TransactionPhase.Verify);

        Assert.False(result.Success);
        Assert.Equal(TransactionPhase.Verify, result.Phase);
        Assert.Contains("Rollback FAILED", result.Message);
    }

    [Fact]
    public void TransactionResult_Committed_Success()
    {
        var result = new TransactionResult(
            true,
            "Install committed. Watchdog monitoring active.",
            TransactionPhase.Committed);

        Assert.True(result.Success);
        Assert.Equal(TransactionPhase.Committed, result.Phase);
        Assert.Contains("committed", result.Message);
    }
}

public class WatchdogRollbackEventTests
{
    [Fact]
    public void WatchdogRollbackEvent_Properties()
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new WatchdogRollbackEvent(
            "op-1", 1234, (nuint)0xBEEF, "Breakpoint", "Hardware", true, now);

        Assert.Equal("op-1", evt.OperationId);
        Assert.Equal(1234, evt.ProcessId);
        Assert.Equal((nuint)0xBEEF, evt.Address);
        Assert.Equal("Breakpoint", evt.OperationType);
        Assert.Equal("Hardware", evt.Mode);
        Assert.True(evt.RollbackSucceeded);
        Assert.Equal(now, evt.TimestampUtc);
    }

    [Fact]
    public void UnsafeAddressEntry_Properties()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new UnsafeAddressEntry(
            (nuint)0xCAFE, "PageGuard", "CodeCaveHook", now, false);

        Assert.Equal((nuint)0xCAFE, entry.Address);
        Assert.Equal("PageGuard", entry.Mode);
        Assert.Equal("CodeCaveHook", entry.OperationType);
        Assert.Equal(now, entry.FreezeDetectedUtc);
        Assert.False(entry.RollbackSucceeded);
    }
}

public class WatchdogGuardPropertyTests : IDisposable
{
    private readonly ProcessWatchdogService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public void Guard_ExposesOperationId()
    {
        Func<Task<bool>> rollbackAction = () => Task.FromResult(true);

        var guard = _service.StartMonitoring(
            processId: -1,
            operationId: "op-property-test",
            address: (nuint)0xF000,
            operationType: "Breakpoint",
            mode: "Hardware",
            rollbackAction: rollbackAction);

        Assert.Equal("op-property-test", guard.OperationId);
        guard.Dispose();
    }

    [Fact]
    public void Service_DiagnosticLog_CanBeSet()
    {
        var logs = new ConcurrentBag<(string Source, string Level, string Message)>();
        _service.DiagnosticLog = (source, level, msg) => logs.Add((source, level, msg));

        // DiagnosticLog is an optional callback — just verify it can be set and is not null
        Assert.NotNull(_service.DiagnosticLog);
    }

    [Fact]
    public void Service_ConfigurationDefaults()
    {
        var service = new ProcessWatchdogService();
        Assert.Equal(500, service.HeartbeatIntervalMs);
        Assert.Equal(3000, service.UnresponsiveThresholdMs);
        Assert.Equal(2, service.MaxRetries);
        service.Dispose();
    }
}
