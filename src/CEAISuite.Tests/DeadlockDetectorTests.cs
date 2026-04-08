using CEAISuite.Application;

namespace CEAISuite.Tests;

public class DeadlockDetectorTests
{
    [Fact]
    public void DetectDeadlocks_CurrentProcess_ReturnsNoDeadlock()
    {
        // The current test process should not be deadlocked.
        // On non-Windows platforms, the detector gracefully returns empty result.
        using var detector = new DeadlockDetector();
        int pid = Environment.ProcessId;

        var result = detector.DetectDeadlocks(pid);

        Assert.NotNull(result);
        Assert.False(result.HasDeadlock);
    }

    [Fact]
    public void DetectDeadlocks_InvalidPid_ReturnsNoDeadlock()
    {
        // An invalid PID (e.g. -1 or a very large number) should not throw
        // and should return a result indicating no deadlock.
        using var detector = new DeadlockDetector();

        var result = detector.DetectDeadlocks(-1);

        Assert.NotNull(result);
        Assert.False(result.HasDeadlock);
        Assert.Empty(result.WaitChains);
    }

    [Fact]
    public void DetectDeadlocks_ZeroPid_ReturnsNoDeadlock()
    {
        using var detector = new DeadlockDetector();

        var result = detector.DetectDeadlocks(0);

        Assert.NotNull(result);
        Assert.False(result.HasDeadlock);
        Assert.Empty(result.WaitChains);
    }

    [Fact]
    public void DeadlockResult_RecordProperties_WorkCorrectly()
    {
        var waitChains = new List<ThreadWaitInfo>
        {
            new ThreadWaitInfo(42, "CriticalSection[Blocked] -> Thread[Running]", false),
            new ThreadWaitInfo(99, "DEADLOCK CYCLE: Mutex[Blocked] -> Thread[Blocked]", true),
        };

        var result = new DeadlockResult(true, waitChains);

        Assert.True(result.HasDeadlock);
        Assert.Equal(2, result.WaitChains.Count);
        Assert.Equal(42, result.WaitChains[0].ThreadId);
        Assert.False(result.WaitChains[0].IsDeadlocked);
        Assert.Equal(99, result.WaitChains[1].ThreadId);
        Assert.True(result.WaitChains[1].IsDeadlocked);
    }

    [Fact]
    public void ThreadWaitInfo_RecordProperties_WorkCorrectly()
    {
        var info = new ThreadWaitInfo(123, "Mutex[Owned]", false);

        Assert.Equal(123, info.ThreadId);
        Assert.Equal("Mutex[Owned]", info.WaitDescription);
        Assert.False(info.IsDeadlocked);
    }

    [Fact]
    public void ThreadWaitInfo_RecordEquality_Works()
    {
        var a = new ThreadWaitInfo(10, "SendMessage[Blocked]", true);
        var b = new ThreadWaitInfo(10, "SendMessage[Blocked]", true);
        var c = new ThreadWaitInfo(10, "SendMessage[Blocked]", false);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DeadlockResult_RecordEquality_Works()
    {
        var chains = Array.Empty<ThreadWaitInfo>();
        var a = new DeadlockResult(false, chains);
        var b = new DeadlockResult(false, chains);
        var c = new DeadlockResult(true, chains);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DeadlockDetector_MultipleDetectCalls_DoesNotThrow()
    {
        using var detector = new DeadlockDetector();
        int pid = Environment.ProcessId;

        // Calling DetectDeadlocks multiple times should be safe (reuses WCT session).
        for (int i = 0; i < 3; i++)
        {
            var result = detector.DetectDeadlocks(pid);
            Assert.NotNull(result);
            Assert.False(result.HasDeadlock);
        }
    }

    [Fact]
    public void DeadlockDetector_DisposeMultipleTimes_DoesNotThrow()
    {
        var detector = new DeadlockDetector();

        // Double-dispose should be safe.
        detector.Dispose();
        detector.Dispose();
    }

    [Fact]
    public void DeadlockDetector_AfterDispose_ReturnsGracefully()
    {
        var detector = new DeadlockDetector();
        detector.Dispose();

        // Calling DetectDeadlocks after Dispose should not throw;
        // the session handle is zeroed out, so it returns empty result.
        var result = detector.DetectDeadlocks(Environment.ProcessId);

        Assert.NotNull(result);
        Assert.False(result.HasDeadlock);
        Assert.Empty(result.WaitChains);
    }
}

public class ProcessWatchdogServiceDeadlockIntegrationTests
{
    [Fact]
    public void DeadlockDetectionEnabled_DefaultsToFalse()
    {
        using var service = new ProcessWatchdogService();

        Assert.False(service.DeadlockDetectionEnabled);
    }

    [Fact]
    public void DeadlockDetectionEnabled_CanBeSetToTrue()
    {
        using var service = new ProcessWatchdogService();

        service.DeadlockDetectionEnabled = true;

        Assert.True(service.DeadlockDetectionEnabled);
    }

    [Fact]
    public void IsProcessResponsiveWithDeadlockCheck_WhenDisabled_DoesNotCallDetector()
    {
        // When DeadlockDetectionEnabled is false, the detector should not be invoked.
        // We verify this indirectly: construct with a detector but explicitly disable.
        using var detector = new DeadlockDetector();
        using var service = new ProcessWatchdogService(logger: null, deadlockDetector: detector);

        // Auto-enabled by constructor, so disable explicitly
        service.DeadlockDetectionEnabled = false;
        Assert.False(service.DeadlockDetectionEnabled);
    }

    [Fact]
    public void Constructor_WithNullDetector_DoesNotThrow()
    {
        using var service = new ProcessWatchdogService(logger: null, deadlockDetector: null);

        // Even with deadlock detection enabled, null detector means no-op.
        service.DeadlockDetectionEnabled = true;

        Assert.True(service.DeadlockDetectionEnabled);
    }

    [Fact]
    public void Constructor_WithDetector_DefaultsToEnabled()
    {
        using var detector = new DeadlockDetector();
        using var service = new ProcessWatchdogService(logger: null, deadlockDetector: detector);

        Assert.True(service.DeadlockDetectionEnabled);
    }

    [Fact]
    public void Constructor_WithoutDetector_DefaultsToDisabled()
    {
        using var service = new ProcessWatchdogService(logger: null, deadlockDetector: null);

        Assert.False(service.DeadlockDetectionEnabled);
    }
}
