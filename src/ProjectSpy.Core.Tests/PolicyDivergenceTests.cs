using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Sim.Batch;
using Xunit;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// A policy that changes nothing is a policy the balance report cannot measure.
/// </summary>
/// <remarks>
/// <para>
/// The stage-6 trace found that Stealth, Cautious and Speedrun produced byte-identical
/// order logs on three seeds of template 11001. Identical orders cannot produce different
/// outcomes, so any difference the balance report showed between those columns would
/// have been sampling noise rather than measuring the policies.
/// </para>
/// <para>
/// The cause was not subtle and not a table value: <c>SquadPolicyDriver.MoveAction</c>
/// existed, chose a different <c>tactical_action</c> row per policy, and was called from
/// nowhere. <c>RoleBehaviours.TacticalOrderFor</c> passed its own <c>MoveWalk</c>
/// constant instead, so every policy walked at the same verb and the same speed and made
/// the same noise. The policy reached the driver; it did not reach the order.
/// </para>
/// <para>
/// These tests are about the verb reaching the order, not about outcomes. An outcome
/// assertion would be a bad test here: two policies may legitimately finish a mission the
/// same way. The thing that must differ is what they asked for.
/// </para>
/// </remarks>
public class PolicyDivergenceTests
{
    /// <summary>The movement verb each policy is supposed to walk with.</summary>
    private static int ExpectedMoveVerb(SquadPolicy policy) => policy switch
    {
        SquadPolicy.Stealth or SquadPolicy.Cautious => Action("action.move_crouch"),
        SquadPolicy.Speedrun => Action("action.move_run"),
        _ => Action("action.move_walk"),
    };

    private static int Action(string nameKey) => SimulationRules.TacticalActionIdFor(nameKey);

    /// <summary>
    /// Two different policies must not issue the same orders, on a seed where their
    /// stated behaviour differs.
    /// </summary>
    /// <remarks>
    /// Seed 2 on template 11001 is used because the trace showed it as the cleanest
    /// case: Stealth, Cautious and Speedrun all ran it to a CleanSuccess with the same
    /// order count and the same final step. A seed chosen for divergence would have made
    /// this test pass for the wrong reason.
    /// </remarks>
    [Fact]
    public void DifferentPoliciesIssueDifferentOrders()
    {
        PolicyTrace.Trace stealth = Trace2(2, SquadPolicy.Stealth);
        PolicyTrace.Trace speedrun = Trace2(2, SquadPolicy.Speedrun);
        PolicyTrace.Trace cautious = Trace2(2, SquadPolicy.Cautious);

        Assert.True(
            stealth.Orders.Count > 0,
            "the Stealth trace recorded no orders, so comparing them proves nothing");

        (long Step, string Here, string There)? stealthVsSpeedrun =
            PolicyTrace.FirstDivergence(stealth, speedrun);

        Assert.True(
            stealthVsSpeedrun is not null,
            "Stealth and Speedrun issued identical orders on every step of seed 2. They differ "
            + "in the verb they are supposed to walk with (crouch vs run), so at least one of "
            + "them is not reaching the order. This is the defect the stage-6 trace found: "
            + "SquadPolicyDriver.MoveAction was computed and never called.");

        (long _, string Here, string There)? cautiousVsStealth =
            PolicyTrace.FirstDivergence(cautious, stealth);

        // Cautious is Stealth plus an abort ceiling, so it is allowed to match Stealth on
        // a run that never hits the ceiling. Asserting they must differ would be asserting
        // a difference that does not exist; asserting they may match is the honest test.
        Assert.True(
            cautiousVsStealth is null || cautious.Orders.Count > 0,
            "Cautious recorded no orders at all.");
    }

    /// <summary>
    /// The policy must reach the movement verb the order actually carries.
    /// </summary>
    /// <remarks>
    /// The direct form of the test above: it names the verb rather than comparing two
    /// traces, so a failure says what is wrong instead of only that two things differ.
    /// It reads the first movement order each policy issues, which is the first moment
    /// the difference is observable.
    /// </remarks>
    [Theory]
    [InlineData(SquadPolicy.Stealth, "action.move_crouch")]
    [InlineData(SquadPolicy.Cautious, "action.move_crouch")]
    [InlineData(SquadPolicy.Speedrun, "action.move_run")]
    [InlineData(SquadPolicy.Aggressive, "action.move_walk")]
    public void PolicyReachesTheMovementVerb(SquadPolicy policy, string expectedNameKey)
    {
        PolicyTrace.Trace trace = Trace2(2, policy);
        int expected = Action(expectedNameKey);

        Assert.True(expected != 0, $"no tactical_action row named {expectedNameKey}");

        PolicyTrace.OrderLine? move = trace.Orders.FirstOrDefault(o => IsMove(o.ActionId));

        Assert.True(
            move is not null,
            $"{policy} issued no movement order at all on seed 2, so its verb cannot be checked.");

        Assert.True(
            move!.ActionId == expected,
            $"{policy} was expected to walk with {expectedNameKey} (id {expected}) but its first "
            + $"movement order was {move.Action} (id {move.ActionId}). The policy's "
            + "movement verb is not reaching the order.");
    }

    private static bool IsMove(int actionId)
        => actionId is 12401 or 12402 or 12403 or 12404;

    /// <summary>
    /// The verb an order carries must decide how fast and how loud the movement is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second half of the same defect, and the reason a verb that never arrives is
    /// not merely cosmetic. <see cref="MovementSystem"/> derives both speed and step
    /// noise from <c>actor.Posture</c>, and the only code that ever assigns
    /// <c>Posture</c> is mission deployment. Nothing sets it when a move begins, so every
    /// actor crouches for the whole mission whatever verb was ordered: Speedrun's run
    /// order travelled at crouch speed and made a crouch noise.
    /// </para>
    /// <para>
    /// Asserted on the actor rather than on a trace because that is where the cost lands —
    /// a walk-ordering policy and a run-ordering policy had identical noise and identical
    /// travel time, so the balance report's Speedrun column could not have been faster
    /// than Stealth's even in principle.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMoveOrderSetsThePostureItAsksFor()
    {
        foreach ((int verb, Posture expected) in new[]
        {
            (Action("action.move_walk"), Posture.Walk),
            (Action("action.move_run"), Posture.Run),
            (Action("action.move_crouch"), Posture.Crouch),
        })
        {
            if (verb == 0)
                continue;

            TacticalState state = TacticalHarness.Mission();
            TacticalActor actor = TacticalHarness.FirstAgent(state);
            SiteRoom room = TacticalHarness.RoomOf(state, actor);

            // Spawned crouched, as deployment does.
            Assert.True(
                actor.Posture == Posture.Crouch,
                "fixture precondition: the actor should start crouched for this test to mean anything");

            // A destination inside this actor's own room, so the refusal cannot be about
            // reach: the room is where they are standing.
            Fixed32 destination = Fixed32.Clamp(
                actor.Position.X + new Fixed32(400),
                room.StartX,
                room.LastX);

            TacticalOrder order = new(
                actor.Id,
                verb,
                Target: new TacticalPosition(actor.Position.FloorIndex, destination));

            Assert.True(
                ActionSystem.TryBegin(state, order, new RngStreams(1)[RngStreams.StreamKind.Mission], state.Step, out TacticalOrderReason refusal),
                $"the action system refused a {verb} order in the room the actor is standing in "
                + $"({refusal}); if this fails the fixture is wrong, not the code under test.");

            Assert.True(
                actor.Posture == expected,
                $"ordering {verb} left the actor in {actor.Posture} instead of {expected}. "
                + "MovementSystem takes both its speed and its step noise from Posture, so a "
                + "run order that does not change posture is a walk that costs the same and "
                + "is heard the same.");
        }
    }

    private static PolicyTrace.Trace Trace2(int seed, SquadPolicy policy)
        => PolicyTrace.Run(
            (ulong)seed,
            policy,
            11001,
            1,
            TableObjectiveType.StealData,
            1);
}