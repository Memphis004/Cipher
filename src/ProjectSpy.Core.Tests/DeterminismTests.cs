using ProjectSpy.Core;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The two headline stage-3 guarantees: a year of unattended simulation stays legal and
/// reproducible, and two identical sessions produce byte-identical state.
/// </summary>
/// <remarks>
/// These are the tests that would catch the failures nobody writes a unit test for: a
/// resource that creeps negative over four hundred settlements, an agent stuck in a
/// status no rule can move them out of, and a desync that only appears after the
/// eleventh week.
/// </remarks>
public class DeterminismTests
{
    private const ulong Seed = 20260202UL;
    private const int OneYearInDays = 365;

    // ---- the 365-day run -----------------------------------------------------

    [Fact]
    public void AYearOfUnattendedSimulationCompletesWithoutThrowing()
    {
        // No exception of any kind is the bare minimum. A tick pipeline that throws on
        // some uncommon state combination would otherwise look fine until a player's
        // save hit it.
        GameSession session = BuildAgency();

        World.AdvanceDays(session, OneYearInDays);

        Assert.True(session.World.Validate(out string problem), problem);
    }

    [Fact]
    public void AYearOfSimulationNeverProducesANegativeResource()
    {
        // A negative resource is the single worst thing a rule path can do: it poisons
        // every total downstream and there is no correct way to display it.
        GameSession session = BuildAgency();

        for (int day = 0; day <= OneYearInDays; day++)
        {
            session.AdvanceTicks(Tick.TicksPerDay);

            Resources r = session.World.Resources;

            Assert.True(
                r.Funds >= 0 && r.Intel >= 0 && r.Materials >= 0 && r.Reputation >= 0 && r.Heat >= 0,
                $"day {day}: {r}");
        }
    }

    [Fact]
    public void AYearOfSimulationLeavesNoAgentInAStuckStatus()
    {
        // "Stuck" means: an agent whose status is one no rule can move them out of. If
        // an agent can reach a terminal-looking status and stay there through a year,
        // either the status is a valid resting place or nothing can ever move them.
        GameSession session = BuildAgency();

        World.AdvanceDays(session, OneYearInDays);

        foreach (Agent agent in session.World.Agents.Values)
        {
            Assert.True(Enum.IsDefined(typeof(AgentStatus), agent.Status),
                $"agent {agent.Id} has an undefined status: {agent.Status}");

            // Assigned means somewhere to go. If they claim a room, the room must exist
            // and must actually contain them, or nothing will ever move them again.
            if (agent.AssignedRoomId != 0)
            {
                Room? room = session.World.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
                Assert.NotNull(room);
                Assert.Contains(agent.Id, room!.AssignedAgentIds);
            }

            // A working status with no room is the same deadlock wearing a hat.
            if (agent.Status == AgentStatus.Training || agent.Status == AgentStatus.Resting)
            {
                Assert.True(agent.AssignedRoomId != 0,
                    $"agent {agent.Id} is {agent.Status} with no room");
            }
        }
    }

    [Fact]
    public void AYearOfSimulationLeavesNoRoomClaimingAnAgentWhoLeft()
    {
        GameSession session = BuildAgency();

        World.AdvanceDays(session, OneYearInDays);

        foreach (Room room in session.World.BaseLayout.Rooms)
        {
            foreach (AgentId occupantId in room.AssignedAgentIds)
            {
                Agent? occupant = session.World.GetAgent(occupantId);

                Assert.NotNull(occupant);
                Assert.True(
                    occupant!.AssignedRoomId == room.Id.Value,
                    $"agent {occupantId} is in room {room.Id} but points at {occupant.AssignedRoomId}");
            }
        }
    }

    [Fact]
    public void AYearOfSimulationLeavesEveryAgentStatInRange()
    {
        GameSession session = BuildAgency();

        World.AdvanceDays(session, OneYearInDays);

        foreach (Agent agent in session.World.Agents.Values)
        {
            Assert.InRange(agent.Loyalty, 0, 100);
            Assert.InRange(agent.PhysicalStamina, 0, Agent.MaxStamina);
            Assert.InRange(agent.MentalStamina, 0, Agent.MaxStamina);
            Assert.InRange(agent.InjurySeverity, 0, Agent.MaxInjurySeverity);

            foreach (SkillKind skill in SkillSet.Kinds)
            {
                Assert.InRange(agent.Skills[skill], 0, SimulationRules.MaxLevel);
            }
        }
    }

    [Fact]
    public void AYearOfSimulationReachesAStableFinalState()
    {
        GameSession session = BuildAgency();
        World.AdvanceDays(session, OneYearInDays);

        ulong first = session.World.ComputeStateHash();
        ulong canonical = WorldStateSerializer.ComputeCanonicalDigest(session.World);

        // Re-reading the world must not change it. If serializing consumed a lazy
        // initialisation or a lazily-advanced RNG, the "stable final state" would only
        // be stable until someone looked at it.
        Assert.Equal(first, session.World.ComputeStateHash());
        Assert.Equal(canonical, WorldStateSerializer.ComputeCanonicalDigest(session.World));

        // And advancing further from a legal state must stay legal.
        World.AdvanceDays(session, 30);
        Assert.True(session.World.Validate(out string problem), problem);
    }

    [Fact]
    public void TheYearHashIsTheSameForTheSameSeed()
    {
        ulong a = RunAndHash();
        ulong b = RunAndHash();

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeedsActuallyDiverge()
    {
        // A determinism test that passes because the simulation ignores its seed proves
        // nothing at all. This is the counterweight that keeps the test honest.
        ulong a = RunAndHash(Seed);
        ulong b = RunAndHash(Seed + 1);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TheYearRunActuallyDoesSomething()
    {
        // Guards the vacuous-pass risk: a simulation that refused every assignment and
        // never settled would also be stable, legal, and negative-resource free.
        GameSession session = BuildAgency();

        World.AdvanceDays(session, OneYearInDays);

        Assert.NotEqual(Resources.Starting, session.World.Resources);
        Assert.True(session.World.LastSettledWeek > 0, "the weekly settlement never ran");

        bool anyoneWorked = session.World.Agents.Values.Any(a =>
            a.Skills[SkillSet.Kinds[0]] > 0 || a.Loyalty != Agent.StartingLoyalty);

        Assert.True(anyoneWorked, "no agent trained, drifted, or changed at all");
    }

    // ---- byte-identical replay ----------------------------------------------

    [Fact]
    public void TwoSessionsWithTheSameSeedAndCommandLogAreByteIdentical()
    {
        GameSession first = PlayScriptedRun();
        GameSession second = PlayScriptedRun();

        byte[] a = WorldStateSerializer.ToCanonicalBytes(first.World);
        byte[] b = WorldStateSerializer.ToCanonicalBytes(second.World);

        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TheCommandLogsOfTwoIdenticalRunsMatch()
    {
        GameSession first = PlayScriptedRun();
        GameSession second = PlayScriptedRun();

        Assert.Equal(
            first.CommandLog.Select(c => c.GetType().Name).ToList(),
            second.CommandLog.Select(c => c.GetType().Name).ToList());

        Assert.NotEmpty(first.CommandLog);
    }

    [Fact]
    public void ReplayingARunIntoAFreshSessionIsByteIdentical()
    {
        GameSession original = PlayScriptedRun();

        Assert.True(original.VerifyReplay(out ulong replayHash));
        Assert.Equal(original.World.ComputeStateHash(), replayHash);

        GameSession replay = original.CreateReplaySession();
        original.Replay(replay);

        Assert.Equal(
            WorldStateSerializer.ToCanonicalBytes(original.World),
            WorldStateSerializer.ToCanonicalBytes(replay.World));
    }

    [Fact]
    public void ReplayPreservesTheOrderCommandsAndTicksHappenedIn()
    {
        // The regression this guards: with two parallel logs, a replay drained every
        // command before the first tick batch, so "build, play a week, build again"
        // replayed both builds before the week ever ran. The final state then differed
        // for reasons that had nothing to do with determinism.
        GameSession original = PlayScriptedRun();

        GameSession replay = original.CreateReplaySession();
        original.Replay(replay);

        Assert.Equal(
            WorldStateSerializer.ToCanonicalBytes(original.World),
            WorldStateSerializer.ToCanonicalBytes(replay.World));
    }

    [Fact]
    public void ADifferentCommandLogProducesADifferentWorld()
    {
        // The mirror image: if every session converged regardless of input, the byte
        // comparison above would be vacuous.
        GameSession straight = BuildAgency();
        straight.Execute(new SetFlagCommand("run.marker", true));
        straight.AdvanceTicks(Tick.TicksPerDay);

        GameSession indebted = BuildAgency();
        indebted.Execute(new TakeLoanCommand(FirstLoanTierId()));
        indebted.AdvanceTicks(Tick.TicksPerDay);

        Assert.NotEqual(
            WorldStateSerializer.ComputeCanonicalDigest(straight.World),
            WorldStateSerializer.ComputeCanonicalDigest(indebted.World));
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// A base with rooms, a roster and a mole — enough that every stage-3 system has
    /// something to act on during the year.
    /// </summary>
    private static GameSession BuildAgency(ulong seed = Seed)
    {
        GameSession session = World.Session(seed);

        World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        World.Place(session, World.Rooms.Infirmary, 4, 0, 3);
        World.Place(session, World.Rooms.Range, 8, 0, 3);
        World.Place(session, World.Rooms.ServerRoom, 12, 0, 3);
        World.Place(session, World.Rooms.Lounge, 16, 0, 3);
        World.Place(session, World.Rooms.Hr, 0, 1, 3);
        World.Place(session, World.Rooms.CounterIntel, 4, 1, 3);

        // One mole among four, so the weekly Heat spike is actually present in the run.
        World.Hire(session, World.Classes.Operative, 200, hiddenTraits: new[] { World.Traits.Mole });
        World.Hire(session, World.Classes.Operative, 200);
        World.Hire(session, World.Classes.Operative, 200);
        World.Hire(session, World.Classes.Infiltrator, 240);

        return session;
    }

    /// <summary>Runs the standard year and returns the final hash.</summary>
    private static ulong RunAndHash(ulong seed = Seed)
    {
        GameSession session = BuildAgency(seed);
        World.AdvanceDays(session, OneYearInDays);
        return session.World.ComputeStateHash();
    }

    /// <summary>
    /// A run whose <em>every</em> state change goes through a command.
    /// </summary>
    /// <remarks>
    /// This matters more than it looks. A replay is built from the command log and the
    /// seed alone, so any setup done by poking the world directly — placing a room,
    /// shoving an agent onto the roster — is invisible to the replay. An earlier version
    /// of this test did exactly that and failed, and the failure was correct: the replay
    /// session started with an empty base and could never have matched. Building the
    /// agency by command is also the closer analogue of what a player actually does.
    /// </remarks>
    private static GameSession PlayScriptedRun()
    {
        // A bare world has an empty unlocked-room-type set, which BaseLayout treats as
        // "everything is available", so no setup is needed to place any room.
        GameSession session = new(Seed);

        foreach ((int typeId, int x, int y, string key) in ScriptedRooms)
        {
            CommandResult result = session.Execute(new BuildRoomCommand(typeId, x, y, 3, key, 0));
            Assert.True(result.IsOk, $"could not build {key}: {result.Reason}");
        }

        session.Execute(new SetFlagCommand("run.marker", true));

        // The HR desk needs a day before the pool fills.
        session.AdvanceTicks(Tick.TicksPerDay);

        // Hire through commands so the roster is part of the replayable input.
        foreach (Agent recruit in session.World.Recruits.Values.OrderBy(r => r.Id.Value).ToList())
        {
            CommandResult result = session.Execute(
                new HireRecruitCommand(recruit.Id, RecruitmentSystem.HiringFeeFor(recruit)));

            Assert.True(result.IsOk, $"could not hire {recruit.Id}: {result.Reason}");
        }

        var agents = session.World.Agents.Values.OrderBy(a => a.Id.Value).ToList();
        var rooms = session.World.BaseLayout.Rooms.OrderBy(r => r.Id.Value).ToList();

        foreach (Agent agent in agents)
        {
            Room room = rooms[agents.IndexOf(agent) % rooms.Count];
            session.Execute(new AssignAgentToRoomCommand(agent.Id, new RoomId(room.Id.Value)));
        }

        session.Execute(new TakeLoanCommand(FirstLoanTierId()));
        session.AdvanceTicks(Tick.TicksPerWeek);

        if (agents.Count > 0)
            session.Execute(new StartInvestigationCommand(agents[0].Id));

        session.AdvanceTicks(Tick.TicksPerWeek);

        return session;
    }

    /// <summary>The base the scripted run assembles, one command per room.</summary>
    private static readonly (int TypeId, int X, int Y, string Key)[] ScriptedRooms =
    {
        (World.Rooms.Dorm, 0, 0, "room.dorm"),
        (World.Rooms.Infirmary, 4, 0, "room.infirmary"),
        (World.Rooms.Range, 8, 0, "room.range"),
        (World.Rooms.ServerRoom, 12, 0, "room.server_room"),
        (World.Rooms.Lounge, 16, 0, "room.lounge"),
        (World.Rooms.Hr, 0, 1, "room.hr"),
        (World.Rooms.CounterIntel, 4, 1, "room.counter_intel"),
    };

    private static int FirstLoanTierId()
    {
        LoanTier tier = SimulationRules.LoanTiers().First();
        return tier.Id;
    }
}
