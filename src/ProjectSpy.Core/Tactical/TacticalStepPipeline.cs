namespace ProjectSpy.Core.Tactical;

/// <summary>
/// The ten phases of a tactical step, in the fixed order they must run.
/// </summary>
/// <remarks>
/// <para>
/// knowledge.md rule 6 names this order as part of the save and replay contract:
/// <c>ApplyQueuedCommands → MovementResolution → ActionProgress → NoisePropagation →
/// Perception → NpcPlanning → AlarmUpdate → DamageAndStatus → ObjectiveCheck →
/// EventEmit</c>. It is declared once, here, and asserted by <c>TacticalStepOrderTests</c>
/// rather than being left to whoever writes the pipeline. Reordering these phases
/// changes every outcome for an existing save and silently invalidates every recorded
/// replay, which is the same argument that governs <see cref="TickPipeline"/>.
/// </para>
/// <para>
/// This is the <em>second</em> of the two orders rule 6 protects, and the two never
/// interleave: the strategic pipeline runs between missions and this one runs inside
/// them, and <see cref="GameSession"/> throws rather than letting both be live at once.
/// </para>
/// <para>
/// The enum values are the order. Phases are held in a dictionary and executed in enum
/// order, so a phase registered later cannot land in the wrong slot.
/// </para>
/// </remarks>
public enum TacticalStepPhase
{
    /// <summary>Take every order issued since the last step and start it.</summary>
    ApplyQueuedCommands = 0,

    /// <summary>Move everybody who is moving by one step.</summary>
    MovementResolution = 1,

    /// <summary>Spend one step on everybody's action in flight.</summary>
    ActionProgress = 2,

    /// <summary>Work out who heard the noises this step made.</summary>
    NoisePropagation = 3,

    /// <summary>Resolve what every observer can see.</summary>
    Perception = 4,

    /// <summary>Decide what the NPCs want and start their orders.</summary>
    NpcPlanning = 5,

    /// <summary>Recompute the site alarm from what the NPCs know, and apply its band.</summary>
    AlarmUpdate = 6,

    /// <summary>Advance bleed-out timers and stamina recovery.</summary>
    DamageAndStatus = 7,

    /// <summary>Progress the objective and decide whether the mission is over.</summary>
    ObjectiveCheck = 8,

    /// <summary>Publish everything this step produced.</summary>
    EventEmit = 9,
}

/// <summary>What one phase of a step did.</summary>
/// <param name="Phase">Which phase.</param>
/// <param name="Detail">A localization key or code describing the outcome.</param>
public readonly record struct TacticalPhaseReport(TacticalStepPhase Phase, string Detail);

/// <summary>
/// Runs the ten step phases in their fixed order.
/// </summary>
/// <remarks>
/// <para>
/// Shaped exactly like <see cref="TickPipeline"/> — a dictionary keyed by phase,
/// executed in enum order — because the two orders are the same kind of promise and a
/// runner that behaved differently from the other one would be a second thing to learn.
/// </para>
/// <para>
/// <b>Reports, not side effects on the caller.</b> Every phase returns what it did, and
/// the runner collects them. That is what lets a test assert that a step ran in the
/// canonical order and that a phase did something, without a phase having to write to a
/// shared log that a bug in the ordering could corrupt.
/// </para>
/// </remarks>
public sealed class TacticalStepPipeline
{
    private readonly Dictionary<TacticalStepPhase, ITacticalStepPhase> _phases = new();

    /// <summary>
    /// The canonical order every step must follow. Reading this is how the test proves
    /// the contract.
    /// </summary>
    public static IReadOnlyList<TacticalStepPhase> CanonicalOrder { get; } = new[]
    {
        TacticalStepPhase.ApplyQueuedCommands,
        TacticalStepPhase.MovementResolution,
        TacticalStepPhase.ActionProgress,
        TacticalStepPhase.NoisePropagation,
        TacticalStepPhase.Perception,
        TacticalStepPhase.NpcPlanning,
        TacticalStepPhase.AlarmUpdate,
        TacticalStepPhase.DamageAndStatus,
        TacticalStepPhase.ObjectiveCheck,
        TacticalStepPhase.EventEmit,
    };

    /// <summary>Phases currently registered.</summary>
    public IReadOnlyCollection<ITacticalStepPhase> Registered => _phases.Values;

    /// <summary>Registers or replaces a phase.</summary>
    public void Register(ITacticalStepPhase phase)
    {
        if (phase is null) throw new ArgumentNullException(nameof(phase));
        _phases[phase.Phase] = phase;
    }

    /// <summary>Runs every registered phase in canonical order, collecting the reports.</summary>
    public IReadOnlyList<TacticalPhaseReport> RunStep(StepContext context)
    {
        var reports = new List<TacticalPhaseReport>(CanonicalOrder.Count);

        foreach (TacticalStepPhase phase in CanonicalOrder)
        {
            if (!_phases.TryGetValue(phase, out ITacticalStepPhase? implementation))
                continue;

            reports.Add(new TacticalPhaseReport(phase, implementation.Run(context)));
        }

        return reports;
    }
}

/// <summary>Everything a step phase is allowed to touch.</summary>
/// <remarks>
/// A struct rather than the state itself so that a phase cannot hold on to it and read
/// it after the step has ended — which would be reading a world that no longer exists,
/// and would make a phase's behaviour depend on when it was called rather than on when
/// it ran.
/// </remarks>
/// <param name="State">The mission being played.</param>
/// <param name="Rng">The Tactical stream. Rule 6: a named stream, never a global.</param>
/// <param name="PlannerRng">The Goap stream, used only by the planning phase.</param>
public readonly record struct StepContext(
    TacticalState State,
    IRng Rng,
    IRng PlannerRng);

/// <summary>One stage of the tactical step pipeline.</summary>
public interface ITacticalStepPhase
{
    /// <summary>The phase this implementation provides. Order comes from the enum.</summary>
    TacticalStepPhase Phase { get; }

    /// <summary>Runs for a single step and returns what it did.</summary>
    string Run(StepContext context);
}
