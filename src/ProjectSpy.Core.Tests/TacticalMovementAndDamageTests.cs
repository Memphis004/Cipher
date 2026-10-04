using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Movement direction, room edges, and what happens when you hit somebody twice.
/// </summary>
/// <remarks>
/// <para>
/// Every test here is a bug the 5,000-mission sweep found and this file pins down. The
/// sweep is the thing that finds them — it runs every pipeline over every site template
/// and asserts only that nothing broke — but it takes six minutes, so a regression it
/// catches would not be caught again for six minutes. These are the fast versions.
/// </para>
/// <para>
/// All three were invisible to inspection. Movement had a direction, encoded twice, in
/// two places that disagreed. Clamping used a bound that was correct in every case
/// except the one it existed to handle. And the corpse arithmetic looked fine right up
/// until the second hit.
/// </para>
/// </remarks>
public class TacticalMovementAndDamageTests
{
    /// <summary><c>action.move_walk</c>.</summary>
    private const int MoveWalk = 12401;

    /// <summary><c>action.combat_melee</c>, the non-lethal row.</summary>
    private const int Melee = 12421;

    [Fact]
    public void AWalkOrderToTheLeftMovesTheActorLeft()
    {
        // The sweep's first finding: an agent asked to walk to a point behind them walked
        // forwards instead, because the movement step added its distance to X and never
        // looked at where the actor was going. Left as the direction of travel, it is a
        // bug that only shows on a leftward order.
        (TacticalMissionRunner runner, TacticalActor agent, SiteRoom room) = Fresh();

        int from = agent.Position.X.Raw;
        int to = room.StartX.Raw + 10;

        Assert.True(to < from,
            $"This fixture needs an agent with room to its left; it is at {from} in [{room.StartX.Raw},{room.LastX.Raw}].");

        Order(runner, agent, new TacticalOrder(
            agent.Id, MoveWalk, Target: new TacticalPosition(agent.Position.FloorIndex, new Fixed32(to))));

        Assert.True(
            agent.Position.X.Raw < from,
            $"Agent walked right to {agent.Position.X.Raw} after being ordered left to {to} from {from}.");
    }

    [Fact]
    public void AWalkOrderToTheRightMovesTheActorRight()
    {
        // The mirror of the test above, because "movement works" is only true if both
        // directions work, and the bug only ever broke one of them.
        (TacticalMissionRunner runner, TacticalActor agent, SiteRoom room) = Fresh();

        int from = agent.Position.X.Raw;
        int to = room.LastX.Raw - 10;

        Assert.True(to > from,
            $"This fixture needs an agent with room to its right; it is at {from} in [{room.StartX.Raw},{room.LastX.Raw}].");

        Order(runner, agent, new TacticalOrder(
            agent.Id, MoveWalk, Target: new TacticalPosition(agent.Position.FloorIndex, new Fixed32(to))));

        Assert.True(
            agent.Position.X.Raw > from,
            $"Agent walked left to {agent.Position.X.Raw} after being ordered right to {to} from {from}.");
    }

    [Fact]
    public void AnActorNeverEndsAStepOutsideEveryRoom()
    {
        // `SiteRoom.Contains` is half-open, so the right edge of a room is the first
        // centimetre of the next one — or of nothing at all, at the end of a floor.
        // Clamping a walk to the room's `EndX` therefore put the actor exactly one
        // centimetre outside the building, which the mission's own validator then
        // reported as an actor off the map.
        for (ulong seed = 1; seed <= 25; seed++)
        {
            TacticalMissionRunner runner = TacticalHarness.Runner(seed);

            foreach (TacticalActor actor in runner.State.Squad)
            {
                SiteRoom room = TacticalHarness.RoomOf(runner.State, actor);

                // The far edge of the actor's own room: the worst case a clamped order
                // can produce.
                runner.State.PendingOrders.Add(new TacticalOrder(actor.Id, MoveWalk,
                    Target: new TacticalPosition(actor.Position.FloorIndex, room.EndX)));
            }

            // Checked every step rather than at the end, because the failure this is
            // about is a transient one: an actor steps outside the building, and by the
            // time the walk finishes something else has moved them back inside. A walk
            // at a crouch covers six centimetres a step, so a room four metres wide
            // takes most of seven hundred steps to cross — long enough to walk out of
            // the world and be rescued by the walk itself.
            for (int step = 1; step <= 1_200; step++)
            {
                runner.Step();

                Assert.True(runner.State.ValidateActors(out string problem),
                    $"Seed {seed} put an actor outside every room at step {step}: {problem}");
            }
        }
    }

    [Fact]
    public void HittingSomebodyWhoIsAlreadyDeadDoesNothing()
    {
        // The sweep's second finding. Damage was subtracted before the damage system was
        // asked whether the target could take it, and the damage system refuses a corpse —
        // so the refusal discarded the outcome but not the subtraction. A player
        // methodically working through the same guard walked that guard's health to
        // -660, which the validator correctly called impossible and which no amount of
        // reading the code had suggested.
        TacticalMissionRunner runner = TacticalHarness.Runner();

        TacticalActor agent = TacticalHarness.FirstAgent(runner.State);
        TacticalActor guard = TacticalHarness.FirstGuard(runner.State);

        guard.Health = 0;
        guard.Condition = ActorCondition.Dead;
        guard.BleedOutStepsRemaining = 0;

        for (int attempt = 0; attempt < 12; attempt++)
        {
            Order(runner, agent, new TacticalOrder(
                agent.Id, Melee, TargetActorId: guard.Id, Target: guard.Position));

            StepUntilSettled(runner, 100);
        }

        Assert.Equal(0, guard.Health);
        Assert.Equal(ActorCondition.Dead, guard.Condition);
        Assert.True(runner.State.ValidateActors(out string problem), $"A dead guard was left invalid: {problem}");
    }

    [Fact]
    public void HittingALiveGuardDoesReduceThemAndNeverBelowZero()
    {
        // The other half of the contract, so the fix above cannot be a "skip damage
        // entirely" that quietly made the squad harmless.
        TacticalMissionRunner runner = TacticalHarness.Runner();

        TacticalActor agent = TacticalHarness.FirstAgent(runner.State);
        TacticalActor guard = TacticalHarness.FirstGuard(runner.State);

        int before = guard.Health;

        Order(runner, agent, new TacticalOrder(
            agent.Id, Melee, TargetActorId: guard.Id, Target: guard.Position));

        StepUntilSettled(runner, 100);

        Assert.True(guard.Health < before, $"A melee attack left the guard at full health ({before}).");
        Assert.InRange(guard.Health, 0, guard.MaxHealth);
    }

    /// <summary>A runner over a fresh mission, plus its first agent and that agent's room.</summary>
    private static (TacticalMissionRunner Runner, TacticalActor Agent, SiteRoom Room) Fresh()
    {
        TacticalMissionRunner runner = TacticalHarness.Runner();
        TacticalActor agent = TacticalHarness.FirstAgent(runner.State);

        return (runner, agent, TacticalHarness.RoomOf(runner.State, agent));
    }

    /// <summary>Queues one order and steps the mission until it has been carried out.</summary>
    private static void Order(TacticalMissionRunner runner, TacticalActor actor, TacticalOrder order)
    {
        runner.State.PendingOrders.Add(order);

        // Generous, because the action is only finished when the walk reaches its target
        // and a crouch covers six centimetres a step across a room that can be four
        // metres wide.
        for (int step = 0; step < 1_200; step++)
        {
            runner.Step();

            if (runner.State.PendingOrders.Count == 0 && actor.Action is null)
                return;
        }

        Assert.Fail($"Agent {actor.Id} never finished order {order.ActionId}; it is still {actor.Action}.");
    }

    /// <summary>Steps a mission until nothing is in flight, or the budget runs out.</summary>
    private static void StepUntilSettled(TacticalMissionRunner runner, int budget)
    {
        for (int step = 0; step < budget; step++)
        {
            runner.Step();

            bool busy = false;

            foreach (TacticalActor actor in runner.State.SortedActors)
            {
                if (actor.Action is not null)
                {
                    busy = true;
                    break;
                }
            }

            if (!busy && step > 4)
                return;
        }
    }
}