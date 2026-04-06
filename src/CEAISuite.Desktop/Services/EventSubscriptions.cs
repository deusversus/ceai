namespace CEAISuite.Desktop.Services;

/// <summary>
/// Tracks event subscriptions so they can be unsubscribed in one call.
/// Prevents the common WPF leak: publisher (singleton service) holds a strong
/// reference to the subscriber (window/VM) via the delegate, preventing GC.
///
/// Usage:
///   _subs.Add(service, nameof(service.Changed), handler);  // or use Subscribe helper
///   _subs.Add&lt;Action&gt;(() => service.Changed += h, () => service.Changed -= h);
///   // ... on shutdown:
///   _subs.Dispose();
/// </summary>
public sealed class EventSubscriptions : IDisposable
{
    private readonly List<Action> _unsubscribers = [];
    private bool _disposed;

    /// <summary>
    /// Register an arbitrary subscribe/unsubscribe pair.
    /// The <paramref name="unsubscribe"/> action is called on Dispose.
    /// </summary>
    public void Add(Action subscribe, Action unsubscribe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        subscribe();
        _unsubscribers.Add(unsubscribe);
    }

    /// <summary>
    /// Subscribe an <see cref="Action"/> handler to an event and track the unsubscription.
    /// </summary>
    public void Subscribe(Action<Action> addHandler, Action<Action> removeHandler, Action handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        addHandler(handler);
        _unsubscribers.Add(() => removeHandler(handler));
    }

    /// <summary>
    /// Subscribe an <see cref="Action{T}"/> handler to an event and track the unsubscription.
    /// </summary>
    public void Subscribe<T>(Action<Action<T>> addHandler, Action<Action<T>> removeHandler, Action<T> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        addHandler(handler);
        _unsubscribers.Add(() => removeHandler(handler));
    }

    /// <summary>
    /// Subscribe an <see cref="Action{T1, T2}"/> handler and track the unsubscription.
    /// </summary>
    public void Subscribe<T1, T2>(Action<Action<T1, T2>> addHandler, Action<Action<T1, T2>> removeHandler, Action<T1, T2> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        addHandler(handler);
        _unsubscribers.Add(() => removeHandler(handler));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Unsubscribe in reverse order (LIFO) for symmetry
        for (var i = _unsubscribers.Count - 1; i >= 0; i--)
            _unsubscribers[i]();
        _unsubscribers.Clear();
    }
}
