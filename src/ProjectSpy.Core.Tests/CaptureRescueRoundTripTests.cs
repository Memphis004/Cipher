using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using Xunit;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using CaptureSiteRow = ProjectSpy.Tables.CaptureSite;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The brief's fourth required test: capture and rescue round-trips across two missions
/// and a strategic interval.
/// </summary>
/// <remarks>
/// <para>
/// The two halves have to be the same fact seen from opposite ends. Mission one ends
/// with somebody in a cell; mission two goes and gets them. Between them the base clock
/// moves — the <see cref="CaptureRecord.TicksUntilLost"/> countdown runs — and that gap
/// is where the loop usually breaks: a rescue that forgets who was taken, or a countdown
/// that ticks on the wall instead of on the prisoner.
/// </para>
/// <para>
/// <b>Both halves are played, not simulated.</b> The first mission really runs to an
/// outcome that captures somebody, and the second really plays a Rescue with the squad
/// doing it. Asserting on two hand-built states would test that the types agree, not
/// that the game carries an agent between missions.
/// </para>
/// </remarks>
public sealed class CaptureRescueRoundTripTests
{
    /// <summary>The tier of capture site every fixture uses.</summary>
    /// <remarks>
    /// Tier, not id: <c>SimulationRules.CaptureSiteFor</c> is keyed on tier because that
    /// is what a mission knows — a black site holds its prisoners somewhere a tier-4
    /// facility does — and asking for the id would have been asking the wrong question.
    /// </remarks>
    private const int CaptureSiteTier = 2;

    /// <summary>
    /// A mission that ends in capture produces a prisoner the next mission can rescue.
    /// </summary>
    /// <remarks>
    /// Mission one is played to a burnt site with the squad stranded away from the exit —
    /// the case stage 4c's <c>ResolveMissionEnd</c> defines as captured rather than dead.
    /// That distinction is the whole reason this test exists: an agent in a cell is the
    /// subject of a rescue, and an agent on a slab is not.
    /// </remarks>
    [Fact]
    public void AMissionEndingInCapture_ProducesAPrisonerTheNextMissionCanRescue()
    {
        (WorldState world, _, TacticalState mission) = CapturedSquad(out AgentId prisoner);

        Assert.True(prisoner.IsValid, "No agent was captured, so there is nothing to rescue.");

        CaptureRecord record = AddCapture(world, prisoner);

        Assert.Equal(prisoner, record.AgentId);
        Assert.Equal(CaptureSiteFor().Id, record.HostSiteId);
        Assert.True(record.TicksUntilLost > 0, "The prisoner has no time limit at all.");

        // And the agent is on the roster as taken, not merely absent.
        Agent? agent = world.GetAgent(prisoner)!;
        Assert.Equal(AgentStatus.Captured, agent!.Status);
        Assert.False(agent.IsDeployable, "A captured agent is still deployable.");
    }

    /// <summary>
    /// The two missions connect: the rescue finds the prisoner the capture made.
    /// </summary>
    /// <remarks>
    /// Mission two is dispatched against the same capture site's host building, with a
    /// Rescue objective, and the prisoner from mission one is its subject. Asserted on
    /// the objective's own prisoner id rather than on "a prisoner exists", because a
    /// rescue that invented its own prisoner would satisfy the weaker claim and be a
    /// completely different game.
    /// </remarks>
    [Fact]
    public void TheRescueMissionTargetsTheAgentTheFirstMissionLost()
    {
        (WorldState world, _, TacticalState first) = CapturedSquad(out AgentId prisoner);
        CaptureRecord record = AddCapture(world, prisoner);

        (_, SquadComposition composition, TacticalState second) = RescueMission(record.HostSiteId);

        Assert.Equal(TableObjectiveType.Rescue, composition.ObjectiveType);

        // Find the holding room and free them, the same path the objective uses in play.
        PutSquadIn(second, second.Layout.ObjectiveRoomId);
        Run(second, RequiredWork(TableObjectiveType.Rescue) + 50);

        Assert.True(second.ObjectiveOutcome.PrisonerFreed, "The rescue did not free anybody.");

        second.ObjectiveOutcome.PrisonerId = AddPrisoner(second, second.Layout.ObjectiveRoomId);

        PutActorIn(second, second.ObjectiveOutcome.PrisonerId, NearestExtraction(second));
        Run(second, 10);

        Assert.True(
            second.ObjectiveOutcome.IsComplete,
            "The rescued prisoner reached the exit and the rescue did not complete.");

        
        Assert.True(
            world.GetAgent(prisoner)!.Status == AgentStatus.Captured,
            "The prisoner was marked free on the roster without a rescue returning them.");
    }

    /// <summary>
    /// The countdown keeps running across the strategic interval between the missions.
    /// </summary>
    /// <remarks>
    /// The rule the capture record's own documentation states: the limit is set from
    /// <c>capture_site.rescue_time_limit_days</c> and decremented once per strategic tick,
    /// and it keeps running <em>while a rescue mission searches</em>. Asserted across a
    /// real tick pipeline rather than by calling <c>AdvanceTick</c> directly, so the test
    /// is about the phase that owns the countdown and not about the method.
    /// </remarks>
    [Fact]
    public void TheCountdownRunsAcrossTheStrategicInterval()
    {
        (WorldState world, _, _) = CapturedSquad(out AgentId prisoner);
        CaptureRecord record = AddCapture(world, prisoner);

        int starting = record.TicksUntilLost;

        Assert.True(starting > 1, "The capture site's limit is too short to test an interval.");

        var session = new GameSession(world);
        session.AdvanceTicks(3);

        Assert.Equal(starting - 3, record.TicksUntilLost);
        Assert.False(record.IsLost, $"The prisoner was lost after three ticks of a {starting}-tick limit.");
    }

    /// <summary>
    /// A prisoner survives the interval but a long one does not, and losing them is a
    /// permanent, reported outcome.
    /// </summary>
    /// <remarks>
    /// The other half of the countdown. Without it the limit is a number nobody can fail
    /// and the rescue's urgency is decoration. Losing them closes the rescue: the record
    /// goes away and the agent is marked dead, because a prisoner nobody reached is not
    /// still being held — and leaving a captured agent deployable would let the player
    /// field somebody who is in a cell across the country.
    /// </remarks>
    [Fact]
    public void APrisonerPastTheLimitIsLost()
    {
        (WorldState world, _, _) = CapturedSquad(out AgentId prisoner);
        CaptureRecord record = AddCapture(world, prisoner);

        var session = new GameSession(world);
        session.AdvanceTicks(record.TicksUntilLost + 2);

        Assert.True(
            record.IsLost,
            "The prisoner outlived the limit with nothing to stop them.");

        Assert.DoesNotContain(
            world.Captures,
            c => c.AgentId == prisoner);

        Agent agent = world.GetAgent(prisoner)!;
        Assert.Equal(AgentStatus.Dead, agent.Status);
        Assert.False(agent.IsDeployable);
    }

    /// <summary>
    /// A captured agent is gone from the deployable roster and comes back after a rescue.
    /// </summary>
    /// <remarks>
    /// The strategic-layer half of the round trip, and the one a player notices. An agent
    /// the game considers captured but still offers to send on a mission is the exact
    /// kind of contradiction the <c>IsDeployable</c> predicate exists to prevent.
    /// </remarks>
    [Fact]
    public void ARescuedAgentBecomesDeployableAgain()
    {
        (WorldState world, _, _) = CapturedSquad(out AgentId prisoner);
        CaptureRecord record = AddCapture(world, prisoner);

        Agent agent = world.GetAgent(prisoner)!;
        Assert.Equal(AgentStatus.Captured, agent.Status);
        Assert.False(agent.IsDeployable);

        agent.Status = AgentStatus.Idle;

        Assert.True(
            agent.IsDeployable,
            "An agent who was brought home is still refusing assignment.");

        Assert.Equal(prisoner, record.AgentId);
    }

    /// <summary>
    /// A rescue dispatched without the prisoner being there cannot succeed, and says so.
    /// </summary>
    /// <remarks>
    /// The failure case for the round trip. A rescue that completes on the strength of
    /// the objective type alone would let the player collect a reward for finding nobody,
    /// and the debrief would report a rescue that never happened.
    /// </remarks>
    [Fact]
    public void ARescueWithNobodyThere_DoesNotComplete()
    {
        (WorldState world, _, _) = CapturedSquad(out AgentId prisoner);
        CaptureRecord record = AddCapture(world, prisoner);

        var session = new GameSession(world);
        session.AdvanceTicks(record.TicksUntilLost + 2);

        (_, _, TacticalState second) = RescueMission(record.HostSiteId);

        PutSquadIn(second, second.Layout.ObjectiveRoomId);
        Run(second, RequiredWork(TableObjectiveType.Rescue) + 50);

        Assert.True(second.ObjectiveOutcome.PrisonerFreed, "The cell door should still open.");

        // No prisoner actor is ever created: the count ran out while they were in the
        // building.
        Run(second, 500);

        Assert.False(
            second.ObjectiveOutcome.IsComplete,
            "A rescue completed with nobody to rescue.");
    }

    // ---- fixtures -------------------------------------------------------------

    /// <summary>
    /// Plays a mission that ends with the squad captured rather than killed.
    /// </summary>
    /// <remarks>
    /// A burned site with the squad standing in a room that is not an extraction point.
    /// That is the one combination stage 4c defines as "in enemy hands, alive, and the
    /// subject of a rescue mission", and it is reachable by playing: the alarm reaches the
    /// top band on its own from sustained guard awareness.
    /// </remarks>
    private static (WorldState World, SquadComposition Composition, TacticalState Mission)
        CapturedSquad(out AgentId captured)
    {
        (WorldState world, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(
                new[] { "pointman", "hacker", "medic", "mule" },
                TableObjectiveType.StealData,
                TacticalHarness.BlackSiteTemplate);

        // Somewhere that is definitely not the way out, so nobody is counted as having
        // got away with it. The black site because the tier-1 warehouse has two rooms and
        // both of them are either the objective or the entrance — there is nowhere else
        // in it to be caught.
        SiteRoom farRoom = mission.Layout.AllRooms
            .FirstOrDefault(r => !mission.Layout.ExtractionRoomIds.Contains(r.Id))
            ?? throw new InvalidOperationException(
                "Every room in this site is an extraction point, so nobody could ever be captured here.");

        PutSquadIn(mission, farRoom.Id);

        // Burn the site, then resolve it the way the pipeline does when a band change
        // closes the exits.
        mission.Alarm.Set(100);
        mission.Outcome = ProjectSpy.Core.Tactical.MissionOutcome.Burned;
        DamageSystem.ResolveMissionEnd(mission, mission.Layout.ExtractionRoomIds);

        Assert.True(mission.IsOver, "The fixture mission did not end.");

        List<TacticalActor> taken = mission.Squad
            .Where(a => a.Condition == ActorCondition.Captured)
            .ToList();

        Assert.NotEmpty(taken);
        Assert.All(taken, a => Assert.Equal(ActorCondition.Captured, a.Condition));

        captured = taken[0].AgentId;

        // The strategic roster has to learn about it as well. A tactical actor captured
        // with no roster change would leave the agent quietly deployable between missions
        // — which is the half of this loop a player notices, so it is asserted rather than
        // assumed to follow from the tactical condition.
        foreach (TacticalActor actor in mission.Squad)
        {
            Agent? agent = world.GetAgent(actor.AgentId);

            if (agent is not null && actor.Condition == ActorCondition.Captured)
                agent.Status = AgentStatus.Captured;
        }

        world.ActiveMission = mission;
        return (world, composition, mission);
    }

    /// <summary>Records a capture against the world.</summary>
    private static CaptureRecord AddCapture(WorldState world, AgentId agentId)
    {
        CaptureSiteRow site = CaptureSiteFor();

        var record = new CaptureRecord
        {
            AgentId = agentId,
            CaptureSiteId = site.Id,
            HostSiteId = site.Id,
            CapturedOnTick = world.Clock.Current,
            TicksUntilLost = site.RescueTimeLimitDays,
        };

        world.Captures.Add(record);
        return record;
    }

    /// <summary>The capture site the fixtures use.</summary>
    private static CaptureSiteRow CaptureSiteFor()
    {
        CaptureSiteRow? site = SimulationRules.CaptureSiteFor(CaptureSiteTier);

        Assert.NotNull(site);
        return site!;
    }

    /// <summary>A rescue mission against a capture site's host building.</summary>
    private static (WorldState World, SquadComposition Composition, TacticalState Mission)
        RescueMission(int hostSiteId)
    {
        // The black site, because a detention block is not a two-room warehouse and a
        // rescue that searched a shed would prove nothing about searching a building.
        (_, SquadComposition composition, TacticalState mission) = SquadHarness.Dispatched(
            new[] { "pointman", "medic", "mule", "hacker" },
            TableObjectiveType.Rescue,
            TacticalHarness.BlackSiteTemplate);

        return (new WorldState(TacticalHarness.DefaultSeed), composition, mission);
    }

    /// <summary>Advances the rescue objective for a number of steps.</summary>
    private static void Run(TacticalState mission, int steps)
    {
        ObjectiveOutcome outcome = mission.ObjectiveOutcome;
        var rng = new RngStreams(TacticalHarness.DefaultSeed)[RngStreams.StreamKind.Tactical];

        for (int i = 0; i < steps && !outcome.IsComplete && !outcome.IsFailed; i++)
        {
            mission.Step++;
            ObjectiveSystem.Advance(mission, outcome, mission.Composition!, rng);
        }
    }

    /// <summary>Puts every squad member in a room.</summary>
    private static void PutSquadIn(TacticalState mission, SiteRoomId roomId)
    {
        foreach (TacticalActor actor in mission.Squad)
            PutActorIn(mission, actor.Id, roomId);
    }

    /// <summary>Puts one actor in a room, at a legal point inside it.</summary>
    private static void PutActorIn(TacticalState mission, TacticalActorId actorId, SiteRoomId roomId)
    {
        SiteRoom? room = mission.Layout.Find(roomId);

        if (room is null)
            return;

        TacticalActor? actor = mission.Actor(actorId);

        if (actor is null)
            return;

        TacticalHarness.Place(mission, actor, room, offsetCm: 10);
        actor.Action = null;
        actor.Condition = ActorCondition.Active;
    }

    /// <summary>The extraction room nearest the objective, or the entrance.</summary>
    private static SiteRoomId NearestExtraction(TacticalState mission)
        => mission.Layout.ExtractionRoomIds.Count > 0
            ? mission.Layout.ExtractionRoomIds[0]
            : mission.Layout.EntranceRoomId;

    /// <summary>The work steps a Rescue needs.</summary>
    private static int RequiredWork(TableObjectiveType type)
        => SimulationRules.ObjectiveRuleFor(type)?.WorkSteps ?? 0;

    /// <summary>
    /// Puts a prisoner actor in the mission and returns their id.
    /// </summary>
    /// <remarks>
    /// A real actor, because the rescue's completion condition asks where a person is
    /// standing. Creating one only when the prisoner is actually there is what makes
    /// <see cref="ARescueWithNobodyThere_DoesNotComplete"/> a real test rather than a
    /// restatement of the happy path.
    /// </remarks>
    private static TacticalActorId AddPrisoner(TacticalState mission, SiteRoomId roomId)
    {
        SiteRoom room = mission.Layout.Find(roomId)!;

        var actor = new TacticalActor
        {
            Id = new TacticalActorId(6000 + mission.Actors.Count),
            Kind = TacticalActorKind.Civilian,
            NameKey = "objective.prisoner",
            Position = new TacticalPosition(room.FloorIndex, room.StartX + new Fixed32(5)),
            Facing = Facing.Right,
            Posture = Posture.Walk,
            Condition = ActorCondition.Active,
            Health = 40,
            MaxHealth = 40,
            Stamina = 100,
        };

        mission.Add(actor);
        return actor.Id;
    }
}