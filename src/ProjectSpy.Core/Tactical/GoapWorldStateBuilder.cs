using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// What an NPC has personally observed since the last world-state rebuild.
/// </summary>
/// <remarks>
/// <para>
/// The distinction this type exists to enforce: <b>the site knows</b> versus
/// <b>this NPC knows</b>. A door in the next room is open, but unless this guard has
/// walked past it or been told, that is not a fact about <em>them</em> and must not
/// enter their plan. Findings are how the outside world — perception, the alarm system,
/// another NPC's report — tells this builder something without the builder reaching
/// into the site to look.
/// </para>
/// <para>
/// Deliberately a plain mutable record rather than a set of events drained from a queue:
/// findings are latched and cleared by <see cref="GoapAgent.Invalidate"/>, so a guard
/// that hears a noise and is interrupted before it replans still knows about the noise
/// when it gets there. Draining on read would lose it.
/// </para>
/// </remarks>
public sealed class GoapFindings
{
    /// <summary>A noise was heard from a direction worth walking to.</summary>
    public bool NoiseHeard { get; set; }

    /// <summary>The team was positively identified, not merely glimpsed.</summary>
    public bool ContactIdentified { get; set; }

    /// <summary>A body was found where this NPC could see it.</summary>
    public bool BodyFound { get; set; }

    /// <summary>A colleague was found missing from a post they should hold.</summary>
    public bool ColleagueMissing { get; set; }

    /// <summary>A colleague is down within this NPC's sight.</summary>
    public bool AllyDown { get; set; }

    /// <summary>Somebody reported an intruder, and this NPC believes them.</summary>
    public bool ToldOfIntruder { get; set; }

    /// <summary>Something this NPC is guarding was interfered with.</summary>
    public bool ObjectiveTampered { get; set; }

    /// <summary>
    /// This civilian has heard something terrifying and is not thinking any more.
    /// </summary>
    /// <remarks>
    /// Latched by the alarm, not inferred. A civilian panics because they heard the
    /// alarm or saw the crowd start moving — not because Core decided civilians are
    /// uninvolved — which is what lets a player set a site off with a noise far from
    /// where anybody is.
    /// </remarks>
    public bool Panicked { get; set; }

    /// <summary>A prisoner came within reach.</summary>
    public bool PrisonerNearby { get; set; }

    /// <summary>The lights went out.</summary>
    public bool PowerOut { get; set; }

    /// <summary>
    /// A door on this NPC's own route was found standing open.
    /// </summary>
    /// <remarks>
    /// Counted rather than flagged so that a guard who finds three doors open behaves
    /// differently from one who found one, which is the difference between closing a
    /// door and starting to think the building has been broken into.
    /// </remarks>
    public int SuspiciousDoorsSeen { get; set; }

    /// <summary>
    /// How good the place this NPC last hid something in was, 0 to 100.
    /// </summary>
    /// <remarks>
    /// A quality rather than a flag, because hiding a body somewhere good and
    /// somewhere hopeless are different outcomes and the brief asks that the hiding
    /// place matter. A guard's chance of finding it scales with this.
    /// </remarks>
    public int HidingPlaceQuality { get; set; }

    /// <summary>
    /// True once this NPC has transmitted on a radio.
    /// </summary>
    /// <remarks>
    /// A relay rather than a flag, and the distinction is the whole mechanic. Carrying a
    /// radio is not the escalation; transmitting on it is. The gap between the two is
    /// exactly the window a player spends cutting the wire or taking the carrier down
    /// first, so collapsing them into one boolean would delete the counter-play the brief
    /// asks for.
    /// </remarks>
    public bool RadioTransmitted { get; set; }

    /// <summary>
    /// True when this NPC's radio has been cut or is otherwise unserviceable.
    /// </summary>
    /// <remarks>
    /// The counter-play half of the radio mechanic, and the reason
    /// <see cref="RadioTransmitted"/> is not enough on its own: a guard who is silenced
    /// must be distinguishable from one who never carried a handset, so that a player
    /// can tell a successful silent takedown from a guard who happened not to have one.
    /// </remarks>
    public bool RadioCut { get; set; }

    /// <summary>Clears everything. Called when a plan is rebuilt from scratch.</summary>
    public void Clear()
    {
        NoiseHeard = false;
        ContactIdentified = false;
        BodyFound = false;
        ColleagueMissing = false;
        AllyDown = false;
        ToldOfIntruder = false;
        ObjectiveTampered = false;
        Panicked = false;
        PrisonerNearby = false;
        PowerOut = false;
        SuspiciousDoorsSeen = 0;
        HidingPlaceQuality = 0;
        RadioTransmitted = false;
        RadioCut = false;
    }
}

/// <summary>
/// Assembles one NPC's <see cref="GoapWorldState"/> from what that NPC knows.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the no-omniscience boundary.</b> Every fact here comes from one of three
/// places, and all three are about this NPC rather than about the site:
/// </para>
/// <list type="number">
/// <item>
/// <see cref="TacticalActor.Memory"/> — what this NPC perceived, and when. Perception
/// writes it and nothing else does.
/// </item>
/// <item>
/// <see cref="GoapFindings"/> — what has been reported to or observed by this NPC.
/// </item>
/// <item>
/// <see cref="TacticalActor"/>'s own condition, position and suspicion — facts about
/// itself.
/// </item>
/// </list>
/// <para>
/// The builder deliberately receives no <see cref="TacticalState"/>. That is not a
/// style preference; it is the mechanism. It is structurally impossible for this code
/// to read the player's position, the site alarm, or another guard's memory, so the
/// "NPCs never act on information they do not possess" test can corrupt every one of
/// those and assert the plans are unchanged. A builder that took the mission would make
/// that test pass only as long as nobody later added a line that used it.
/// </para>
/// <para>
/// <b>Where the site-wide alarm is a genuine exception.</b> The alarm level is read by
/// <see cref="GoapGoalSelector"/>, not here, and only for goal <em>priority</em> — never
/// as a fact the NPC acts on. A siren is public; what caused it is not. That split is
/// what lets a guard respond to a building-wide alarm without the guard thereby knowing
/// where the intruder is.
/// </para>
/// </remarks>
public static class GoapWorldStateBuilder
{
    /// <summary>
    /// Builds the world state for <paramref name="actor"/>.
    /// </summary>
    /// <param name="actor">The NPC. Read for its own condition and its own memory.</param>
    /// <param name="findings">What this NPC has been told or has noticed.</param>
    /// <param name="step">The step being resolved, for the "steps since" counters.</param>
    /// <param name="now">
    /// Facts about the immediate surroundings this NPC can see without a perception
    /// roll: which door it is standing at, whether the light above it is out.
    /// </param>
    public static GoapWorldState Build(
        TacticalActor actor,
        GoapFindings findings,
        long step,
        GoapLocalObservations now)
    {
        if (actor is null) throw new ArgumentNullException(nameof(actor));
        if (findings is null) throw new ArgumentNullException(nameof(findings));

        var world = new GoapWorldState();

        ApplySelf(actor, world);
        ApplyKnowledge(actor, world, findings, step, now);
        ApplySurroundings(actor, world, now);
        ApplyLifeCheck(actor, world);

        return world;
    }

    /// <summary>
    /// Facts about the NPC itself, which it does not have to perceive.
    /// </summary>
    private static void ApplySelf(TacticalActor actor, GoapWorldState world)
    {
        world.Set(GoapKey.NoAlarm, true);
        world.Set(GoapKey.DayNotFinished, true);
        world.Set(GoapKey.AtPost, true);
    }

    /// <summary>
    /// A sanity net: nothing the builder writes may be true for an actor that cannot act.
    /// </summary>
    /// <remarks>
    /// Not paranoia. A downed guard with a live plan is a guard that gets up and walks,
    /// and the damage phase that put them down does not know anything about the planner.
    /// This is the cheapest place to enforce the invariant because the builder is the
    /// only thing that produces a world state.
    /// </remarks>
    private static void ApplyLifeCheck(TacticalActor actor, GoapWorldState world)
    {
        if (actor.CanAct)
            return;

        world.Set(GoapKey.HasIntruder, false);
        world.Set(GoapKey.HasContact, false);
        world.Set(GoapKey.TargetVisible, false);
        world.Set(GoapKey.InMeleeRange, false);
        world.Set(GoapKey.SelfEndangered, false);
        world.Set(GoapKey.Exposed, false);
    }

    /// <summary>
    /// What the NPC has perceived, heard, or been told.
    /// </summary>
    /// <remarks>
    /// The contact and noise counters are the whole no-omniscience mechanism in
    /// arithmetic form. They are computed from <see cref="PerceptionMemory"/>'s own
    /// record of what this NPC saw and when — never from where anybody actually is. A
    /// guard that has lost the team has a rising "steps since contact" counter and a
    /// frozen last-known position, which is exactly the state a stealth player is
    /// manipulating.
    /// </remarks>
    private static void ApplyKnowledge(
        TacticalActor actor,
        GoapWorldState world,
        GoapFindings findings,
        long step,
        GoapLocalObservations now)
    {
        PerceptionMemory memory = actor.Memory;

        bool contact = memory.HasContact || findings.ContactIdentified || findings.ToldOfIntruder;
        world.Set(GoapKey.HasIntruder, contact);
        world.Set(GoapKey.HasContact, contact);

        if (memory.LastSeenActorId.IsValid && memory.StepsSinceContact(step) == 0)
            world.Set(GoapKey.TargetVisible, true);

        // "Lost" means the contact went cold, which requires having had one. Without
        // this guard a guard who has never seen anybody in the entire mission reads as
        // one who saw somebody and has been chasing a stale position for hours — which
        // satisfies `goal.pursue_target`'s own satisfaction condition and would leave a
        // guard standing still on a patrol, convinced it had already given up.
        bool everHadContact = memory.PeakLevel != PerceptionLevel.None;

        if (everHadContact && memory.StepsSinceContact(step) >= TargetLostThresholdSteps)
            world.Set(GoapKey.TargetLostForSteps, true);

        // Zero when nothing was ever seen, so the counter reads as "no contact" rather
        // than as an astronomically old one.
        world.SetCount(GoapKey.StepsSinceContact, everHadContact ? ClampSteps(memory.StepsSinceContact(step)) : 0);

        // Noise. `LastNoiseStep` is zero until something is heard, and treating that as
        // "heard at step zero" would make a guard that has heard nothing all mission
        // look like it is investigating a noise from the start of the mission.
        bool heard = memory.LastNoiseOrigin.IsValid || findings.NoiseHeard;

        world.Set(GoapKey.HasNoiseHeard, heard);
        world.SetCount(GoapKey.StepsSinceNoise, heard ? ClampSteps(step - memory.LastNoiseStep) : 0);

        // A noise stops being worth walking towards once it is old enough to have been
        // overtaken by whatever happened since. Left latched here rather than as a
        // separate "still fresh" flag so that the freshness test lives in exactly one
        // place and the planner does not have to re-derive it.
        if (heard && world.GetCount(GoapKey.StepsSinceNoise) > NoiseStillFreshSteps)
            world.Set(GoapKey.HasNoiseHeard, false);

        if (findings.BodyFound)
            world.Set(GoapKey.BodyUnsearched, true);

        world.Set(GoapKey.AllyDown, findings.AllyDown);
        world.Set(GoapKey.PrisonerNearby, findings.PrisonerNearby);
        world.Set(GoapKey.PowerOut, findings.PowerOut);
        world.Set(GoapKey.ObjectiveTampered, findings.ObjectiveTampered);
        world.Set(GoapKey.Panicked, findings.Panicked);

        // Panic follows the alarm, which is public. This is the civilian half of the
        // same public information guards read, and it is why a noise far from anybody
        // can still empty a room.
        if (now.AlarmRaised && actor.IsCivilian)
            world.Set(GoapKey.HeardAlarm, true);

        // The alarm is public. A guard may know the building is on alert without knowing
        // where the intruder is, and that asymmetry is what keeps an alarm from
        // functioning as a tracker.
        world.Set(GoapKey.NoAlarm, !now.AlarmRaised);

        if (findings.SuspiciousDoorsSeen > 0)
            world.Set(GoapKey.SuspiciousDoorOpen, true);

        world.SetCount(GoapKey.SuspiciousDoorsSeen, findings.SuspiciousDoorsSeen);
        world.SetCount(GoapKey.ColleaguesMissingCount, findings.ColleagueMissing ? 1 : 0);

        if (findings.ColleagueMissing)
            world.Set(GoapKey.ColleagueMissing, true);

        // `InspectedNoiseSource` and the other latched accomplishments are NOT rebuilt
        // here. They are the effects of actions this NPC already performed, and the
        // agent applies them to its world state as each action completes. Rebuilding
        // from observations alone would wipe them every step and the NPC would re-do the
        // same job forever — the "replanning picks the same action again" failure the
        // table validator warns about for an action with no effects.
    }

    /// <summary>
    /// What the NPC can see of the room it is standing in, with no roll required.
    /// </summary>
    /// <remarks>
    /// Not a loophole in the no-omniscience rule. Standing in a room and seeing its
    /// walls is not privileged information; it is the definition of being there. The
    /// distinction that matters is that these observations come from
    /// <see cref="GoapLocalObservations"/>, which the caller fills in from this
    /// actor's own room only.
    /// </remarks>
    private static void ApplySurroundings(TacticalActor actor, GoapWorldState world, GoapLocalObservations now)
    {
        if (now.DoorOpenAtPost)
            world.Set(GoapKey.SuspiciousDoorOpen, true);

        if (now.SuspiciousDoorsClosed)
            world.Set(GoapKey.SuspiciousDoorsClosed, true);

        if (now.InCover)
            world.Set(GoapKey.InCover, true);

        // Exposed is the absence of cover, not a separate observation. Deriving it here
        // means there is one definition of "exposed" rather than two that can disagree.
        world.Set(GoapKey.Exposed, !now.InCover);

        if (now.RoomIsDark)
            world.Set(GoapKey.PowerOut, true);

        if (now.DayFinished)
        {
            world.Set(GoapKey.DayFinished, true);
            world.Set(GoapKey.DayNotFinished, false);
        }

        if (now.WorkDone)
            world.Set(GoapKey.WorkDone, true);

        if (actor.Condition == ActorCondition.Downed)
            world.Set(GoapKey.SelfEndangered, true);

        if (now.InDangerZone && actor.Condition == ActorCondition.Active)
            world.Set(GoapKey.SelfEndangered, true);
    }

    /// <summary>
    /// Turns an unbounded "steps since" into a number a world state can hold.
    /// </summary>
    /// <remarks>
    /// <see cref="PerceptionMemory.StepsSinceContact"/> returns
    /// <see cref="long.MaxValue"/> when nothing was ever perceived, which is the right
    /// answer for a caller asking "how stale is this" and the wrong answer for a value
    /// being stored in a small integer.
    /// </remarks>
    private static int ClampSteps(long steps) => steps >= MaxCountableSteps ? (int)MaxCountableSteps : (int)Math.Max(0, steps);

    /// <summary>The largest "steps since" a counter will hold.</summary>
    private const int MaxCountableSteps = 100000;

    /// <summary>How long a contact stays worth chasing before it counts as lost.</summary>
    private static int TargetLostThresholdSteps
        => Math.Max(1, SimulationRules.Goap("goap_missing_colleague_steps", 150) / 3);

    /// <summary>How recent a noise has to be to be worth walking towards.</summary>
    private const int NoiseStillFreshSteps = 200;
}

/// <summary>
/// What one NPC can see of its own immediate surroundings, with no roll required.
/// </summary>
/// <remarks>
/// Filled in by the caller from the actor's <em>own</em> room and nothing else. Held as
/// its own type so that "what this NPC can see where it stands" is a first-class input
/// to the builder rather than something the builder went and fetched — the same
/// structural guarantee that keeps the mission out of the builder's hands.
/// </remarks>
public readonly record struct GoapLocalObservations
{
    /// <summary>A door on this NPC's route stands open where it should be shut.</summary>
    public bool DoorOpenAtPost { get; init; }

    /// <summary>The doors this NPC is responsible for have been shut.</summary>
    public bool SuspiciousDoorsClosed { get; init; }

    /// <summary>There is something here to hide behind.</summary>
    public bool InCover { get; init; }

    /// <summary>The room this NPC stands in has no working light.</summary>
    public bool RoomIsDark { get; init; }

    /// <summary>The work period this NPC is inside has ended.</summary>
    public bool DayFinished { get; init; }

    /// <summary>This NPC has finished the work it was assigned.</summary>
    public bool WorkDone { get; init; }

    /// <summary>Somebody is shooting where this NPC is standing.</summary>
    public bool InDangerZone { get; init; }

    /// <summary>
    /// The building-wide alarm has been raised.
    /// </summary>
    /// <remarks>
    /// The one piece of site-wide state the builder accepts, and deliberately limited to
    /// a single bit. The <em>level</em> is read by the goal selector for priority, not
    /// here as a fact to act on — a guard learns that there is an alarm, not where it
    /// came from, and the difference is the whole reason a lockdown does not tell the
    /// player exactly where they are.
    /// </remarks>
    public bool AlarmRaised { get; init; }

    /// <summary>Nothing observed. The state of an NPC that has just started.</summary>
    public static GoapLocalObservations Nothing => default;
}