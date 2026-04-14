namespace CEAISuite.Engine.Abstractions;

/// <summary>
/// C1: Lightweight pub/sub bus for breakpoint lifecycle events.
/// Engine publishes events; ViewModels subscribe for reactive UI updates.
/// Interface lives in Abstractions so Engine.Windows can reference it.
/// </summary>
public interface IBreakpointEventBus
{
    /// <summary>Publish a breakpoint event to all subscribers.</summary>
    void Publish(BreakpointEvent evt);

    /// <summary>Subscribe to breakpoint events. Returns an unsubscribe action.</summary>
    IDisposable Subscribe(Action<BreakpointEvent> handler);
}

/// <summary>Base type for all breakpoint lifecycle events.</summary>
public abstract record BreakpointEvent(string BreakpointId);

/// <summary>A breakpoint was added to the session.</summary>
public sealed record BreakpointAddedEvent(
    string BreakpointId,
    string Address,
    string Mode,
    string Type) : BreakpointEvent(BreakpointId);

/// <summary>A breakpoint was removed from the session.</summary>
public sealed record BreakpointRemovedEvent(string BreakpointId) : BreakpointEvent(BreakpointId);

/// <summary>A breakpoint was hit during execution.</summary>
public sealed record BreakpointHitOccurredEvent(
    string BreakpointId,
    string Address,
    int ThreadId,
    int TotalHitCount) : BreakpointEvent(BreakpointId);

/// <summary>A breakpoint's lifecycle status changed.</summary>
public sealed record BreakpointStateChangedEvent(
    string BreakpointId,
    string NewStatus) : BreakpointEvent(BreakpointId);

/// <summary>A breakpoint was auto-disabled due to hit-rate throttling.</summary>
public sealed record BreakpointThrottledEvent(
    string BreakpointId,
    int HitsPerSecond) : BreakpointEvent(BreakpointId);

/// <summary>A stepping operation completed (step-in, step-over, step-out, or continue).</summary>
public sealed record StepCompletedEvent(
    string BreakpointId,
    nuint NewRip,
    int ThreadId,
    StoppedReason Reason) : BreakpointEvent(BreakpointId);
