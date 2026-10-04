using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

using InteractableKind = ProjectSpy.Core.InteractableType;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// How a crossing answers a locked connection.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, and the difference between them is the difference between a squad that
/// looks for the way round and a squad that does not.
/// </para>
/// <para>
/// <b>There is no third option, because the table has no verb for one.</b>
/// <c>tactical_action</c> offers open, close, lock and force against a connection.
/// There is no pick. A Stealth squad was specified as preferring "an unlocked alternate
/// route, then pick, force only as a last resort", and the middle of that has no action
/// to map onto — so rather than invent one and quietly change what the rules cost, the
/// gap is named here: routing around, or forcing. Adding a pick verb is a change to
/// <c>data/tactical_action.csv</c> and a decision for the brief's owner, not a code
/// detail.
/// </para>
/// <para>
/// A reason code rather than a bool so the player's order and a policy's order can be
/// read apart in a log (rule 4).
/// </para>
/// </remarks>
public enum LockPreference
{
    /// <summary>Ask for a lock-free route; force only when the building offers none.</summary>
    RouteAround = 0,

    /// <summary>Treat a lock as a cost and take the cheapest route, which may go through one.</summary>
    ForceThrough = 1,
}

/// <summary>
/// What a squad member does when nobody is driving them and nothing is queued.
/// </summary>
/// <remarks>
/// <para>
/// <b>Seven behaviours, written out, no search.</b> This is the direct answer to the
/// brief's "constrained, readable behaviour set (not GOAP — the player must be able to
/// predict their own squad)". Every method here is a straight-line decision a player
/// could describe to a friend: the Point Man walks to the next waypoint and stops; the
/// Medic walks to whoever is worst off and works on them. There is no utility score, no
/// candidate generation, no plan — because anything that searched would eventually find
/// a solution its author did not intend, and a squad the player cannot predict is a squad
/// they cannot command.
/// </para>
/// <para>
/// <b>They share the guards' actions, not their planner.</b> Every order this file
/// produces goes through <see cref="ActionSystem.TryBegin"/>, which is the same gate the
/// guards' GOAP executor goes through. That is what makes the required test possible:
/// "role behaviours never issue an illegal action" is checkable precisely because there
/// is one legality oracle and both callers use it.
/// </para>
/// <para>
/// <b>Advancing across the building is walk-to-the-door then traverse.</b> Movement is
/// validated against the actor's own room, so "go to the terminal" cannot be one order;
/// it is a walk to the doorway, a traverse, and then a new decision from the room on the
/// other side. <see cref="Advance"/> is that loop for all seven behaviours, which is why
/// they differ in what they head towards and not in how they travel.
/// </para>
/// </remarks>
public static class RoleBehaviours
{
    // The tactical actions the behaviours issue. Spelled out rather than read from the
    // table so that a behaviour cannot quietly become "whatever row 12403 says today"
    // and change every recorded mission when a designer retunes a row.

    /// <summary>Walking, quietly. 24cm a step.</summary>
    private const int MoveWalk = 12401;

    /// <summary>Running. Fast, loud, and the reason a Mule cannot hurry.</summary>
    private const int MoveRun = 12402;

    /// <summary>Searching a container for loot.</summary>
    private const int SearchContainer = 12414;

    /// <summary>Hiding or dragging a body.</summary>
    private const int HideBody = 12416;

    /// <summary>Working on a terminal.</summary>
    private const int HackTerminal = 12413;

    /// <summary>Opening a shut door.</summary>
    private const int OpenDoor = 12409;

    /// <summary>Crossing an open doorway.</summary>
    private const int TraverseDoor = 12407;

    /// <summary>Getting through a locked or barricaded door.</summary>
    private const int ForceDoor = 12412;

    /// <summary>Treating an injured ally.</summary>
    private const int Heal = 12427;

    /// <summary>Bringing a downed ally back.</summary>
    private const int Revive = 12428;

    /// <summary>Lifting a downed ally.</summary>
    private const int CarryAlly = 12429;

    /// <summary>Standing still and watching.</summary>
    private const int Observe = 12433;

    /// <summary>Telling the rest of the squad something.</summary>
    private const int SignalSquad = 12430;

    /// <summary>
    /// How close an actor must be to a doorway before a traverse replaces a walk.
    /// </summary>
    /// <remarks>
    /// <b>Structural, not tuned.</b> <see cref="MovementSystem"/> clamps a walk to the
    /// actor's room span, so a member walking at the door's exact x stops there. The
    /// tolerance exists only so the member does not have to be perfectly on the
    /// centimetre; it is a door's own width, roughly, and not a balance number.
    /// </remarks>
    private const int DoorwayToleranceCm = 60;

    /// <summary>Health an actor is at when they need nobody's help.</summary>
    private const int FullHealth = 100;

    /// <summary>
    /// Runs a member's role behaviour for one step.
    /// </summary>
    /// <returns>
    /// True when an action was started. False means the behaviour has nothing to do this
    /// step — a normal answer, not a failure: an Overwatch member with nothing to report
    /// should stand there, and issuing an action every step would fill the order log with
    /// noise and make the log useless for finding the interesting orders.
    /// </returns>
    public static bool TryStart(
        TacticalState state,
        SquadComposition composition,
        TacticalActor actor,
        IRng rng)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (composition is null) throw new ArgumentNullException(nameof(composition));
        if (actor is null) throw new ArgumentNullException(nameof(actor));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        SquadMember? member = composition.MemberFor(actor.AgentId);

        if (member is null)
            return false;

        TacticalOrder? order = Decide(state, composition, actor, member);

        if (order is null)
            return false;

        // Validate before TryBegin even though TryBegin validates. Not redundancy for its
        // own sake: this is the assertion the "role behaviours never issue an illegal
        // action" test rests on, and having it in the implementation means the property
        // holds of the running game and not only of a test calling a different entry
        // point.
        if (ActionSystem.Validate(state, order).IsRejected)
            return false;

        return ActionSystem.TryBegin(state, order, rng, state.Step, out _);
    }

    /// <summary>
    /// What a member's role behaviour would do right now, without doing it.
    /// </summary>
    /// <param name="state">The mission.</param>
    /// <param name="composition">Who is on the squad and in what role.</param>
    /// <param name="actor">The member to ask about.</param>
    /// <returns>The order, or null when the behaviour has nothing to do.</returns>
    /// <remarks>
    /// <para>
    /// The probe the legality test uses, and public for exactly that reason: a test that
    /// asserted only on <see cref="TryStart"/>'s return value could not tell "the
    /// behaviour chose nothing" from "the behaviour chose something illegal and the
    /// legality gate quietly swallowed it", and those are very different bugs.
    /// </para>
    /// <para>
    /// <b>Side-effect free.</b> It decides exactly as <see cref="TryStart"/> decides and
    /// then stops; the only thing it does not do is begin the action. Nothing here may
    /// mutate — a probe that nudged an actor's facing as a side effect would make the
    /// test's own measurements move.
    /// </para>
    /// </remarks>
    public static TacticalOrder? Probe(
        TacticalState state, SquadComposition composition, TacticalActor actor)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (composition is null) throw new ArgumentNullException(nameof(composition));
        if (actor is null) throw new ArgumentNullException(nameof(actor));

        SquadMember? member = composition.MemberFor(actor.AgentId);

        return member is null ? null : Decide(state, composition, actor, member);
    }

    /// <summary>
    /// Turns a standing order into the action that carries it out.
    /// </summary>
    /// <param name="state">The mission.</param>
    /// <param name="composition">Who is on the squad and in what role.</param>
    /// <param name="actor">The member carrying the order.</param>
    /// <param name="order">What the player asked for.</param>
    /// <param name="post">The forward post, for the orders that need it.</param>
    /// <param name="locks">How this caller answers a locked door.</param>
    /// <param name="moveActionId">
    /// The <c>tactical_action</c> row this caller travels on.
    /// </param>
    /// <returns>The order to issue, or null when the order needs no action right now.</returns>
    /// <remarks>
    /// <para>
    /// <c>moveActionId</c> defaults to <see cref="MoveWalk"/> because that is what a
    /// player pressing <c>move to</c> means — the game's default verb. It is a parameter
    /// because it has to be: <see cref="SquadPolicyDriver"/> chooses its verb by policy,
    /// and it used to have no way to pass it, so a Speedrun squad's run order was
    /// silently downgraded to a walk at the boundary. The policy picked a verb, computed
    /// the right id, and called nothing.
    /// </para>
    /// <para>
    /// Only the <c>MoveTo</c> order uses it. The role-driven orders below decide their
    /// own verb from what the member is doing — a Medic running to a casualty runs, and
    /// that is not a policy question.
    /// </para>
    /// </remarks>
    public static TacticalOrder? TacticalOrderFor(
        TacticalState state,
        SquadComposition composition,
        TacticalActor actor,
        SquadStandingOrder order,
        CommandPostState post,
        LockPreference locks = LockPreference.RouteAround,
        int moveActionId = MoveWalk)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (composition is null) throw new ArgumentNullException(nameof(composition));
        if (actor is null) throw new ArgumentNullException(nameof(actor));
        if (order is null) throw new ArgumentNullException(nameof(order));
        if (post is null) throw new ArgumentNullException(nameof(post));

        return order.Kind switch
        {
            SquadOrderKind.MoveTo => MoveTo(state, actor, order.Target, moveActionId, locks),
            SquadOrderKind.Hold => Stop(actor),
            SquadOrderKind.Follow => Follow(state, actor, order.TargetActorId),
            SquadOrderKind.Stack => Stack(state, actor, order.ConnectionId),
            SquadOrderKind.OverwatchDirection => Overwatch(actor, order.Facing),
            SquadOrderKind.UseGadget => GadgetUse(actor, order),
            SquadOrderKind.HideBody => Hide(actor, order),
            SquadOrderKind.CarryAlly => Carry(state, actor, order.TargetActorId),
            SquadOrderKind.Regroup => Regroup(state, actor, order.Target),
            SquadOrderKind.Abort => Abort(state, actor),
            SquadOrderKind.RequestExtraction => post.RequestExtraction(actor),
            _ => null,
        };
    }

    /// <summary>
    /// The one dispatch, on the behaviour rather than on a string.
    /// </summary>
    /// <remarks>
    /// The default arm throws rather than returning null. A role whose
    /// <c>auto_behaviour_set</c> names a behaviour nobody implemented is a data/code
    /// disagreement, and returning null would produce a member who silently stands still
    /// for the whole mission while the dispatch screen promised them a job — the exact
    /// quiet failure rule 5 exists to forbid.
    /// </remarks>
    private static TacticalOrder? Decide(
        TacticalState state,
        SquadComposition composition,
        TacticalActor actor,
        SquadMember member)
    {
        return member.Behaviour switch
        {
            RoleBehaviour.None => null,
            RoleBehaviour.PointMan => PointMan(state, actor),
            RoleBehaviour.Overwatch => OverwatchBehaviour(state, actor),
            RoleBehaviour.Mule => Mule(state, actor),
            RoleBehaviour.Hacker => Hacker(state, actor),
            RoleBehaviour.Medic => Medic(state, actor),
            RoleBehaviour.Scout => Scout(state, actor),
            RoleBehaviour.Handler => Handler(state, actor),
            _ => throw new NotImplementedException(
                $"TODO(stage-4e): role behaviour {member.Behaviour} has no implementation."),
        };
    }

    // ---- the seven behaviours -------------------------------------------------

    /// <summary>
    /// The Point Man: advance to the next marked waypoint, then hold it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Marked waypoint" is the nearest extraction point until the objective is done,
    /// and the room on the route to the objective afterwards. Inverting that — pushing
    /// from the start — would make the extraction point somewhere the squad merely
    /// passes through rather than somewhere it can fall back to, and the brief's abort
    /// requirement depends on the second.
    /// </para>
    /// <para>
    /// Holding is an <see cref="Observe"/>, not a stop. A Point Man who has reached the
    /// waypoint is covering it, and "cover" and "stand there" are different instructions.
    /// </para>
    /// </remarks>
    private static TacticalOrder? PointMan(TacticalState state, TacticalActor actor)
    {
        if (!state.Objective.IsComplete)
        {
            SiteRoomId? extraction = NearestOf(state, actor, state.Layout.ExtractionRoomIds);

            if (extraction is { } safe)
                return Advance(state, actor, safe, MoveWalk);
        }

        return Advance(state, actor, state.Layout.ObjectiveRoomId, MoveWalk);
    }/// <summary>
    /// The Overwatch member: stay put, face the way they were told, report contacts.
    /// </summary>
/// <remarks>
    /// <para>
    /// Reports with a squad signal, which is an action the guards can hear and the player
    /// can see in the log. It never moves the member and never costs much, which is the
    /// entire promise the role name makes.
    /// </para>
    /// <para>
    /// <b>Only what they can see, and only nearby.</b> The reporting radius comes from
    /// <c>squad_rule.squad_order_overwatch_report_cm</c>, because a member relaying
    /// something across a whole building is an omniscient radio and makes the Overwatch
    /// role strictly better than standing behind them.
    /// </para>
/// </remarks>
    private static TacticalOrder? OverwatchBehaviour(TacticalState state, TacticalActor actor)
    {
        if (actor.Memory.HasIdentification && CanReachToReport(actor, actor.Memory.LastSeen))
            return new TacticalOrder(actor.Id, SignalSquad);

        return Overwatch(actor, actor.Facing);
    }

    /// <summary>
    /// Whether something the member perceived is close enough to shout about.
    /// </summary>
    /// <remarks>
    /// Distance on the member's own floor. A sighting through a wall in the next room is
    /// not a shout, and treating it as one would have the Overwatch member reporting
    /// contacts from the far side of the building.
    /// </remarks>
    private static bool CanReachToReport(TacticalActor actor, TacticalPosition where)
    {
        if (!where.IsValid || where.FloorIndex != actor.Position.FloorIndex)
            return false;

        int radius = SimulationRules.Squad(
            "squad_order_overwatch_report_cm", DefaultOverwatchReportCm);

        return Fixed32.Distance(actor.Position.X, where.X).Raw <= radius;
    }

    /// <summary>
    /// The Mule: fetch the wounded, then fetch the loot, then carry it out.
    /// </summary>
    /// <remarks>
    /// <b>Injuries come first.</b> A Mule who walks past a bleeding operative to reach a
    /// filing cabinet is playing the player rather than serving them. The list is
    /// people-first and loot-second in a fixed order, which is what keeps the behaviour
    /// predictable without making it a planner.
    /// </remarks>
    private static TacticalOrder? Mule(TacticalState state, TacticalActor actor)
    {
        if (actor.Carrying.IsValid)
            return null;

        TacticalActor? casualty = WorstAlly(state, actor);

        if (casualty is not null)
        {
            return Adjacent(state, actor, casualty)
                ? new TacticalOrder(actor.Id, CarryAlly, TargetActorId: casualty.Id)
                : AdvanceToActor(state, actor, casualty, MoveWalk);
        }

        SiteInteractable? container = NearestOf(state, actor, InteractableKind.Container);

        if (container is null)
            return null;

        return RoomOf(state, actor) == container.RoomId
            ? new TacticalOrder(actor.Id, SearchContainer, InteractableId: container.Id)
            : Advance(state, actor, container.RoomId, MoveWalk);
    }

    /// <summary>
    /// The Hacker: go to the nearest hackable thing and work it.
    /// </summary>
    /// <remarks>
    /// Terminals first, then whatever door is in the way. A terminal is worth fifty
    /// steps of work; a locked door is worth ten. The order is fixed rather than
    /// cost-ranked because a cost-ranked choice starts depending on which terminal
    /// happened to be nearer, and the player would be watching behaviour they could not
    /// predict.
    /// </remarks>
    private static TacticalOrder? Hacker(TacticalState state, TacticalActor actor)
    {
        SiteRoomId here = RoomOf(state, actor);

        // Nothing to hack once the objective's work is done. This check is what makes the
        // mission end: the Hacker was the member standing at the terminal for the whole
        // mission, and with nothing to tell it the job was finished it re-issued the
        // hack every time the previous one completed — forever, on a site whose objective
        // had been worked out three thousand steps earlier. The objective, the ending and
        // the role behaviour were three systems and only the first two had an opinion.
        if (ObjectiveSystem.WorkFinished(state, state.ObjectiveOutcome))
            return Leave(state, actor);

        // The mission's own work comes first, and it is checked before the local terminal
        // sweep because the objective prop is an InteractableKind.Objective rather than a
        // Terminal. Matching on kind alone left the Hacker standing in the objective
        // room with nothing to do, having decided the room downstairs was nearer.
        int workInteractable = state.ObjectiveOutcome.WorkInteractableId;

        if (workInteractable != 0)
        {
            SiteInteractable? work = Find(state, workInteractable);

            if (work is not null)
            {
                if (work.RoomId == here)
                    return StartObjectiveWork(state, actor);

                return Advance(state, actor, work.RoomId, MoveWalk);
            }
        }

        foreach (SiteInteractable interactable in state.Layout.Interactables)
        {
            if (interactable.Kind == InteractableKind.Terminal && interactable.RoomId == here)
                return new TacticalOrder(actor.Id, HackTerminal, InteractableId: interactable.Id);
        }

        SiteInteractable? terminal = NearestOf(state, actor, InteractableKind.Terminal);

        if (terminal is not null)
            return Advance(state, actor, terminal.RoomId, MoveWalk);

        // No terminal in this building. Then the only lock in the job is a door, and a
        // Hacker opening doors is not a consolation prize.
        SiteConnection? door = NearestDoor(state, actor);

        return door is null
            ? null
            : Advance(state, actor, RoomIdAt(state, actor, door.Id), MoveWalk);
    }

    /// <summary>The mission's own work, as a tactical order.</summary>
    /// <remarks>
    /// The verb comes from <c>objective_rule.work_action</c> rather than from a switch
    /// here, for the same reason the policy driver reads it there: the six objective
    /// types need six different verbs, and the role behaviour should not have to be
    /// rewritten when a designer adds a seventh.
    /// </remarks>
    private static TacticalOrder? StartObjectiveWork(TacticalState state, TacticalActor actor)
    {
        string? nameKey = SimulationRules.ObjectiveRuleFor(state.ObjectiveOutcome.Type)?.WorkAction;

        if (string.IsNullOrEmpty(nameKey))
            return null;

        int action = SimulationRules.TacticalActionIdFor(nameKey);

        if (action == 0)
            return null;

        int interactable = state.ObjectiveOutcome.WorkInteractableId;

        return interactable != 0
            ? new TacticalOrder(actor.Id, action, InteractableId: interactable)
            : new TacticalOrder(actor.Id, action);
    }

    /// <summary>One interactable by id, or null.</summary>
    private static SiteInteractable? Find(TacticalState state, int interactableId)
    {
        foreach (SiteInteractable interactable in state.Layout.Interactables)
        {
            if (interactable.Id == interactableId)
                return interactable;
        }

        return null;
    }

    /// <summary>
    /// The Medic: go to whoever is worst off and work on them.
    /// </summary>
    /// <remarks>
    /// Worst means lowest health with a downed body ranked below merely hurt: a body at
    /// five health is a timer, and somebody standing at eighty is not. A member already
    /// carrying somebody does not go looking for a second patient they cannot pick up.
    /// </remarks>
    private static TacticalOrder? Medic(TacticalState state, TacticalActor actor)
    {
        if (actor.Carrying.IsValid)
            return null;

        TacticalActor? patient = WorstAlly(state, actor);

        if (patient is null)
            return null;

        if (!Adjacent(state, actor, patient))
            return AdvanceToActor(state, actor, patient, MoveRun);

        int action = patient.Condition == ActorCondition.Downed ? Revive : Heal;
        return new TacticalOrder(actor.Id, action, TargetActorId: patient.Id);
    }

    /// <summary>
    /// The Scout: get ahead of the squad, look at what is there, report it.
    /// </summary>
    /// <remarks>
    /// Aimed one hop further along the route to the objective than the controlled agent
    /// is, so "ahead" is measured against the team rather than against the map. Once
    /// there it observes and reports: the Scout's value is the information arriving, not
    /// the floor covered.
    /// </remarks>
    private static TacticalOrder? Scout(TacticalState state, TacticalActor actor)
    {
        SiteRoomId objective = state.Layout.ObjectiveRoomId;
        SiteRoomId here = RoomOf(state, actor);

        if (objective == here)
            return actor.Memory.HasIdentification
                ? new TacticalOrder(actor.Id, SignalSquad)
                : Overwatch(actor, Facing.Right);

        SiteRoomId ahead = NextOnRoute(state, here, objective);

        return ahead == here
            ? new TacticalOrder(actor.Id, Observe)
            : Advance(state, actor, ahead, MoveWalk);
    }

    /// <summary>
    /// The Handler: hold the post, watch the approach, keep the radio alive.
    /// </summary>
    /// <remarks>
    /// The least interesting behaviour on purpose. A Handler is not there to do anything;
    /// they are there so the four support abilities exist. All they do unattended is
    /// watch the corridor they were posted at, because a Handler who wanders has silently
    /// removed every option the player had.
    /// </remarks>
    private static TacticalOrder? Handler(TacticalState state, TacticalActor actor)
    {
        if (actor.Memory.HasIdentification)
            return new TacticalOrder(actor.Id, SignalSquad);

        return Overwatch(actor, Facing.Right);
    }

    // ---- movement -------------------------------------------------------------

    /// <summary>
    /// Moves one step along the route towards a room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two orders, alternating: walk to the doorway of the next room on the route, then
    /// traverse it. Neither alone would get anybody anywhere — a walk is clamped to the
    /// actor's own room and a traverse is refused from further than arm's reach — so the
    /// loop is the minimum that actually crosses a building.
    /// </para>
    /// <para>
    /// A locked door is forced rather than refused, because the alternative is a Hacker
    /// or Point Man standing in front of a locked door forever, which looks like a bug
    /// and is indistinguishable from one.
    /// </para>
    /// </remarks>
    private static TacticalOrder? Advance(
        TacticalState state,
        TacticalActor actor,
        SiteRoomId destination,
        int moveActionId,
        LockPreference locks = LockPreference.RouteAround)
    {
        SiteRoomId here = RoomOf(state, actor);

        if (here == destination)
            return Overwatch(actor, actor.Facing);

        return Hop(state, actor, destination, moveActionId, locks);
    }

    /// <summary>
    /// Head for the nearest way out, once there is nothing left inside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The role behaviours' answer to "the job is done", and it routes through
    /// <see cref="ObjectiveSystem.ExtractionRooms"/> like everything else, so a member
    /// leaving on their own initiative walks to the same door the mission ending and the
    /// objective are both waiting at.
    /// </para>
    /// <para>
    /// Overwatch rather than null when there is nowhere to go, so a member standing in a
    /// building with no exit watches instead of freezing — and so the caller can tell
    /// "nothing to do" from "nothing it can do".
    /// </para>
    /// </remarks>
    private static TacticalOrder? Leave(TacticalState state, TacticalActor actor)
    {
        SiteRoomId? exit = NearestRoomOf(state, actor, ObjectiveSystem.ExtractionRooms(state));

        if (exit is not { } room || room == RoomOf(state, actor))
            return Overwatch(actor, actor.Facing);

        return Advance(state, actor, room, MoveWalk);
    }

    /// <summary>
    /// The room from a list the actor can get to most cheaply, or null.
    /// </summary>
    /// <remarks>
    /// Nearest by the pathfinder's own cost so that a role behaviour and the policy
    /// driving it do not disagree about which door is closest, and walked in list order
    /// so that two equal-cost candidates resolve identically on every replay (rule 6).
    /// </remarks>
    private static SiteRoomId? NearestRoomOf(
        TacticalState state, TacticalActor actor, IReadOnlyList<SiteRoomId> rooms)
    {
        SiteRoomId? best = null;
        int bestSteps = int.MaxValue;

        foreach (SiteRoomId candidate in rooms)
        {
            if (!candidate.IsValid || state.Layout.Find(candidate) is null)
                continue;

            int steps = RoomDistance(state, RoomOf(state, actor), candidate);

            if (steps < bestSteps)
            {
                bestSteps = steps;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The order for getting through a doorway the member is already standing at.
    /// </summary>
    /// <remarks>
    /// The verb follows the door's live state and never the preference, because by the
    /// time this is reached the route has already been chosen: a preference that said
    /// "route around" has run out of ways round, and one that said "force" has not.
    /// </remarks>
    private static TacticalOrder Through(TacticalState state, TacticalActor actor, SiteConnection door)
    {
        ConnectionState current = state.StateOf(door.Id);

        int action = current switch
        {
            ConnectionState.Open => TraverseDoor,
            ConnectionState.Closed => OpenDoor,
            _ => ForceDoor,
        };

        return new TacticalOrder(actor.Id, action, ConnectionId: door.Id);
    }

    /// <summary>Moves one room towards another actor.</summary>
    private static TacticalOrder? AdvanceToActor(
        TacticalState state, TacticalActor actor, TacticalActor other, int moveActionId)
    {
        SiteRoom? room = state.RoomOf(other);

        return room is null
            ? null
            : Advance(state, actor, room.Id, moveActionId);
    }

    /// <summary>
    /// The room one hop further along the route from one room to another.
    /// </summary>
    /// <remarks>
    /// Read off the route the pathfinder already computed, so "one step ahead of the
    /// team" means one connection further rather than a nearest-room guess that could
    /// send the Scout sideways into a cupboard.
    /// </remarks>
    private static SiteRoomId NextOnRoute(TacticalState state, SiteRoomId from, SiteRoomId to)
    {
        SiteConnection? first = FirstHop(state, from, to);
        return first is null ? from : first.Other(from);
    }

    /// <summary>The first connection on the route between two rooms, or null.</summary>
    private static SiteConnection? FirstHop(
        TacticalState state, SiteRoomId from, SiteRoomId to, PathOptions? options = null)
    {
        if (from == to)
            return null;

        // ForAgent, not ForNpc: these are the squad's own legs. The crawlspace ban
        // belongs to guards, and applying it here meant a member standing in a room
        // reachable only through a vent or a window found no route, issued no order,
        // and stood there for the rest of the mission.
        PathOptions rules = options is null
            ? PathOptions.ForAgent
            : options with { AllowCrawlable = PathOptions.ForAgent.AllowCrawlable };

        TacticalPath path = Pathfinder.FindRoute(state.Layout, state.Doors, from, to, rules);

        if (!path.ReachedTarget || path.Connections.Count == 0)
            return null;

        return state.Layout.FindConnectionFor(path.Connections[0]);
    }

    // ---- the player's orders --------------------------------------------------

    /// <summary>A movement towards a named point, or nothing when already on it.</summary>
    /// <remarks>
    /// Null on a zero-distance target because
    /// <see cref="MovementSystem.TryBeginMove"/> refuses one with
    /// <see cref="TacticalOrderReason.OutOfReach"/>, and an order that is always refused
    /// would sit at the head of a queue for the rest of the mission.
    /// </remarks>
    private static TacticalOrder? Move(TacticalActor actor, TacticalPosition target, int moveActionId)
    {
        if (!target.IsValid || target.FloorIndex != actor.Position.FloorIndex)
            return null;

        if (Fixed32.Distance(actor.Position.X, target.X).Raw <= 0)
            return null;

        return new TacticalOrder(actor.Id, moveActionId, Target: target);
    }

    /// <summary>
    /// Walks toward a position, routing room by room when the destination is not
    /// reachable in a straight line from where the member is standing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves of this were broken.</b> <see cref="Move"/> issues a plain walk to
    /// an x on the actor's own floor, which is correct for "walk to that spot in this
    /// room" and wrong for everything else. A destination in another room was walked at
    /// until the member reached a point that turned out to be inside the room they were
    /// already standing in; a destination on another floor resolved to nothing at all.
    /// Either way the order was <em>accepted</em>, the member stood still or stopped
    /// early, and no refusal was written anywhere — a silent no-op, which is the one
    /// failure mode a player cannot diagnose from the screen.
    /// </para>
    /// <para>
    /// The route is <see cref="Pathfinder"/>'s — the same graph the guards and
    /// <see cref="SquadPolicyDriver"/> walk — so "the way there" means one thing in this
    /// game. Only the first hop is issued: the member re-decides at every step boundary,
    /// which is what lets a player retarget mid-walk simply by issuing a new order.
    /// </para>
    /// </remarks>
    private static TacticalOrder? MoveTo(
        TacticalState state,
        TacticalActor actor,
        TacticalPosition target,
        int moveActionId,
        LockPreference locks = LockPreference.RouteAround)
    {
        if (!target.IsValid)
            return null;

        SiteRoom? here = state.RoomOf(actor);

        if (here is null)
            return null;

        SiteRoom? destination = state.Layout.RoomContaining(target)
            ?? NearestRoomOnFloor(state, target.FloorIndex, target.X);

        if (destination is null)
            return null;

        // Same room: a walk is the whole answer, and routing would be silly — it would
        // path out of the room and back in to arrive at a point already in reach.
        if (destination.Id == here.Id && target.FloorIndex == actor.Position.FloorIndex)
            return Move(actor, target, moveActionId);

        // The same hop every other way of crossing a room uses, including the walk to
        // the doorway that has to come first.
        //
        // This used to issue the traversal directly. That is a legal-looking order and
        // it is refused as OutOfReach from anywhere but arm's length of the door, so a
        // player who told a member to go to another room watched them stand still and
        // be told nothing, for ever. The role behaviours had the walk-then-traverse loop
        // two functions away the whole time. One hop, one loop, one answer.
        return Hop(state, actor, destination.Id, moveActionId, locks);
    }

    /// <summary>
    /// One step of a route: walk to the doorway if it is not within reach, then cross.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves are needed.</b> <see cref="MovementSystem"/> clamps a walk to the
    /// room the actor is already in, and refuses a traversal from beyond arm's reach, so
    /// neither order alone crosses a building. Alternating them is the minimum that
    /// does.
    /// </para>
    /// <para>
    /// <b>And the crossing action depends on what the door is doing.</b> An open door is
    /// traversed, a shut one opened, a locked one forced — because the only two door
    /// verbs the table offers that change a connection's state are open and force, and a
    /// traversal of a locked door is refused. Forcing is last: the route is asked for
    /// without locks first, so a building with a way round is walked round.
    /// </para>
    /// </remarks>
    private static TacticalOrder? Hop(
        TacticalState state,
        TacticalActor actor,
        SiteRoomId destination,
        int moveActionId,
        LockPreference locks)
    {
        SiteRoom? here = state.Layout.Find(RoomOf(state, actor));

        if (here is null)
            return null;

        SiteConnection? next = Route(state, here.Id, destination, locks);

        if (next is null)
            return null;

        // Standing on this room's side of the shared wall is what the walk aims for. A
        // vertical connection lands on a different x upstairs, which is why the two
        // sides are read separately rather than assuming one doorway.
        Fixed32 door = here.Id == next.RoomA ? next.X : next.UpperX;

        if (Fixed32.Distance(actor.Position.X, door).Raw <= DoorwayToleranceCm)
            return Through(state, actor, next);

        return new TacticalOrder(actor.Id, moveActionId, Target: new TacticalPosition(
            actor.Position.FloorIndex, door));
    }

    /// <summary>
    /// The first connection on the route from one room to another, honouring a lock rule.
    /// </summary>
    /// <remarks>
    /// Asked without locks first whatever the preference, so that a building with an
    /// unlocked way round is never charged for picking one that does not exist. Only
    /// when there is genuinely no lock-free route does a caller who wants to force
    /// through pay for one.
    /// </remarks>
    private static SiteConnection? Route(
        TacticalState state, SiteRoomId from, SiteRoomId to, LockPreference locks)
    {
        // The two rules differ in which route they ask for, which is the whole of their
        // character. Going through a lock is only ever a fallback for one of them.
        if (locks == LockPreference.ForceThrough)
            return FirstHop(state, from, to, new PathOptions { AllowLocked = true });

        return FirstHop(state, from, to, new PathOptions { AllowLocked = false })
               ?? FirstHop(state, from, to, new PathOptions { AllowLocked = true });
    }

    /// <summary>
    /// The room on a floor whose span comes closest to a point on that floor.
    /// </summary>
    /// <remarks>
    /// Used when the destination <em>position</em> is not inside any room — the normal
    /// case for a MoveTo resolved to a room's near edge on a floor that is not the
    /// actor's. Choosing by distance to the room's span rather than by room id keeps the
    /// choice stable and cheap, and ties never happen for a lane whose rooms partition
    /// the floor.
    /// </remarks>
    private static SiteRoom? NearestRoomOnFloor(TacticalState state, int floorIndex, Fixed32 x)
    {
        SiteRoom? best = null;
        long bestGap = long.MaxValue;

        foreach (SiteRoom room in state.Layout.AllRooms)
        {
            if (room.FloorIndex != floorIndex)
                continue;

            long gap = room.Contains(x)
                ? 0
                : room.StartX.Raw > x.Raw ? room.StartX.Raw - x.Raw : x.Raw - room.EndX.Raw;

            if (gap >= bestGap)
                continue;

            bestGap = gap;
            best = room;
        }

        return best;
    }

    /// <summary>The stop order.</summary>
    private static TacticalOrder Stop(TacticalActor actor)
        => new(actor.Id, TacticalOrder.StopActionId);

    /// <summary>Standing still and watching, facing a given way.</summary>
    private static TacticalOrder Overwatch(TacticalActor actor, Facing facing)
    {
        // Facing is set here rather than in an action because there is no action that
        // turns somebody: a member told to watch the north wall has to already be facing
        // it, and pretending otherwise would be a behaviour that cannot be executed.
        actor.Facing = facing;
        return new TacticalOrder(actor.Id, Observe);
    }

    /// <summary>Follows the controlled agent, stopping a standoff short of them.</summary>
    private static TacticalOrder? Follow(TacticalState state, TacticalActor actor, TacticalActorId leaderId)
    {
        TacticalActor? leader = leaderId.IsValid ? state.Actor(leaderId) : AnchorOf(state, actor);

        if (leader is null || leader.Position.FloorIndex != actor.Position.FloorIndex)
            return null;

        int standoff = SimulationRules.Squad(
            "squad_order_follow_standoff_cm", DefaultFollowStandoffCm);

        // Approach from whichever side the follower is already on, so it stops behind the
        // leader rather than trying to occupy the same centimetre forever.
        int sign = leader.Position.X.Raw >= actor.Position.X.Raw ? -1 : 1;
        var target = new TacticalPosition(
            actor.Position.FloorIndex,
            new Fixed32(leader.Position.X.Raw + (sign * standoff)));

        return Move(actor, target, MoveWalk);
    }

    /// <summary>
    /// Holds one side of a connection.
    /// </summary>
    /// <remarks>
    /// The offset comes from <c>squad_rule</c> so a squad stacked on one door occupies a
    /// believable spread rather than a line of actors standing inside each other, and a
    /// designer can widen it when a doorway is being used as a chokepoint.
    /// </remarks>
    private static TacticalOrder? Stack(
        TacticalState state, TacticalActor actor, SiteConnectionId connectionId)
    {
        SiteConnection? connection = state.Layout.FindConnectionFor(connectionId);

        if (connection is null)
            return null;

        SiteRoom? here = state.RoomOf(actor);

        if (here is null || (here.Id != connection.RoomA && here.Id != connection.RoomB))
            return null;

        int offset = SimulationRules.Squad("squad_order_stack_offset_cm", DefaultStackOffsetCm);
        Fixed32 door = here.Id == connection.RoomA ? connection.X : connection.UpperX;

        // This side's wall is on the far side of the door from the room being entered, so
        // stand the offset back into this room and face the opening.
        int sign = door.Raw >= here.LastX.Raw ? -1 : 1;
        var target = new TacticalPosition(
            actor.Position.FloorIndex, new Fixed32(door.Raw + (sign * offset)));

        return Move(actor, target, MoveWalk);
    }

    /// <summary>
    /// Uses a gadget.
    /// </summary>
    /// <remarks>
    /// Two gadget families map onto an action: a restorative one treats its own user,
    /// and a technical one works the thing the player pointed at. Anything else is
    /// refused rather than approximated, because an action that quietly did nothing would
    /// still have spent a use the player paid for.
    /// </remarks>
    private static TacticalOrder? GadgetUse(TacticalActor actor, SquadStandingOrder order)
    {
        int effect = GadgetEffect(order.ItemId);

        if (effect == GadgetEffectRestore)
            return new TacticalOrder(actor.Id, Heal, TargetActorId: actor.Id);

        if (effect == GadgetEffectTechnical && order.InteractableId != 0)
            return new TacticalOrder(actor.Id, SearchContainer, InteractableId: order.InteractableId);

        return null;
    }

    /// <summary>Hides or drags the body the player pointed at.</summary>
    private static TacticalOrder? Hide(TacticalActor actor, SquadStandingOrder order)
    {
        if (order.InteractableId != 0)
            return new TacticalOrder(actor.Id, HideBody, InteractableId: order.InteractableId);

        return order.TargetActorId.IsValid
            ? new TacticalOrder(actor.Id, HideBody, TargetActorId: order.TargetActorId)
            : null;
    }

    /// <summary>Picks up a downed ally.</summary>
    private static TacticalOrder? Carry(
        TacticalState state, TacticalActor actor, TacticalActorId targetActorId)
    {
        if (!targetActorId.IsValid || actor.Carrying.IsValid)
            return null;

        TacticalActor? target = state.Actor(targetActorId);

        return target is null || target.AgentId == actor.AgentId
            ? null
            : new TacticalOrder(actor.Id, CarryAlly, TargetActorId: targetActorId);
    }

    /// <summary>
    /// Comes back to the controlled agent.
    /// </summary>
    /// <remarks>
    /// Inside the regroup radius the member has arrived and simply holds, which is what
    /// stops a Mule shuffling back and forth across a doorway for the rest of the mission.
    /// </remarks>
    private static TacticalOrder? Regroup(TacticalState state, TacticalActor actor, TacticalPosition point)
    {
        TacticalPosition target = point.IsValid ? point : AnchorOf(state, actor)?.Position ?? actor.Position;
        int radius = SimulationRules.Squad("squad_order_regroup_radius_cm", DefaultRegroupRadiusCm);

        if (target.FloorIndex == actor.Position.FloorIndex
            && Fixed32.Distance(actor.Position.X, target.X).Raw <= radius)
        {
            return Overwatch(actor, actor.Facing);
        }

        return Move(actor, target, MoveRun);
    }

    /// <summary>
    /// Heads for the nearest extraction point.
    /// </summary>
    /// <remarks>
    /// <b>It moves. It does not teleport and it does not end the mission.</b> The brief
    /// insists aborting takes real steps, because an abort that cost nothing would make
    /// every firefight a decision to reset rather than a decision to survive. Everything
    /// after arrival — whether the mission is over, who is still in the building — is the
    /// objective system's business, not this one's.
    /// </remarks>
    private static TacticalOrder? Abort(TacticalState state, TacticalActor actor)
    {
        SiteRoomId? nearest = NearestOf(state, actor, state.Layout.ExtractionRoomIds);

        return nearest is null
            ? null
            : Advance(state, actor, nearest.Value, MoveRun);
    }

    // ---- shared helpers -------------------------------------------------------

    /// <summary>The squadmate with the most urgent condition, excluding the caller.</summary>
    /// <remarks>
    /// A downed body outranks a merely hurt one at equal health, because a Mule's time
    /// spent walking to somebody at ninety is time not spent walking to somebody at five.
    /// </remarks>
    private static TacticalActor? WorstAlly(TacticalState state, TacticalActor actor)
    {
        TacticalActor? worst = null;

        foreach (TacticalActor ally in state.Squad)
        {
            if (ally.AgentId == actor.AgentId)
                continue;

            if (ally.Condition == ActorCondition.Active && ally.Health >= FullHealth)
                continue;

            if (worst is null || Severity(ally) < Severity(worst))
                worst = ally;
        }

        return worst;
    }

    /// <summary>
    /// Ranks how urgent somebody's condition is, lower being more urgent.
    /// </summary>
    /// <remarks>
    /// Structural rather than tuned: it answers "which of these two first", and no
    /// number in it is a designer choice.
    /// </remarks>
    private static int Severity(TacticalActor actor)
        => (actor.Condition == ActorCondition.Downed ? -1000 : 0) + actor.Health;

    /// <summary>True when two actors are close enough for a contact action.</summary>
    private static bool Adjacent(TacticalState state, TacticalActor actor, TacticalActor other)
        => actor.Position.FloorIndex == other.Position.FloorIndex
           && Fixed32.Distance(actor.Position.X, other.Position.X).Raw <= ReachCm;

    /// <summary>
    /// Where a regrouping member goes when the order named no point.
    /// </summary>
    /// <remarks>
    /// The controlled agent, and failing that the lowest-ided squadmate. Using the
    /// controlled agent means a regroup order issued while the player is driving somebody
    /// else still sends the group to the person actually being watched.
    /// </remarks>
    private static TacticalActor? AnchorOf(TacticalState state, TacticalActor actor)
    {
        TacticalActor? first = null;

        foreach (TacticalActor ally in state.Squad)
        {
            if (ally.AgentId == actor.AgentId)
                continue;

            if (first is null || ally.Id.Value < first.Id.Value)
                first = ally;
        }

        return first;
    }

    /// <summary>The room an actor is standing in, or the entrance when off-map.</summary>
    private static SiteRoomId RoomOf(TacticalState state, TacticalActor actor)
        => state.RoomOf(actor)?.Id ?? state.Layout.EntranceRoomId;

    /// <summary>The room on the far side of a connection from the actor.</summary>
    private static SiteRoomId RoomIdAt(TacticalState state, TacticalActor actor, SiteConnectionId id)
    {
        SiteConnection? connection = state.Layout.FindConnectionFor(id);

        return connection is null
            ? RoomOf(state, actor)
            : connection.Other(RoomOf(state, actor));
    }

    /// <summary>The connection in this room the pathfinder would open first.</summary>
    /// <remarks>
    /// Only connections touching the actor's own room qualify, and the cheapest wins.
    /// Routed with <see cref="PathOptions.ForAgent"/>: an agent may use a crawlspace
    /// where a guard could not, so the squad is allowed to take the vent a guard would
    /// have to walk around. This comment said so for a while before the code agreed with
    /// it.
    /// </remarks>
    private static SiteConnection? NearestDoor(TacticalState state, TacticalActor actor)
    {
        SiteRoomId here = RoomOf(state, actor);
        SiteConnection? best = null;
        int bestSteps = int.MaxValue;

        foreach (SiteConnection connection in state.Layout.Connections)
        {
            if (connection.RoomA != here && connection.RoomB != here)
                continue;

            int steps = Pathfinder.ConnectionSteps(
                connection, state.Layout, state.Doors, PathOptions.ForAgent);

            if (steps >= bestSteps)
                continue;

            bestSteps = steps;
            best = connection;
        }

        return best;
    }

    /// <summary>
    /// The nearest interactable of a kind, in pathfinder steps.
    /// </summary>
    /// <remarks>
    /// Ties broken by interactable id rather than by generation order, so two runs of the
    /// same building pick the same container — which is what lets a recorded mission
    /// replay.
    /// </remarks>
    private static SiteInteractable? NearestOf(
        TacticalState state, TacticalActor actor, InteractableKind kind)
    {
        SiteInteractable? best = null;
        int bestSteps = int.MaxValue;
        SiteRoomId here = RoomOf(state, actor);

        foreach (SiteInteractable interactable in state.Layout.Interactables)
        {
            if (interactable.Kind != kind)
                continue;

            int steps = RoomDistance(state, here, interactable.RoomId);

            if (steps > bestSteps)
                continue;

            if (steps == bestSteps && best is not null && interactable.Id >= best.Id)
                continue;

            bestSteps = steps;
            best = interactable;
        }

        return best;
    }

    /// <summary>The nearest of a set of rooms, in pathfinder steps.</summary>
    private static SiteRoomId? NearestOf(
        TacticalState state, TacticalActor actor, IReadOnlyList<SiteRoomId> rooms)
    {
        SiteRoomId? best = null;
        int bestSteps = int.MaxValue;
        SiteRoomId here = RoomOf(state, actor);

        foreach (SiteRoomId room in rooms)
        {
            int steps = RoomDistance(state, here, room);

            if (steps > bestSteps)
                continue;

            bestSteps = steps;
            best = room;
        }

        return best;
    }

    /// <summary>Steps between two rooms as the pathfinder costs them.</summary>
    private static int RoomDistance(TacticalState state, SiteRoomId from, SiteRoomId to)
    {
        if (from == to)
            return 0;

        return Pathfinder.FindRoute(
            state.Layout, state.Doors, from, to, PathOptions.ForAgent).TotalSteps;
    }

    /// <summary>How far apart two things must be before a contact action is legal.</summary>
    /// <remarks>
    /// Reuses <c>squad_order_regroup_radius_cm</c> rather than introducing a second
    /// "how close is close" number. The two questions have the same answer in this game
    /// — a room's width — and a designer tuning one and not the other would be tuning
    /// two different definitions of contact by accident.
    /// </remarks>
    private static int ReachCm
        => SimulationRules.Squad("squad_order_regroup_radius_cm", DefaultRegroupRadiusCm);

    /// <summary>Gadget families that restore the user.</summary>
    private const int GadgetEffectRestore = 1;

    /// <summary>Gadget families that work on a thing in the world.</summary>
    private const int GadgetEffectTechnical = 2;

    /// <summary>
    /// Maps <c>gadget.effect_type</c> onto the family the UseGadget order can act on.
    /// </summary>
    /// <remarks>
    /// An effect the table does not describe maps to zero and the order is refused,
    /// which spends no use. Spending a stimulant on nothing because a designer renamed an
    /// effect string is a worse bug than a gadget that quietly does nothing, and
    /// <c>TableValidator</c> checks the vocabulary so the rename is caught at build time.
    /// </remarks>
    private static int GadgetEffect(int gadgetId)
    {
        ProjectSpy.Tables.Gadget? gadget = SimulationRules.GadgetFor(gadgetId);

        if (gadget is null)
            return 0;

        return gadget.EffectType switch
        {
            "StaminaRestore" or "MentalRestore" => GadgetEffectRestore,
            "RevealBonus" or "IntelBonus" or "InfiltrationBonus" => GadgetEffectTechnical,
            _ => 0,
        };
    }

    /// <summary>Follow distance, absent the table.</summary>
    private const int DefaultFollowStandoffCm = 150;

    /// <summary>Stacking offset, absent the table.</summary>
    private const int DefaultStackOffsetCm = 60;

    /// <summary>Regroup radius, absent the table.</summary>
    private const int DefaultRegroupRadiusCm = 600;

    /// <summary>How far an Overwatch member can report a contact, absent the table.</summary>
    private const int DefaultOverwatchReportCm = 1800;
}