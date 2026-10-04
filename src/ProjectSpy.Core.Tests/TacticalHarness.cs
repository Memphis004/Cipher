using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Shared fixtures for the stage-4c tactical tests.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these tests needs a real building. Perception needs a room, a light and
/// an occluder; noise needs a door to attenuate through; the fuzz sweep needs guards
/// with routes and civilians with somewhere to stand. Hand-assembling those for each
/// test would mean every test asserted against a fixture that had drifted from what the
/// generator actually produces, so the fixtures here generate sites the same way a
/// mission does.
/// </para>
/// <para>
/// <b>No tables, no run.</b> Every helper asserts that the tables loaded before using
/// them (rule 3): a fixture that silently fell back to a default would let a test pass
/// against numbers that are not the game's.
/// </para>
/// </remarks>
internal static class TacticalHarness
{
    /// <summary>The warehouse-district template: the simplest tier-1 building.</summary>
    internal const int WarehouseTemplate = 11001;

    /// <summary>The black site: four floors, the highest tier, the most guards.</summary>
    internal const int BlackSiteTemplate = 11013;

    /// <summary>Mission ids used by the fixtures, so a failure names a known mission.</summary>
    internal const int MissionId = 4;

    /// <summary>Seeds used across the fixtures, so one broken seed cannot fail everything.</summary>
    internal const ulong DefaultSeed = 20260903UL;

    /// <summary>Requires the compiled tables, with a message naming what to run.</summary>
    internal static void RequireTables()
    {
        Assert.True(
            SimulationRules.AreTablesLoaded,
            "Tables did not load, so the tactical simulation is untestable (knowledge.md rule 3). " +
            "Run tools/gen.ps1.");
    }

    /// <summary>Generates a site.</summary>
    internal static SiteLayout Site(
        int templateId = WarehouseTemplate,
        int tier = 1,
        int missionId = MissionId,
        ulong seed = DefaultSeed)
    {
        RequireTables();

        SiteLayout layout = SiteGenerator.Generate(templateId, tier, missionId, seed, SiteGenerator.DeriveMapSeed(seed, missionId));

        Assert.True(layout.Validate(out string problem),
            $"The generated site is not playable: {problem}");

        return layout;
    }

    /// <summary>A squad of agents with ordinary, mid-range skills.</summary>
    internal static IReadOnlyList<Agent> Squad(WorldState world, int count = 4)
    {
        var squad = new List<Agent>(count);

        for (int i = 0; i < count; i++)
        {
            squad.Add(world.AddAgent(new Agent
            {
                Name = $"Agent{i + 1}",
                Codename = $"S{i + 1}",
                ClassId = World.Classes.Infiltrator,
                Skills = new SkillSet
                {
                    Infiltration = 40,
                    Combat = 40,
                    Tech = 40,
                    Social = 40,
                    Nerve = 40,
                },
            }));
        }

        return squad;
    }

    /// <summary>A world with the given seed and a squad already on the roster.</summary>
    internal static WorldState WorldWithSquad(ulong seed = DefaultSeed, int squadSize = 4)
    {
        var world = new WorldState(seed);
        Squad(world, squadSize);
        return world;
    }

    /// <summary>
    /// A mission that is ready to step: a generated site, a deployed squad, every guard
    /// and civilian standing where the generator put them.
    /// </summary>
    internal static TacticalState Mission(
        ulong seed = DefaultSeed,
        int templateId = WarehouseTemplate,
        int tier = 1,
        int squadSize = 4,
        SiteLayout? layout = null)
    {
        RequireTables();

        WorldState world = WorldWithSquad(seed, squadSize);
        SiteLayout site = layout ?? Site(templateId, tier, MissionId, seed);

        return TacticalMission.Create(site, SquadOf(world), MissionId, Tick.Zero);
    }

    /// <summary>
    /// A mission already advanced to a step, for tests that are about the
    /// <em>slot</em> on the world rather than about what is in the building.
    /// </summary>
    /// <remarks>
    /// Stage 1 tested <c>WorldState.ActiveMission</c> by assigning a bare
    /// <c>TacticalState</c>. Stage 4c gave that type a building to live in, so a
    /// mission with no layout can no longer be entered or hashed — which is correct,
    /// because such a thing is not a mission. These tests now build a real one and
    /// overwrite only the step. The identity fields stay <c>init</c> because nothing
    /// legitimately renames a mission once it has been created, and a test that needed
    /// to would be testing the wrong thing.
    /// </remarks>
    internal static TacticalState MissionAt(long step)
    {
        TacticalState mission = Mission();
        mission.Step = step;
        return mission;
    }

    /// <summary>A mission plus the world it belongs to, for tests that need both.</summary>
    internal static (WorldState World, TacticalState Mission) WorldAndMission(
        ulong seed = DefaultSeed,
        int templateId = WarehouseTemplate,
        int tier = 1,
        int squadSize = 4)
    {
        var world = new WorldState(seed);
        IReadOnlyList<Agent> squad = Squad(world, squadSize);
        SiteLayout site = Site(templateId, tier, MissionId, seed);

        return (world, TacticalMission.Create(site, squad, MissionId, Tick.Zero));
    }

    /// <summary>A session already in Tactical mode, with the mission attached.</summary>
    internal static GameSession PlayingSession(
        ulong seed = DefaultSeed,
        int templateId = WarehouseTemplate,
        int tier = 1,
        int squadSize = 4)
    {
        (WorldState world, TacticalState mission) = WorldAndMission(seed, templateId, tier, squadSize);

        var session = new GameSession(world);
        session.EnterTacticalMode(mission);
        return session;
    }

    /// <summary>A runner over a fresh mission, for tests that step without a session.</summary>
    internal static TacticalMissionRunner Runner(
        ulong seed = DefaultSeed,
        int templateId = WarehouseTemplate,
        int tier = 1,
        int squadSize = 4)
    {
        TacticalState mission = Mission(seed, templateId, tier, squadSize);
        return new TacticalMissionRunner(mission, new RngStreams(seed));
    }

    /// <summary>The squad an already-built world holds, in roster order.</summary>
    private static IReadOnlyList<Agent> SquadOf(WorldState world)
    {
        var squad = new List<Agent>();
        foreach (Agent agent in world.Agents.Values.OrderBy(a => a.Id.Value))
            squad.Add(agent);

        return squad;
    }

    /// <summary>The room an actor is standing in, failing the test if they are off the map.</summary>
    internal static SiteRoom RoomOf(TacticalState state, TacticalActor actor)
    {
        SiteRoom? room = state.RoomOf(actor);
        Assert.NotNull(room);
        return room!;
    }

    /// <summary>The first guard on the site, in id order.</summary>
    internal static TacticalActor FirstGuard(TacticalState state)
    {
        IReadOnlyList<TacticalActor> guards = state.Guards;
        Assert.NotEmpty(guards);
        return guards[0];
    }

    /// <summary>The first squad member, in id order.</summary>
    internal static TacticalActor FirstAgent(TacticalState state)
    {
        IReadOnlyList<TacticalActor> squad = state.Squad;
        Assert.NotEmpty(squad);
        return squad[0];
    }

    /// <summary>Puts an actor somewhere exact, for a perception or noise fixture.</summary>
    internal static void Place(TacticalState state, TacticalActor actor, SiteRoom room, int offsetCm)
    {
        Fixed32 x = room.StartX + new Fixed32(offsetCm);

        // Clamp inside the room rather than trusting the fixture's offset: an actor left
        // outside every interval would fail the mission's own invariant and make the test
        // about placement rather than about the thing under test.
        x = Fixed32.Clamp(x, room.StartX, room.EndX - new Fixed32(1));

        actor.Position = new TacticalPosition(room.FloorIndex, x);
    }
}
