namespace ProjectSpy.Core;

/// <summary>
/// The single entry point Presentation talks to. Owns the <see cref="WorldState"/>,
/// executes commands, advances time, and publishes the resulting events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Command flow.</b> Presentation builds an <see cref="ICommand"/> and calls
/// <see cref="Execute"/>. Core validates it, applies it if legal, appends it to the
/// command log, and publishes whatever <see cref="GameEvent"/>s resulted. A rejected
/// command changes nothing and still publishes a <see cref="CommandRejected"/> so the
/// UI can explain itself.
/// </para>
/// <para>
/// <b>Why a log.</b> Because the only mutation path is a command, the log plus the
/// seed is a complete description of the run. That is what stage 5's
/// <c>ReplayVerifier</c> re-executes to prove determinism.
/// </para>
/// <para>
/// <b>Threading.</b> Single-threaded by design. Unity calls this from its main loop;
/// the stage-6 simulator drives it from its own loop. Nothing here is thread-safe on
/// purpose — concurrent mutation of a deterministic simulation is how determinism
/// dies.
/// </para>
/// </remarks>
public sealed class GameSession : IEventSink
{
    private readonly List<ICommand> _commandLog = new();
    private readonly List<GameEvent> _pending = new();
    private readonly EventStream _stream = new();
    private readonly TickPipeline _pipeline;

    /// <summary>Creates a session over a fresh world.</summary>
    public GameSession(ulong seed, int baseWidth = 24, int baseHeight = 12)
        : this(new WorldState(seed, baseWidth, baseHeight))
    {
    }

    /// <summary>Creates a session over an existing world — used when loading a save.</summary>
    public GameSession(WorldState world)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        _pipeline = new TickPipeline(this);
        WireClock();
    }

    /// <summary>The world this session owns. Presentation reads it; never writes it.</summary>
    public WorldState World { get; }

    /// <summary>The tick pipeline, so stage 3 can register its phases.</summary>
    public TickPipeline Pipeline => _pipeline;

    /// <summary>Current time.</summary>
    public Tick CurrentTick => World.Clock.Current;

    /// <summary>Every successfully applied command, in order. The replay input.</summary>
    public IReadOnlyList<ICommand> CommandLog => _commandLog;

    /// <summary>Events published during the current command or tick, awaiting flush.</summary>
    public IReadOnlyList<GameEvent> PendingEvents => _pending;

    /// <summary>
    /// Subscribes to the event stream. Disposing the returned token detaches the
    /// handler, which is how the Unity layer avoids leaking into destroyed objects.
    /// </summary>
    public IDisposable Subscribe(Action<GameEvent> handler) => _stream.Subscribe(handler);

    /// <summary>
    /// Subscribes to a single event kind. Preferred by Presentation, since unrelated
    /// churn then never invalidates its caches.
    /// </summary>
    public IDisposable Subscribe(GameEventKind kind, Action<GameEvent> handler)
        => _stream.Subscribe(kind, handler);

    /// <summary>
    /// Drains everything accumulated since the last flush and publishes it to
    /// subscribers in order.
    /// </summary>
    /// <remarks>
    /// Commands publish into a pending buffer rather than straight to subscribers, so
    /// a handler can never re-enter the session mid-command and observe a half-mutated
    /// world. This is the flush point.
    /// </remarks>
    public void FlushEvents()
    {
        if (_pending.Count == 0)
            return;

        var batch = _pending.ToArray();
        _pending.Clear();

        foreach (GameEvent gameEvent in batch)
            _stream.Publish(gameEvent);
    }

    void IEventSink.Publish(GameEvent gameEvent) => Publish(gameEvent);

    /// <summary>Queues an event for the next <see cref="FlushEvents"/>.</summary>
    private void Publish(GameEvent gameEvent) => _pending.Add(gameEvent);

    // ---- Commands ------------------------------------------------------------

    /// <summary>
    /// Validates and, if legal, applies a command. Returns the validation result:
    /// <see cref="CommandResult.Ok"/> means it was applied.
    /// </summary>
    public CommandResult Execute(ICommand command)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));

        CommandResult validation = command.Validate(World);

        if (validation.IsRejected)
        {
            Publish(new CommandRejected(World.Clock.Current, validation.Reason, validation.Args));
            FlushEvents();
            return validation;
        }

        // Validate and Apply must agree. A command that rejects itself after passing
        // validation would corrupt the log, so this is a hard failure rather than a
        // graceful one — it is a bug in the command, not a player situation.
        CommandResult recheck = command.Validate(World);
        if (recheck.IsRejected)
        {
            throw new InvalidOperationException(
                $"Command '{command.GetType().Name}' passed validation then rejected itself " +
                $"with reason '{recheck.Reason}'. Validation must be deterministic.");
        }

        Resources before = World.Resources;

        // Each command chooses its own stream so subsystem rolls stay independent.
        command.Apply(World, World.RngStreams[RngStreams.StreamKind.World]);

        if (!World.Resources.Equals(before))
            Publish(new ResourcesChanged(World.Clock.Current, before, World.Resources));

        _commandLog.Add(command);
        FlushEvents();
        return CommandResult.Ok;
    }

    /// <summary>
    /// Advances one tick: raises tick/day/week notifications, then runs the tick
    /// pipeline in its canonical order, then flushes events.
    /// </summary>
    public void AdvanceTick()
    {
        Tick previous = World.Clock.Current;

        World.Clock.Advance();
        Tick now = World.Clock.Current;

        Publish(new TickAdvanced(now, now.Day, now.HourOfDay));

        if (!now.IsSameDay(previous))
            Publish(new DayChanged(now, now.Day, now.Week));

        if (!now.IsSameWeek(previous))
            Publish(new WeekSettled(now, now.Week, SalariesPaid: 0, UpkeepPaid: 0, InterestPaid: 0));

        _pipeline.RunTick(World, new PhaseContext(now)
        {
            IsDayStart = !now.IsSameDay(previous),
            IsWeekStart = !now.IsSameWeek(previous),
            Events = this,
        });

        FlushEvents();
    }

    /// <summary>Advances <paramref name="count"/> ticks, flushing once at the end.</summary>
    public void AdvanceTicks(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Cannot rewind time.");

        for (int i = 0; i < count; i++)
            AdvanceTick();
    }

    /// <summary>Advances to the start of the next day.</summary>
    public void AdvanceToNextDay()
    {
        int ticksIntoDay = World.Clock.Current.HourOfDay;
        int toMidnight = Tick.TicksPerDay - ticksIntoDay;
        AdvanceTicks(toMidnight);
    }

    /// <summary>Advances to the start of the next week.</summary>
    public void AdvanceToNextWeek()
    {
        int tickOfWeek = World.Clock.Current.TickOfWeek;
        int toWeekStart = Tick.TicksPerWeek - tickOfWeek;
        AdvanceTicks(toWeekStart);
    }

    // ---- Replay --------------------------------------------------------------

    /// <summary>
    /// Builds a second session that replays this session's seed and command log.
    /// Used by the stage-5 verifier and by determinism tests.
    /// </summary>
    /// <remarks>
    /// Tick advances are recorded as commands so the replay reproduces time exactly;
    /// without that, a log of only player decisions would not capture how far the
    /// clock ran between them.
    /// </remarks>
    public GameSession CreateReplaySession()
    {
        var replay = new GameSession(World.Seed, World.BaseLayout.Width, World.BaseLayout.Height);
        replay.World.Resources = World.Resources;
        return replay;
    }

    /// <summary>
    /// Replays this session's log against a fresh session and returns whether the two
    /// states match.
    /// </summary>
    /// <remarks>
    /// TODO(stage-5): extend to check the per-1000-tick checkpoint hashes carried in
    /// a ReplayFile, and report the first divergent tick rather than only a boolean.
    /// </remarks>
    public bool VerifyReplay(out ulong replayHash)
    {
        GameSession replay = CreateReplaySession();

        foreach (ICommand command in _commandLog)
        {
            replay.Execute(command);
            replay.AdvanceTick();
        }

        replayHash = replay.World.ComputeStateHash();
        return replayHash == World.ComputeStateHash();
    }

    // ---- Diagnostics ---------------------------------------------------------

    /// <summary>
    /// Checks world invariants. Returns false and names the first problem when the
    /// world has drifted into an illegal state.
    /// </summary>
    public bool ValidateWorld(out string problem) => World.Validate(out problem);

    /// <summary>Records that the day ended, for the top-bar date widget.</summary>
    private void WireClock()
    {
        // The clock's own events are not forwarded to subscribers directly: the tick
        // path already publishes TickAdvanced/DayChanged as part of AdvanceTick, and
        // forwarding here too would double every event.
    }
}
