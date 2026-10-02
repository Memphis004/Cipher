namespace ProjectSpy.Core;

/// <summary>
/// Minimal synchronous publish/subscribe used inside Core.
/// </summary>
/// <remarks>
/// Core is netstandard2.1 with no third-party packages, so it cannot use
/// <c>System.Reactive</c>. This is deliberately small and synchronous: the
/// simulation is single-threaded, so a handler that throws propagates immediately
/// to the caller rather than being swallowed by a scheduler — which is what you
/// want while hunting determinism bugs.
/// </remarks>
internal sealed class EventBus
{
    private readonly List<Subscription> _subscriptions = new();
    private bool _publishing;

    internal IDisposable Subscribe<T>(Action<T> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var subscription = new Subscription(this, typeof(T), handler);
        _subscriptions.Add(subscription);
        return subscription;
    }

    internal void Publish<T>(T message)
    {
        // Copy first: a handler may legitimately unsubscribe (or subscribe) during
        // dispatch, and mutating the live list would otherwise throw.
        Subscription[] snapshot = _subscriptions.ToArray();
        _publishing = true;
        try
        {
            foreach (Subscription subscription in snapshot)
            {
                if (subscription.Cancelled)
                    continue;

                if (subscription.EventType == typeof(T))
                    ((Action<T>)subscription.Handler).Invoke(message);
            }
        }
        finally
        {
            _publishing = false;
        }

        RemoveCancelled();
    }

    private void Remove(Subscription subscription)
    {
        subscription.Cancel();

        // Safe to compact immediately when not mid-dispatch.
        if (!_publishing)
            _subscriptions.Remove(subscription);
    }

    private void RemoveCancelled()
    {
        for (int i = _subscriptions.Count - 1; i >= 0; i--)
        {
            if (_subscriptions[i].Cancelled)
                _subscriptions.RemoveAt(i);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly EventBus _bus;

        internal Subscription(EventBus bus, Type eventType, Delegate handler)
        {
            _bus = bus;
            EventType = eventType;
            Handler = handler;
        }

        internal Type EventType { get; }
        internal Delegate Handler { get; }
        internal bool Cancelled { get; private set; }

        /// <summary>Marks the subscription dead; the bus compacts on its next dispatch.</summary>
        internal void Cancel() => Cancelled = true;

        public void Dispose()
        {
            if (Cancelled)
                return;

            Cancel();
            _bus.Remove(this);
        }
    }
}

/// <summary>A disposable that disposes several others together.</summary>
internal sealed class CompositeDisposable : IDisposable
{
    private readonly IReadOnlyList<IDisposable> _items;

    internal CompositeDisposable(IReadOnlyList<IDisposable> items) => _items = items;

    public void Dispose()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            _items[i].Dispose();
    }
}
