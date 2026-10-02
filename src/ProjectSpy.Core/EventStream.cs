namespace ProjectSpy.Core;

/// <summary>
/// The event stream Presentation subscribes to.
/// </summary>
/// <remarks>
/// <para>
/// Shaped like <c>IObservable&lt;T&gt;</c> (a <c>Subscribe</c> returning a
/// disposable token) so that the Unity layer can drop in an R3 subject at stage 7
/// without changing any Core code — but implemented here because Core targets
/// netstandard2.1 with no third-party packages.
/// </para>
/// <para>
/// Dispatch is synchronous and in publish order. A handler that throws propagates to
/// the publisher: during determinism work, a swallowed exception in a UI handler is
/// far more expensive to debug than a loud one.
/// </para>
/// </remarks>
public sealed class EventStream
{
    private readonly List<Handler> _handlers = new();
    private bool _dispatching;

    /// <summary>Number of live subscriptions. Diagnostics and leak checks.</summary>
    public int SubscriberCount
    {
        get
        {
            int count = 0;
            foreach (Handler handler in _handlers)
            {
                if (!handler.Cancelled)
                    count++;
            }

            return count;
        }
    }

    /// <summary>Attaches a handler. Dispose the result to detach.</summary>
    public IDisposable Subscribe(Action<GameEvent> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var entry = new Handler(this, handler);
        _handlers.Add(entry);
        return entry;
    }

    /// <summary>
    /// Attaches a handler for one event kind only, so unrelated churn does not
    /// invalidate Presentation caches.
    /// </summary>
    public IDisposable Subscribe(GameEventKind kind, Action<GameEvent> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        return Subscribe(gameEvent =>
        {
            if (gameEvent.Kind == kind)
                handler(gameEvent);
        });
    }

    internal void Publish(GameEvent gameEvent)
    {
        // Snapshot: a handler may unsubscribe or subscribe during dispatch.
        Handler[] snapshot = _handlers.ToArray();
        _dispatching = true;
        try
        {
            foreach (Handler handler in snapshot)
            {
                if (!handler.Cancelled)
                    handler.Callback(gameEvent);
            }
        }
        finally
        {
            _dispatching = false;
        }

        Compact();
    }

    private void Remove(Handler handler)
    {
        handler.Cancel();
        if (!_dispatching)
            _handlers.Remove(handler);
    }

    private void Compact()
    {
        for (int i = _handlers.Count - 1; i >= 0; i--)
        {
            if (_handlers[i].Cancelled)
                _handlers.RemoveAt(i);
        }
    }

    private sealed class Handler : IDisposable
    {
        private readonly EventStream _stream;

        internal Handler(EventStream stream, Action<GameEvent> callback)
        {
            _stream = stream;
            Callback = callback;
        }

        internal Action<GameEvent> Callback { get; }
        internal bool Cancelled { get; private set; }

        /// <summary>Marks the handler dead; the stream compacts on its next dispatch.</summary>
        internal void Cancel() => Cancelled = true;

        public void Dispose()
        {
            if (Cancelled)
                return;

            Cancel();
            _stream.Remove(this);
        }
    }
}
