using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Turns planned GOAP actions into orders the tactical layer can actually carry out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The planner decides, this type does.</b> A <see cref="GoapPlan"/> is a list of
/// <c>goap_action</c> ids and means nothing on its own: "investigate this noise" has to
/// become a movement order towards a position on this floor, and "raise the alarm" has to
/// become a change to the site alarm. Keeping that translation here rather than inside
/// the planner is what lets the planner be tested without a building, and what lets the
/// tactical movement code stay ignorant of GOAP.
/// </para>
/// <para>
/// <b>Actions that need a target it cannot find are skipped, not failed.</b> An NPC whose
/// plan says "walk to the noise" and whose noise has since been resolved by somebody else
/// should do something else, not stand still and not consume its plan. Skipping advances
/// the plan and asks for a replan, so the NPC recovers on the next budgeted search
/// instead of being stuck.
/// </para>
/// <para>
/// <b>No randomness.</b> Every order here is a pure function of the mission state, so
/// two runs of the same mission issue the same orders in the same order. That is what
/// lets the replays rule 6 requires actually replay.
/// </para>
/// </remarks>
public static class GoapExecutor
{
    /// <summary>
    /// Issues the order for an NPC's next planned action.
    /// </summary>
    /// <param name="state">The mission.</param>
    /// <param name="agent">The NPC to act.</param>
    /// <param name="step">The step being resolved.</param>
    /// <param name="rng">The planning stream. Unused today; passed so that any future
    /// action needing a roll gets a named stream rather than reaching for a shared one.</param>
    /// <returns>True when an order was issued.</returns>
    public static bool ExecuteNext(TacticalState state, GoapAgent agent, long step, IRng rng)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        if (!agent.Plan.HasActions)
            return false;

        int actionId = agent.Plan.NextActionId;
        GoapActionDef? action = GoapCatalog.Load().Action(actionId);

        if (action is null)
        {
            // The plan names an action the catalog does not hold. Cannot happen from a
            // catalog-built plan, but skipping is strictly better than throwing mid-step.
            agent.AdvancePlan(step);
            return false;
        }

        TacticalOrder? order = OrderFor(state, agent, action, step);

        if (order is not null && ActionSystem.TryBegin(state, order, rng, step, out _))
        {
            agent.AdvancePlan(step);
            return true;
        }

        // The order could not be started. Advance past it and ask for a replan rather
        // than retrying the same impossible order every step, which is how an NPC ends
        // up permanently stuck on one action.
        agent.AdvancePlan(step);
        agent.Invalidate(GoapInvalidationReason.ActionCompleted, step);
        return false;
    }

    /// <summary>
    /// The order a GOAP action translates into, or null when it needs none.
    /// </summary>
    /// <remarks>
    /// The switch is on <em>what the action is for</em>, read from its id rather than
    /// from its localization key. Ids are stable data; name keys are a localization
    /// concern that a translator can change, and behaviour keyed off a translatable
    /// string would break the first time the Thai column disagreed with the English one.
    /// </remarks>
    private static TacticalOrder? OrderFor(TacticalState state, GoapAgent agent, GoapActionDef action, long step)
    {
        TacticalActor actor = agent.Actor;

        return action.Id switch
        {
            InvestigatePointActionId => MoveTowards(agent, PickNoiseOrigin(agent), step, MoveCrouch),
            ChaseContactActionId => MoveTowards(agent, PickLastSeen(agent), step, MoveRun),
            WalkPatrolRouteActionId => NextPatrolLeg(state, actor),
            StandPostActionId => HoldPost(state, actor),
            FleeActionId or CivilianFleeActionId => MoveTowards(agent, NearestExit(state, actor), step, MoveRun),
            CivilianReportActionId => MoveTowards(agent, NearestGuard(state, actor), step, MoveRun),
            ReportMissingActionId => MoveTowards(agent, NearestGuard(state, actor, exclude: actor), step, MoveWalk),
            CallBackupActionId => Transmit(state, agent),
            RaiseAlarmActionId => RaiseAlarm(state, agent),
            SearchBodyActionId => SearchBody(state, agent),
            TakeCoverActionId => TakeCover(state, actor),
            CloseDoorActionId => CloseSuspiciousDoor(state, actor),
            _ => null,
        };
    }

    /// <summary>The key localization-only action: a guard going to look at a noise.</summary>
    private const int InvestigatePointActionId = 12153;

    /// <summary>Chasing a contact.</summary>
    private const int ChaseContactActionId = 12154;

    /// <summary>Walking the patrol route.</summary>
    private const int WalkPatrolRouteActionId = 12151;

    /// <summary>Holding a sentry post.</summary>
    private const int StandPostActionId = 12152;

    /// <summary>Fleeing danger.</summary>
    private const int FleeActionId = 12159;

    /// <summary>A civilian running for safety.</summary>
    private const int CivilianFleeActionId = 12167;

    /// <summary>A civilian running to a guard to report.</summary>
    private const int CivilianReportActionId = 12169;

    /// <summary>A guard reporting a colleague missing.</summary>
    private const int ReportMissingActionId = 12164;

    /// <summary>Calling for backup.</summary>
    private const int CallBackupActionId = 12156;

    /// <summary>Raising the alarm.</summary>
    private const int RaiseAlarmActionId = 12157;

    /// <summary>Searching a body.</summary>
    private const int SearchBodyActionId = 12172;

    /// <summary>Taking cover.</summary>
    private const int TakeCoverActionId = 12158;

    /// <summary>Closing an open door.</summary>
    private const int CloseDoorActionId = 12163;

    // ---- the walking rows ----------------------------------------------------

    /// <summary>Walking pace.</summary>
    private const int MoveWalk = 12401;

    /// <summary>Running pace.</summary>
    private const int MoveRun = 12402;

    /// <summary>Creeping pace, for going quietly to look at something.</summary>
    private const int MoveCrouch = 12403;

    /// <summary>A movement order to a point, or null when there is nowhere to go.</summary>
    private static TacticalOrder? MoveTowards(GoapAgent agent, TacticalPosition where, long step, int actionId)
    {
        if (!where.IsValid)
            return null;

        return new TacticalOrder(agent.Actor.Id, actionId, Target: where);
    }

    /// <summary>Where this NPC last heard the noise, if it is somewhere it can stand.</summary>
    private static TacticalPosition PickNoiseOrigin(GoapAgent agent)
    {
        TacticalPosition origin = agent.Actor.Memory.LastNoiseOrigin;
        return origin.FloorIndex == agent.Actor.Position.FloorIndex ? origin : TacticalPosition.None;
    }

    /// <summary>Where this NPC last saw the team, if it is somewhere it can stand.</summary>
    private static TacticalPosition PickLastSeen(GoapAgent agent)
    {
        TacticalPosition seen = agent.Actor.Memory.LastSeen;
        return seen.FloorIndex == agent.Actor.Position.FloorIndex ? seen : TacticalPosition.None;
    }

    /// <summary>
    /// An order to walk to the next room on this guard's own route.
    /// </summary>
    /// <remarks>
    /// Reuses the stage-4c planner's reasoning rather than re-deriving it: route to the
    /// door rather than the far wall of the next room, so a patrol reads as a patrol
    /// rather than as aimless drifting through the middle of every corridor.
    /// </remarks>
    private static TacticalOrder? NextPatrolLeg(TacticalState state, TacticalActor actor)
    {
        if (actor.RouteIndex < 0)
            return null;

        SiteGuard? guard = FindGuard(state, actor);

        if (guard is null || guard.PatrolRoute.Count == 0)
            return null;

        SiteRoom? here = state.RoomOf(actor);

        if (here is null)
            return null;

        SiteRoomId next = guard.PatrolRoute[actor.RouteIndex % guard.PatrolRoute.Count];

        if (next == here.Id)
            return null;

        SiteConnection? connection = state.Layout.FindConnection(here.Id, next);

        if (connection is null)
            return null;

        Fixed32 doorX = here.Id == connection.RoomA ? connection.X : connection.UpperX;
        return new TacticalOrder(actor.Id, MoveWalk, Target: new TacticalPosition(here.FloorIndex, doorX));
    }

    /// <summary>
    /// A sentry standing its post: an order to hold where it is.
    /// </summary>
    /// <remarks>
    /// Returns null, which the caller reads as "no order needed". Standing still is not
    /// an action in this system — issuing a move of zero centimetres every step would
    /// fill the order log with noise and give the movement phase work for no reason. The
    /// sentry is doing nothing because nothing is happening, which is correct.
    /// </remarks>
    private static TacticalOrder? HoldPost(TacticalState state, TacticalActor actor) => null;

    /// <summary>The middle of the nearest extraction room, on this actor's floor.</summary>
    private static TacticalPosition NearestExit(TacticalState state, TacticalActor actor)
    {
        SiteRoom? here = state.RoomOf(actor);

        if (here is null)
            return TacticalPosition.None;

        SiteRoomId best = SiteRoomId.None;
        int bestSteps = int.MaxValue;

        foreach (SiteRoomId candidate in state.Layout.ExtractionRoomIds)
        {
            SiteRoom? room = state.Layout.Find(candidate);

            if (room is null || room.FloorIndex != actor.Position.FloorIndex)
                continue;

            TacticalPath path = Pathfinder.FindRoute(state.Layout, state.Doors, here.Id, candidate);

            if (!path.ReachedTarget || path.TotalSteps >= bestSteps)
                continue;

            bestSteps = path.TotalSteps;
            best = candidate;
        }

        if (!best.IsValid)
            return TacticalPosition.None;

        SiteRoom? chosen = state.Layout.Find(best);

        return chosen is null
            ? TacticalPosition.None
            : new TacticalPosition(chosen.FloorIndex, (chosen.StartX + chosen.EndX) / 2);
    }

    /// <summary>The nearest guard on this floor, for a report to go to.</summary>
    /// <remarks>
    /// Nearest by absolute centimetres on the same floor, not by path cost: this runs
    /// once per report and the difference only matters for a guard on another floor,
    /// which is excluded anyway. The requirement that a panicked civilian must reach
    /// <em>a</em> guard is what matters, and the cheapest way to satisfy it is to walk at
    /// the closest one.
    /// </remarks>
    private static TacticalPosition NearestGuard(TacticalState state, TacticalActor actor, TacticalActor? exclude = null)
    {
        TacticalActor? best = null;
        int bestDistance = int.MaxValue;

        foreach (TacticalActor other in state.SortedActors)
        {
            if (!other.IsGuard || other.Id == actor.Id || !other.CanAct)
                continue;

            if (exclude is not null && other.Id == exclude.Id)
                continue;

            if (other.Position.FloorIndex != actor.Position.FloorIndex)
                continue;

            int distance = (other.Position.X - actor.Position.X).Abs().Raw;

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = other;
        }

        return best is null ? TacticalPosition.None : best.Position;
    }

    /// <summary>
    /// Calling for backup: tells the site an incident is happening.
    /// </summary>
    /// <remarks>
    /// Deliberately weaker than raising the alarm. <c>goap_goal.call_for_backup</c> and
    /// <c>goap_goal.raise_alarm</c> are separate goals with separate priorities, and
    /// collapsing them would remove the choice a guard makes when they are not sure what
    /// they saw.
    /// </remarks>
    private static TacticalOrder? Transmit(TacticalState state, GoapAgent agent)
    {
        state.Alarm.Raise(BackupAlarmPoints());
        state.Record("log.goap.backup_called", agent.Actor.Id.Value);
        return null;
    }

    /// <summary>
    /// Raising the alarm: the site-wide jump a guard makes on finding a body.
    /// </summary>
    /// <remarks>
    /// The brief's third emergent behaviour. It goes straight through
    /// <see cref="AlarmState.Raise"/> rather than through a goal chain, because a guard
    /// who has just found a body does not weigh the alarm against closing a door — and
    /// because routing it through the planner would mean the alarm depended on a plan
    /// succeeding, so a building full of guards could fail to notice a corpse.
    /// </remarks>
    private static TacticalOrder? RaiseAlarm(TacticalState state, GoapAgent agent)
    {
        state.Alarm.Raise(AlarmPoints());
        state.Record("log.goap.alarm_raised", agent.Actor.Id.Value);
        return null;
    }

    /// <summary>
    /// Searching a body, which converts a discovery into a confirmed one.
    /// </summary>
    /// <remarks>
    /// The alarm has already jumped by the time this runs, because finding is what
    /// triggered the plan. Searching adds the evidence — it tells the site <em>that</em>
    /// somebody is dead rather than merely that something is wrong, which is why it is a
    /// separate action with its own cost and duration in the table.
    /// </remarks>
    private static TacticalOrder? SearchBody(TacticalState state, GoapAgent agent)
    {
        agent.Findings.BodyFound = false;
        state.Alarm.Raise(BodySearchAlarmPoints());
        state.Record("log.goap.body_searched", agent.Actor.Id.Value);
        return null;
    }

    /// <summary>An order to the nearest cover this actor can reach in its own room.</summary>
    private static TacticalOrder? TakeCover(TacticalState state, TacticalActor actor)
    {
        SiteRoom? room = state.RoomOf(actor);

        if (room is null)
            return null;

        Fixed32 nearest = actor.Position.X;
        bool found = false;

        foreach (SiteOccluder occluder in state.Layout.Occluders)
        {
            if (occluder.RoomId != room.Id)
                continue;

            // Duck behind the occluder rather than onto it, so the guard ends up on the
            // side that is not visible and not standing inside a filing cabinet.
            Fixed32 behind = occluder.X + new Fixed32(CoverOffsetCm);

            if (!room.Contains(behind))
                continue;

            if (found && (behind - actor.Position.X).Abs() >= (nearest - actor.Position.X).Abs())
                continue;

            nearest = behind;
            found = true;
        }

        if (!found)
            return null;

        return new TacticalOrder(actor.Id, MoveRun, Target: new TacticalPosition(room.FloorIndex, nearest));
    }

    /// <summary>How far behind an occluder to stand. Structural: half a body-width.</summary>
    private const int CoverOffsetCm = 40;

    /// <summary>
    /// An order to shut an open door on this guard's own route.
    /// </summary>
    /// <remarks>
    /// Scoped to the actor's own room for the same reason the door is only suspicious
    /// when the route passes it: a guard must not cross the building to close a door
    /// behind somebody else's patrol.
    /// </remarks>
    private static TacticalOrder? CloseSuspiciousDoor(TacticalState state, TacticalActor actor)
    {
        SiteRoom? room = state.RoomOf(actor);

        if (room is null)
            return null;

        foreach (SiteConnection connection in state.Layout.ConnectionsAt(room.Id))
        {
            if (connection.Kind is not (SiteConnectionKind.Door or SiteConnectionKind.LockedDoor))
                continue;

            if (state.StateOf(connection.Id) != ConnectionState.Open)
                continue;

            if (!GoapRouteRules.WalksRoute(state.Layout, actor.GuardId, actor.RouteIndex, connection))
                continue;

            Fixed32 x = room.Id == connection.RoomA ? connection.X : connection.UpperX;
            return new TacticalOrder(actor.Id, CloseDoorActionId, ConnectionId: connection.Id,
                Target: new TacticalPosition(room.FloorIndex, x));
        }

        return null;
    }

    /// <summary>The generated guard behind an actor, or null.</summary>
    private static SiteGuard? FindGuard(TacticalState state, TacticalActor actor)
    {
        foreach (SiteGuard guard in state.Layout.Guards)
        {
            if (guard.Id == actor.GuardId)
                return guard;
        }

        return null;
    }

    /// <summary>Alarm points a call for backup is worth.</summary>
    private static int BackupAlarmPoints()
        => SimulationRules.PercentOf(AlarmState.Max, Math.Max(0, SimulationRules.Goap("goap_radio_alarm_percent", 45)) / 2);

    /// <summary>Alarm points raising the alarm is worth.</summary>
    private static int AlarmPoints() => SimulationRules.PercentOf(AlarmState.Max, 100);

    /// <summary>Alarm points searching a body adds.</summary>
    private static int BodySearchAlarmPoints() => SimulationRules.PercentOf(AlarmState.Max, 100) / 2;
}

/// <summary>
/// The route question both the director and the executor need to ask.
/// </summary>
/// <remarks>
/// Extracted because "does this guard's own patrol pass through this door?" is asked once
/// when deciding whether a door is suspicious and again when deciding whether to close
/// it. Two copies of that answer would be free to disagree, and a guard that judged a
/// door its business and then refused to walk to it would be a bug nobody could see.
/// </remarks>
public static class GoapRouteRules
{
    /// <summary>
    /// Whether this guard's patrol route includes this connection as a leg.
    /// </summary>
    /// <param name="layout">The building.</param>
    /// <param name="guardId">Which guard.</param>
    /// <param name="routeIndex">Where it currently is on the route.</param>
    /// <param name="connection">The connection to test.</param>
    public static bool WalksRoute(SiteLayout layout, SiteGuardId guardId, int routeIndex, SiteConnection connection)
    {
        if (routeIndex < 0)
            return false;

        foreach (SiteGuard guard in layout.Guards)
        {
            if (guard.Id != guardId)
                continue;

            if (guard.PatrolRoute.Count == 0)
                return false;

            // Every leg, not just the current one. A guard is responsible for the whole
            // loop it walks, because it will be back round it shortly and should not
            // decide then that the door it passed was none of its business.
            for (int i = 0; i < guard.PatrolRoute.Count; i++)
            {
                SiteRoomId from = guard.PatrolRoute[i];
                SiteRoomId to = guard.PatrolRoute[(i + 1) % guard.PatrolRoute.Count];

                if ((from == connection.RoomA && to == connection.RoomB)
                    || (from == connection.RoomB && to == connection.RoomA))
                    return true;
            }

            return false;
        }

        return false;
    }
}