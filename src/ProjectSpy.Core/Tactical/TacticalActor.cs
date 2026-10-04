using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Something standing in the building: a squad member, a guard, or a civilian.
/// </summary>
/// <remarks>
/// <para>
/// <b>One type, three populations.</b> The differences between an agent, a guard and a
/// civilian are which tables they read and which rules may touch them, not which fields
/// they hold. A guard is an actor with an archetype; a civilian is an actor without
/// one. Splitting them into three classes would triple the number of places that
/// iterate "everything in the mission" and each of those would have to remember the
/// other two.
/// </para>
/// <para>
/// <b>No room id.</b> The actor stores a <see cref="TacticalPosition"/> and nothing else
/// about where it is; <see cref="SiteLayout.RoomContaining"/> resolves that to a room.
/// Keeping a room id alongside the position would be a second source of truth about
/// location, and the two would disagree the first time something moved a step without
/// also updating the room.
/// </para>
/// <para>
/// <b>The bindings are one-way.</b> An actor may carry an <see cref="AgentId"/> or a
/// <see cref="SiteGuardId"/>, but nothing walks back from an id to an actor, because
/// the actor's identity inside a mission is its <see cref="TacticalActorId"/> and the
/// strategic ids are only ever used to write results back out at mission end.
/// </para>
/// </remarks>
public sealed class TacticalActor
{
    /// <summary>Identity for the whole mission.</summary>
    public TacticalActorId Id { get; init; }

    /// <summary>Which population this is.</summary>
    public TacticalActorKind Kind { get; init; }

    /// <summary>The strategic agent this actor is, when it is a squad member.</summary>
    public AgentId AgentId { get; init; } = AgentId.None;

    /// <summary>The generated guard this actor is, when it is site security.</summary>
    public SiteGuardId GuardId { get; init; } = SiteGuardId.None;

    /// <summary>
    /// The <c>guard_archetype</c> row backing this guard, or 0.
    /// </summary>
    /// <remarks>
    /// Copied in at mission start rather than looked up through the layout on every
    /// perception. The perception phase runs a check per observer per target per step —
    /// on a building with fifteen guards and four agents that is sixty archetype lookups
    /// every 100 ms — and each one walking the layout's guard list would turn the
    /// cheapest part of the step into the most expensive. The row is immutable, so a
    /// copy cannot drift.
    /// </remarks>
    public int GuardArchetypeId { get; init; }

    /// <summary>The generated civilian this actor is, when it is a member of the public.</summary>
    public SiteCivilianId CivilianId { get; init; } = SiteCivilianId.None;

    /// <summary>Localization key for what this is called. Core never writes prose.</summary>
    public string NameKey { get; init; } = string.Empty;

    /// <summary>Where it stands.</summary>
    public TacticalPosition Position { get; set; }

    /// <summary>Which way it is looking.</summary>
    public Facing Facing { get; set; } = Facing.Right;

    /// <summary>How it is holding itself.</summary>
    public Posture Posture { get; set; } = Posture.Crouch;

    /// <summary>How alive it is. <see cref="ActorCondition.Active"/> means it acts.</summary>
    public ActorCondition Condition { get; set; } = ActorCondition.Active;

    /// <summary>Current hit points.</summary>
    public int Health { get; set; }

    /// <summary>Maximum hit points, from the archetype or the agent's condition.</summary>
    public int MaxHealth { get; set; }

    /// <summary>
    /// Steps of unconsciousness left before this actor dies, or zero while it is not down.
    /// </summary>
    /// <remarks>
    /// Counted down in 100 ms steps by <see cref="DamageSystem"/>. The timer is the
    /// whole reason a downed agent is a decision rather than a status: an ally can still
    /// reach them, and every step they spend doing anything else is a step off the
    /// clock.
    /// </remarks>
    public long BleedOutStepsRemaining { get; set; }

    /// <summary>Stamina for movement and for the strenuous actions.</summary>
    public int Stamina { get; set; } = 100;

    /// <summary>
    /// True when this actor is carrying somebody.
    /// </summary>
    /// <remarks>
    /// Set by the carry and drag actions. Carrying halves the carrier's speed and costs
    /// stamina every step, which is why "go and get them" is a real cost rather than a
    /// free rescue — and a carried actor's own actions are suspended, so a second
    /// carry cannot start while this one is in flight.
    /// </remarks>
    public TacticalActorId Carrying { get; set; } = TacticalActorId.None;

    /// <summary>True when somebody else is carrying or dragging this actor.</summary>
    public bool IsCarried { get; set; }

    /// <summary>
    /// The disguise this actor is wearing, as an <c>interactable_type</c> disguise id, or
    /// zero when it is in its own clothes.
    /// </summary>
    /// <remarks>
    /// A disguise does not make an actor invisible to a guard who knows to look for one;
    /// it works by lowering the target's <em>identifiability</em>, so the perception
    /// breakdown shows it as the modifier that turned an identification into a notice.
    /// A number rather than a flag because a convincing one and a thin one are different
    /// and the difference is the interesting decision.
/// </remarks>
    public int DisguiseId { get; set; }

    /// <summary>
    /// True once this actor's body has been dealt with — hidden in a container, dragged
    /// somewhere out of the way, or gone.
    /// </summary>
    /// <remarks>
    /// A body somebody walks past is a reason to raise the alarm, so hiding one is a
    /// real action with a real cost and a real failure to notice. Bodies are tracked as
    /// dead actors rather than as a separate collection so that "is there a body here"
    /// is a question about actors and cannot drift out of step with them.
    /// </remarks>
    public bool BodyHidden { get; set; }

    /// <summary>Index into the guard's patrol route, or -1 for an actor with no route.</summary>
    public int RouteIndex { get; set; } = -1;

    /// <summary>True once the guard has left its route to deal with something.</summary>
    public bool IsOffRoute { get; set; }

    /// <summary>How suspicious this actor is of the team, 0-100.</summary>
    public SuspicionMeter Suspicion { get; } = new();

    /// <summary>What this actor currently believes about where the team is.</summary>
    public PerceptionMemory Memory { get; } = new();

    /// <summary>Vision, hearing and skill: what an observer brings to a perception check.</summary>
    public VisionStats Vision { get; set; } = VisionStats.Default;

    /// <summary>
    /// The actor's skills, copied in at mission start.
    /// </summary>
    /// <remarks>
    /// A copy rather than a reference to the strategic <see cref="Agent"/>'s
    /// <see cref="SkillSet"/>. The mission is a self-contained simulation and must
    /// reproduce from seed plus order log alone; if an actor read the strategic agent
    /// live, a save loaded at a different moment in the run would see different skills
    /// and the replay would diverge for a reason that has nothing to do with the
    /// mission.
    /// </remarks>
    public SkillSet SkillSnapshot { get; init; }

    /// <summary>
    /// Accumulated moral strain from lethal acts, 0 to 100.
    /// </summary>
    /// <remarks>
    /// Rule 19 requires that a lethal act cost the doer something and that the cost come
    /// from the table. This is where it lands. It is tracked separately from
    /// <see cref="Stamina"/> because it recovers on its own schedule — slowly, in the
    /// base, between missions — and lumping it into a stamina pool would make killing
    /// cost the same thing as running.
    /// </remarks>
    public int MoralStrain { get; set; }

    /// <summary>The action currently being carried out, if any.</summary>
    public PendingAction? Action { get; set; }

    /// <summary>
    /// True when this actor may be given new orders.
    /// </summary>
    /// <remarks>
    /// The single gate every command validates through. An actor that is dead,
    /// captured, unconscious or already carrying somebody else cannot be given an
    /// order, and having one predicate rather than four call-site checks is what stops
    /// a new action kind from forgetting the condition.
    /// </remarks>
    public bool CanAct => Condition == ActorCondition.Active && !IsCarried;

    /// <summary>True when this actor is squad.</summary>
    public bool IsAgent => Kind == TacticalActorKind.Agent;

    /// <summary>True when this actor is site security.</summary>
    public bool IsGuard => Kind == TacticalActorKind.Guard;

    /// <summary>True when this actor is a member of the public.</summary>
    public bool IsCivilian => Kind == TacticalActorKind.Civilian;

    /// <inheritdoc/>
    public override string ToString()
        => $"{Id} {Kind} {Position} {Posture} {Condition} hp={Health}/{MaxHealth}";
}

/// <summary>
/// What an observer brings to a perception check: how far they can see, how wide the
/// cone is, how well they can hear, and the skill that sharpens both.
/// </summary>
/// <remarks>
/// <para>
/// Filled from <c>guard_archetype</c> for a guard, and derived from an agent's
/// <see cref="SkillSet"/> for a squad member — rule 16 makes Infiltration govern
/// perception range, so an agent's vision is a function of a skill they train at the
/// base and the number changes when they do.
/// </para>
/// <para>
/// <see cref="VisionConeDegrees"/> survives from the table even though the building is
/// one-dimensional, because it still says something real: a cone under 180 degrees
/// means the observer cannot see behind themselves, and a 360-degree one (the blind
/// dog's) means facing does not matter at all. Reading it as "does the target have to be
/// in front of me" rather than trying to reconstruct an angle on a line is the honest
/// reduction.
/// </para>
/// </remarks>
public readonly record struct VisionStats
{
    /// <summary>How far a shape can be made out, in centimetres.</summary>
    public int VisionRangeCm { get; init; }

    /// <summary>
    /// The cone, in degrees. Under 180 means a target behind the observer is not seen
    /// however close it is; 360 means it is seen however far away.
    /// </summary>
    public int VisionConeDegrees { get; init; }

    /// <summary>How far a noise carries to this observer unaided, in centimetres.</summary>
    public int HearingRangeCm { get; init; }

    /// <summary>
    /// A percentage added to the effective sight range, from Infiltration or the
    /// archetype. Zero for an ordinary guard.
    /// </summary>
    public int RangeBonusPercent { get; init; }

    /// <summary>The stats of an observer with nothing: never sees and never hears.</summary>
    public static VisionStats Default => new()
    {
        VisionRangeCm = 0,
        VisionConeDegrees = 180,
        HearingRangeCm = 0,
        RangeBonusPercent = 0,
    };

    /// <summary>True when a target behind the observer would still be seen.</summary>
    public bool SeesBehind => VisionConeDegrees >= 180;
}

/// <summary>
/// An action in progress, and the steps still owed on it.
/// </summary>
/// <remarks>
/// <para>
/// Stored on the actor rather than in a global queue so that an actor is never in two
/// places at once by construction, and so that what an actor is doing is readable
/// without walking a queue. The steps already spent stay spent: if something interrupts
/// the actor, the action resumes where it was rather than starting again.
/// </para>
/// <para>
/// The remaining counters are the shape of the action, not a payload — "which connection
/// are we operating on" or "how far are we going" — so that the action system has one
/// record to interpret instead of a variant per kind.
/// </para>
/// </remarks>
public sealed class PendingAction
{
    /// <summary>What is being done.</summary>
    public TacticalActionKind Kind { get; init; }

    /// <summary>The <c>tactical_action</c> row being carried out.</summary>
    public int ActionId { get; init; }

    /// <summary>Total steps the action costs.</summary>
    public int TotalSteps { get; init; }

    /// <summary>Steps already spent on it.</summary>
    public int StepsSpent { get; set; }

    /// <summary>Steps still owed.</summary>
    public int StepsRemaining => TotalSteps - StepsSpent;

    /// <summary>Fraction of the action done, in percent. Zero when just starting.</summary>
    public int PercentComplete => TotalSteps <= 0
        ? 100
        : (int)((long)StepsSpent * 100L / TotalSteps);

    /// <summary>The connection being crossed or operated, or <see cref="SiteConnectionId.None"/>.</summary>
    public SiteConnectionId ConnectionId { get; init; } = SiteConnectionId.None;

    /// <summary>The interactable being worked, or zero.</summary>
    public int InteractableId { get; init; }

    /// <summary>The light being switched or smashed, or zero.</summary>
    public int LightId { get; init; }

    /// <summary>The target actor, for attacks, support and carry.</summary>
    public TacticalActorId TargetActorId { get; init; } = TacticalActorId.None;

    /// <summary>The item used, for a throw or a weapon.</summary>
    public int ItemId { get; init; }

    /// <summary>
    /// Where a movement is heading, as a point on the current floor.
    /// </summary>
    /// <remarks>Ignored by every kind except <see cref="TacticalActionKind.Move"/>.</remarks>
    public TacticalPosition Target { get; init; } = TacticalPosition.None;

    /// <summary>
    /// How many centimetres of travel are left on a movement.
    /// </summary>
    /// <remarks>
    /// Tracked in centimetres rather than steps because speed is a per-step distance
    /// and the two do not divide evenly. A movement of 100 cm at 6 cm per step is
    /// seventeen steps and a fraction, and rounding that to seventeen up front would
    /// make prone travel quantised to the centimetre and travel at 48 cm per step
    /// quantised differently for no reason a player could see.
    /// </remarks>
    public int RemainingCm { get; set; }

    /// <summary>
    /// Which way along the floor a movement is travelling: <c>+1</c> towards rising
    /// centimetres, <c>-1</c> towards falling ones.
    /// </summary>
    /// <remarks>
    /// Carried on the action rather than read back off <see cref="TacticalActor.Facing"/>
    /// at each step, because facing is what other systems observe and movement is what
    /// sets it: reading it back would make a move's direction depend on whatever turned
    /// the actor since, which is exactly the kind of coupling that makes a replay diverge
    /// from the run it is replaying. Ignored by every kind except
    /// <see cref="TacticalActionKind.Move"/>.
    /// </remarks>
    public int DirectionSign { get; init; } = 1;

    /// <summary>True once the action has finished its steps and applied its effects.</summary>
    public bool IsComplete => StepsRemaining <= 0;

    /// <inheritdoc/>
    public override string ToString()
        => $"{Kind} action {ActionId} {StepsSpent}/{TotalSteps}";
}

/// <summary>
/// What one observer believes about the team right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Belief, not truth.</b> This is the last place something was actually perceived,
/// not where anything is. A guard who has lost sight of the team is looking for the
/// last place they were, and the gap between the two is what a stealth mission is
/// played on. Collapsing the two would make a guard omniscient the moment it turned
/// away.
/// </para>
/// <para>
/// <b>Decays rather than clears.</b> <see cref="LastSeenStep"/> lets the alarm decide
/// that a sighting is cold, and a guard that forgot instantly would be trivially
/// outwitted by walking behind a pillar.
/// </para>
/// </remarks>
public sealed class PerceptionMemory
{
    /// <summary>Where something was last actually perceived, or <see cref="TacticalPosition.None"/>.</summary>
    public TacticalPosition LastSeen { get; set; } = TacticalPosition.None;

    /// <summary>The step that sighting happened on.</summary>
    public long LastSeenStep { get; set; }

    /// <summary>Who was seen, or <see cref="TacticalActorId.None"/>.</summary>
    public TacticalActorId LastSeenActorId { get; set; } = TacticalActorId.None;

    /// <summary>
    /// Where a noise was heard and worth looking at, or
    /// <see cref="TacticalPosition.None"/>.
    /// </summary>
    public TacticalPosition LastNoiseOrigin { get; set; } = TacticalPosition.None;

    /// <summary>The step that noise was heard on.</summary>
    public long LastNoiseStep { get; set; }

    /// <summary>The best level anything has ever reached, so escalation is one-way.</summary>
    public PerceptionLevel PeakLevel { get; set; } = PerceptionLevel.None;

    /// <summary>True when something has been perceived at all.</summary>
    public bool HasContact => PeakLevel >= PerceptionLevel.Noticed;

    /// <summary>True when something has been identified well enough to act on.</summary>
    public bool HasIdentification => PeakLevel >= PerceptionLevel.Identified;

    /// <summary>
    /// Steps since anything was perceived, or a large number when nothing ever was.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="long.MaxValue"/> rather than throwing or clamping to a magic
    /// "very long time": every caller wants "how stale is this" and the only question
    /// any of them could ask a smaller value is "is it stale yet", which compares
    /// against a step budget of its own.
    /// </remarks>
    public long StepsSinceContact(long now)
        => PeakLevel == PerceptionLevel.None ? long.MaxValue : now - LastSeenStep;

    /// <summary>Records a perception. Never lowers <see cref="PeakLevel"/>.</summary>
    public void NotePerception(TacticalActorId actorId, TacticalPosition where, PerceptionLevel level, long step)
    {
        if (level > PeakLevel)
            PeakLevel = level;

        if (level < PerceptionLevel.Identified)
            return;

        LastSeen = where;
        LastSeenActorId = actorId;
        LastSeenStep = step;
    }

    /// <summary>Records a noise worth walking towards.</summary>
    public void NoteNoise(TacticalPosition origin, long step)
    {
        LastNoiseOrigin = origin;
        LastNoiseStep = step;
    }

    /// <summary>Forgets everything. Used when a mission's alarm resets or on re-entry.</summary>
    public void Clear()
    {
        LastSeen = TacticalPosition.None;
        LastSeenActorId = TacticalActorId.None;
        LastNoiseOrigin = TacticalPosition.None;
        LastSeenStep = 0;
        LastNoiseStep = 0;
        PeakLevel = PerceptionLevel.None;
    }
}
