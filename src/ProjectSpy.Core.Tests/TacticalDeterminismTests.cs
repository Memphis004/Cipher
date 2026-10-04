using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Determinism inside a mission.
/// </summary>
/// <remarks>
/// <para>
/// Rule 6: the same seed plus the same ordered command log must produce an identical
/// world. For a mission that means something sharper than for the base — a mission is
/// tens of thousands of steps long, involves several RNG streams, and every one of its
/// subsystems (perception, noise, the pathfinder, the planner) can introduce
/// nondeterminism on its own. A determinism test that ran a mission for twenty steps
/// would pass against every one of those bugs.
/// </para>
/// <para>
/// Hence the hash-every-hundred-steps shape. It catches a divergence the moment it
/// appears and names <em>when</em> it appeared, which turns "the replay desyncs
/// somewhere" into "the replay desyncs at step 1400, in the alarm" — and a hundred
/// steps of sampling is fine-grained enough to localise it and coarse enough not to cost
/// anything.
/// </para>
/// </remarks>
public class TacticalDeterminismTests
{
    /// <summary>Steps between state-hash comparisons.</summary>
    private const int HashInterval = 100;

    /// <summary>How long the hashed run goes.</summary>
    private const int Steps = 2_000;

    /// <summary>Seeds the hashed run is repeated across.</summary>
    private static readonly ulong[] Seeds = { 1UL, 7UL, 4242UL, 99_991UL, 202_609_03UL };

    /// <summary>
    /// Runs the same mission twice with the same seed and the same orders, comparing the
    /// full state hash every hundred steps.
    /// </summary>
    [Fact]
    public void TheSameSeedAndOrderLogReplayIdenticallyEveryHundredSteps()
    {
        foreach (ulong seed in Seeds)
        {
            List<ulong> first = RunAndHash(seed, Steps);
            List<ulong> second = RunAndHash(seed, Steps);

            Assert.Equal(first.Count, second.Count);

            for (int i = 0; i < first.Count; i++)
            {
                Assert.True(
                    first[i] == second[i],
                    $"Seed {seed} diverged at step {(i + 1) * HashInterval}: " +
                    $"0x{first[i]:X16} vs 0x{second[i]:X16}");
            }
        }
    }

    [Fact]
    public void DifferentSeedsActuallyDiverge()
    {
        // The counterweight to the test above. A determinism test passes just as happily
        // against a simulation that ignores its seed, and the only way to know this one is
        // doing any work is to show two seeds produce different missions.
        List<ulong> a = RunAndHash(1UL, 600);
        List<ulong> b = RunAndHash(2UL, 600);

        Assert.NotEqual(a[^1], b[^1]);
    }

    [Fact]
    public void TheOrderLogChangesTheOutcome()
    {
        // And the other counterweight: a simulation that ignores its commands would also
        // replay perfectly. Two runs with different orders must not hash alike.
        List<ulong> patient = RunAndHash(11UL, 600, ScriptedOrders.Patient);
        List<ulong> reckless = RunAndHash(11UL, 600, ScriptedOrders.Reckless);


        Assert.NotEqual(patient[^1], reckless[^1]);
    }

    [Fact]
    public void TheStateHashSeesActorsDoorStatesAndTheAlarm()
    {
        // The hash is the cheap check, so it has to actually cover the fields the
        // mission owns. A field missing from it is a field a replay can diverge in and
        // the determinism test cannot see.
        TacticalState mission = TacticalHarness.Mission();
        ulong before = HashOf(mission);

        TacticalActor actor = TacticalHarness.FirstAgent(mission);
        actor.Position = new TacticalPosition(actor.Position.FloorIndex, actor.Position.X + new Fixed32(10));

        Assert.NotEqual(before, HashOf(mission));
    }

    [Fact]
    public void TheStateHashSeesTheAlarmButNotTheOrderItWasComputedIn()
    {
        TacticalState mission = TacticalHarness.Mission();
        ulong calm = HashOf(mission);

        mission.Alarm.Set(80);
        Assert.NotEqual(calm, HashOf(mission));

        // Reaching the same level by a different route must be the same world, or the
        // hash would be a fingerprint of history rather than of state.
        TacticalState other = TacticalHarness.Mission();
        other.Alarm.Set(80);

        Assert.Equal(HashOf(mission), HashOf(other));
    }

    // ---- the pathfinder is where "stable" is easiest to break ---------------

    [Fact]
    public void ThePathfinderReturnsTheSameRouteForTheSameBuildingEveryTime()
    {
        // Rule 6 says a route may not depend on hash or insertion order. Building the
        // same layout several times and asking several times is the cheapest way to catch
        // a dictionary iteration leaking into the tie-break.
        for (ulong seed = 1; seed <= 40; seed++)
        {
            SiteLayout layout = TacticalHarness.Site(seed: seed);
            IReadOnlyList<SiteRoom> rooms = layout.Rooms;

            if (rooms.Count < 2)
                continue;

            SiteRoomId from = rooms[0].Id;
            SiteRoomId to = rooms[^1].Id;

            IReadOnlyList<SiteConnectionId> baseline = Pathfinder.FindRoute(layout, null, from, to).Connections;

            for (int repeat = 0; repeat < 5; repeat++)
            {
                SiteLayout again = TacticalHarness.Site(seed: seed);
                IReadOnlyList<SiteConnectionId> route = Pathfinder.FindRoute(again, null, from, to).Connections;

                Assert.Equal(baseline, route);
            }
        }
    }

    [Fact]
    public void ThePathfinderBreaksCostTiesOnIdRatherThanOnDiscovery()
    {
        // Two rooms at identical cost must always resolve the same way. Asked repeatedly
        // with the queries in a different order, the answer must not change.
        SiteLayout layout = TacticalHarness.Site();
        SiteRoomId target = layout.ObjectiveRoomId;
        SiteRoomId entrance = layout.EntranceRoomId;

        IReadOnlyList<SiteRoom> rooms = layout.Rooms;

        // Every room asks in the same order, so the routes below are a function of the
        // building rather than of anything the search happened to touch first.
        var first = new List<IReadOnlyList<SiteConnectionId>>();

        foreach (SiteRoom room in rooms)
            first.Add(Pathfinder.FindRoute(layout, null, room.Id, target).Connections);

        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = rooms.Count - 1; i >= 0; i--)
            {
                IReadOnlyList<SiteConnectionId> route = Pathfinder.FindRoute(layout, null, rooms[i].Id, target).Connections;
                Assert.Equal(first[i], route);
            }
        }

        // And the entrance specifically, which every mission actually uses.
        //
        // Asked first and asked last, rather than compared against `first[0]` on the
        // assumption that the entrance is the building's first room. It usually is not:
        // `Rooms` is every room of the building in generator order, and the entrance is
        // wherever step five put it. The old form compared whatever room happened to be
        // at index zero against the entrance and called it the same route, which it was
        // only for as long as both of them were unreachable and both answered with an
        // empty list.
        IReadOnlyList<SiteConnectionId> entranceFirst =
            Pathfinder.FindRoute(layout, null, entrance, target).Connections;

        Assert.Equal(
            entranceFirst,
            Pathfinder.FindRoute(layout, null, entrance, target).Connections);

        Assert.False(
            entranceFirst.Count == 0,
            $"The entrance cannot reach the objective room in the {TacticalHarness.WarehouseTemplate} warehouse.");
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>Runs a mission and returns the state hash at every interval.</summary>
    private static List<ulong> RunAndHash(
        ulong seed,
        int steps,
        Func<ulong, IReadOnlyList<TacticalOrder>>? policy = null)
    {
        TacticalMissionRunner runner = TacticalHarness.Runner(seed);
        TacticalState mission = runner.State;
        var hashes = new List<ulong>();

        for (int step = 1; step <= steps; step++)
        {
            if (policy is not null)
            {
                IReadOnlyList<TacticalOrder> scripted = policy(seed);

                // Each squad member is re-issued on its own offset, so the four of them
                // do not all act on the same step and the movement phase never has four
                // actors in flight by accident.
                for (int i = 0; i < scripted.Count; i++)
                    mission.PendingOrders.Add(ScriptedOrders.AtStep(i, step, scripted[i]));
            }

            runner.Step();

            if (step % HashInterval == 0)
                hashes.Add(HashOf(mission));
        }

        return hashes;
    }

    /// <summary>
    /// A hash over everything a mission owns.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="WorldState.ComputeStateHash"/> by way of a one-mission
    /// world, so the hashed thing is the same fingerprint the strategic determinism test
    /// uses. A tactical-only hash would let the two disagree about what "the same world"
    /// means, and the save format would then be covering two different definitions.
    /// </remarks>
    private static ulong HashOf(TacticalState mission)
    {
        var world = new WorldState(1UL) { ActiveMission = mission };
        return world.ComputeStateHash();
    }
}

/// <summary>
/// Two scripted ways to play a mission, used by the determinism tests.
/// </summary>
/// <remarks>
/// A determinism test proves the replay reproduces the run. It does not prove the run
/// did anything, and a mission in which nobody ever gives an order replays perfectly
/// while testing nothing about orders at all — so both of these actually issue orders.
/// </remarks>
internal static class ScriptedOrders
{
    /// <summary>Orders issued by a player who walks to the objective and waits.</summary>
    internal static IReadOnlyList<TacticalOrder> Patient(ulong seed)
    {
        var orders = new List<TacticalOrder>();

        for (int i = 0; i < 4; i++)
        {
            orders.Add(new TacticalOrder(
                new TacticalActorId(i + 1),
                ActionId: 12401,
                Target: new TacticalPosition(0, new Fixed32(200 + (i * 300)))));
        }

        return orders;
    }

    /// <summary>Orders issued by a player who sprints, opens doors and makes noise.</summary>
    internal static IReadOnlyList<TacticalOrder> Reckless(ulong seed)
    {
        var orders = new List<TacticalOrder>();

        for (int i = 0; i < 4; i++)
        {
            orders.Add(new TacticalOrder(
                new TacticalActorId(i + 1),
                ActionId: 12402,
                Target: new TacticalPosition(0, new Fixed32(900 + (i * 500)))));
        }

        return orders;
    }

    /// <summary>
    /// Re-issues a policy order every <c>ReissueInterval</c> steps, so the run is a long
    /// stream of commands rather than four that finish immediately.
    /// </summary>
    internal const int ReissueInterval = 25;

    /// <summary>The order for one actor on one step.</summary>
    internal static TacticalOrder AtStep(int index, int step, TacticalOrder seedOrder)
    {
        // Every order is re-issued on its own offset so the four squad members do not all
        // act on the same step and the movement phase never has four actors in flight
        // at once by accident.
        if (step % ReissueInterval != index * 3)
            return seedOrder;

        return seedOrder with
        {
            Target = new TacticalPosition(
                seedOrder.Target.FloorIndex,
                seedOrder.Target.X + new Fixed32(((step / ReissueInterval) % 7) * 40)),
        };
    }
}
