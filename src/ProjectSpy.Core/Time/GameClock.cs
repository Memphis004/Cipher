namespace ProjectSpy.Core;

/// <summary>
/// The authoritative game clock. Owns the current <see cref="Tick"/> and raises
/// change notifications as time advances.
/// </summary>
/// <remarks>
/// <para>
/// The clock deliberately knows nothing about what a tick <em>means</em> for the
/// world. Room construction, training and economy all react to these notifications
/// rather than the clock calling into them, which keeps the tick pipeline order
/// (stage 3) explicit and testable.
/// </para>
/// <para>
/// Subscriptions are exposed as methods rather than C# events so that the Unity
/// layer can detach cleanly on scene teardown without a leaked handler keeping a
/// destroyed <c>GameObject</c> alive.
/// </para>
/// </remarks>
public sealed class GameClock
{
    private readonly EventBus _events = new();

    /// <summary>Creates a clock starting at <paramref name="start"/>.</summary>
    public GameClock(Tick start = default) => Current = start;

    /// <summary>The current absolute tick.</summary>
    public Tick Current { get; private set; }

    /// <summary>The current day.</summary>
    public int CurrentDay => Current.Day;

    /// <summary>The current week.</summary>
    public int CurrentWeek => Current.Week;

    /// <summary>Total ticks advanced since the clock was created.</summary>
    public long TicksElapsed { get; private set; }

    /// <summary>
    /// Advances the clock one tick, raising <see cref="OnTick"/>, then
    /// <see cref="OnDayChanged"/> and <see cref="OnWeekChanged"/> as applicable.
    /// </summary>
    public void Advance() => Advance(1);

    /// <summary>
    /// Advances the clock by <paramref name="ticks"/>, raising notifications in
    /// tick order. A multi-tick advance crosses midnight and week boundaries
    /// correctly: each boundary raises exactly once, and day/week handlers run
    /// after the tick handler for the tick they belong to.
    /// </summary>
    public void Advance(int ticks)
    {
        if (ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "The clock only moves forward.");

        for (int i = 0; i < ticks; i++)
        {
            Tick previous = Current;
            Current = new Tick(Current.Value + 1);
            TicksElapsed++;

            _events.Publish(new TickAdvancedEvent(Current));

            // Order matters: week contains days, so a week boundary that is also a
            // day boundary must report the day first and the week second.
            if (!Current.IsSameDay(previous))
                _events.Publish(new DayChangedEvent(Current.Day, Current.Week));

            if (!Current.IsSameWeek(previous))
                _events.Publish(new WeekChangedEvent(Current.Week, previous.Week));
        }
    }

    /// <summary>
    /// Jumps directly to a tick, raising the same notifications. Used when loading a
    /// save: the clock is set without replaying a year of per-tick events.
    /// </summary>
    public void SetTo(Tick tick)
    {
        if (tick.Value < Current.Value)
            throw new ArgumentOutOfRangeException(
                nameof(tick), tick, "Time does not move backwards outside of a load.");

        if (tick.Value == Current.Value)
            return;

        Advance((int)Math.Min(tick.Value - Current.Value, int.MaxValue));
    }

    // ---- Subscriptions -------------------------------------------------------

    /// <summary>Subscribes to every tick.</summary>
    public IDisposable OnTick(Action<TickAdvancedEvent> handler) => _events.Subscribe(handler);

    /// <summary>Subscribes to midnight rollovers.</summary>
    public IDisposable OnDayChanged(Action<DayChangedEvent> handler) => _events.Subscribe(handler);

    /// <summary>Subscribes to week rollovers.</summary>
    public IDisposable OnWeekChanged(Action<WeekChangedEvent> handler) => _events.Subscribe(handler);

    /// <summary>
    /// Subscribes to all three notifications at once, which is what the stage-3 tick
    /// pipeline needs.
    /// </summary>
    public IDisposable Subscribe(
        Action<TickAdvancedEvent>? onTick = null,
        Action<DayChangedEvent>? onDayChanged = null,
        Action<WeekChangedEvent>? onWeekChanged = null)
    {
        var subscriptions = new List<IDisposable>(3);
        if (onTick is not null) subscriptions.Add(OnTick(onTick));
        if (onDayChanged is not null) subscriptions.Add(OnDayChanged(onDayChanged));
        if (onWeekChanged is not null) subscriptions.Add(OnWeekChanged(onWeekChanged));
        return new CompositeDisposable(subscriptions);
    }
}

/// <summary>Published for every single tick advance.</summary>
public sealed record TickAdvancedEvent(Tick Tick);

/// <summary>Published once when the clock crosses midnight.</summary>
public sealed record DayChangedEvent(int Day, int Week);

/// <summary>Published once when the clock crosses into a new week.</summary>
public sealed record WeekChangedEvent(int NewWeek, int PreviousWeek);
