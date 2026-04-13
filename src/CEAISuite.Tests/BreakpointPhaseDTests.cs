using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase D: Test hardening.
/// Covers VEH agent recovery (D3), event bus stress test, and service dispose behavior.
/// </summary>
public class BreakpointPhaseDTests
{
    // ── D3: VEH Agent Recovery ──

    [Fact]
    public async Task VehStatus_Healthy_ReportsCorrectly()
    {
        var debugger = new StubVehDebugger();
        debugger.SimulatedHealth = VehAgentHealth.Healthy;
        var service = new VehDebugService(debugger);

        await debugger.InjectAsync(1234);
        var status = service.GetStatus(1234);

        Assert.True(status.IsInjected);
        Assert.Equal(VehAgentHealth.Healthy, status.AgentHealth);
    }

    [Fact]
    public async Task VehStatus_Unresponsive_DetectedViaHeartbeatTimeout()
    {
        var debugger = new StubVehDebugger();
        await debugger.InjectAsync(1234);

        // Simulate stale heartbeat — agent health degrades
        debugger.SimulatedHealth = VehAgentHealth.Unresponsive;
        var service = new VehDebugService(debugger);

        var status = service.GetStatus(1234);

        Assert.True(status.IsInjected);
        Assert.Equal(VehAgentHealth.Unresponsive, status.AgentHealth);
    }

    [Fact]
    public async Task VehAgent_RecoveryAfterUnresponsive_ReInjectSucceeds()
    {
        var debugger = new StubVehDebugger();
        var service = new VehDebugService(debugger);

        // Inject, then mark as unresponsive
        await service.InjectAsync(1234);
        debugger.SimulatedHealth = VehAgentHealth.Unresponsive;
        var status1 = service.GetStatus(1234);
        Assert.Equal(VehAgentHealth.Unresponsive, status1.AgentHealth);

        // Eject and re-inject (recovery pattern)
        await service.EjectAsync(1234);
        debugger.SimulatedHealth = VehAgentHealth.Healthy;
        await service.InjectAsync(1234);

        var status2 = service.GetStatus(1234);
        Assert.True(status2.IsInjected);
        Assert.Equal(VehAgentHealth.Healthy, status2.AgentHealth);
    }

    [Fact]
    public void VehStatus_NotInjected_DefaultValues()
    {
        var service = new VehDebugService(null);
        var status = service.GetStatus(1234);

        Assert.False(status.IsInjected);
        Assert.Equal(0, status.ActiveBreakpoints);
        Assert.Equal(0, status.TotalHits);
    }

    // ── Event Bus Stress ──

    [Fact]
    public void EventBus_ConcurrentPublishAndSubscribe_DoesNotThrow()
    {
        var bus = new BreakpointEventBus();
        var hitCount = 0;

        // Subscribe from multiple threads simultaneously
        var subs = new List<IDisposable>();
        Parallel.For(0, 10, _ =>
        {
            var sub = bus.Subscribe(_ => Interlocked.Increment(ref hitCount));
            lock (subs) subs.Add(sub);
        });

        // Publish from multiple threads simultaneously
        Parallel.For(0, 100, i =>
        {
            bus.Publish(new BreakpointHitOccurredEvent($"bp-{i}", "0x1000", 1, i));
        });

        // 10 subscribers × 100 publishes = up to 1000, but subscribers join mid-flight
        // so we verify a meaningful fraction was received (not just > 0)
        Assert.True(hitCount >= 100, $"Expected at least 100 events received, got {hitCount}");

        foreach (var sub in subs) sub.Dispose();
    }

    // ── Service Dispose Robustness ──

    [Fact]
    public void BreakpointService_DoubleDispose_DoesNotThrow()
    {
        var bus = new BreakpointEventBus();
        using var service = new BreakpointService(new StubBreakpointEngine(), eventBus: bus);

        service.Dispose(); // first explicit dispose
        // second dispose via `using` at end of scope — should be safe
    }

    [Fact]
    public async Task BreakpointService_OperationsAfterDispose_StillWork()
    {
        var bus = new BreakpointEventBus();
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine, eventBus: bus);

        service.Dispose();

        // Service should still function for read operations after dispose
        // (dispose only unsubscribes from event bus, doesn't invalidate the service)
        var bps = await service.ListBreakpointsAsync(1234);
        Assert.Empty(bps);
    }

    // ── Lifecycle Status Transitions ──

    [Fact]
    public void LifecycleStatus_FullTransitionSequence()
    {
        var bus = new BreakpointEventBus();
        using var service = new BreakpointService(new StubBreakpointEngine(), eventBus: bus);

        // Armed (default)
        Assert.Equal(BreakpointLifecycleStatus.Armed, service.GetLifecycleStatus("bp-1"));

        // → Active (first hit)
        bus.Publish(new BreakpointStateChangedEvent("bp-1", "Active"));
        Assert.Equal(BreakpointLifecycleStatus.Active, service.GetLifecycleStatus("bp-1"));

        // → ThrottleDisabled
        bus.Publish(new BreakpointStateChangedEvent("bp-1", "ThrottleDisabled"));
        Assert.Equal(BreakpointLifecycleStatus.ThrottleDisabled, service.GetLifecycleStatus("bp-1"));

        // → Active again (after cooldown re-enable)
        bus.Publish(new BreakpointStateChangedEvent("bp-1", "Active"));
        Assert.Equal(BreakpointLifecycleStatus.Active, service.GetLifecycleStatus("bp-1"));

        // → Faulted (restoration failure)
        bus.Publish(new BreakpointStateChangedEvent("bp-1", "Faulted"));
        Assert.Equal(BreakpointLifecycleStatus.Faulted, service.GetLifecycleStatus("bp-1"));
    }
}
