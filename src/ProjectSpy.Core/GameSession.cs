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

    /// <summary>
    /// The replay input: commands and tick advances in the order they actually
    /// happened.
    /// </summary>
    /// <remarks>
    /// Stage 1 kept two parallel logs and replayed every command before the first tick
    /// batch. That happens to work while the player only ever issues commands before the
    /// first tick, and silently corrupts the replay the moment they do not: building a
    /// room, playing a week, then building another would replay both builds before the
    /// week ever ran, and the resulting state would differ from the original. A
    /// determinism test built on that arrangement would prove nothing, because the
    /// divergence would have been in the harness rather than in the simulation.
    /// <para>
    /// So the order is recorded directly instead of being reconstructed.
    /// </para>
    /// </remarks>
    private readonly List<ReplayEntry> _replayLog = new();

    /// <summary>Creates a session over a fresh world.</summary>
    public GameSession(ulong seed, int baseWidth = 24, int baseHeight = 12)
        : this(new WorldState(seed, baseWidth, baseHeight))
    {
    }

    /// <summary>Creates a session over an existing world — used when loading a save.</summary>
    public GameSession(WorldState world)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));

        // The world publishes through this session's pending buffer, so a command's
        // events are buffered and flushed at exactly the same point as a phase's.
        World.Events = this;

        // The production pipeline: all six phases, in canonical order. A session
        // constructed this way is fully simulated from tick one.
        _pipeline = Phases.CreateDefault(this);
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

    /// <summary>
    /// Every tick advance, recorded so a replay reproduces time exactly.
    /// </summary>
    /// <remarks>
    /// A log of only player decisions would not capture how far the clock ran between
    /// them, and the simulation is heavily time-dependent — so the advance count has to
    /// be part of the replay input, not an assumption about how fast the player clicks.
    /// </remarks>
    private readonly List<int> _tickLog = new();

    /// <summary>Tick advances in order. The replay input, alongside the command log.</summary>
    public IReadOnlyList<int> TickLog => _tickLog;

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
        _replayLog.Add(ReplayEntry.ForCommand(command));
        FlushEvents();
        return CommandResult.Ok;
    }

    /// <summary>
    /// Advances one tick: raises tick/day/week notifications, then runs the tick
    /// pipeline in its canonical order, then flushes events.
    /// </summary>
    /// <remarks>
    /// The week boundary deliberately does <em>not</em> publish a
    /// <see cref="WeekSettled"/> event here. Stage 1 did, with zero amounts, as a
    /// placeholder; now that <see cref="EconomySystem"/> owns settlement, publishing one
    /// here as well would show the player two settlements a week, one of them always
    /// zero. The real event carries the real totals and comes from the economy phase.
    /// </remarks>
    public void AdvanceTick()
    {
        Tick previous = World.Clock.Current;

        World.Clock.Advance();
        Tick now = World.Clock.Current;

        Publish(new TickAdvanced(now, now.Day, now.HourOfDay));

        if (!now.IsSameDay(previous))
            Publish(new DayChanged(now, now.Day, now.Week));

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

        // Recorded as one entry rather than N, so a replay reproduces the same batching.
        // The batching is what decides whether a periodic rule (weekly settlement, pool
        // refresh) fires, so it has to be replayed exactly rather than inferred.
        _tickLog.Add(count);
        _replayLog.Add(ReplayEntry.ForTicks(count));

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
    /// Builds a fresh session that will replay this one's input.
    /// </summary>
    /// <remarks>
    /// Only the seed and the grid dimensions carry over. Everything else — resources,
    /// roster, RNG state — is reproduced by re-executing the log, because copying it
    /// would let a divergence between the original run and the replay hide behind the
    /// copy.
    /// </remarks>
    public GameSession CreateReplaySession()
        => new(World.Seed, World.BaseLayout.Width, World.BaseLayout.Height);

    /// <summary>
    /// Replays this session's recorded input against a fresh session and compares the
    /// resulting state hashes.
    /// </summary>
    /// <remarks>
    /// A hash match is necessary but not sufficient evidence of determinism: two
    /// different worlds can hash alike if the hash does not actually cover every field.
    /// <see cref="WorldStateSerializer.ToCanonicalBytes"/> exists for the stricter
    /// check, which compares the full state rather than a summary of it.
    /// </remarks>
    public bool VerifyReplay(out ulong replayHash)
    {
        GameSession replay = CreateReplaySession();
        Replay(replay);
        replayHash = replay.World.ComputeStateHash();
        return replayHash == World.ComputeStateHash();
    }

    /// <summary>
    /// Replays the recorded input into a target session, in the order it originally
    /// occurred.
    /// </summary>
    public void Replay(GameSession target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));

        foreach (ReplayEntry entry in _replayLog)
        {
            if (entry.Command is not null)
                target.Execute(entry.Command);
            else
                target.AdvanceTicks(entry.TickCount);
        }
    }

    /// <summary>
    /// One entry in the replay input: either a command or a batch of tick advances.
    /// </summary>
    private readonly record struct ReplayEntry
    {
        private ReplayEntry(ICommand? command, int tickCount)
        {
            Command = command;
            TickCount = tickCount;
        }

        /// <summary>The command to re-execute, or null for a tick batch.</summary>
        internal ICommand? Command { get; }

        /// <summary>How many ticks to advance, or zero for a command.</summary>
        internal int TickCount { get; }

        internal static ReplayEntry ForCommand(ICommand command)
            => new(command, 0);

        internal static ReplayEntry ForTicks(int count)
            => new(null, count);
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
