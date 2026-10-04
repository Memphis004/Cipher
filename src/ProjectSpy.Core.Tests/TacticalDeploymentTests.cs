using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;
using Xunit.Abstractions;

// There are two `MissionOutcome` enums in Core: the strategic one a Mission carries and
// the tactical one a running mission ends with. A test in this namespace sees both, so
// the tactical one is named explicitly wherever it is meant.


namespace ProjectSpy.Core.Tests;

/// <summary>
/// How a mission starts.
///
/// </summary>
/// <remarks>
/// <para>
/// Four bugs lived in this one area, and all four were found by the noise matrix rather
/// than by reading the code. They share a shape: each one was locally reasonable, each
/// one produced a mission that could not be played, and none of them threw. A site could
/// home a guard in the room the squad deploys into; the squad and a guard could be
/// placed on the same centimetre of floor; the squad could grow suspicion of the
/// building's own guards; and the alarm could count that suspicion among the site's
/// awareness. Together they burned a routine mission in thirty-four steps with the
/// player having given no orders at all.
/// </para>
/// <para>
/// Each test below names the failure it prevents. The shared claim is that a mission
/// which nobody has touched yet is a mission the player can still lose on their own
/// terms.
/// </para>
/// </remarks>
public class TacticalDeploymentTests
{
    private readonly ITestOutputHelper output;
    public TacticalDeploymentTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void NoTwoActorsStartOnTheSameCentimetre()
    {
        // The squad deploys into the entrance room; a guard's route may begin there too.
        // Placing everyone at "the middle of the room" put a guard's face 0cm from the
        // whole squad, which perception correctly called an identification.
        for (ulong seed = 1; seed <= 40; seed++)
        {
            TacticalState mission = TacticalHarness.Mission(seed);

            var seen = new Dictionary<(int, int), int>();

            foreach (TacticalActor actor in mission.SortedActors)
            {
                (int floor, int x) key = (actor.Position.FloorIndex, actor.Position.X.Raw);

                if (seen.TryGetValue(key, out int other))
                {
                    Assert.Fail(
                        $"Seed {seed}: actors {other} and {actor.Id} both start at {key.Item1}/{key.Item2}.");
                }

                seen[key] = actor.Id.Value;
            }
        }
    }

    [Fact]
    public void NoGuardIsHomedInTheRoomTheSquadDeploysInto()
    {
        // A guard stationed where the team appears sees them on step one, before the
        // player has given an order, and burns the site inside a minute. The building
        // has plenty of other rooms.
        //
        // Compared by room id rather than by name key: a multi-floor site reuses template
        // names, so `room.office` can be two different rooms on two different floors and
        // a name comparison would report a collision that is not there.
        for (ulong seed = 1; seed <= 40; seed++)
        {
            TacticalState mission = TacticalHarness.Mission(seed);
            SiteRoomId entrance = mission.Layout.EntranceRoomId;

            foreach (TacticalActor guard in mission.Guards)
            {
                SiteRoom? room = mission.Layout.RoomContaining(guard.Position);

                Assert.True(
                    room is null || room.Id != entrance,
                    $"Seed {seed}: guard {guard.Id} is homed in the entrance room ({room?.NameKey}).");
            }
        }
    }

    [Fact]
    public void TheSquadDoesNotBecomeSuspiciousOfTheBuildingsOwnGuards()
    {
        // Perception is not symmetric. A guard watching an agent is something the site
        // can act on; an agent watching a guard is the player looking at a sentry, and
        // raising a suspicion meter for it filled the squad's own meters before the
        // player had done anything.
        GameSession session = TacticalHarness.PlayingSession();

        for (int step = 0; step < 200; step++)
            session.AdvanceTacticalStep();

        TacticalState mission = session.TacticalRunner!.State;

        foreach (TacticalActor agent in mission.Squad)
            Assert.Equal(0, agent.Suspicion.Value);
    }

    [Fact]
    public void APassiveMissionDoesNotRaiseTheAlarm()
    {
        // The alarm is the site's awareness. It reads every actor's suspicion, so it has
        // to read only the guards' — otherwise the infiltrators are counted among the
        // things the site is aware of, and a mission raises its own alarm with nobody
        // having noticed anything.
        GameSession session = TacticalHarness.PlayingSession();

        for (int step = 1; step <= 6_000; step++)
        {
            session.AdvanceTacticalStep();

            if (session.TacticalRunner!.State.Outcome != ProjectSpy.Core.Tactical.MissionOutcome.InProgress)
                break;
        }

        TacticalState mission = session.TacticalRunner!.State;

        output.WriteLine($"after 6000 idle steps: alarm={mission.Alarm.Level} outcome={mission.Outcome}");

        Assert.Equal(ProjectSpy.Core.Tactical.MissionOutcome.InProgress, mission.Outcome);
        Assert.Equal(0, mission.Alarm.Level);
    }

    }