using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// How a squad plays, when nobody is playing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are strategies, not scripts.</b> Each one is a preference over the same
/// primitives the player has — walk here, hold, shoot that, go out that door — so the
/// four of them are playing the same game with the same rules and can be compared.
/// A policy that could do something the player cannot would not be evidence about the
/// design; it would be evidence about the script.
/// </para>
/// <para>
/// <b>Speedrun is the control group, and it is meant to lose.</b> The brief says so
/// directly, and the reason is worth stating: if ignoring noise and walking straight at
/// the objective were a winning strategy on high tiers, then noise, sightlines and the
/// alarm would not be costs. They would be scenery. A policy that finishes missions
/// faster than the cautious one while never being caught would mean the tension the
/// player is supposed to feel is not being charged for anywhere in the rules.
/// </para>
/// </remarks>
public enum SquadPolicy
{
    /// <summary>Never fights. Creeps, and leaves if it is seen.</summary>
    Stealth = 0,

    /// <summary>Removes every guard it can reach and walks into the rest.</summary>
    Aggressive = 1,

    /// <summary>The control group. Shortest route, never reacts to a guard.</summary>
    Speedrun = 2,

    /// <summary>Stealth, with a hard alarm ceiling at which the whole squad gives up.</summary>
    Cautious = 3,
}

/// <summary>
/// Drives a squad under one <see cref="SquadPolicy"/>: decides every member's orders on
/// a fixed cadence and hands them to the same machinery the player's orders go through.
/// </summary>
/// <remarks>
/// <para>
/// <b>It plays every member, including the one under the player's hand.</b> A policy
/// that could only order the AI-controlled members would be measuring a four-man squad
/// with a statue holding the objective, and every number it produced would be about the
/// statue.
/// </para>
/// <para>
/// <b>One channel for all of them: <see cref="TacticalState.PendingOrders"/>.</b> Not
/// <see cref="SquadControl.Issue"/>, because a standing order cannot say "shoot that
/// guard" — <see cref="SquadOrderKind"/> has no such verb — and an Aggressive policy that
/// could not shoot would not be Aggressive. It would be a policy that walks loudly past
/// everybody, which is the one strategy the balance report most needs to be able to
/// compare against. Using the player-input channel for all four keeps a single
/// validation path, and keeps the policy honest about being a stand-in for a person.
/// </para>
/// <para>
/// <b>Deterministic (rule 6).</b> Members are visited in <see cref="TacticalState.Squad"/>
/// order, never in dictionary order; every choice is made from ids and step numbers. Two
/// runs of the same seed and policy produce the same mission, which is what makes the
/// batch balance numbers reproducible at all.
/// </para>
/// </remarks>
public sealed class SquadPolicyDriver
{
    /// <summary>Which strategy this driver plays.</summary>
    public SquadPolicy Policy { get; private set; }

    /// <summary>The step the squad last made a decision on.</summary>
    public long LastThinkStep { get; private set; } = -1;

    /// <summary>How many times the squad has changed plan.</summary>
    public int PlanChanges { get; private set; }

    /// <summary>
    /// Orders the action system refused because they were not legal.
    /// </summary>
    /// <remarks>
    /// A policy must be zero here. The harness fails the run when it is not, because a
    /// policy quietly issuing illegal orders would report a success rate it did not earn
    /// — the orders that did nothing would simply not be in the denominator.
    /// </remarks>
    public int IllegalOrders { get; private set; }

    /// <summary>
    /// Orders skipped because the member was already doing something.
    /// </summary>
    /// <remarks>
    /// Counted separately, and deliberately not treated as a fault.
    /// <see cref="ActionSystem"/> refuses to replace an action in flight — a player gets
    /// the same refusal for tapping a second order mid-movement, and it is what stops a
    /// lockpick being cancelled by a keypress. A policy that re-decides on a cadence and
    /// re-issues a move every twelve steps is therefore refused constantly and quite
    /// correctly, and a harness that called that an illegal order would bury the real
    /// faults under thousands of ordinary ones.
    /// </remarks>
    public int BusySkips { get; private set; }

    /// <summary>
    /// Orders the action system refused because a door in the way would not open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counted apart from <see cref="IllegalOrders"/>, and the distinction is the whole
    /// point. A policy planning a route through a locked door and being told no has not
    /// made a mistake — it has met a building it cannot get through yet, which is a
    /// fact about the building and a decision for the player. Folding it into
    /// <see cref="IllegalOrders"/> would have reported every tier-4 template as a
    /// broken policy, and would have buried the genuine faults under thousands of
    /// ordinary refusals.
    /// </para>
    /// <para>
    /// It is tracked rather than discarded because a high number is itself a finding:
    /// a policy with no way to open a door cannot leave a locked building, and the sweep
    /// says so through this counter.
    /// </para>
    /// </remarks>
    public int BlockedSkips { get; private set; }

    /// <summary>The localization key of what the squad is currently trying to do.</summary>
    public string PlanKey { get; private set; } = PlanStart;

    public SquadPolicyDriver(SquadPolicy policy) => Policy = policy;

    /// <summary>
    /// Switches strategy mid-mission, for an interactive client that hands the squad to a
    /// different plan without ending the mission.
    /// </summary>
    /// <remarks>
    /// The next decision runs immediately rather than waiting out the cadence, because
    /// the whole point of switching is to change what the squad does now. The counters are
    /// left alone: they measure the mission, not the strategy, and zeroing them here would
    /// let a caller launder an illegal-order count by switching policy.
    /// </remarks>
    public void SetPolicy(SquadPolicy policy)
    {
        if (Policy == policy)
            return;

        Policy = policy;
        LastThinkStep = -1;
    }

    private const string PlanStart = "policy.plan.start";
    private const string PlanInfiltrate = "policy.plan.infiltrate";
    private const string PlanWithdraw = "policy.plan.withdraw";
    private const string PlanExtract = "policy.plan.extract";
    private const string PlanGiveUp = "policy.plan.giveup";

    /// <summary>
    /// Runs one decision for the whole squad, if the cadence says it is time.
    /// </summary>
    /// <remarks>
    /// The cadence exists because a policy that re-decided every step would issue an
    /// order every step and bury the order log under thousands of identical "walk to
    /// room 12" entries, which is both unreadable and slower than the simulation it is
    /// observing. It also matches how a person plays: a decision, then a stretch of
    /// living with it.
    /// </remarks>
    public bool Think(TacticalState state, SquadComposition composition)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (composition is null) throw new ArgumentNullException(nameof(composition));

        if (state.IsOver)
            return false;

        int cadence = Math.Max(1, SimulationRules.Squad("squad_policy_rethink_steps", 12));

        if (LastThinkStep >= 0 && state.Step - LastThinkStep < cadence)
            return false;

        LastThinkStep = state.Step;
        DecidePlan(state);

        foreach (TacticalActor actor in state.Squad)
            OrderMember(state, composition, actor);

        return true;
    }

    /// <summary>
    /// What the whole squad is currently trying to do.
    /// </summary>
    /// <remarks>
    /// Shared by all four policies, and the reason they differ is in what they do about
    /// contact rather than in what they compute: every policy agrees where the objective
    /// is and where the way out is, and they disagree about what a guard is.
    /// </remarks>
    private void DecidePlan(TacticalState state)
    {
        string plan;

        // "The work is finished", not "the objective is finished". Four of the six
        // objective types are only *decided* at the extraction point, so waiting for
        // IsComplete before planning the exit is waiting for the squad to be outside
        // before it decides to leave. Speedrun on a warehouse site stood in the server
        // room for four thousand steps having already finished the hack, because the
        // only thing that could tell it the hack was done was the exfiltration that
        // needed it to walk out.
        if (state.ObjectiveOutcome.IsComplete || ObjectiveSystem.WorkFinished(state, state.ObjectiveOutcome))
        {
            plan = PlanExtract;
        }
        else if (Policy == SquadPolicy.Cautious && state.Alarm.Band >= CautiousBand)
        {
            // The one thing Cautious does that Stealth does not, and the whole reason it
            // is a separate policy: a ceiling, past which it stops trying.
            plan = PlanGiveUp;
        }
        else if (Policy != SquadPolicy.Speedrun && HasContact(state))
        {
            plan = PlanWithdraw;
        }
        else
        {
            plan = PlanInfiltrate;
        }

        if (plan == PlanKey)
            return;

        if (PlanKey != PlanStart)
            PlanChanges++;

        PlanKey = plan;
    }

    /// <summary>Orders one member, if the policy has something for it to do.</summary>
    private void OrderMember(
        TacticalState state, SquadComposition composition, TacticalActor actor)
    {
        if (!actor.CanAct)
            return;

        // Somebody already walking is somebody already doing the right thing. Asking
        // again would be refused every time, and — worse — would make the policy look
        // like it was fighting the action system rather than playing the mission.
        if (actor.Action is { IsComplete: false })
        {
            BusySkips++;
            return;
        }

        TacticalOrder? order = Decide(state, composition, actor);

        if (order is null || order.IsStop)
            return;

        // The same legality gate the player's orders pass through.
        TacticalOrderResult check = ActionSystem.Validate(state, order);

        if (check.IsRejected)
        {
            if (check.Reason == TacticalOrderReason.BusyWithAnotherAction)
            {
                BusySkips++;
            }
            else if (check.Reason == TacticalOrderReason.ConnectionBlocked)
            {
                BlockedSkips++;
            }
            else
            {
                IllegalOrders++;
            }

            return;
        }

        state.PendingOrders.Add(order);
    }

    /// <summary>
    /// The order a member issues, as a decision.
    /// </summary>
    /// <remarks>
    /// Deliberately thin. Anything a policy needs that the player also needs belongs in
    /// <see cref="RoleBehaviours"/> or <see cref="ActionSystem"/>; a policy that grew its
    /// own movement solver would stop being comparable to a human playing the same
    /// mission.
    /// </remarks>
    private TacticalOrder? Decide(
        TacticalState state, SquadComposition composition, TacticalActor actor)
    {
        SquadMember? member = composition.MemberFor(actor.AgentId);
        RoleBehaviour behaviour = member?.Behaviour ?? RoleBehaviour.None;

        // An Overwatch member is worth more watching than walking, whatever the plan —
        // except once the squad is leaving, when a stationary marksman is just baggage.
        if (behaviour == RoleBehaviour.Overwatch && PlanKey != PlanGiveUp)
            return Observe(actor);

        switch (PlanKey)
        {
            case PlanGiveUp:
            case PlanExtract:
            case PlanWithdraw:
                return Leave(state, actor);
        }

        // Aggressive shoots rather than slipping past, but only a guard it can actually
        // see. Shooting at a remembered position is how a policy earns a CleanSuccess
        // rate by emptying a magazine into an empty corridor.
        if (Policy == SquadPolicy.Aggressive)
        {
            TacticalActor? seen = SeenGuard(state, actor);

            if (seen is not null)
                return Engage(actor, seen);
        }

        return Work(state, actor);
    }

    /// <summary>
    /// Walks to the objective, and works on it once there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second half is the half that matters, and it was missing until the stage-6
    /// harness ran: <see cref="ObjectiveSystem"/> only advances
    /// <see cref="ObjectiveOutcome.WorkSteps"/> while an actor is <em>executing</em> the
    /// objective's work action, so a policy that walked to the objective room and
    /// stopped there stood in the right place doing nothing for four thousand steps. The
    /// result was a sweep in which every policy scored about 15% and none of them were
    /// distinguishable — the harness was measuring four different walks to a room.
    /// </para>
    /// <para>
    /// Which action counts as the work is <c>objective_rule.work_action</c>, not a switch
    /// here. The six objectives need six different verbs — hack a terminal, plant a
    /// device, take a target down, carry somebody out, watch a room — and a player will
    /// eventually need to choose between them, so the choice belongs in the table where
    /// the objective's other parameters already live.
    /// </para>
    /// </remarks>
    private TacticalOrder? Work(TacticalState state, TacticalActor actor)
    {
        SiteRoom? here = state.RoomOf(actor);

        if (here is null)
            return null;

        if (here.Id != state.Layout.ObjectiveRoomId)
            return Advance(state, actor);

        return StartWork(state, actor);
    }

    /// <summary>
    /// Issues the objective's work action, aimed at whatever the objective points at.
    /// </summary>
    /// <remarks>
    /// The interactable when the objective named one, and the actor itself otherwise —
    /// a Recon watches, and an Assassination has a target rather than a terminal, so an
    /// order with no target is the honest encoding of "there is nothing here to point at"
    /// rather than a null that would silently become a no-op.
    /// </remarks>
    private static TacticalOrder? StartWork(TacticalState state, TacticalActor actor)
    {
        string? nameKey = SimulationRules.ObjectiveRuleFor(state.ObjectiveOutcome.Type)?.WorkAction;

        if (string.IsNullOrEmpty(nameKey))
            return null;

        int action = Action(nameKey);

        if (action == 0)
            return null;

        int interactable = state.ObjectiveOutcome.WorkInteractableId;

        return interactable != 0
            ? new TacticalOrder(actor.Id, action, InteractableId: interactable)
            : new TacticalOrder(actor.Id, action);
    }

    /// <summary>The move action this policy uses.</summary>
    /// <remarks>
    /// <para>
    /// Crouching is not "slow and quiet", it is the only way a Stealth run reaches a room
    /// nobody is looking at; a walking Stealth is a walking Speedrun that took longer.
    /// </para>
    /// <para>
    /// <b>This property was dead code until the stage-6 trace.</b> It chose the right row
    /// per policy and was called from nowhere, so every policy walked with
    /// <c>RoleBehaviours</c>'s own <c>MoveWalk</c>: identical verbs, identical speeds,
    /// identical noise, and three order logs that matched character for character. The
    /// stage-6 policy-divergence test is what keeps it wired.
    /// </para>
    /// </remarks>
    private int MoveAction => Policy switch
    {
        SquadPolicy.Stealth or SquadPolicy.Cautious => Action("action.move_crouch"),
        SquadPolicy.Speedrun => Action("action.move_run"),
        _ => Action("action.move_walk"),
    };

    /// <summary>Handles the straight-line "get to the objective" order.</summary>
    private TacticalOrder? Advance(TacticalState state, TacticalActor actor)
    {
        SiteRoomId target = state.Layout.ObjectiveRoomId;

        return target.IsValid ? WalkTo(state, actor, target) : null;
    }

    /// <summary>Handles "get out of the building".</summary>
    private TacticalOrder? Leave(TacticalState state, TacticalActor actor)
    {
        SiteRoomId? target = NearestExtraction(state, actor);

        return target is null ? null : WalkTo(state, actor, target.Value);
    }

    /// <summary>
    /// A route to a room, as an order, or null when already there or unreachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Delegates to <see cref="RoleBehaviours"/> rather than routing here, which is what
    /// this method's own remarks used to claim it did and no longer did. It had its own
    /// copy of the pathfinder's first hop, and that copy emitted a plain walk for a
    /// horizontal door — which <see cref="MovementSystem"/> clamps to the room the actor
    /// is already standing in. Every policy that tried to leave a room walked to the wall
    /// beside the door and stood there for the rest of the mission, which is why the
    /// sweep reported an objective rate of about 75% and an exit time of zero.
    /// </para>
    /// <para>
    /// One pathfinder, one rule about what crosses a doorway, and a policy that is
    /// comparable to a player issuing the same order.
    /// </para>
    /// </remarks>
    private TacticalOrder? WalkTo(TacticalState state, TacticalActor actor, SiteRoomId target)
    {
        SiteRoom? here = state.RoomOf(actor);
        SiteRoom? destination = state.Layout.Find(target);

        if (here is null || destination is null || here.Id == target)
            return null;

        return RoleBehaviours.TacticalOrderFor(
            state,
            state.Composition!,
            actor,
            new SquadStandingOrder(
                SquadOrderKind.MoveTo,
                new TacticalPosition(destination.FloorIndex, destination.StartX)),
            state.CommandPost,
            LockPreference,
            MoveAction);
    }

    /// <summary>
    /// How this policy answers a locked door.
    /// </summary>
    /// <remarks>
    /// <b>The character, and it is a choice about routes rather than about verbs.</b>
    /// <see cref="LockPreference.RouteAround"/> asks the pathfinder for a lock-free way
    /// first and only forces when the building offers none, which is Stealth's answer
    /// and Cautious's too — a squad that walks the long way round a locked door and a
    /// squad that does not are not the same squad, and the difference is meant to show up
    /// in the alarm column rather than being asserted here.
    /// <see cref="LockPreference.ForceThrough"/> treats a lock as a cost and takes the
    /// cheapest route, which is Speedrun's and Aggressive's answer: the door is noise
    /// either way, so the shortest path is the one they want.
    ///
    /// <para>
    /// Both go through the same <see cref="RoleBehaviours"/> hop and the same
    /// <see cref="ActionSystem"/> gate as a player's order. A policy has no private way
    /// through a locked door; it only chooses which route to ask for.
    /// </para>
    /// </remarks>
    private LockPreference LockPreference => Policy switch
    {
        SquadPolicy.Speedrun or SquadPolicy.Aggressive => Core.Squad.LockPreference.ForceThrough,
        _ => Core.Squad.LockPreference.RouteAround,
    };

    /// <summary>An Overwatch member's watch.</summary>
    private static TacticalOrder? Observe(TacticalActor actor)
    {
        int observe = Action("action.support_observe");

        return observe == 0 ? null : new TacticalOrder(actor.Id, observe);
    }

    /// <summary>
    /// A takedown when close enough, a shot otherwise.
    /// </summary>
    /// <remarks>
    /// Close first because a takedown makes no noise and a shot makes a great deal, and
    /// "Aggressive" is only interesting as a choice between those two rather than as a
    /// synonym for shooting.
    /// </remarks>
    private static TacticalOrder? Engage(TacticalActor actor, TacticalActor guard)
    {
        if (!guard.CanAct || actor.Position.FloorIndex != guard.Position.FloorIndex)
            return null;

        bool adjacent = Fixed32.Distance(actor.Position.X, guard.Position.X).Raw <= 120;

        int action = Action(adjacent ? "action.combat_takedown" : "action.combat_shoot");

        return action == 0
            ? null
            : new TacticalOrder(actor.Id, action, TargetActorId: guard.Id);
    }

    /// <summary>
    /// A guard standing in this actor's way right now.
    /// </summary>
    /// <remarks>
    /// Seen, not remembered, and on the same floor — which is the same bar a player must
    /// clear before pulling a trigger. A policy that shot at remembered positions would
    /// report a hit rate the game's own sight rules do not support, and the balance
    /// report's Aggressive column would be measuring the script's knowledge rather than
    /// the game's.
    /// </remarks>
    private static TacticalActor? SeenGuard(TacticalState state, TacticalActor actor)
    {
        foreach (TacticalActor guard in state.Guards)
        {
            if (!guard.CanAct || guard.Position.FloorIndex != actor.Position.FloorIndex)
                continue;

            if (actor.Position.FloorIndex == guard.Position.FloorIndex
                && Fixed32.Distance(actor.Position.X, guard.Position.X).Raw > ContactRangeCm)
            {
                continue;
            }

            return guard;
        }

        return null;
    }

    /// <summary>How far a squad member notices a guard on its own floor, in centimetres.</summary>
    private const int ContactRangeCm = 900;

    /// <summary>
    /// The nearest room the mission will let the squad leave by.
    /// </summary>
    /// <remarks>
    /// Nearest by route cost rather than by room id, and the candidates are walked in a
    /// fixed order so two equal-cost extractions resolve the same way on every replay.
    /// </remarks>
    private static SiteRoomId? NearestExtraction(TacticalState state, TacticalActor actor)
    {
        SiteRoom? here = state.RoomOf(actor);

        if (here is null)
            return null;

        SiteRoomId? best = null;
        int bestCost = int.MaxValue;

        foreach (SiteRoomId candidate in ExtractionRooms(state))
        {
            TacticalPath path = Pathfinder.FindRoute(state.Layout, state.Doors, here.Id, candidate);

            if (!path.ReachedTarget || path.TotalSteps >= bestCost)
                continue;

            bestCost = path.TotalSteps;
            best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Every room the squad may leave by, the post's called-in vehicle included.
    /// </summary>
    /// <remarks>
    /// Delegated to <see cref="ObjectiveSystem.ExtractionRooms"/>, which is the single
    /// answer. This was a fourth private copy of the list; the objective, the ending and
    /// this one did not all agree, and the policy being the odd one out is how a squad
    /// could be told to walk to a room the objective would not accept it at. The post's
    /// contribution is included because a Handler who called the vehicle in has made the
    /// entrance usable, and a policy that ignored that would be measuring a squad that
    /// never staffed a Handler.
    /// </remarks>
    private static IReadOnlyList<SiteRoomId> ExtractionRooms(TacticalState state)
        => ObjectiveSystem.ExtractionRooms(state);

    /// <summary>True when anybody on the squad has seen anybody.</summary>
    private static bool HasContact(TacticalState state)
    {
        foreach (TacticalActor actor in state.Squad)
        {
            if (actor.Memory.HasContact)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The band at which Cautious gives up.
    /// </summary>
    /// <remarks>
    /// From <c>squad_rule</c>, because "how nervous is too nervous" is exactly the sort
    /// of number a designer tunes, and a policy that hard-coded it would turn the balance
    /// report's Cautious column into a measurement of this file rather than of the game.
    /// </remarks>
    private static AlarmBand CautiousBand
        => (AlarmBand)SimulationRules.Squad("squad_cautious_abort_band", 2);

    /// <summary>
    /// Resolves an action by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not cached in a field. The tables can be loaded after the driver is
    /// constructed, and a cache that captured "zero, no tables" at construction would
    /// silently turn every policy into a policy that stands still — a failure that would
    /// look exactly like a balance problem, in a report about balance.
    /// </para>
    /// <para>
    /// A literal id would be worse: renumbering <c>tactical_action</c> would quietly
    /// convert a cautious policy into a sprinting one, and nobody would find out until
    /// six stages later, in a number, with no error anywhere.
    /// </para>
    /// </remarks>
    private static int Action(string nameKey) => SimulationRules.TacticalActionIdFor(nameKey);
}