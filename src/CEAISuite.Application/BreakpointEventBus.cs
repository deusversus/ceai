using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// C1: Default implementation of breakpoint event bus.
/// Thread-safe; handlers invoked synchronously on the publishing thread.
/// ViewModels must marshal to UI thread via IDispatcherService.
/// </summary>
public sealed class BreakpointEventBus : IBreakpointEventBus
{
    private readonly List<Action<BreakpointEvent>> _handlers = [];
    private readonly object _lock = new();

    public void Publish(BreakpointEvent evt)
    {
        Action<BreakpointEvent>[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _handlers];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                handler(evt);
            }
            catch (Exception)
            {
                // Don't let a failing subscriber crash the publisher (engine debug loop).
                // Intentionally silent — subscribers are responsible for their own error handling.
            }
        }
    }

    public IDisposable Subscribe(Action<BreakpointEvent> handler)
    {
        lock (_lock)
        {
            _handlers.Add(handler);
        }
        return new Unsubscriber(this, handler);
    }

    private void Unsubscribe(Action<BreakpointEvent> handler)
    {
        lock (_lock)
        {
            _handlers.Remove(handler);
        }
    }

    private sealed class Unsubscriber(BreakpointEventBus bus, Action<BreakpointEvent> handler) : IDisposable
    {
        public void Dispose() => bus.Unsubscribe(handler);
    }
}
