using ProjectSpy.Core.Missions;
using ProjectSpy.Tables;

// The action table's bean, aliased so that every crossing between the compiled
// table and Core is visible as one at a call site. Core deliberately has no type
// of its own for an action: the table row *is* the definition, and mirroring its
// columns would be a second place for a designer edit to have to reach.
using TacticalActionRow = ProjectSpy.Tables.TacticalAction;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Movement along a floor's line, and traversal between rooms.
/// </summary>
/// <remarks>
/// <para>
/// Rule 15 is the whole specification: movement is one-dimensional per floor, crossing
/// between floors happens only through a connection, and both are interval maths over
/// centimetres. There is no pathfinding done here — <see cref="Pathfinder"/> answers
/// "how" and this answers "one step at a time", which keeps the two from disagreeing
/// about where somebody is allowed to be.
/// </para>
/// <para>
/// <b>Speed is a distance per step, not a rate.</b> Every step an actor in flight
/// covers <see cref="PostureRules.SpeedPerStep"/> centimetres and makes the step noise
/// of its posture, so moving is simultaneously a position change and a recurring sound.
/// Emitting a noise per step rather than per arrival is the difference between a guard
/// hearing you cross a corridor and hearing you only once you stopped, and it is the
/// whole reason slow postures exist.
/// </para>
/// <para>
/// <b>Nothing here decides intent.</b> This resolves what an actor in flight does; it
/// never starts a movement or picks a destination. Deciding is
/// <see cref="ActionSystem.TryBegin"/> and, for the NPCs, the planner — which means a
/// movement bug is a bug in the arithmetic and not in a decision that was never made.
/// </para>
/// </remarks>
public static class MovementSystem
{
    /// <summary>The movement rows, by id.</summary>
    /// <summary>Walking, at 24cm a step.</summary>
    private const int MoveWalk = 12401;

    /// <summary>Running, at 48cm a step and the loudest gait.</summary>
    private const int MoveRun = 12402;

    /// <summary>Crouch-walking, at 12cm a step and the quietest useful one.</summary>
    private const int MoveCrouch = 12403;

    /// <summary>Crawling. Slower still, and no quieter.</summary>
    private const int MoveProne = 12404;

    private static readonly int[] MoveActionIds = { MoveWalk, MoveRun, MoveCrouch, MoveProne };

    /// <summary>True when an action id is one of the four movement rows.</summary>
    public static bool IsMoveAction(int actionId) => Array.IndexOf(MoveActionIds, actionId) >= 0;

    /// <summary>
    /// Starts a movement towards a point on the actor's own floor.
    /// </summary>
    /// <remarks>
    /// The distance is measured against the actor's room rather than against the whole
    /// floor, and is clamped to the room: walking at a point past the end of the
    /// corridor should stop at the end of the corridor, not send somebody off the edge
    /// of the generated span. An order that asks for no distance at all is refused
    /// rather than silently succeeding, because "you have arrived" is not the right
    /// answer to "walk to the wall".
    /// </remarks>
    public static bool TryBeginMove(
        TacticalState state,
        TacticalActor actor,
        TacticalOrder order,
        TacticalActionRow row,
        out TacticalOrderReason refusal)
    {
        SiteRoom? room = state.RoomOf(actor);

        if (room is null)
        {
            refusal = TacticalOrderReason.OutOfReach;
            return false;
        }

        // The order may name a point on another floor; movement does not cross floors,
        // and silently walking to the same x on the wrong level would be a way worse
        // failure than a refusal.
        if (order.Target.FloorIndex != actor.Position.FloorIndex)
        {
            refusal = TacticalOrderReason.OutOfReach;
            return false;
        }

        Fixed32 clamped = Fixed32.Clamp(order.Target.X, room.StartX, room.LastX);
        int distanceCm = Fixed32.Distance(actor.Position.X, clamped).Raw;

        if (distanceCm <= 0)
        {
            refusal = TacticalOrderReason.OutOfReach;
            return false;
        }

        // Facing follows the direction of travel. An actor that turns as it moves is what
        // makes a guard's cone meaningful: somebody who sprints past behind them is not
        // seen by a facing that never changes.
        if (clamped.Raw > actor.Position.X.Raw)
            actor.Facing = Facing.Right;
        else if (clamped.Raw < actor.Position.X.Raw)
            actor.Facing = Facing.Left;

        // The verb decides the posture, and the posture decides the speed and the noise.
        // Without this the actor kept whatever posture deployment gave them for the whole
        // mission, so a run order travelled at crouch speed and made a crouch noise: the
        // order was accepted, logged, priced from the table, and then had no effect on
        // how the movement actually felt. MovementSystem reads both of those from
        // actor.Posture and from nowhere else, so this is the only place that can carry
        // the choice across.
        if (TryPostureFor(row.Id, out Posture posture))
            actor.Posture = posture;

        actor.Action = new PendingAction
        {
            Kind = TacticalActionKind.Move,
            ActionId = row.Id,
            TotalSteps = row.StepsCost,
            Target = new TacticalPosition(actor.Position.FloorIndex, clamped),
            RemainingCm = distanceCm,
            DirectionSign = clamped.Raw > actor.Position.X.Raw ? 1 : -1,
        };

        refusal = TacticalOrderReason.None;
        return true;
    }

    /// <summary>
    /// The posture a movement verb asks for, if it is a movement verb at all.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="PostureRules.MoveActionId"/>, and kept beside the other
    /// movement arithmetic rather than in <see cref="PostureRules"/> so that the two
    /// cannot disagree: a round trip through a second switch would eventually gain a case
    /// the forward direction does not have.
    /// </remarks>
    private static bool TryPostureFor(int actionId, out Posture posture)
    {
        switch (actionId)
        {
            case MoveProne:
                posture = Posture.Prone;
                return true;

            case MoveCrouch:
                posture = Posture.Crouch;
                return true;

            case MoveWalk:
                posture = Posture.Walk;
                return true;

            case MoveRun:
                posture = Posture.Run;
                return true;

            default:
                posture = Posture.Crouch;
                return false;
        }
    }

    /// <summary>
    /// Advances every actor that is moving, one step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the noises made this step rather than adding them itself, so that the
    /// pipeline has one place where noises enter the state — which is what makes the
    /// canonical order meaningful. A movement that appended to
    /// <see cref="TacticalState.NoiseInFlight"/> from two different phases would
    /// produce a different propagation order depending on which ran first.
    /// </para>
    /// <para>
    /// A carrier moves at half speed. Carrying somebody is a real cost rather than a
    /// cosmetic one, and half is the number that makes "go and fetch them" a decision
    /// the player has to budget time for.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<NoiseEvent> Advance(TacticalState state)
    {
        var noises = new List<NoiseEvent>();

        foreach (TacticalActor actor in state.SortedActors)
        {
            PendingAction? pending = actor.Action;

            if (pending is null || pending.Kind != TacticalActionKind.Move)
                continue;

            if (!actor.CanAct)
                continue;

            int speed = SpeedFor(actor, pending);
            int travelled = Fixed32.Min(new Fixed32(speed), new Fixed32(pending.RemainingCm)).Raw;

            if (travelled <= 0)
                continue;

            pending.RemainingCm -= travelled;
            pending.StepsSpent++;

            // Signed, because a floor runs in both directions. Stepping by a positive
            // distance regardless of where the actor was headed walks every leftward
            // order to the right, off the end of the room and then off the end of the
            // floor — which is exactly what the sweep caught it doing.
            int stepCm = travelled * pending.DirectionSign;

            actor.Position = new TacticalPosition(actor.Position.FloorIndex, actor.Position.X + new Fixed32(stepCm));

            // Every step of movement is audible in the posture's own voice. This is the
            // mechanic the whole posture table exists to serve.
            int profileId = PostureRules.StepNoiseProfileId(actor.Posture);

            var noise = new NoiseEvent(
                actor.Position,
                new Fixed32(NoiseSystem.BaseRadiusCm(profileId)),
                profileId,
                state.Step)
            {
                SourceActorId = actor.Id.Value,
                SourceKey = "noise.step." + actor.Posture.ToString().ToLowerInvariant(),
            };

            state.NoiseInFlight.Add(noise);
            noises.Add(noise);

            if (actor.IsCarried)
                continue;

            if (pending.RemainingCm <= 0)
            {
                actor.Position = pending.Target;
                actor.Action = null;
            }
        }

        return noises;
    }

    /// <summary>
    /// Moves an actor across a connection they finished traversing.
    /// </summary>
    /// <remarks>
    /// The landing point is the connection's own <c>X</c> or <c>UpperX</c>, not a
    /// position the caller chose: a stairwell lands where the stairwell lands, and
    /// letting a traversal put somebody somewhere arbitrary is how an actor ends up
    /// inside a wall.
    /// </remarks>
    public static void CompleteTraversal(TacticalState state, TacticalActor actor, PendingAction pending)
    {
        SiteConnection? connection = state.Layout.FindConnectionFor(pending.ConnectionId);
        if (connection is null)
            return;

        SiteRoom? here = state.RoomOf(actor);
        if (here is null)
            return;

        SiteRoom? across = state.Layout.RoomAcross(connection, here.Id);
        if (across is null)
            return;

        Fixed32 landing = here.Id == connection.RoomA ? connection.UpperX : connection.X;
        Fixed32 clamped = Fixed32.Clamp(landing, across.StartX, across.LastX);

        actor.Position = new TacticalPosition(across.FloorIndex, clamped);
        state.Record("log.traversal.completed", pending.ConnectionId.Value);
    }

    /// <summary>
    /// Steps an actor would need to walk <paramref name="distanceCm"/> in a posture.
    /// </summary>
    /// <remarks>
    /// Rounds up, like the pathfinder. The two must agree: a route costed by the
    /// pathfinder and then walked by this one that disagreed by a step would leave the
    /// team arriving early or late depending on which system had the last word.
    /// </remarks>
    public static int StepsToCover(int distanceCm, Posture posture)
    {
        if (distanceCm <= 0)
            return 0;

        int speed = PostureRules.SpeedPerStep(posture).Raw;
        return speed <= 0 ? distanceCm : (int)(((long)distanceCm + speed - 1) / speed);
    }

    /// <summary>
    /// Centimetres covered per step by an actor, after carrying and injury.
    /// </summary>
    /// <remarks>
    /// Carrying halves it. Wounded actors are deliberately <em>not</em> slowed here:
    /// health affects whether an actor can act, not how fast they cover ground, and
    /// adding a second penalty would make a badly hurt agent unable to leave a room at
    /// all, which is a death sentence disguised as a modifier.
    /// </remarks>
    private static int SpeedFor(TacticalActor actor, PendingAction pending)
    {
        int speed = PostureRules.SpeedPerStep(actor.Posture).Raw;

        if (actor.Carrying.IsValid)
            speed /= 2;

        return speed <= 0 ? 1 : speed;
    }

    /// <summary>
    /// Walks an actor to a room's door, ready to cross, and returns how many steps it
    /// took to get there.
    /// </summary>
    /// <remarks>
    /// Used by the NPC planner, which needs to send a guard through a door without
    /// inventing a position for them. Returns zero when the actor is already at the
    /// door.
    /// </remarks>
    public static int StepToDoor(SiteLayout layout, TacticalActor actor, SiteConnection connection)
    {
        SiteRoom? room = layout.RoomContaining(actor.Position);
        if (room is null)
            return 0;

        Fixed32 doorX = room.Id == connection.RoomA ? connection.X : connection.UpperX;
        int distance = Fixed32.Distance(actor.Position.X, doorX).Raw;

        if (distance <= 0)
            return 0;

        int steps = StepsToCover(distance, actor.Posture);
        actor.Position = new TacticalPosition(actor.Position.FloorIndex, doorX);

        return steps;
    }
}
