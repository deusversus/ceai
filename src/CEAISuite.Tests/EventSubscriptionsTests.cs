using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests;

public sealed class EventSubscriptionsTests
{
    [Fact]
    public void Add_CallsSubscribeImmediately()
    {
        var subs = new EventSubscriptions();
        bool subscribed = false;

        subs.Add(() => subscribed = true, () => { });

        Assert.True(subscribed);
    }

    [Fact]
    public void Dispose_CallsUnsubscribe()
    {
        var subs = new EventSubscriptions();
        bool unsubscribed = false;

        subs.Add(() => { }, () => unsubscribed = true);
        subs.Dispose();

        Assert.True(unsubscribed);
    }

    [Fact]
    public void Dispose_UnsubscribesInReverseOrder()
    {
        var subs = new EventSubscriptions();
        var order = new List<int>();

        subs.Add(() => { }, () => order.Add(1));
        subs.Add(() => { }, () => order.Add(2));
        subs.Add(() => { }, () => order.Add(3));

        subs.Dispose();

        Assert.Equal([3, 2, 1], order);
    }

    [Fact]
    public void Dispose_CalledTwice_OnlyUnsubscribesOnce()
    {
        var subs = new EventSubscriptions();
        int callCount = 0;

        subs.Add(() => { }, () => callCount++);
        subs.Dispose();
        subs.Dispose();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Add_AfterDispose_Throws()
    {
        var subs = new EventSubscriptions();
        subs.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            subs.Add(() => { }, () => { }));
    }

    [Fact]
    public void Subscribe_Action_TracksSubscription()
    {
        var subs = new EventSubscriptions();
        Action? handler = null;
        Action? removedHandler = null;

        subs.Subscribe(
            addHandler: h => handler = h,
            removeHandler: h => removedHandler = h,
            handler: () => { });

        Assert.NotNull(handler);
        Assert.Null(removedHandler);

        subs.Dispose();

        Assert.NotNull(removedHandler);
        Assert.Same(handler, removedHandler);
    }

    [Fact]
    public void Subscribe_ActionT_TracksSubscription()
    {
        var subs = new EventSubscriptions();
        Action<string>? handler = null;
        Action<string>? removedHandler = null;

        subs.Subscribe<string>(
            addHandler: h => handler = h,
            removeHandler: h => removedHandler = h,
            handler: _ => { });

        Assert.NotNull(handler);

        subs.Dispose();

        Assert.Same(handler, removedHandler);
    }

    [Fact]
    public void Subscribe_ActionT1T2_TracksSubscription()
    {
        var subs = new EventSubscriptions();
        Action<string, int>? handler = null;
        Action<string, int>? removedHandler = null;

        subs.Subscribe<string, int>(
            addHandler: h => handler = h,
            removeHandler: h => removedHandler = h,
            handler: (_, _) => { });

        Assert.NotNull(handler);

        subs.Dispose();

        Assert.Same(handler, removedHandler);
    }

    [Fact]
    public void Subscribe_AfterDispose_Throws()
    {
        var subs = new EventSubscriptions();
        subs.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            subs.Subscribe(h => { }, h => { }, () => { }));
    }

    [Fact]
    public void Subscribe_GenericT_AfterDispose_Throws()
    {
        var subs = new EventSubscriptions();
        subs.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            subs.Subscribe<int>(h => { }, h => { }, _ => { }));
    }

    [Fact]
    public void Subscribe_GenericT1T2_AfterDispose_Throws()
    {
        var subs = new EventSubscriptions();
        subs.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            subs.Subscribe<int, string>(h => { }, h => { }, (_, _) => { }));
    }

    [Fact]
    public void MultipleSubscriptions_AllUnsubscribedOnDispose()
    {
        var subs = new EventSubscriptions();
        int unsubCount = 0;

        subs.Add(() => { }, () => unsubCount++);
        subs.Subscribe(h => { }, h => unsubCount++, () => { });
        subs.Subscribe<int>(h => { }, h => unsubCount++, _ => { });
        subs.Subscribe<int, string>(h => { }, h => unsubCount++, (_, _) => { });

        subs.Dispose();

        Assert.Equal(4, unsubCount);
    }
}
