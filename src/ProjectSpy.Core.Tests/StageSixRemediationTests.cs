using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Sim.Batch;
using Xunit;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using TacticalOutcome = ProjectSpy.Core.Tactical.MissionOutcome;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// One regression test per defect the stage-6 playtest found in Core.
/// </summary>
/// <remarks>
/// <para>
/// Nine separate systems were wrong in ways that made the tactical layer unplayable, and
/// none of them was covered by the 795 tests that stayed green throughout. That is the
/// finding this class exists to act on: the suite proved the code did what it did, not
/// that it did what the brief says.
/// </para>
/// <para>
/// <b>Each test is written to fail against the old behaviour.</b> Where the old
/// behaviour can be reconstructed inside the test — a door shut instead of open, a work
/// order issued from the wrong room, an actor left standing in the entrance — it is, so
/// the test says what the fix buys rather than only that the current code agrees with
/// itself. The rest depend on code that simply was not called, and are proved by reverting
/// the call.
/// </para>
/// <para>
/// Asserted through <c>Assert.True(condition, reason)</c> rather than
/// <c>Assert.Equal(a, b, reason)</c>: xunit has no three-argument <c>Equal</c> carrying a
/// message, so the comparison-overload resolution silently picks a numeric-precision
/// overload and the reason string never reaches the failure output.
/// </para>
/// </remarks>
public class StageSixRemediationTests
{
    // ---- (a) nothing ever called SiteLayout.ObserveRoom -----------------------

    /// <summary>
    /// The squad's own spawn room is observed on the very first step.
    /// </summary>
    /// <remarks>
    /// Before the fix, <see cref="SiteLayout.ObserveRoom"/> was called by nothing in the
    /// runtime — only by tests. Every room, including the one the squad was standing in,
    /// stayed sealed for the whole mission, so the player could not see the floor they
    /// were on, and a Recon objective, which counts observed rooms, was arithmetically
    /// impossible rather than merely hard.
    /// </remarks>
    [Fact]
    public void ASquadObservesTheRoomItIsStandingInOnTheFirstStep()
    {
        TacticalState mission = TacticalHarness.Mission();
        SiteRoomId spawn = mission.RoomOf(TacticalHarness.FirstAgent(mission))!.Id;

        Assert.True(
            !mission.Layout.IsRoomObserved(spawn),
            "The room was already observed before a single step ran. If this ever becomes "
            + "true the fixture no longer proves that the step loop is what opens it.");

        var runner = new TacticalMissionRunner(mission, new RngStreams(TacticalHarness.DefaultSeed));
        runner.Step();

        Assert.True(
            mission.Layout.IsRoomObserved(spawn),
            $"Room {spawn.Value} is where the squad deployed, and after one step it is still "
            + "sealed. Observation has to follow from the squad standing somewhere, or the "
            + "player's own floor is invisible.");
    }

    /// <summary>
    /// Walking past a shut door reveals nothing; walking past an open one reveals the room.
    /// </summary>
    /// <remarks>
    /// The two halves matter separately. "The room is observed" alone would also be
    /// satisfied by observing every room the squad had ever entered — a different and much
    /// worse game, in which a doorway means nothing. The test is judged against the same
    /// question the guards' sightlines are judged by, so the map can never claim a view
    /// the guards do not have.
    /// </remarks>
    [Fact]
    public void ObservingFollowsPerceptionRatherThanArrival()
    {
        SiteLayout layout = PerceptionFixture.Build(
            leftLight: SiteLightLevel.Lit, rightLight: SiteLightLevel.Lit);

        TacticalState mission = TacticalHarness.Mission(squadSize: 3, layout: layout);

        SiteRoom? left = layout.Find(PerceptionFixture.Left);
        Assert.NotNull(left);

        foreach (TacticalActor member in mission.Squad)
            TacticalHarness.Place(mission, member, left!, 100);

        var runner = new TacticalMissionRunner(mission, new RngStreams(TacticalHarness.DefaultSeed));

        // Shut. The door blocks vision, so the room on the far side is not visible.
        mission.Doors[PerceptionFixture.Door] = ConnectionState.Closed;
        runner.Step();

        Assert.True(mission.Layout.IsRoomObserved(PerceptionFixture.Left), "sanity: the room they are in");

        Assert.True(
            !mission.Layout.IsRoomObserved(PerceptionFixture.Right),
            "The squad is standing in the left room and the door between the rooms is shut. "
            + "Observing the room beyond it would make doorways meaningless.");

        // Open. Same positions, same squad, one door state changed.
        mission.Doors[PerceptionFixture.Door] = ConnectionState.Open;
        runner.Step();

        Assert.True(
            mission.Layout.IsRoomObserved(PerceptionFixture.Right),
            "The door is standing open and the squad is in the room beside it. An open door "
            + "is a hole in the wall; refusing to see through it would make the player's "
            + "view stricter than the guards', which is the one thing fog must never do.");
    }

    // ---- (b) agents never perceived guards ------------------------------------

    /// <summary>
    /// A guard in an agent's cone is recorded, and the guard's suspicion does not move.
    /// </summary>
    /// <remarks>
    /// Before the fix the perception phase ran guard-to-agent only, so nothing ever
    /// wrote to a squad member's memory. A guard could not appear on the player's screen
    /// under any circumstances, which made the fog-of-war layer untestable rather than
    /// merely unproven — and made every stealth decision the player could make blind.
    /// </remarks>
    [Fact]
    public void AgentsRecordGuardSightingsWithoutMovingTheGuardsSuspicion()
    {
        SiteLayout layout = PerceptionFixture.Build();

        TacticalActor agent = Actor(TacticalActorKind.Agent, PerceptionFixture.InLeft(100));
        TacticalActor guard = Actor(TacticalActorKind.Guard, PerceptionFixture.InRight(100));

        LightState light = PerceptionFixture.Lighting(layout);
        var byId = new Dictionary<int, TacticalActor>
        {
            [agent.Id.Value] = agent,
            [guard.Id.Value] = guard,
        };

        // The door has to be stated, not assumed: a connection nobody has opened is read
        // as its generated type, and this fixture's door type blocks vision. Without this
        // the test would be measuring the closed-door case (which the next test owns) and
        // would fail for a reason that has nothing to do with what it claims.
        var doors = new Dictionary<SiteConnectionId, ConnectionState>
        {
            [PerceptionFixture.Door] = ConnectionState.Open,
        };

        Perception perception = PerceptionSystem.CanPerceive(layout, light, agent, guard, doors);

        Assert.True(
            perception.Level != PerceptionLevel.None,
            "Fixture fault: two actors 200 cm apart in adjacent lit rooms with the door "
            + "open, and the guard is not perceived at all.");

        Assert.True(PerceptionSystem.RecordSighting(perception, byId, step: 5));
        Assert.True(agent.Memory.LastSeenActorId == guard.Id, "the sighting was recorded against the wrong actor");
        Assert.True(agent.Memory.LastSeenStep == 5L, "the sighting was recorded at the wrong step");
        Assert.True(agent.Memory.HasContact, "the squad member does not know it saw anything");

        int before = guard.Suspicion.Value;
        Assert.True(before == 0, "Fixture fault: the guard starts suspicious.");

        Assert.True(
            guard.Suspicion.Value == before,
            "Recording a sighting moved the guard's suspicion. If the squad's own eyes fed "
            + "the alarm, a player who spotted a patrol would raise the site's suspicion for it.");
    }

    /// <summary>A guard behind a shut door is not recorded, at any range.</summary>
    [Fact]
    public void AgentsDoNotRecordAGuardBehindAClosedDoor()
    {
        SiteLayout layout = PerceptionFixture.Build(blocksVision: true);

        TacticalActor agent = Actor(TacticalActorKind.Agent, PerceptionFixture.InLeft(100), range: 5000);
        TacticalActor guard = Actor(TacticalActorKind.Guard, PerceptionFixture.InRight(100));

        var byId = new Dictionary<int, TacticalActor>
        {
            [agent.Id.Value] = agent,
            [guard.Id.Value] = guard,
        };

        var doors = new Dictionary<SiteConnectionId, ConnectionState>
        {
            [PerceptionFixture.Door] = ConnectionState.Closed,
        };

        Perception perception = PerceptionSystem.CanPerceive(
            layout, PerceptionFixture.Lighting(layout), agent, guard, doors);

        Assert.True(
            perception.Blocker == PerceptionBlocker.Occluded,
            $"Expected the shut door to be the blocker, got {perception.Blocker}.");

        Assert.True(!PerceptionSystem.RecordSighting(perception, byId, step: 1), "a blocked sight was recorded anyway");
        Assert.True(!agent.Memory.HasContact, "the squad member remembers a guard it cannot see");
    }

    /// <summary>
    /// The step pipeline itself gives the squad guard sightings, not just the helper.
    /// </summary>
    /// <remarks>
    /// The two tests above exercise <see cref="PerceptionSystem.RecordSighting"/> in
    /// isolation. This one runs the real phase pipeline, because the defect was never in
    /// the helper — the helper did not exist — but in a phase that called nothing like it.
    /// A unit test of a function the runtime never invokes would have stayed green the
    /// whole time the game was broken, which is exactly what happened.
    /// </remarks>
    [Fact]
    public void TheStepPipelineGivesTheSquadGuardSightings()
    {
        SiteLayout layout = PerceptionFixture.Build(
            leftLight: SiteLightLevel.Lit, rightLight: SiteLightLevel.Lit);

        TacticalState mission = TacticalHarness.Mission(squadSize: 3, layout: layout);

        SiteRoom? left = layout.Find(PerceptionFixture.Left);
        Assert.NotNull(left);

        TacticalActor watcher = mission.Squad[0];
        TacticalHarness.Place(mission, watcher, left!, 100);
        watcher.Facing = Facing.Right;

        // Park a guard in the right-hand room, in the open, facing back at the door.
        // The fixture building generates no site security at all -- it is a lighting and
        // occlusion fixture, not a population one -- so the guard is added here rather
        // than taken from the mission, which would otherwise be empty.
        TacticalActor guard = Actor(
            TacticalActorKind.Guard,
            new TacticalPosition(0, new Fixed32(PerceptionFixture.RightStartCm + 100)),
            range: 3000,
            cone: 180,
            facing: Facing.Left,
            id: 9_001);
        mission.Add(guard);

        mission.Doors[PerceptionFixture.Door] = ConnectionState.Open;

        var runner = new TacticalMissionRunner(mission, new RngStreams(TacticalHarness.DefaultSeed));

        // Guards walk their patrol, so the geometry moves underneath the squad. Give the
        // phase a few steps to catch the guard before it walks out of sight.
        for (int i = 0; i < 4 && !watcher.Memory.HasContact; i++)
            runner.Step();

        Assert.True(
            watcher.Memory.HasContact,
            "A guard stood 100 cm inside a lit room with the door open, in front of a squad "
            + "member facing it, and after a step the squad member has not noticed anything. "
            + "The player would have no way to know a guard was there.");
    }

    // ---- (c) door state was ignored in sightlines -----------------------------

    /// <summary>
    /// The live door state decides the sightline, in both directions.
    /// </summary>
    /// <remarks>
    /// Before the fix, <c>CountOccluders</c> read only the door <em>type</em>'s
    /// <c>blocks_vision</c> and never looked at <see cref="ConnectionState"/>. Opening a
    /// door therefore changed nothing about what could be seen through it, so the most
    /// basic stealth decision in the game — closing the door behind you — was worth
    /// nothing. <see cref="ConnectionState"/>'s own documentation already promised
    /// "blocks sight and sound until opened"; the code was contradicting its own spec.
    /// </remarks>
    [Fact]
    public void LiveDoorStateDecidesWhetherASightlineGoesThrough()
    {
        SiteLayout layout = PerceptionFixture.Build(blocksVision: true);

        TacticalActor guard = Actor(TacticalActorKind.Guard, PerceptionFixture.InLeft(100), range: 4000);
        TacticalActor agent = Actor(TacticalActorKind.Agent, PerceptionFixture.InRight(100));

        LightState light = PerceptionFixture.Lighting(layout);

        var shut = new Dictionary<SiteConnectionId, ConnectionState>
        {
            [PerceptionFixture.Door] = ConnectionState.Closed,
        };

        var open = new Dictionary<SiteConnectionId, ConnectionState>
        {
            [PerceptionFixture.Door] = ConnectionState.Open,
        };

        int blocked = PerceptionSystem.CountOccluders(layout, guard, agent, shut);
        int clear = PerceptionSystem.CountOccluders(layout, guard, agent, open);

        Assert.True(blocked > 0, "A shut door whose type blocks_vision must occlude the sightline.");
        Assert.True(clear == 0, $"An open door left {clear} occluders in the way. It is a hole in the wall.");

        Assert.True(
            PerceptionSystem.CanPerceive(layout, light, guard, agent, shut).Blocker == PerceptionBlocker.Occluded,
            "A shut door did not report as the occluder.");

        Assert.True(
            PerceptionSystem.CanPerceive(layout, light, guard, agent, open).Blocker == PerceptionBlocker.None,
            "An open door still reported as the occluder.");
    }

    // ---- (d) objective work was credited from anywhere ------------------------

    /// <summary>
    /// Hacking from outside the objective room does not advance the objective.
    /// </summary>
    /// <remarks>
    /// Before the fix, <c>IsSomebodyWorking</c> matched on the action id alone with no
    /// room check, and the action system never verified that the terminal existed in the
    /// room the actor stood in. A squad could therefore finish a StealData from the lobby
    /// without ever entering the server room. The sweep showed it plainly: a mission
    /// reporting that nobody had ever entered the objective room, alongside a complete
    /// work bar.
    /// </remarks>
    [Fact]
    public void ObjectiveWorkOutsideTheObjectiveRoomDoesNotAdvance()
    {
        MissionFixture fixture = Fixture(11005, TableObjectiveType.StealData, seed: 7);
        TacticalState mission = fixture.Mission;

        int work = mission.ObjectiveOutcome.WorkInteractableId;
        Assert.True(work != 0, "The fixture is useless without a work interactable to aim at.");

        SiteRoomId workRoom = RoomHolding(fixture, work);
        SiteRoomId elsewhere = mission.Layout.EntranceRoomId;

        Assert.True(
            workRoom != elsewhere,
            $"Fixture fault: the objective room and the entrance are the same room ({workRoom.Value}), "
            + "so this test cannot tell a room-scoped check from an unscoped one.");

        SiteRoom? lobby = mission.Layout.Find(elsewhere);
        Assert.NotNull(lobby);

        foreach (TacticalActor member in mission.Squad)
            TacticalHarness.Place(mission, member, lobby!, 50);

        // Everybody else is told to stand still. Without this the test measures nothing
        // it claims: the squad's own role behaviours are still running, and they walk
        // the Hacker to the terminal and hack it properly while this test is asserting
        // that nobody can. The work then advances from the right room, by the right
        // action, and the failure message would name a defect that does not exist.
        mission.Control.HoldAll();

        HackUntilFinished(fixture, work, 400);

        Assert.True(
            mission.ObjectiveOutcome.WorkSteps == 0,
            "A squad member hacked the objective terminal from another room on the floor and "
            + "the objective advanced. Work has to happen where the work is.");
    }

    /// <summary>The same order, from inside the work room, does advance it.</summary>
    [Fact]
    public void ObjectiveWorkInsideTheWorkRoomAdvances()
    {
        MissionFixture fixture = Fixture(11005, TableObjectiveType.StealData, seed: 7);
        TacticalState mission = fixture.Mission;

        int work = mission.ObjectiveOutcome.WorkInteractableId;
        SiteRoom? room = mission.Layout.Find(RoomHolding(fixture, work));
        Assert.NotNull(room);

        foreach (TacticalActor member in mission.Squad)
            TacticalHarness.Place(mission, member, room!, 50);

        // Held for the same reason as the other half: this test is about whether an
        // ordered hack in the right room counts, not about whether the squad could
        // have got there by itself.
        mission.Control.HoldAll();

        HackUntilFinished(fixture, work, 400);

        Assert.True(
            mission.ObjectiveOutcome.WorkSteps > 0,
            "The squad is standing in the room holding the terminal, hacking it, and the "
            + "objective did not move. The room check has rejected the work it should allow.");
    }

    // ---- (e) SeedObjective took the first terminal on the site ----------------

    /// <summary>
    /// The seeded work interactable is in the objective room, not merely on the site.
    /// </summary>
    /// <remarks>
    /// Before the fix, <c>SeedObjective</c> took the first Terminal it found anywhere in
    /// the building. On a site whose objective room was upstairs and whose ground floor
    /// had a reception terminal, StealData pointed at the reception terminal: the squad
    /// hacked the wrong thing in the wrong place, and the objective room's own prop was
    /// never used.
    /// </remarks>
    [Fact]
    public void SeedObjectivePicksTheWorkInteractableInsideTheObjectiveRoom()
    {
        int checkedSites = 0;
        int sitesWithADecoy = 0;

        foreach (ProjectSpy.Tables.SiteTemplate template in SimulationRules.AllSiteTemplates())
        {
            for (int index = 0; index < 4; index++)
            {
                ulong seed = 0xBEEF0000UL + (ulong)index * 104729UL + (ulong)template.Id;

                MissionFixture fixture = MissionFixture.Create(
                    seed, template.Id, template.Tier, TableObjectiveType.StealData, index + 1,
                    MissionFixture.RolesFor(TableObjectiveType.StealData));

                checkedSites++;

                int work = fixture.Mission.ObjectiveOutcome.WorkInteractableId;

                Assert.True(
                    work != 0,
                    $"site {template.Id} mission {index} seeded no work interactable at all.");

                SiteRoomId room = RoomHolding(fixture, work);

                Assert.True(
                    room == fixture.Mission.Layout.ObjectiveRoomId,
                    $"site {template.Id} mission {index} points the objective at a work "
                    + $"interactable in room {room.Value}, but the objective room is "
                    + $"{fixture.Mission.Layout.ObjectiveRoomId.Value}.");

                foreach (SiteInteractable other in fixture.Mission.Layout.Interactables)
                {
                    bool usable = other.Kind is InteractableType.Objective or InteractableType.Terminal;

                    if (usable && other.RoomId != fixture.Mission.Layout.ObjectiveRoomId)
                    {
                        sitesWithADecoy++;
                        break;
                    }
                }
            }
        }

        Assert.True(checkedSites > 0, "no sites were checked at all");
        Assert.True(
            sitesWithADecoy > 0,
            "None of the sampled sites had a usable terminal anywhere except its objective "
            + "room, so this test never had the chance to fail against the old behaviour. "
            + "The decoy it is written for was not exercised.");
    }

    // ---- (f) the objective room had nothing in it -----------------------------

    /// <summary>Every generated site holds something the objective can be done on.</summary>
    [Fact]
    public void EverySiteHasAWorkInteractableInItsObjectiveRoom()
    {
        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();
        Assert.True(templates.Count > 0, "no site templates");

        int sites = 0;

        foreach (ProjectSpy.Tables.SiteTemplate template in templates)
        {
            for (int index = 0; index < 40; index++)
            {
                ulong seed = 0x0B1EC70FUL + (ulong)index * 15485863UL + (ulong)template.Id;

                SiteLayout layout = SiteGenerator.Generate(
                    template.Id, template.Tier, index, seed,
                    SiteGenerator.DeriveMapSeed(seed, index));

                sites++;

                bool found = false;

                foreach (SiteInteractable interactable in layout.Interactables)
                {
                    if (interactable.RoomId == layout.ObjectiveRoomId
                        && interactable.Kind is InteractableType.Objective or InteractableType.Terminal)
                    {
                        found = true;
                        break;
                    }
                }

                Assert.True(
                    found,
                    $"site {template.Id} tier {template.Tier} mission {index}: the objective "
                    + $"room {layout.ObjectiveRoomId.Value} holds nothing to work on. No "
                    + "objective type can be completed by standing in an empty room, so this "
                    + "is part of the mission's content missing rather than a variation.");
            }
        }

        Assert.True(sites > 0, "no sites were generated");
    }

    // ---- (g) order move <room> was a no-op across a boundary ------------------

    /// <summary>
    /// A MoveTo order actually puts the actor in the room it names.
    /// </summary>
    /// <remarks>
    /// Before the fix, <c>MoveTo</c> emitted a plain walk whenever the destination was on
    /// the actor's own floor. <c>MovementSystem</c> clamps a walk to the room the actor is
    /// already in, so "walk to room 7" from room 3 walked to the wall beside the door and
    /// stopped. The command was accepted, logged as legal, and did nothing at all — on
    /// every site, on every floor, for the entire mission.
    /// </remarks>
    [Fact]
    public void AMoveToOrderCrossesRoomsAndFloors()
    {
        MissionFixture fixture = UnpatrolledFixture(11005, TableObjectiveType.StealData, seed: 7);
        TacticalState mission = fixture.Mission;

        (SiteRoomId start, SiteRoomId destination) = TwoHopPair(mission);

        SiteRoom? startRoom = mission.Layout.Find(start);
        SiteRoom? destinationRoom = mission.Layout.Find(destination);
        Assert.NotNull(startRoom);
        Assert.NotNull(destinationRoom);

        TacticalActor mover = RoleThatMayMove(mission);
        TacticalHarness.Place(mission, mover, startRoom!, 50);

        Assert.True(mission.RoomOf(mover)!.Id == start, "the fixture did not place the mover where it said");

        var composition = PointmanComposition(mission);

        MoveToAndRun(mission, composition, mover,
            new TacticalPosition(destinationRoom!.FloorIndex, destinationRoom.StartX),
            fixture.Seed, 1500);

        Assert.True(
            mission.RoomOf(mover)!.Id == destination,
            $"The member was told to go to room {destination.Value}, two hops away, and after "
            + $"1500 steps is still in room {mission.RoomOf(mover)!.Id.Value}.");
    }

    /// <summary>
    /// Crossing a room boundary is issued as a traversal, never as a walk.
    /// </summary>
    /// <remarks>
    /// The behavioural test above could in principle be satisfied by luck over a long step
    /// budget, so the order itself is checked too: a hop between rooms must be
    /// <c>action.traverse_door</c> or <c>action.traverse_vent</c>. This is the assertion
    /// that names the actual bug rather than its consequence.
    /// </remarks>
    [Fact]
    public void CrossingARoomBoundaryIsATraversalNotAWalk()
    {
        MissionFixture fixture = UnpatrolledFixture(11005, TableObjectiveType.StealData, seed: 7);
        TacticalState mission = fixture.Mission;

        (SiteRoomId start, SiteRoomId destination) = TwoHopPair(mission);

        SiteRoom? startRoom = mission.Layout.Find(start);
        SiteRoom? destinationRoom = mission.Layout.Find(destination);
        Assert.NotNull(startRoom);
        Assert.NotNull(destinationRoom);

        TacticalActor mover = RoleThatMayMove(mission);
        TacticalHarness.Place(mission, mover, startRoom!, 50);

        var target = new TacticalPosition(destinationRoom!.FloorIndex, destinationRoom.StartX);
        List<(SiteRoomId Room, TacticalOrder Order)> issued = new();

        MoveToAndRun(mission, PointmanComposition(mission), mover, target, fixture.Seed, 1500, issued);

        Assert.True(
            mission.RoomOf(mover)!.Id == destination,
            $"The member was told to go to room {destination.Value}, two hops away, and after "
            + $"1500 steps is still in room {mission.RoomOf(mover)!.Id.Value}. It issued "
            + $"{issued.Count} orders and none of them got it across.");

        int traverseDoor = SimulationRules.TacticalActionIdFor("action.traverse_door");
        int traverseVent = SimulationRules.TacticalActionIdFor("action.traverse_vent");

        int[] walks =
        {
            SimulationRules.TacticalActionIdFor("action.move_walk"),
            SimulationRules.TacticalActionIdFor("action.move_run"),
            SimulationRules.TacticalActionIdFor("action.move_crouch"),
            SimulationRules.TacticalActionIdFor("action.move_prone"),
        };

        // Every hop between rooms is a traversal or a door action, and neither is a walk.
        // Asserting on the orders that were actually issued rather than on what one call
        // returns keeps this test from keeping a second copy of the router: a test that
        // recomputes the route to check the route is only ever as right as the copy.
        foreach ((SiteRoomId room, TacticalOrder order) in issued)
        {
            if (order.ActionId == traverseDoor || order.ActionId == traverseVent)
                continue;

            Assert.True(
                Array.IndexOf(walks, order.ActionId) < 0 || IsADoorway(mission, order, room),
                $"Standing in room {room.Value} the member issued a walk aimed at {order.Target.X}, "
                + "which is not a doorway of any of that room's connections. A walk is clamped to "
                + "the room the actor is already in, so that order stops at the wall beside the "
                + "door and never crosses anything.");
        }

        Assert.True(
            issued.Any(i => i.Order.ActionId == traverseDoor || i.Order.ActionId == traverseVent),
            "The member arrived, but not by traversing anything: none of the "
            + $"{issued.Count} orders it issued was a traversal. Crossing a room boundary is a "
            + "traversal, and arriving some other way would be a different defect that this test "
            + "should not be satisfied by.");
    }

    // ---- (h) no mission with a handler could ever finish ----------------------

    /// <summary>
    /// A squad containing a Handler reaches a finished outcome.
    /// </summary>
    /// <remarks>
    /// Before the fix, two things were true at once and neither alone was survivable.
    /// <c>agent_role</c> gives the handler no <c>MoveTo</c>, and the mission ended only
    /// when the <em>whole</em> squad stood at an extraction point. So a handler, who could
    /// never be told to walk to one, held the mission open forever. Not rarely — never, on
    /// every site, under every policy, and therefore in every number the balance report had
    /// quoted so far.
    /// </remarks>
    [Fact]
    public void ASquadContainingAHandlerCanFinish()
    {
        int total = 0;

        foreach (ProjectSpy.Tables.SiteTemplate template in SimulationRules.AllSiteTemplates())
        {
            for (int index = 0; index < 2; index++)
            {
                ulong seed = 0x11ADEDUL + (ulong)index * 7907UL + (ulong)template.Id;

                MissionFixture fixture = MissionFixture.Create(
                    seed, template.Id, template.Tier, TableObjectiveType.StealData, index + 1,
                    MissionFixture.RolesFor(TableObjectiveType.StealData));

                MissionRun run = MissionRun.Play(fixture, SquadPolicy.Speedrun);
                total++;

                Assert.True(
                    run.Outcome != TacticalOutcome.InProgress,
                    $"site {template.Id} mission {index}: the mission ran out of steps still "
                    + "in progress. A composition the dispatch screen will accept must not be "
                    + "able to produce a mission that cannot end.");
            }
        }

        Assert.True(total > 0, "no missions were played");
    }

    /// <summary>
    /// A clean exfiltration is not scored a Disaster.
    /// </summary>
    /// <remarks>
    /// The same trap one layer down. The mission ended on the "whole squad is out" test
    /// and then <c>ResolveSystem</c> decided "extracted" by a different rule, so a team
    /// that had walked out without a scratch was handed to the enemy as a capture and
    /// scored <c>Disaster, lost 1</c>. Both sides now ask
    /// <see cref="TacticalState.IsStillInside"/> — one predicate, one answer.
    /// </remarks>
    [Fact]
    public void ACleanExfiltrationIsNotScoredDisaster()
    {
        int cleanRuns = 0;

        foreach (ProjectSpy.Tables.SiteTemplate template in SimulationRules.AllSiteTemplates())
        {
            for (int index = 0; index < 2; index++)
            {
                ulong seed = 0x5A1ADUL + (ulong)index * 3571UL + (ulong)template.Id;

                MissionFixture fixture = MissionFixture.Create(
                    seed, template.Id, template.Tier, TableObjectiveType.StealData, index + 1,
                    MissionFixture.RolesFor(TableObjectiveType.StealData));

                MissionRun run = MissionRun.Play(fixture, SquadPolicy.Speedrun);

                Assert.True(
                    !(run.Class == ResolveClass.Disaster && run.Lost == 0),
                    $"site {template.Id} mission {index}: the mission was scored "
                    + $"{run.Class} with lost {run.Lost}. A team that lost nobody cannot have "
                    + "been scored a Disaster.");

                Assert.True(
                    !(run.TeamExtracted && run.Class == ResolveClass.Disaster),
                    $"site {template.Id} mission {index}: the resolution says the team "
                    + "extracted and also that the mission was a Disaster. Those two answers "
                    + "come from the same predicate and cannot disagree.");

                if (run.Clean)
                    cleanRuns++;
            }
        }

        Assert.True(cleanRuns > 0, "no run in the sweep produced a clean success at all");
    }

    /// <summary>
    /// A member no order can move is not counted as still inside the building.
    /// </summary>
    /// <remarks>
    /// The single predicate both the ending and the resolution use, asserted on its own.
    /// The failure mode being pinned here is two files each holding their own idea of
    /// "still inside"; a test that only inspected the final class would have been satisfied
    /// by whichever of the two happened to be right that week.
    /// </remarks>
    [Fact]
    public void TheEndingAndTheResolutionAskTheSameQuestionAboutExtraction()
    {
        MissionFixture fixture = Fixture(11001, TableObjectiveType.StealData, seed: 7);
        TacticalState mission = fixture.Mission;

        foreach (TacticalActor member in mission.Squad)
        {
            member.Condition = ActorCondition.Active;
            member.Action = null;
        }

        // Find the member no order can move. The fixture has to be built around one: a
        // squad of movers would satisfy every assertion below without ever exercising the
        // predicate, and the defect was precisely that this member held the mission open.
        TacticalActor? handler = null;

        foreach (TacticalActor member in mission.Squad)
        {
            if (mission.Composition?.MemberFor(member.AgentId)?.Role is { } role
                && !role.Allows(SquadOrderKind.MoveTo))
            {
                handler = member;
                break;
            }
        }

        Assert.NotNull(handler);

        // Everybody else is standing in the building, which is the "before" half: only
        // the one member who cannot be ordered out is treated as outside.
        foreach (TacticalActor member in mission.Squad)
        {
            if (member.Id == handler!.Id)
                continue;

            Assert.True(
                mission.IsStillInside(member),
                $"Fixture fault: member {member.Id.Value} is already considered out of the "
                + "building before anything happened.");
        }

        Assert.True(
            !mission.IsStillInside(handler!),
            "A member whose role permits no move order is stranded by definition. Counting "
            + "them as still inside the building means no mission that fields one can ever "
            + "reach its ending.");

        mission.Outcome = TacticalOutcome.Exfiltrated;
        mission.ObjectiveOutcome.IsComplete = true;

        MissionResolution resolution = ResolveSystem.Classify(
            mission, mission.ObjectiveOutcome, aborted: false);

        Assert.True(resolution.Lost == 0, $"the resolution lost {resolution.Lost} of a squad that is all still standing");
        Assert.True(resolution.TeamExtracted, "the resolution says nobody got out");
        Assert.True(
            resolution.Class != ResolveClass.Disaster,
            $"a squad that is all still standing and not inside was scored {resolution.Class}");
    }

    // ---- (i) the policy driver had its own copy of (g) -------------------------

    /// <summary>
    /// A policy's orders are the orders a player could have given, and it crosses rooms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SquadPolicyDriver"/> had a private copy of the pathfinder's first hop,
    /// and that copy had the same defect the player-facing order had. Every policy that
    /// tried to leave a room walked to the wall beside the door and stood there for the
    /// rest of the mission — which is why the first stage-6 sweep reported an objective
    /// rate near 75% and a mean time-to-extraction of zero. The driver now delegates to
    /// <see cref="RoleBehaviours"/>, as its own remarks claimed it always had.
    /// </para>
    /// <para>
    /// <b>Asserted on the orders, not only on the outcome.</b> The obvious version of this
    /// test — "did anybody leave the entrance room" — cannot tell a traversal bug from a
    /// mission that was lost, burned or captured before anybody got anywhere. Speedrun
    /// forces doors, forcing is loud, and on some sites the alarm passes the objective's
    /// tolerance band within eighty steps: a correct policy and a correct mission that
    /// ended early are indistinguishable from a policy that never moved. So the shape of
    /// every order the driver issues is checked as well, and the movement is checked
    /// across the whole sweep rather than site by site.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePolicyDriverUsesTheSameTraversalPathAsAPlayerOrder()
    {
        int total = 0;
        int sitesWhereSomebodyMoved = 0;

        int[] walks =
        {
            SimulationRules.TacticalActionIdFor("action.move_walk"),
            SimulationRules.TacticalActionIdFor("action.move_run"),
            SimulationRules.TacticalActionIdFor("action.move_crouch"),
            SimulationRules.TacticalActionIdFor("action.move_prone"),
            SimulationRules.TacticalActionIdFor("action.move_vault"),
            SimulationRules.TacticalActionIdFor("action.move_climb"),
        };

        foreach (ProjectSpy.Tables.SiteTemplate template in SimulationRules.AllSiteTemplates())
        {
            for (int index = 0; index < 2; index++)
            {
                ulong seed = 0x9011CEUL + (ulong)index * 6151UL + (ulong)template.Id;

                MissionFixture fixture = MissionFixture.Create(
                    seed, template.Id, template.Tier, TableObjectiveType.StealData, index + 1,
                    MissionFixture.RolesFor(TableObjectiveType.StealData));

                TacticalState mission = fixture.Mission;
                SiteRoomId entrance = mission.Layout.EntranceRoomId;

                if (entrance == mission.Layout.ObjectiveRoomId)
                    continue;

                total++;

                var runner = new TacticalMissionRunner(mission, new RngStreams(fixture.Seed));
                var driver = new SquadPolicyDriver(SquadPolicy.Speedrun);

                bool movedRooms = false;

                for (int step = 0; step < 900 && !mission.IsOver; step++)
                {
                    mission.PendingOrders.Clear();

                    driver.Think(mission, fixture.Composition);

                    foreach (TacticalOrder order in mission.PendingOrders)
                    {
                        TacticalActor? actor = mission.Actor(order.ActorId);

                        if (actor is null || !actor.IsAgent)
                            continue;

                        SiteRoom? here = mission.RoomOf(actor);

                        if (here is null)
                            continue;

                        // Every order a policy issues must be one the player's channel
                        // could have carried: a walk aimed at a doorway of the room the
                        // member is standing in, or an order about a door or a thing.
                        bool walk = Array.IndexOf(walks, order.ActionId) >= 0;

                        Assert.True(
                            !walk || order.Target.IsValid && IsADoorway(mission, order, here.Id),
                            $"site {template.Id} step {step}: the policy issued action "
                            + $"{order.ActionId} aimed at {order.Target.X} to a member standing in "
                            + $"room {here.Id.Value}, which is not a doorway of that room. This is "
                            + "the driver's own copy of the route, and it is the copy that used to "
                            + "walk at the wall.");

                        Assert.True(
                            !walk || order.Target.IsValid,
                            $"site {template.Id} step {step}: the policy issued a walk with no "
                            + "target, so there is nothing for MovementSystem to move towards.");
                    }

                    runner.Step();

                    foreach (TacticalActor member in mission.Squad)
                    {
                        if (mission.RoomOf(member) is { } room && room.Id != entrance)
                        {
                            movedRooms = true;
                            break;
                        }
                    }
                }

                if (movedRooms)
                    sitesWhereSomebodyMoved++;

                Assert.True(
                    driver.IllegalOrders == 0,
                    $"site {template.Id}: the policy issued {driver.IllegalOrders} orders the "
                    + "action system refused as illegal. A policy is not allowed a private "
                    + "channel; it uses the same gate the player's orders go through.");
            }
        }

        Assert.True(total > 0, "no missions were played");

        Assert.True(
            sitesWhereSomebodyMoved > 0,
            "Across " + total + " missions, no squad member ever left the entrance room. A "
            + "policy that cannot leave the entrance is not a policy.");
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>An actor with exactly the identity, position and optics a test asks for.</summary>
    private static TacticalActor Actor(
        TacticalActorKind kind,
        TacticalPosition at,
        int range = 2000,
        int cone = 180,
        Facing facing = Facing.Right,
        int id = 0)
        => new()
        {
            // Ids are unique per test rather than per kind, because several tests add an
            // actor to a mission that already has members with ids of their own.
            Id = new TacticalActorId(id != 0 ? id : kind == TacticalActorKind.Agent ? 1 : 2),
            Kind = kind,
            Position = at,
            Facing = facing,
            Posture = Posture.Walk,
            Condition = ActorCondition.Active,
            Health = 100,
            MaxHealth = 100,
            Stamina = 100,
            Vision = new VisionStats
            {
                VisionRangeCm = range,
                VisionConeDegrees = cone,
                HearingRangeCm = 0,
                RangeBonusPercent = 0,
            },
        };

    /// <summary>
    /// A dispatched StealData mission on a real generated site with the patrols removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No guards, on purpose.</b> A test about whether <c>order move &lt;room&gt;</c>
    /// crosses a room boundary is a test about the movement system, and a patrol that
    /// arrests the walker half way across turns it into a combat test that fails for
    /// reasons having nothing to do with the order. The first attempt at the (g) test
    /// failed exactly that way: the member crossed two rooms correctly, was captured on
    /// the way, and the failure message claimed it had not crossed anything.
    /// </para>
    /// <para>
    /// The building is otherwise untouched — same generator, same rooms, same doors,
    /// same locks — because the traversal has to be proved on a real layout rather than
    /// on a purpose-built one.
    /// </para>
    /// </remarks>
    private static MissionFixture UnpatrolledFixture(
        int templateId, TableObjectiveType objective, ulong seed)
    {
        TacticalHarness.RequireTables();

        int tier = SimulationRules.AllSiteTemplates().First(t => t.Id == templateId).Tier;
        var world = new WorldState(seed);
        var composition = new SquadComposition { ObjectiveType = objective };
        var roster = new List<Agent>();

        int infiltrator = SimulationRules.AllAgentClasses()
            .First(c => c.NameKey == "class.infiltrator").Id;

        foreach (string role in MissionFixture.RolesFor(objective))
        {
            Agent agent = world.AddAgent(new Agent
            {
                Name = role,
                Codename = role.ToUpperInvariant(),
                ClassId = infiltrator,
                Skills = new SkillSet
                {
                    Infiltration = 50,
                    Combat = 50,
                    Tech = 50,
                    Social = 50,
                    Nerve = 50,
                },
            });

            roster.Add(agent);
            composition.Add(agent.Id, MissionFixture.RoleIdFor(role));
        }

        ulong mapSeed = SiteGenerator.DeriveMapSeed(seed, 1);
        SiteLayout layout = SiteGenerator.Generate(templateId, tier, 1, seed, mapSeed);

        Assert.True(layout.Validate(out string problem), $"generated site is unplayable: {problem}");

        layout.MainSite.Guards.Clear();

        if (layout.ForwardPost is { } post)
            post.Guards.Clear();

        bool dispatched = SquadDeployment.TryDispatch(
            world, composition, 1, layout, roster[0].Id, recordRolls: false,
            out TacticalState? mission, out IReadOnlyList<DispatchRefusal> refusals);

        Assert.True(
            dispatched && mission is not null,
            "The fixture squad was refused dispatch: "
            + string.Join("; ", refusals.Select(r => r.MessageKey)));

        return new MissionFixture
        {
            Seed = seed,
            TemplateId = templateId,
            Tier = tier,
            Objective = objective,
            World = world,
            Composition = composition,
            Mission = mission,
            ShootActions = 0,
        };
    }

    /// <summary>A dispatched StealData mission on a real generated site.</summary>
    private static MissionFixture Fixture(int templateId, TableObjectiveType objective, ulong seed)
    {
        TacticalHarness.RequireTables();

        int tier = SimulationRules.AllSiteTemplates().First(t => t.Id == templateId).Tier;

        return MissionFixture.Create(
            seed, templateId, tier, objective, missionId: 1, MissionFixture.RolesFor(objective));
    }

    /// <summary>The room an interactable is standing in.</summary>
    private static SiteRoomId RoomHolding(MissionFixture fixture, int interactableId)
    {
        foreach (SiteInteractable interactable in fixture.Mission.Layout.Interactables)
        {
            if (interactable.Id == interactableId)
                return interactable.RoomId;
        }

        throw new InvalidOperationException($"No interactable with id {interactableId} on this site.");
    }

    /// <summary>
    /// A pair of same-floor rooms with one room between them.
    /// </summary>
    /// <remarks>
    /// Searched for rather than assumed, because a generated site has no promise that any
    /// particular room — the entrance least of all — has two same-floor neighbours. A
    /// fixture that picked "the first room" would fail on a building that was merely
    /// shaped differently from the one it was written against, which measures the
    /// generator, not the traversal.
    /// </remarks>
    private static (SiteRoomId Start, SiteRoomId Destination) TwoHopPair(TacticalState state)
    {
        foreach (SiteRoom room in state.Layout.AllRooms)
        {
            if (TryTwoHops(state, room.Id, out SiteRoomId far))
                return (room.Id, far);
        }

        throw new InvalidOperationException(
            $"Site {state.Layout.SiteTemplateId} has no pair of same-floor rooms two hops apart.");
    }

    /// <summary>Whether <paramref name="from"/> has a two-hop neighbour on its own floor.</summary>
    private static bool TryTwoHops(TacticalState state, SiteRoomId from, out SiteRoomId destination)
    {
        destination = default;
        SiteRoom? origin = state.Layout.Find(from);

        if (origin is null)
            return false;

        foreach (SiteConnection first in state.Layout.ConnectionsAt(from).OrderBy(c => c.Id.Value))
        {
            SiteRoomId middle = first.Other(from);
            SiteRoom? middleRoom = state.Layout.Find(middle);

            if (middleRoom is null || middleRoom.FloorIndex != origin.FloorIndex)
                continue;

            foreach (SiteConnection second in state.Layout.ConnectionsAt(middle).OrderBy(c => c.Id.Value))
            {
                SiteRoomId far = second.Other(middle);

                if (far == from)
                    continue;

                SiteRoom? farRoom = state.Layout.Find(far);

                if (farRoom is not null && farRoom.FloorIndex == origin.FloorIndex)
                {
                    destination = far;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a move order aims at a doorway on the route, rather than anywhere in the
    /// actor's own room.
    /// </summary>
    /// <summary>
    /// Whether a move order aimed at an x is aiming at a doorway rather than at a spot
    /// in whichever room the actor happens to be standing in.
    /// </summary>
    /// <remarks>
    /// Checked against the building rather than against a recomputed route: "is this x a
    /// doorway of one of this room's connections" is the question a walk is actually
    /// answering, and it does not need a pathfinder to answer it.
    /// </remarks>
    private static bool IsADoorway(TacticalState state, TacticalOrder order, SiteRoomId room)
    {
        if (!order.Target.IsValid)
            return false;

        foreach (SiteConnection connection in state.Layout.ConnectionsAt(room))
        {
            Fixed32 x = connection.RoomA == room ? connection.X : connection.UpperX;

            if (Fixed32.Distance(order.Target.X, x).Raw <= 400)
                return true;
        }

        return false;
    }

    /// <summary>A squad member whose role permits a move order.</summary>
    private static TacticalActor RoleThatMayMove(TacticalState state)
    {
        foreach (TacticalActor member in state.Squad)
        {
            if (state.Composition?.MemberFor(member.AgentId)?.Role is { } role
                && role.Allows(SquadOrderKind.MoveTo))
            {
                return member;
            }
        }

        throw new InvalidOperationException("No squad member may be ordered to move.");
    }

    /// <summary>Every member given the pointman role, so the fixture can issue MoveTo.</summary>
    private static SquadComposition PointmanComposition(TacticalState mission)
    {
        var composition = new SquadComposition { ObjectiveType = TableObjectiveType.StealData };
        int pointman = MissionFixture.RoleIdFor("pointman");

        foreach (TacticalActor member in mission.Squad)
            composition.Add(new AgentId(member.AgentId!.Value), pointman);

        return composition;
    }

    /// <summary>Re-issues a hack order on the work interactable until it lands or runs out.</summary>
    private static void HackUntilFinished(MissionFixture fixture, int workInteractableId, int maxSteps)
    {
        TacticalState mission = fixture.Mission;
        int hack = SimulationRules.TacticalActionIdFor("action.interact_hack_terminal");

        var runner = new TacticalMissionRunner(mission, new RngStreams(fixture.Seed));

        for (int step = 0; step < maxSteps; step++)
        {
            if (mission.ObjectiveOutcome.WorkSteps > 0)
                return;

            if (mission.Squad[0].Action is not { IsComplete: false })
            {
                mission.PendingOrders.Add(
                    new TacticalOrder(mission.Squad[0].Id, hack, InteractableId: workInteractableId));
            }

            runner.Step();

            if (mission.IsOver)
                return;
        }
    }

    /// <summary>Issues a MoveTo through the player's channel and steps until it lands.</summary>
    private static void MoveToAndRun(
        TacticalState state,
        SquadComposition composition,
        TacticalActor mover,
        TacticalPosition target,
        ulong seed,
        int maxSteps,
        List<(SiteRoomId Room, TacticalOrder Order)>? issued = null)
    {
        var runner = new TacticalMissionRunner(state, new RngStreams(seed));

        for (int step = 0; step < maxSteps; step++)
        {
            // Standing orders are single-shot — popped once they start — so the intent is
            // held and re-issued, which is what a player tapping the key twice does.
            if (mover.Action is not { IsComplete: false })
            {
                TacticalOrder? order = RoleBehaviours.TacticalOrderFor(
                    state, composition, mover,
                    new SquadStandingOrder(SquadOrderKind.MoveTo, target),
                    state.CommandPost);

                if (order is not null && !ActionSystem.Validate(state, order).IsRejected)
                {
                    state.PendingOrders.Add(order);
                    issued?.Add((state.RoomOf(mover)!.Id, order));
                }
            }

            runner.Step();

            if (state.IsOver)
                return;
        }
    }
}
