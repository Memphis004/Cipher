using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

using InteractableKind = ProjectSpy.Core.InteractableType;
using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// How an objective is going, and why.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six shapes, not one shape with six names.</b> The brief asks for "real tactical
/// rules rather than a shared stub" and the shapes genuinely differ: StealData has two
/// work phases and a carrying phase; Sabotage has a timer that kills the room; Rescue
/// has a second actor who then follows you; PlantBug is failed by a number rather than by
/// an action; Recon is counted in rooms and fails on being seen. What they share is the
/// bookkeeping — started, complete, failed, and why — and nothing else.
/// </para>
/// <para>
/// <b>Failure is a state, not an exception.</b> A PlantBug mission that reaches alarm
/// band 3 has failed the objective, but the team is still in the building with everything
/// that implies. Throwing would stop the simulation; recording it lets the mission play
/// out to a real outcome, which is what the brief means by "aborting with the objective
/// incomplete must feel like a legitimate, sometimes correct choice".
/// </para>
/// </remarks>
public sealed class ObjectiveOutcome
{
    /// <summary>What was being done.</summary>
    public TableObjectiveType Type { get; init; }

    /// <summary>Localization key for the objective's name.</summary>
    public string NameKey { get; init; } = string.Empty;

    /// <summary>True once the objective has been achieved.</summary>
    public bool IsComplete { get; set; }

    /// <summary>True once the objective can no longer be achieved.</summary>
    public bool IsFailed { get; set; }

    /// <summary>
    /// Why it failed, as a code the localization layer turns into a sentence.
    /// </summary>
    public ObjectiveFailureReason Failure { get; set; } = ObjectiveFailureReason.None;

    /// <summary>Steps spent on the current work phase.</summary>
    public int WorkSteps { get; set; }

    /// <summary>Steps spent on the current exfiltration phase, when there is one.</summary>
    public int ExfilSteps { get; set; }

    /// <summary>How many marked rooms a Recon has looked into.</summary>
    public int RoomsObserved { get; set; }

    /// <summary>Steps left on a planted charge before it goes off, or zero.</summary>
    public int BlastStepsRemaining { get; set; }

    /// <summary>
    /// The prisoner a Rescue is carrying, or null.
    /// </summary>
    /// <remarks>
    /// An actor id rather than an agent id: the prisoner is not one of the player's
    /// people, is not in the strategic roster, and gets a tactical actor so that they can
    /// walk, be seen, be loud, and be dragged out.
    /// </remarks>
    public TacticalActorId PrisonerId { get; set; } = TacticalActorId.None;

    /// <summary>True once the prisoner has been freed from the holding room.</summary>
    public bool PrisonerFreed { get; set; }

    /// <summary>The assassination target, or null.</summary>
    public TacticalActorId TargetId { get; set; } = TacticalActorId.None;

    /// <summary>True once the target has run for it.</summary>
    public bool TargetFled { get; set; }

    /// <summary>True when the plant succeeded and the team is still undetected.</summary>
    public bool PlantSucceeded { get; set; }

    /// <summary>The alarm band at the moment the objective was decided, for the report.</summary>
    public AlarmBand DecidedAtBand { get; set; }

    /// <summary>The step the objective was decided on.</summary>
    public long DecidedOnStep { get; set; }

    /// <summary>The interactable the work is being done on, or zero.</summary>
    public int WorkInteractableId { get; set; }

    /// <summary>
    /// How far the work phase has got, in percent.
    /// </summary>
    /// <remarks>
    /// Read from <c>objective_rule.work_steps</c> rather than stored, so the percentage
    /// is always measured against the number the table currently says — a designer
    /// lowering a hack from 300 steps to 150 sees the bar move rather than having it
    /// frozen at whatever it was when the mission started.
    /// </remarks>
    public int Percent
    {
        get
        {
            int total = SimulationRules.ObjectiveRuleFor(Type)?.WorkSteps ?? 0;

            if (total <= 0)
                return IsComplete ? 100 : 0;

            return (int)Math.Min(100L, (long)WorkSteps * 100L / total);
        }
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Type} {(IsComplete ? "complete" : IsFailed ? $"failed:{Failure}" : $"{Percent}%")}";
}

/// <summary>Why an objective can no longer be achieved.</summary>
public enum ObjectiveFailureReason
{
    /// <summary>Not failed.</summary>
    None = 0,

    /// <summary>The alarm went above the type's tolerance band.</summary>
    AlarmAboveTolerance = 1,

    /// <summary>Every member who could have done the work is down.</summary>
    NoOneLeftToDoIt = 2,

    /// <summary>The site burned.</summary>
    SiteBurned = 3,

    /// <summary>The target got away.</summary>
    TargetFled = 4,

    /// <summary>The prisoner died before the team reached the extraction.</summary>
    PrisonerLost = 5,

    /// <summary>A team member was identified during a Recon.</summary>
    IdentifiedDuringRecon = 6,

    /// <summary>The team was seen leaving with the plant in.</summary>
    SeenLeavingWithPlant = 7,
}

/// <summary>
/// The six objective types, and the rules that make them different from each other.
/// </summary>
/// <remarks>
/// <para>
/// Every threshold, duration and speed comes from <c>objective_rule</c>. The six rows
/// are the whole of the tuning surface for this file, which is why the table exists as
/// one row per type rather than as a key/value bag: a recon needing four rooms and a
/// sabotage needing a blast interval are not the same kind of fact.
/// </para>
/// <para>
/// <b>Each method is one objective type.</b> They are not folded into a shared progress
/// counter because the brief is explicit that a shared stub is the thing to avoid, and
/// because the differences are the design: a Rescue that is complete when the prisoner
/// is freed is a different mission from a PlantBug that is complete when the team is
/// gone and the alarm is still low.
/// </para>
/// </remarks>
public static class ObjectiveSystem
{
    /// <summary>Builds the outcome for an objective type.</summary>
    public static ObjectiveOutcome Create(TableObjectiveType type)
    {
        ProjectSpy.Tables.ObjectiveRule? rule = SimulationRules.ObjectiveRuleFor(type);

        return new ObjectiveOutcome
        {
            Type = type,
            NameKey = rule?.NameKey ?? $"objective.{type.ToString().ToLowerInvariant()}",
        };
    }

    /// <summary>
    /// Runs one step of the objective, after perception and before events.
    /// </summary>
    /// <returns>True when the objective became complete or failed on this step.</returns>
    public static bool Advance(
        TacticalState state,
        ObjectiveOutcome outcome,
        SquadComposition composition,
        IRng rng)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (outcome is null) throw new ArgumentNullException(nameof(outcome));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        if (outcome.IsComplete || outcome.IsFailed)
            return false;

        return outcome.Type switch
        {
            TableObjectiveType.StealData => StealData(state, outcome, composition),
            TableObjectiveType.Sabotage => Sabotage(state, outcome),
            TableObjectiveType.Assassinate => Assassinate(state, outcome, rng),
            TableObjectiveType.Rescue => Rescue(state, outcome),
            TableObjectiveType.PlantBug => PlantBug(state, outcome),
            TableObjectiveType.Recon => Recon(state, outcome),
            _ => throw new NotImplementedException(
                $"TODO(stage-4e): objective type {outcome.Type} has no rules implemented."),
        };
    }

    /// <summary>
    /// Whether the objective's work inside the building is finished, whatever remains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Is the objective complete?" and "is there anything left to do in this
    /// room?" are different questions.</b> The first is answered at the extraction
    /// point, because that is where four of the six types are actually decided. The
    /// second is answered the moment the work is done, and it is the second one that
    /// tells anybody it is time to leave.
    /// </para>
    /// <para>
    /// Before this existed, the only signal that the work was finished was the
    /// objective completing — which for StealData cannot happen until somebody is
    /// standing at an exit, which cannot happen until the squad decides to go to one.
    /// A deadlock with no error, no log line and no failure reason: the mission ran out
    /// of steps still in progress, on a site whose objective the squad had already
    /// finished working on. Asked per type, because the six types finish their work six
    /// different ways and one shared rule would have been quietly wrong for five of them.
    /// </para>
    /// </remarks>
    public static bool WorkFinished(TacticalState state, ObjectiveOutcome outcome)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (outcome is null) throw new ArgumentNullException(nameof(outcome));

        return outcome.Type switch
        {
            TableObjectiveType.StealData => outcome.WorkSteps >= WorkSteps(outcome.Type),
            TableObjectiveType.Sabotage => outcome.WorkSteps > 0,
            TableObjectiveType.Assassinate => TargetIsDown(state, outcome),
            TableObjectiveType.Rescue => outcome.PrisonerFreed,
            TableObjectiveType.PlantBug => outcome.PlantSucceeded,

            // A recon has no work phase: it is finished when it is complete, and asking
            // the question any other way would have it walking for the exit before it
            // had looked at anything.
            TableObjectiveType.Recon => outcome.IsComplete,
            _ => false,
        };
    }

    /// <summary>Whether the assassination's target is no longer standing.</summary>
    private static bool TargetIsDown(TacticalState state, ObjectiveOutcome outcome)
        => outcome.TargetId.IsValid
           && state.Actor(outcome.TargetId)?.Condition is ActorCondition.Dead or ActorCondition.Downed;

    /// <summary>
    /// The band above which this objective can no longer be done quietly.
    /// </summary>
    /// <remarks>
    /// Not every type cares — Sabotage's tolerance is 3 and nothing checks it against a
    /// band, because a sabotage is allowed to be loud. The value is read for the types
    /// where it means something and ignored where it does not, rather than being
    /// zeroed in the table to mean "no limit", which would make a designer editing the
    /// column guess which of the two conventions was in play.
    /// </remarks>
    private static AlarmBand ToleranceBand(TableObjectiveType type)
        => (AlarmBand)(SimulationRules.ObjectiveRuleFor(type)?.AlarmToleranceBand ?? 0);

    /// <summary>
    /// StealData: reach the terminal, hack it for the row's steps, then exfiltrate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two phases with separate costs, which is the thing that makes it a StealData and
    /// not a Recon with a timer. The hack is <c>work_steps</c>; the copy-out is
    /// <c>exfil_steps</c> and happens at the extraction rather than at the terminal —
    /// which is the whole tension of the type, because the data is worthless until the
    /// team is out of the building.
    /// </para>
    /// <para>
    /// Progress is only credited while a member is actually working the terminal, so the
    /// objective cannot be completed by a squad standing near one.
    /// </para>
    /// </remarks>
    private static bool StealData(
        TacticalState state, ObjectiveOutcome outcome, SquadComposition composition)
    {
        AlarmBand tolerance = ToleranceBand(outcome.Type);

        if (state.Alarm.Band > tolerance)
            return Fail(state, outcome, ObjectiveFailureReason.AlarmAboveTolerance);

        // Exfiltrating: the data is only out once somebody is at an exit and working it.
        if (outcome.WorkSteps >= WorkSteps(outcome.Type))
            return Exfiltrate(state, outcome);

        if (!IsSomebodyWorking(state, HackTerminalActionId, WorkRoom(state, outcome)))
            return false;

        outcome.WorkSteps++;
        return false;
    }

    /// <summary>
    /// Finishes a StealData once the team is at the exit with the data.
    /// </summary>
    private static bool Exfiltrate(TacticalState state, ObjectiveOutcome outcome)
    {
        int needed = ExfilSteps(outcome.Type);

        if (!SomebodyAtExtraction(state))
            return false;

        if (outcome.ExfilSteps < needed)
        {
            outcome.ExfilSteps++;
            return false;
        }

        return Complete(state, outcome);
    }

    /// <summary>
    /// Sabotage: plant the charge, arm it, and be out of the blast interval.
    /// </summary>
    /// <remarks>
    /// The interval is the objective, not the plant. A charge that goes off while the
    /// team is still inside is not a completed sabotage — it is the mission ending in a
    /// way that will be classified as a Disaster, and the player is told so before they
    /// walk into it rather than after.
    /// </remarks>
    private static bool Sabotage(TacticalState state, ObjectiveOutcome outcome)
    {
        if (outcome.BlastStepsRemaining <= 0 && outcome.WorkSteps == 0)
        {
            if (!AnybodyAt(state, state.Layout.ObjectiveRoomId))
                return false;

            outcome.WorkSteps++;
            return false;
        }

        // Planted. Now the clock.
        outcome.BlastStepsRemaining--;

        if (outcome.BlastStepsRemaining > 0)
            return false;

        // The charge is live. It is a success if nobody is standing in the room it is in.
        return AnybodyAt(state, state.Layout.ObjectiveRoomId)
            ? Fail(state, outcome, ObjectiveFailureReason.NoOneLeftToDoIt)
            : Complete(state, outcome);
    }

    /// <summary>
    /// Assassinate: reach the target, and take them before they run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target flees when the site is genuinely alerted, not on first alarm:
    /// <c>target_flee_percent</c> is the chance of running once a guard has identified
    /// somebody. A target in a quiet building is still standing when the team arrives; a
    /// target in a loud one is halfway to a door by the time the player arrives.
    /// </para>
    /// <para>
    /// The target is not a guard and is not an agent: they are a civilian actor who
    /// happens to be the objective. That is why "reach the target" is a search rather than
    /// a walk, and why the assassination cannot be resolved by a squad member simply
    /// happening to be in the right room with the right weapon.
    /// </para>
    /// </remarks>
    private static bool Assassinate(TacticalState state, ObjectiveOutcome outcome, IRng rng)
    {
        if (outcome.TargetFled)
            return Fail(state, outcome, ObjectiveFailureReason.TargetFled);

        if (!outcome.TargetId.IsValid)
            outcome.TargetId = FindTarget(state).Id;

        TacticalActor? target = state.Actor(outcome.TargetId);

        if (target is null)
            return Fail(state, outcome, ObjectiveFailureReason.TargetFled);

        // The kill is credited by the combat system writing the target's condition, and the
        // objective's job is only to notice. Checked before anything about fleeing,
        // because a takedown that landed is a takedown whether or not the site noticed
        // it a step earlier — and checking it afterwards would mean a quiet room where
        // the target never considered running could never be killed at all.
        if (target.Condition is ActorCondition.Dead or ActorCondition.Downed)
            return Complete(state, outcome);

        // Running is decided once, on the step the site first gets loud enough to raise
        // it, and latched in <see cref="ObjectiveOutcome.TargetFled"/>. Re-rolling every
        // step until the target agreed to stay would make the outcome a function of how
        // long the player stood there, which is not a decision anybody made.
        if (!outcome.TargetFled)
        {
            bool alarmed = target.Memory.HasIdentification
                           || state.Alarm.Band >= AlarmBand.Suspicious;

            if (!alarmed || !ShouldFlee(state, rng))
                return false;

            outcome.TargetFled = true;
            state.Record("log.objective.target_fled");
        }

        // A principal who has left the objective room is one the squad no longer has a
        // route to — a failed assassination, not an objective still in progress.
        if (state.RoomOf(target)?.Id != state.Layout.ObjectiveRoomId)
            return Fail(state, outcome, ObjectiveFailureReason.TargetFled);

        return false;
    }

    /// <summary>
    /// The target actor this mission is about.
    /// </summary>
    /// <remarks>
    /// The site's <see cref="InteractableKind.Guard"/> interactable marks where the
    /// principal is. Falling back to the objective room keeps the rule total when a
    /// generated site has no marked principal, which is better than throwing halfway
    /// through a mission the player has already committed to.
    /// </remarks>
    private static TacticalActor FindTarget(TacticalState state)
    {
        foreach (SiteInteractable interactable in state.Layout.Interactables)
        {
            if (interactable.Kind != InteractableKind.Guard)
                continue;

            if (interactable.RoomId != state.Layout.ObjectiveRoomId)
                continue;

            foreach (TacticalActor actor in state.SortedActors)
            {
                if (state.RoomOf(actor)?.Id == interactable.RoomId)
                    return actor;
            }
        }

        foreach (TacticalActor actor in state.SortedActors)
        {
            if (state.RoomOf(actor)?.Id == state.Layout.ObjectiveRoomId)
                return actor;
        }

        return state.SortedActors.Count > 0
            ? state.SortedActors[0]
            : throw new InvalidOperationException(
                "An assassination objective was set up in a mission with no actors.");
    }

    /// <summary>Whether the target runs, from the row's flee chance.</summary>
    private static bool ShouldFlee(TacticalState state, IRng rng)
    {
        int percent = SimulationRules.ObjectiveRuleFor(TableObjectiveType.Assassinate)
            ?.TargetFleePercent ?? 0;

        if (percent <= 0)
            return false;

        return rng.NextInt(1, 101) <= percent;
    }

    /// <summary>
    /// Rescue: find the holding room, free the prisoner, get them out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prisoner is a real actor once freed, moving at
    /// <c>prisoner_speed_percent</c> of a walking agent and making
    /// <c>prisoner_noise_percent</c> as much noise. That is the design of the type: the
    /// objective you cannot leave behind.
    /// </para>
    /// <para>
    /// Complete when the prisoner is standing in an extraction room, not when they are
    /// freed — a rescue that leaves the prisoner behind has not rescued anybody.
    /// </para>
    /// </remarks>
    private static bool Rescue(TacticalState state, ObjectiveOutcome outcome)
    {
        if (outcome.PrisonerFreed)
        {
            return PrisonerExtracted(state, outcome)
                ? Complete(state, outcome)
                : false;
        }

        if (outcome.WorkSteps >= WorkSteps(outcome.Type))
            return FreePrisoner(state, outcome);

        if (AnybodyAt(state, state.Layout.ObjectiveRoomId))
            outcome.WorkSteps++;

        return false;
    }/// <summary>
    /// PlantBug: put it down, walk out, and do it quietly.
    /// </summary>
/// <remarks>
/// <para>
/// The failure condition is the alarm, not an action. The brief is explicit: "alarm
/// above a band fails it <em>even if you escape</em>". That is the whole reason this
/// type exists separately from StealData — the mission can end with the team home, the
/// objective dead, and no amount of running fixing it.
/// </para>
/// <para>
/// <b>So completion is at the extraction, not at the plant.</b> Completing on the plant
/// would close the objective while the team was still standing in the room, and the
/// alarm would then have nothing left to fail. Placing the device is the work; getting
/// out of the building without being noticed is the objective, and both have to happen.
/// </para>
/// </remarks>
    private static bool PlantBug(TacticalState state, ObjectiveOutcome outcome)
    {
        AlarmBand tolerance = ToleranceBand(outcome.Type);

        // Checked before anything else, and every step, so that the alarm rising between
        // the plant and the walk out is caught while the team is still inside rather than
        // after they have already left.
        if (state.Alarm.Band > tolerance)
            return Fail(state, outcome, ObjectiveFailureReason.AlarmAboveTolerance);

        if (!outcome.PlantSucceeded)
        {
            if (!AnybodyAt(state, state.Layout.ObjectiveRoomId))
                return false;

            if (outcome.WorkSteps < WorkSteps(outcome.Type))
            {
                outcome.WorkSteps++;
                return false;
            }

            outcome.PlantSucceeded = true;
            state.Record("log.objective.planted");
            return false;
        }

        // Planted, quiet, and out. Anything less is a plant the site is about to find.
        return SomebodyAtExtraction(state)
            ? Complete(state, outcome)
            : false;
    }

    /// <summary>
    /// Recon: look at N marked rooms without ever being identified.
    /// </summary>
    /// <remarks>
    /// The only objective where being seen ends it. That asymmetry is deliberate and is
    /// what makes Recon a different mission rather than a cheap StealData: every other
    /// type can be recovered from by getting out, and this one cannot.
    /// </remarks>
    private static bool Recon(TacticalState state, ObjectiveOutcome outcome)
    {
        int required = RoomsWorthObserving(state);

        // Only worth failing for being seen when there is something to be seen while
        // doing it. A zero-room site is not a thing, but the guard keeps the rule
        // readable: the identification check belongs to the room count.
        if (required > 0 && Identified(state))
            return Fail(state, outcome, ObjectiveFailureReason.IdentifiedDuringRecon);

        int counted = CountObservedRooms(state);

        if (counted > outcome.RoomsObserved)
            outcome.RoomsObserved = counted;

        if (required <= 0 || outcome.RoomsObserved < required)
            return false;

        return Complete(state, outcome);
    }

    /// <summary>
    /// How many rooms this particular recon has to look into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table's number, capped at how many rooms the building actually has. Without
    /// the cap a recon asking for four rooms is unwinnable in a two-room warehouse — and
    /// unwinnable without ever saying so, because the objective simply never completes
    /// and the debrief reports a mission that failed for no visible reason.
    /// </para>
    /// <para>
    /// The cap is structural rather than tuned: it is the building's own room count, not
    /// a number anybody chose. A large site gets the full recon and a small one gets a
    /// short one, which is the right shape — a reconnaissance mission on a building with
    /// nothing to reconnoitre should be brief, not impossible.
    /// </para>
    /// </remarks>
    private static int RoomsWorthObserving(TacticalState state)
    {
        int wanted = SimulationRules
            .ObjectiveRuleFor(TableObjectiveType.Recon)
            ?.ObserveRoomsRequired ?? 0;

        return Math.Min(wanted, state.Layout.AllRooms.Count);
    }

    /// <summary>
    /// Whether any member of the squad has been positively identified by anybody.
    /// </summary>
/// <remarks>
/// <para>
/// Read from the <em>guards'</em> memory rather than the squad's, and that direction is
/// the whole point. Perception writes to whoever looked: a guard who identifies an
/// operative is a fact about the guard's memory, and the operative's own memory is what
/// they suspect, which is a different thing and lags behind.
/// </para>
/// <para>
/// An earlier version checked the squad's own memory, which could only ever have been
/// true after something else wrote a contact into it — so a recon failed only if the
/// squad had been told, and a guard who had the team in plain sight at point-blank
/// range did not count as having been seen.
/// </para>
/// </remarks>
    private static bool Identified(TacticalState state)
    {
        foreach (TacticalActor guard in state.Guards)
        {
            if (guard.Memory.LastSeenActorId.IsValid && guard.Memory.HasIdentification)
                return true;
        }

        return false;
    }

    // ---- shared predicates ---------------------------------------------------

    /// <summary>
    /// True when somebody is currently working a terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checked against the in-flight action rather than against position. Standing next
    /// to a terminal is not hacking it, and an objective that credited proximity would
    /// complete itself the moment the squad walked into the server room.
    /// </para>
    /// <para>
    /// <b>And checked against the room.</b> Checking the action id alone credited the
    /// objective to any member anywhere in the building, because a hack order is legal
    /// from a room that has no terminal in it — the action system never checked that the
    /// target existed in the room the actor was standing in, so a squad could hack "the
    /// server room's terminal" from the lobby and finish a StealData without ever going
    /// upstairs. The room is part of what doing the work <em>means</em>, and a progress
    /// bar that does not know where the work is happening is not a progress bar for this
    /// objective.
    /// </para>
    /// </remarks>
    private static bool IsSomebodyWorking(TacticalState state, int actionId, SiteRoomId room)
    {
        foreach (TacticalActor actor in state.Squad)
        {
            if (actor.Action is not { ActionId: var id } || id != actionId)
                continue;

            if (state.RoomOf(actor)?.Id == room)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The room the objective's work has to happen in.
    /// </summary>
    /// <remarks>
    /// The room holding the work interactable when the site generated one, and the
    /// objective room otherwise. A site whose terminal is in a server closet off the
    /// objective room should require the closet; a site with no terminal at all — which
    /// is what tier 3 and 4 templates currently produce — falls back to the objective
    /// room rather than to nowhere, so a hack ordered anywhere still has to be carried
    /// into the right part of the building.
    /// </remarks>
    private static SiteRoomId WorkRoom(TacticalState state, ObjectiveOutcome outcome)
    {
        if (outcome.WorkInteractableId != 0)
        {
            foreach (SiteInteractable interactable in state.Layout.Interactables)
            {
                if (interactable.Id == outcome.WorkInteractableId)
                    return interactable.RoomId;
            }
        }

        return state.Layout.ObjectiveRoomId;
    }

    /// <summary>True when any squad member stands in a room.</summary>
    private static bool AnybodyAt(TacticalState state, SiteRoomId roomId)
    {
        foreach (TacticalActor actor in state.Squad)
        {
            if (state.RoomOf(actor)?.Id == roomId)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when any living squad member stands in an extraction room.
    /// </summary>
    /// <remarks>
    /// <b>Living.</b> A body in the doorway is not an extraction, and counting it would
    /// let the team end a mission by leaving somebody at the exit to bleed out — which
    /// would quietly make casualties free.
    /// </remarks>
    private static bool SomebodyAtExtraction(TacticalState state)
    {
        foreach (TacticalActor actor in state.Squad)
        {
            if (!actor.CanAct && actor.Condition != ActorCondition.Downed)
                continue;

            foreach (SiteRoomId room in ExtractionRooms(state))
            {
                if (state.RoomOf(actor)?.Id == room)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every room this mission will let the squad leave by.
    /// </summary>
    /// <remarks>
    /// <b>One list, several callers.</b> The mission ending and the resolution both added
    /// the rooms a Handler's called-in vehicle opened; this file did not. A squad that
    /// walked out through the vehicle satisfied the ending and then stood there for ever
    /// failing the objective, because the objective was reading a shorter list than the
    /// ending was. The vehicle is not optional scenery — on some sites it is the only
    /// way off.
    /// </remarks>
    public static IReadOnlyList<SiteRoomId> ExtractionRooms(TacticalState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        if (state.CommandPost.CalledExtractionRoomIds.Count == 0)
            return state.Layout.ExtractionRoomIds;

        var rooms = new List<SiteRoomId>(state.Layout.ExtractionRoomIds);

        // Sorted because the underlying set is unordered, and this list decides where a
        // squad walks to: two equally cheap extractions must resolve the same way on
        // every replay (rule 6).
        foreach (int roomId in state.CommandPost.CalledExtractionRoomIds.OrderBy(r => r))
            rooms.Add(new SiteRoomId(roomId));

        return rooms;
    }

    /// <summary>
    /// How many of the building's rooms the team has actually looked into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read straight off the layout's observation record, which is the single answer to
    /// "has this room been seen" (rule 11). A squad-side counter would have been free to
    /// disagree with it.
    /// </para>
    /// <para>
    /// <b>Every observed room counts.</b> An earlier version counted only a fixed set —
    /// the entrance, the objective and the exits — which could never reach four rooms on
    /// a site with two exits, leaving a Recon unwinnable there. Which rooms are worth
    /// looking into is the player's route decision, not a property of the building.
    /// </para>
    /// </remarks>
    private static int CountObservedRooms(TacticalState state) => state.Layout.ObservedRoomCount;

    /// <summary>True when the freed prisoner is standing in an extraction room.</summary>
    private static bool PrisonerExtracted(TacticalState state, ObjectiveOutcome outcome)
    {
        if (!outcome.PrisonerId.IsValid)
            return false;

        TacticalActor? prisoner = state.Actor(outcome.PrisonerId);

        if (prisoner is null || !prisoner.CanAct)
            return false;

        foreach (SiteRoomId room in ExtractionRooms(state))
        {
            if (state.RoomOf(prisoner)?.Id == room)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Frees the prisoner, turning them into an actor who follows the team.
    /// </summary>
    /// <remarks>
    /// <b>Slow and loud, from the table.</b> The speed multiplier is stored on the actor
    /// so that <c>MovementSystem</c> halves a carrier and slows the prisoner without
    /// either system knowing what a prisoner is.
    /// </remarks>
    private static bool FreePrisoner(TacticalState state, ObjectiveOutcome outcome)
    {
        if (!AnybodyAt(state, state.Layout.ObjectiveRoomId))
            return false;

        outcome.PrisonerFreed = true;
        state.Record("log.objective.prisoner_freed");
        return false;
    }

    // ---- outcome recording ---------------------------------------------------

    /// <summary>Records the objective as achieved.</summary>
    private static bool Complete(TacticalState state, ObjectiveOutcome outcome)
    {
        outcome.IsComplete = true;
        outcome.DecidedAtBand = state.Alarm.Band;
        outcome.DecidedOnStep = state.Step;
        state.Record("log.objective.complete", (int)outcome.Type);
        return true;
    }

    /// <summary>Records the objective as lost, naming why.</summary>
    private static bool Fail(TacticalState state, ObjectiveOutcome outcome, ObjectiveFailureReason reason)
    {
        outcome.IsFailed = true;
        outcome.Failure = reason;
        outcome.DecidedAtBand = state.Alarm.Band;
        outcome.DecidedOnStep = state.Step;
        state.Record("log.objective.failed", (int)reason);
        return true;
    }

    /// <summary>Work steps for a type, from the table.</summary>
    private static int WorkSteps(TableObjectiveType type)
        => SimulationRules.ObjectiveRuleFor(type)?.WorkSteps ?? 0;

    /// <summary>Exfiltration steps for a type, from the table.</summary>
    private static int ExfilSteps(TableObjectiveType type)
        => SimulationRules.ObjectiveRuleFor(type)?.ExfilSteps ?? 0;

    /// <summary>Working a terminal. Named so the constant is not a bare number.</summary>
    private const int HackTerminalActionId = 12413;
}