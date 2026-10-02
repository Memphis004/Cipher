namespace ProjectSpy.Core;

/// <summary>
/// The per-tick simulation phases, in the fixed order they must run.
/// </summary>
/// <remarks>
/// <para>
/// This order is part of the save contract: reordering these phases changes every
/// outcome for an existing save and silently invalidates every recorded replay. It
/// is therefore declared once, here, and asserted by
/// <c>TickOrderTests</c> rather than being left to whoever writes the pipeline.
/// </para>
/// <para>
/// Construction must resolve before training does, so a room finishes in the tick
/// its construction completes and an agent can use it the same tick rather than
/// losing a tick per room.
/// </para>
/// </remarks>
public enum TickPhase
{
    /// <summary>Advance construction timers and finish completed rooms.</summary>
    RoomConstruction = 0,

    /// <summary>Apply training gains and stamina cost.</summary>
    Training = 1,

    /// <summary>Restore stamina and heal injuries.</summary>
    Recovery = 2,

    /// <summary>Advance in-flight missions and resolve events.</summary>
    MissionProgress = 3,

    /// <summary>Roll narrative and world events.</summary>
    EventChecks = 4,

    /// <summary>Apply status drift, morale and burnout decay.</summary>
    StatusDecay = 5,
}

/// <summary>One stage of the per-tick pipeline.</summary>
public interface ITickPhase
{
    /// <summary>The phase this implementation provides. Order comes from the enum.</summary>
    TickPhase Phase { get; }

    /// <summary>Runs for a single tick.</summary>
    void Run(WorldState world, PhaseContext context);
}

/// <summary>Per-tick context handed to each phase.</summary>
public readonly record struct PhaseContext(Tick Tick)
{
    /// <summary>True when this tick crossed midnight.</summary>
    public bool IsDayStart { get; init; }

    /// <summary>True when this tick crossed into a new week.</summary>
    public bool IsWeekStart { get; init; }

    /// <summary>
    /// Phase-owned sink for events. Using the context rather than the session keeps
    /// a phase from publishing into a stream it was not handed.
    /// </summary>
    public IEventSink Events { get; init; } = null!;
}

/// <summary>Sink the pipeline and commands publish events into.</summary>
public interface IEventSink
{
    /// <summary>Publishes an event to subscribers.</summary>
    void Publish(GameEvent gameEvent);
}

/// <summary>
/// Runs the per-tick phases in their fixed order.
/// </summary>
/// <remarks>
/// Phases are held in a dictionary and executed in <see cref="TickPhase"/> order
/// rather than in registration order, so adding a phase later cannot accidentally
/// run it in the wrong slot.
/// </remarks>
public sealed class TickPipeline
{
    private readonly Dictionary<TickPhase, ITickPhase> _phases = new();
    private readonly IEventSink _events;

    public TickPipeline(IEventSink events)
        => _events = events ?? throw new ArgumentNullException(nameof(events));

    /// <summary>
    /// The canonical order every run must follow. Reading this is how the stage-3
    /// test proves the contract.
    /// </summary>
    public static IReadOnlyList<TickPhase> CanonicalOrder { get; } = new[]
    {
        TickPhase.RoomConstruction,
        TickPhase.Training,
        TickPhase.Recovery,
        TickPhase.MissionProgress,
        TickPhase.EventChecks,
        TickPhase.StatusDecay,
    };

    /// <summary>Phases currently registered.</summary>
    public IReadOnlyCollection<ITickPhase> Registered => _phases.Values;

    /// <summary>Registers or replaces a phase.</summary>
    public void Register(ITickPhase phase)
    {
        if (phase is null) throw new ArgumentNullException(nameof(phase));
        _phases[phase.Phase] = phase;
    }

    /// <summary>
    /// Runs every registered phase in canonical order. Phases not yet implemented
    /// simply have no implementation registered, which keeps the pipeline runnable at
    /// stage 1 while each phase lands in stage 3.
    /// </summary>
    public void RunTick(WorldState world, PhaseContext context)
    {
        foreach (TickPhase phase in CanonicalOrder)
        {
            if (_phases.TryGetValue(phase, out ITickPhase? implementation))
                implementation.Run(world, context);
        }
    }
}
