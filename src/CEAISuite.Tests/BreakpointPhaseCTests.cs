using System.IO;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase C: Observability &amp; UX tests.
/// Covers event bus (C1), lifecycle status wiring (C2), hit log enhancements (C3), persistence (C4).
/// </summary>
public class BreakpointPhaseCTests
{
    // ── C1: Event Bus ──

    [Fact]
    public void EventBus_PublishAndSubscribe_Works()
    {
        var bus = new BreakpointEventBus();
        BreakpointEvent? received = null;
        using var sub = bus.Subscribe(evt => received = evt);

        bus.Publish(new BreakpointAddedEvent("bp-1", "0x1000", "Hardware", "HardwareExecute"));

        Assert.NotNull(received);
        Assert.IsType<BreakpointAddedEvent>(received);
        Assert.Equal("bp-1", received.BreakpointId);
    }

    [Fact]
    public void EventBus_Unsubscribe_StopsDelivery()
    {
        var bus = new BreakpointEventBus();
        var count = 0;
        var sub = bus.Subscribe(_ => count++);

        bus.Publish(new BreakpointRemovedEvent("bp-1"));
        Assert.Equal(1, count);

        sub.Dispose();
        bus.Publish(new BreakpointRemovedEvent("bp-2"));
        Assert.Equal(1, count); // no more deliveries
    }

    [Fact]
    public void EventBus_FailingSubscriber_DoesNotCrashPublisher()
    {
        var bus = new BreakpointEventBus();
        var secondReceived = false;

        using var sub1 = bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var sub2 = bus.Subscribe(_ => secondReceived = true);

        // Should not throw despite first subscriber failing
        bus.Publish(new BreakpointHitOccurredEvent("bp-1", "0x1000", 1, 1));

        Assert.True(secondReceived);
    }

    [Fact]
    public void EventBus_MultipleSubscribers_AllReceive()
    {
        var bus = new BreakpointEventBus();
        var count = 0;
        using var sub1 = bus.Subscribe(_ => Interlocked.Increment(ref count));
        using var sub2 = bus.Subscribe(_ => Interlocked.Increment(ref count));
        using var sub3 = bus.Subscribe(_ => Interlocked.Increment(ref count));

        bus.Publish(new BreakpointThrottledEvent("bp-1", 250));

        Assert.Equal(3, count);
    }

    // ── C2: Lifecycle Status via Event Bus ──

    [Fact]
    public void BreakpointService_SubscribesToEventBus_UpdatesLifecycle()
    {
        var bus = new BreakpointEventBus();
        using var service = new BreakpointService(new StubBreakpointEngine(), eventBus: bus);

        // Simulate engine publishing a state change
        bus.Publish(new BreakpointStateChangedEvent("bp-1", "Active"));
        Assert.Equal(BreakpointLifecycleStatus.Active, service.GetLifecycleStatus("bp-1"));

        bus.Publish(new BreakpointStateChangedEvent("bp-1", "ThrottleDisabled"));
        Assert.Equal(BreakpointLifecycleStatus.ThrottleDisabled, service.GetLifecycleStatus("bp-1"));

        bus.Publish(new BreakpointStateChangedEvent("bp-1", "Faulted"));
        Assert.Equal(BreakpointLifecycleStatus.Faulted, service.GetLifecycleStatus("bp-1"));
    }

    [Fact]
    public void BreakpointService_InvalidStatusString_IgnoredGracefully()
    {
        var bus = new BreakpointEventBus();
        using var service = new BreakpointService(new StubBreakpointEngine(), eventBus: bus);

        bus.Publish(new BreakpointStateChangedEvent("bp-1", "NotARealStatus"));

        // Should remain Armed (default) — invalid status silently ignored
        Assert.Equal(BreakpointLifecycleStatus.Armed, service.GetLifecycleStatus("bp-1"));
    }

    [Fact]
    public void BreakpointService_Dispose_UnsubscribesFromBus()
    {
        var bus = new BreakpointEventBus();
        var service = new BreakpointService(new StubBreakpointEngine(), eventBus: bus);
        service.Dispose();

        // After dispose, events should not update lifecycle
        bus.Publish(new BreakpointStateChangedEvent("bp-1", "Active"));
        Assert.Equal(BreakpointLifecycleStatus.Armed, service.GetLifecycleStatus("bp-1"));
    }

    // ── C3: Hit Log Filtering + Statistics ──

    [Fact]
    public async Task GetFilteredHitLog_FilterByThread()
    {
        var engine = new StubBreakpointEngine();
        using var service = new BreakpointService(engine);

        var bp = await engine.SetBreakpointAsync(1234, (nuint)0x1000, BreakpointType.Software);
        engine.AddCannedHits(bp.Id,
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>()),
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 2, DateTimeOffset.UtcNow, new Dictionary<string, string>()),
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

        var filtered = await service.GetFilteredHitLogAsync(bp.Id, new HitLogFilter(ThreadId: 1));

        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, h => Assert.Equal(1, h.ThreadId));
    }

    [Fact]
    public async Task GetHitStatistics_ComputesCorrectly()
    {
        var engine = new StubBreakpointEngine();
        using var service = new BreakpointService(engine);

        var bp = await engine.SetBreakpointAsync(1234, (nuint)0x1000, BreakpointType.Software);
        var now = DateTimeOffset.UtcNow;
        engine.AddCannedHits(bp.Id,
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, now, new Dictionary<string, string>()),
            new BreakpointHitEvent(bp.Id, (nuint)0x2000, 2, now.AddSeconds(1), new Dictionary<string, string>()),
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, now.AddSeconds(2), new Dictionary<string, string>()));

        var stats = await service.GetHitStatisticsAsync(bp.Id);

        Assert.Equal(3, stats.TotalHits);
        Assert.Equal(2, stats.UniqueThreads);
        Assert.True(stats.TopAddresses.Count > 0);
        Assert.NotNull(stats.FirstHit);
        Assert.NotNull(stats.LastHit);
    }

    [Fact]
    public async Task GetHitStatistics_EmptyLog_ReturnsZeros()
    {
        var engine = new StubBreakpointEngine();
        using var service = new BreakpointService(engine);

        var bp = await engine.SetBreakpointAsync(1234, (nuint)0x1000, BreakpointType.Software);
        var stats = await service.GetHitStatisticsAsync(bp.Id);

        Assert.Equal(0, stats.TotalHits);
        Assert.Equal(0, stats.HitsPerSecond);
        Assert.Null(stats.FirstHit);
    }

    // ── C4: Breakpoint Persistence ──

    [Fact]
    public async Task ExportHitLog_CsvFormat_WritesFile()
    {
        var engine = new StubBreakpointEngine();
        using var service = new BreakpointService(engine);

        var bp = await engine.SetBreakpointAsync(1234, (nuint)0x1000, BreakpointType.Software);
        engine.AddCannedHits(bp.Id,
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>()),
            new BreakpointHitEvent(bp.Id, (nuint)0x2000, 2, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

        var tempFile = Path.GetTempFileName();
        try
        {
            var result = await service.ExportHitLogAsync(bp.Id, tempFile, HitLogExportFormat.Csv);
            Assert.Contains("2 hits", result);

            var csv = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("BreakpointId,Address,ThreadId,Timestamp", csv); // header
            Assert.Contains("0x1000", csv);
            Assert.Contains("0x2000", csv);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExportHitLog_PathTraversal_Rejected()
    {
        var engine = new StubBreakpointEngine();
        using var service = new BreakpointService(engine);
        var bp = await engine.SetBreakpointAsync(1234, (nuint)0x1000, BreakpointType.Software);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExportHitLogAsync(bp.Id, "../../../etc/passwd", HitLogExportFormat.Csv));
    }

    [Fact]
    public async Task GetFilteredHitLog_FilterByTimestamp()
    {
        var engine = new StubBreakpointEngine();
        using var service = new BreakpointService(engine);

        var bp = await engine.SetBreakpointAsync(1234, (nuint)0x1000, BreakpointType.Software);
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var t3 = DateTimeOffset.UtcNow;

        engine.AddCannedHits(bp.Id,
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, t1, new Dictionary<string, string>()),
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, t2, new Dictionary<string, string>()),
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, t3, new Dictionary<string, string>()));

        // Filter: only hits after t2 (exclusive of t1, inclusive of t2 and t3)
        var filtered = await service.GetFilteredHitLogAsync(bp.Id,
            new HitLogFilter(MinTimestamp: t2.AddSeconds(-1)));

        Assert.Equal(2, filtered.Count);
    }

    // ── C4: Breakpoint Persistence ──

    [Fact]
    public async Task SaveAndLoadProfile_RoundTrip()
    {
        var engine = new StubBreakpointEngine();
        using var service = new BreakpointService(engine);

        // Create breakpoints
        await service.SetBreakpointAsync(1234, "0x1000", BreakpointType.HardwareExecute, BreakpointMode.Hardware);
        await service.SetBreakpointAsync(1234, "0x2000", BreakpointType.Software, BreakpointMode.Software);

        var tempFile = Path.GetTempFileName();
        try
        {
            // Save
            var saveResult = await service.SaveProfileAsync(1234, "test-profile", tempFile);
            Assert.Contains("2 breakpoints", saveResult);
            Assert.True(File.Exists(tempFile));

            // Verify XML content
            var xml = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("test-profile", xml);
            Assert.Contains("0x1000", xml);
            Assert.Contains("0x2000", xml);

            // Load into a fresh service
            var engine2 = new StubBreakpointEngine();
            using var service2 = new BreakpointService(engine2);
            var loadResult = await service2.LoadProfileAsync(1234, tempFile);
            Assert.Contains("2 breakpoints", loadResult);

            var bps = await service2.ListBreakpointsAsync(1234);
            Assert.Equal(2, bps.Count);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadProfile_PathTraversal_Rejected()
    {
        using var service = new BreakpointService(new StubBreakpointEngine());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.LoadProfileAsync(1234, "../../../etc/passwd"));
    }

    [Fact]
    public async Task SaveProfile_ConditionalBreakpoints_Preserved()
    {
        var engine = new StubBreakpointEngine();
        using var service = new BreakpointService(engine);

        var cond = new BreakpointCondition("RAX == 0x100", BreakpointConditionType.RegisterCompare);
        await service.SetConditionalBreakpointAsync(1234, "0x3000", BreakpointType.HardwareWrite, cond,
            threadFilter: 42);

        var tempFile = Path.GetTempFileName();
        try
        {
            await service.SaveProfileAsync(1234, "cond-profile", tempFile);
            var xml = await File.ReadAllTextAsync(tempFile);

            Assert.Contains("RAX == 0x100", xml);
            Assert.Contains("RegisterCompare", xml);
            Assert.Contains("42", xml);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
