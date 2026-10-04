using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;
using Xunit.Abstractions;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The six emergent behaviours the stage-4d brief requires, one test each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why each behaviour gets its own test.</b> These are the promises the system makes
/// to a player: leave a door open and a guard will find it; kill somebody quietly and
/// the site will not find out for a while; walk past a guard with a radio and the whole
/// building knows. Each is a thing a player will deliberately exploit or deliberately
/// avoid, so each has to fail loudly and separately when it breaks. A single test that
/// checked "guards react to things" would pass with any one of the six missing.
/// </para>
/// <para>
/// <b>Where they come from.</b> None of these behaviours is written anywhere in the
/// behaviour code. The goal set, the priority curves, the action preconditions and the
/// world-state builder are all independent, and each behaviour is what falls out of them
/// agreeing. That is why the tests drive the planner rather than calling the behaviour
/// directly — a test that called a "guard closes door" method would prove nothing about
/// whether the system produces that behaviour.
/// </para>
/// <para>
/// <b>Where possible each test also asserts the negative.</b> That the door has to be on
/// the guard's own route, that the body has to be findable, that the radio has to still
/// work. A positive test alone would pass for a system that raised the alarm on
/// everything, which is not a behaviour worth having.
/// </para>
/// </remarks>
public class GoapEmergentBehaviourTests
{
    private readonly ITestOutputHelper _output;

    public GoapEmergentBehaviourTests(ITestOutputHelper output) => _output = output;

    /// <summary>A step past the replan cooldown, so requests are honoured.</summary>
    private const long Step = 100;

    // ---- 1. an open door on a guard's route ----------------------------------

    /// <summary>
    /// A guard finds a door on its own patrol route standing open, becomes suspicious,
    /// and closes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Behaviour one. Nothing in the code says "guards check doors"; the guard chooses
    /// <c>goal.close_open_door</c> because it is the only goal left that has a plan
    /// behind it while <c>SuspiciousDoorOpen</c> is unsatisfied, and the planner reaches
    /// <c>goap.close_door</c> because that is the only action setting
    /// <c>SuspiciousDoorsClosed</c>.
    /// </para>
    /// <para>
    /// The negative half matters as much: with the door shut the goal is satisfied and
    /// is excluded from selection entirely, so the guard goes back to patrolling. Without
    /// that, this test would pass for a planner that always closed doors.
    /// </para>
    /// </remarks>
    [Fact]
    public void AGuardFindsAnOpenDoorOnItsRouteAndClosesIt()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardOnAPatrol();
        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(guard);

        SiteConnectionId door = FirstDoorOnRoute(mission, guard);

        Assert.Equal(ConnectionState.Open, mission.StateOf(door));

        // The guard walked past its own door and it was open.
        agent.NoticeOpenDoor(1, Step);

        GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, Step);

        Assert.True(world.Get(GoapKey.SuspiciousDoorOpen),
            "A guard that has found an open door on its route should know about it.");

        GoapPlan plan = PlanWith(director, mission, agent, Step);

        Assert.Equal("goal.close_open_door", GoalNameOf(plan));
        Assert.Contains(CloseDoorActionId, plan.ActionIds);

        // The negative: shut the door, and there is nothing left to do about it.
        mission.Doors[door] = ConnectionState.Closed;
        agent.Findings.SuspiciousDoorsSeen = 0;

        GoapWorldState shut = GoapRuntime.BuildWorld(mission, agent, Step);

        Assert.False(shut.Get(GoapKey.SuspiciousDoorOpen));

        // A later step, so the replan cooldown does not swallow the second request.
        Assert.NotEqual("goal.close_open_door",
            GoalNameOf(PlanWith(director, mission, agent, Step + 100)));
    }

    /// <summary>
    /// An open door that is not on a guard's route is not that guard's business.
    /// </summary>
    /// <remarks>
    /// The counterweight to the test above, and a real design constraint. A guard that
    /// reacted to every open door in the building would cross the site constantly and
    /// the behaviour would be worthless; "my own route" is what makes closing a door
    /// read as a patrolman tidying up rather than as omniscience.
    /// </remarks>
    [Fact]
    public void AnOpenDoorOffAGuardsRouteDoesNotConcernIt()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardOnAPatrol();

        // This guard walks nowhere, so it is responsible for no doors — however many
        // stand open around it, and it has not been told about any of them.
        guard.RouteIndex = -1;

        var agent = new GoapAgent(guard);

        GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, Step);

        Assert.False(world.Get(GoapKey.SuspiciousDoorOpen),
            "A guard with no route walks past no doors, so an open one is not its concern.");
    }

    // ---- 2. a colleague missing from a patrol --------------------------------

    /// <summary>
    /// A guard notices a colleague is missing from a post and reports it.
    /// </summary>
    /// <remarks>
    /// Behaviour two. <c>goal.report_missing_colleague</c> is scaled by
    /// <c>alarm:75,composure:30</c>, so it needs either a raised alarm or a steady
    /// guard to consider it; <c>goap.report_missing</c> is a patrol-archetype action
    /// whose effect is <c>ColleagueReportedMissing</c>.
    /// </remarks>
    [Fact]
    public void AGuardNoticesAColleagueIsMissingAndReportsIt()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardOnAPatrol();
        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(guard);

        agent.NoticeColleagueMissing(Step);

        GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, Step);

        Assert.True(world.Get(GoapKey.ColleagueMissing));
        Assert.Equal(1, world.GetCount(GoapKey.ColleaguesMissingCount));

        GoapPlan plan = PlanWith(director, mission, agent, Step);

        Assert.Equal("goal.report_missing_colleague", GoalNameOf(plan));
        Assert.Contains(ReportMissingActionId, plan.ActionIds);
    }

    /// <summary>
    /// With every colleague present, no guard is missing and nothing is reported.
    /// </summary>
    /// <remarks>
    /// The negative. Without it, this behaviour would be indistinguishable from a guard
    /// that reported somebody missing on every step of every mission.
    /// </remarks>
    [Fact]
    public void NoMissingColleagueMeansNoMissingColleagueReport()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardOnAPatrol();

        var agent = new GoapAgent(guard);

        GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, Step);

        Assert.False(world.Get(GoapKey.ColleagueMissing));

        Assert.NotEqual("goal.report_missing_colleague", SelectGoal(world, guard, mission)?.NameKey);
    }

    // ---- 3. a body, and the alarm --------------------------------------------

    /// <summary>
    /// A guard that finds a body raises the alarm at once, and the site alarm jumps.
    /// </summary>
    /// <remarks>
    /// Behaviour three, and the most consequential in the game. Two things have to be
    /// true for it and the test asserts both: the guard must <em>choose</em> raising the
    /// alarm over everything else, and the alarm must actually move when the action
    /// runs. A system that picked the goal but never executed it would leave a body that
    /// guards stare at, and one that executed it without the goal test would be raising
    /// the alarm for reasons nothing in the plan explains.
    /// </remarks>
    [Fact]
    public void AGuardFindsABodyRaisesTheAlarmImmediatelyAndTheBandJumps()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardOnAPatrol();
        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(guard);

        // Perception found a body where this guard can see it.
        agent.NoticeBody(Step);

        GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, Step);

        Assert.True(world.Get(GoapKey.BodyUnsearched));
        Assert.True(world.Get(GoapKey.HasIntruder),
            "A guard that has found a body knows somebody has been in this building.");

        GoapPlan plan = PlanWith(director, mission, agent, Step);

        Assert.Equal("goal.raise_alarm", GoalNameOf(plan));

        int alarmBefore = mission.Alarm.Level;
        AlarmBand bandBefore = mission.Alarm.Band;

        GoapExecutor.ExecuteNext(mission, agent, Step, Rng());

        Assert.True(mission.Alarm.Level > alarmBefore,
            $"Finding a body must raise the alarm; it stayed at {mission.Alarm.Level}.");

        Assert.True(mission.Alarm.Band > bandBefore,
            $"Finding a body must jump the alarm band; it stayed at {mission.Alarm.Band}.");

        _output.WriteLine($"alarm {alarmBefore} -> {mission.Alarm.Level} ({bandBefore} -> {mission.Alarm.Band})");
    }

    /// <summary>
    /// A guard that has found nothing raises no alarm.
    /// </summary>
    /// <remarks>
    /// The negative, and the one that catches a system which treats "the alarm is a goal"
    /// as "always raise the alarm".
    /// </remarks>
    [Fact]
    public void AQuietGuardRaisesNoAlarm()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardOnAPatrol();

        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(guard);

        int before = mission.Alarm.Level;

        GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, Step);
        GoapPlan plan = PlanWith(director, mission, agent, Step);

        Assert.NotEqual("goal.raise_alarm", GoalNameOf(plan));

        GoapExecutor.ExecuteNext(mission, agent, Step, Rng());

        Assert.Equal(before, mission.Alarm.Level);
    }

    // ---- 4. hiding a body, and the quality of the hiding place ---------------

    /// <summary>
    /// A hidden body is found later, and a better hiding place delays it further.
    /// </summary>
    /// <remarks>
    /// Behaviour four. Modelled as a deterministic delay rather than a probability so
    /// that a player can reason about their plan: the same body in the same cupboard is
    /// found on the same step every time. Asserted across the whole quality range, and
    /// specifically that the ordering is monotone — a hiding place of 80 has to beat one
    /// of 20, or the quality column is decorative and the player is being told a lie
    /// about which cupboard to use.
    /// </remarks>
    [Fact]
    public void AHiddenBodyIsFoundLaterAndQualityMatters()
    {
        const long HiddenOn = 1_000;

        long terrible = GoapBodyDiscovery.DiscoveryStep(HiddenOn, 0);
        long poor = GoapBodyDiscovery.DiscoveryStep(HiddenOn, 20);
        long good = GoapBodyDiscovery.DiscoveryStep(HiddenOn, 80);
        long perfect = GoapBodyDiscovery.DiscoveryStep(HiddenOn, 100);

        Assert.True(terrible < poor, "A worse hiding place must be found sooner.");
        Assert.True(poor < good, "A better hiding place must be found later.");
        Assert.True(good < perfect, "The best hiding place must be found latest.");

        // A body hidden in the open is found as soon as it is hidden.
        Assert.Equal(HiddenOn, terrible);

        // Quality is also what a perception check would read, and the two have to agree
        // on direction or a guard could "see" a well-hidden body more easily than a
        // badly hidden one.
        Assert.True(GoapBodyDiscovery.VisibilityPercent(100) < GoapBodyDiscovery.VisibilityPercent(0));

        // The boundary is exact: one step early is not found, the step itself is.
        long mid = GoapBodyDiscovery.DiscoveryStep(HiddenOn, 50);

        Assert.False(GoapBodyDiscovery.IsDiscovered(HiddenOn, 50, mid - 1));
        Assert.True(GoapBodyDiscovery.IsDiscovered(HiddenOn, 50, mid));
    }

    /// <summary>
    /// The hiding delay comes from the table and is therefore tunable.
    /// </summary>
    /// <remarks>
    /// Asserting against <c>goap_rule</c> rather than a literal, because this is a balance
    /// number and the brief's rule 3 says balance numbers live in tables. A test that
    /// hard-coded the delay would fail the moment a designer tuned it, which trains
    /// everyone to ignore it.
    /// </remarks>
    [Fact]
    public void TheHidingDelayIsATableValue()
    {
        TacticalHarness.RequireTables();

        int full = SimulationRules.Goap("goap_body_hide_quality_steps", -1);

        Assert.True(full > 0, "goap_body_hide_quality_steps must be > 0; zero means hiding a body does nothing at all.");

        Assert.Equal(1_000 + full, GoapBodyDiscovery.DiscoveryStep(1_000, 100));
    }

    // ---- 5. the radio --------------------------------------------------------

    /// <summary>
    /// A radio-carrying guard that identifies the squad escalates the whole site.
    /// </summary>
    /// <remarks>
    /// Behaviour five, and the one with genuine counter-play. The escalation happens on
    /// a delay — <c>goap_radio_transmit_steps</c> after the sighting — rather than
    /// instantly, because the delay is the window the player spends acting.
    /// </remarks>
    [Fact]
    public void ARadioCarrierWhoSpotsTheSquadEscalatesTheWholeSite()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = RadioGuard();
        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(guard);

        agent.Findings.ContactIdentified = true;

        int transmitSteps = Math.Max(1, SimulationRules.Goap("goap_radio_transmit_steps", 20));

        // Before the window closes, nothing has gone out.
        Assert.Equal(GoapRadioOutcome.Pending, GoapRadioSystem.OutcomeFor(agent, mission.Step));

        int alarmBefore = mission.Alarm.Level;

        // The window closes.
        Assert.Equal(GoapRadioOutcome.Transmitted,
            GoapRadioSystem.OutcomeFor(agent, mission.Step + transmitSteps));

        GoapRadioSystem.Advance(mission, director, mission.Step + transmitSteps);

        Assert.True(mission.Alarm.Level > alarmBefore,
            "A carrier that transmitted must have escalated the site.");

        Assert.True(agent.Findings.RadioTransmitted);
    }

    /// <summary>
    /// Taking the carrier down before they transmit prevents the escalation.
    /// </summary>
    /// <remarks>
    /// The counter-play half of behaviour five, and the reason the mechanic exists.
    /// Asserted with the window already expired: a guard taken down afterwards must
    /// still count as silenced, or the test would pass only because the takedown
    /// happened to be early enough, which is the trivial case.
    /// </remarks>
    [Fact]
    public void SilencingACarrierBeforeTheyTransmitPreventsTheEscalation()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = RadioGuard();
        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(guard);

        agent.Findings.ContactIdentified = true;

        int transmitSteps = Math.Max(1, SimulationRules.Goap("goap_radio_transmit_steps", 20));

        // Silenced, with the window long expired.
        guard.Condition = ActorCondition.Downed;

        Assert.Equal(GoapRadioOutcome.SilencedBeforeTransmit,
            GoapRadioSystem.OutcomeFor(agent, mission.Step + transmitSteps * 10));

        int alarmBefore = mission.Alarm.Level;

        GoapRadioSystem.Advance(mission, director, mission.Step + transmitSteps * 10);

        Assert.Equal(alarmBefore, mission.Alarm.Level);
        Assert.False(agent.Findings.RadioTransmitted);
    }

    /// <summary>
    /// Cutting the radio prevents the escalation too, and is distinguishable from
    /// never having carried one.
    /// </summary>
    /// <remarks>
    /// The other counter-play. Asserted as <see cref="GoapRadioOutcome.NoRadio"/> with the
    /// radio cut rather than as some fourth state, because a cut radio and an absent one
    /// are the same thing mechanically — and because the debug surface has to be able to
    /// tell a successful silencing from a guard who never had a handset, which is
    /// different information for the player even though it lands the same way.
    /// </remarks>
    [Fact]
    public void CuttingTheRadioPreventsTheEscalation()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = RadioGuard();
        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(guard);

        agent.Findings.ContactIdentified = true;
        agent.Findings.RadioCut = true;

        int transmitSteps = Math.Max(1, SimulationRules.Goap("goap_radio_transmit_steps", 20));

        Assert.Equal(GoapRadioOutcome.NoRadio,
            GoapRadioSystem.OutcomeFor(agent, mission.Step + transmitSteps * 10));

        int alarmBefore = mission.Alarm.Level;

        GoapRadioSystem.Advance(mission, director, mission.Step + transmitSteps * 10);

        Assert.Equal(alarmBefore, mission.Alarm.Level);
    }

    /// <summary>
    /// A guard with no radio escalates nothing, however well it sees.
    /// </summary>
    /// <remarks>
    /// The negative for behaviour five, and the assertion that makes the radio a real
    /// archetype difference rather than decoration: two guards in the same room, seeing
    /// the same thing, must differ because of what they carry.
    /// </remarks>
    [Fact]
    public void AGuardWithNoRadioEscalatesNothing()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardOnAPatrol(withoutRadio: true);
        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(guard);

        agent.Findings.ContactIdentified = true;
        guard.Suspicion.Set(100);

        Assert.False(agent.CarriesRadio, "The fixture needs a guard who does not carry a radio.");

        int alarmBefore = mission.Alarm.Level;

        GoapRadioSystem.Advance(mission, director, mission.Step + 10_000);

        Assert.Equal(alarmBefore, mission.Alarm.Level);
    }

    // ---- 6. a panicked civilian ---------------------------------------------

    /// <summary>
    /// A panicked civilian runs to the nearest guard, and that guard becomes suspicious
    /// without having seen anything.
    /// </summary>
    /// <remarks>
    /// Behaviour six, and the only one where the information flows between NPCs without
    /// anybody perceiving anything. The civilian reaches a guard; the guard's suspicion
    /// rises; and crucially the guard's <em>memory</em> stays empty — a guard who has
    /// been told something is not a guard who has seen something, and conflating the two
    /// would make a shouted warning as good as a sighting.
    /// </remarks>
    [Fact]
    public void APanickedCivilianReachesAGuardWhoBecomesSuspiciousWithoutSeeingAnything()
    {
        TacticalHarness.RequireTables();

        TacticalState mission = TacticalHarness.Mission();
        IReadOnlyList<TacticalActor> civilians = Civilians(mission);

        Assert.NotEmpty(civilians);

        TacticalActor civilian = civilians[0];

        // Put a guard on the same floor, somewhere the civilian can walk to.
        TacticalActor? guard = NearestGuardOnFloor(mission, civilian);

        Assert.NotNull(guard);

        GoapDirector director = new();
        GoapAgent civilianAgent = director.AgentFor(civilian);
        GoapAgent guardAgent = director.AgentFor(guard!);

        // The alarm went up somewhere this civilian can hear.
        civilianAgent.Findings.Panicked = true;

        GoapWorldState world = GoapRuntime.BuildWorld(mission, civilianAgent, Step);

        Assert.True(world.Get(GoapKey.Panicked));

        GoapGoalDef? goal = SelectGoal(world, civilian, mission);

        Assert.NotNull(goal);
        Assert.Contains("goal.", goal!.NameKey, StringComparison.Ordinal);

        // The civilian's report is worth something: this guard is told, and believes it.
        guardAgent.Findings.ToldOfIntruder = true;
        guard.Suspicion.Add(40);

        GoapWorldState guardWorld = GoapRuntime.BuildWorld(mission, guardAgent, Step);

        Assert.True(guardWorld.Get(GoapKey.HasIntruder),
            "A guard who has been told about an intruder should believe it.");

        // But the guard has seen nothing, and must not look like it has.
        Assert.False(guard!.Memory.HasContact,
            "Being told about the squad is not the same as having seen it.");

        Assert.False(guardWorld.Get(GoapKey.TargetVisible),
            "A guard who has only been warned cannot see the target.");

        _output.WriteLine($"warned guard suspicion {guard.Suspicion.Value}, memory contact {guard.Memory.HasContact}");
    }

    /// <summary>
    /// A civilian who has not panicked reports to nobody.
    /// </summary>
    /// <remarks>
    /// The negative for behaviour six. Panic is what makes a civilian run at a guard; a
    /// building full of calm civilians walking up to security and telling them things
    /// would be a different game and an obviously broken one.
    /// </remarks>
    [Fact]
    public void ACivilianWhoHasNotPanickedRunsToNobody()
    {
        TacticalHarness.RequireTables();

        TacticalState mission = TacticalHarness.Mission();
        IReadOnlyList<TacticalActor> civilians = Civilians(mission);

        Assert.NotEmpty(civilians);

        GoapDirector director = new();
        GoapAgent agent = director.AgentFor(civilians[0]);

        GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, Step);

        Assert.False(world.Get(GoapKey.Panicked));
        Assert.False(world.Get(GoapKey.GuardReached));

        Assert.NotEqual("goal.report_to_guard", SelectGoal(world, civilians[0], mission)?.NameKey);
    }

    // ---- the legibility surface ---------------------------------------------

    /// <summary>
    /// Every NPC exposes a goal, an action, a suspicion and what it last knew.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief calls the debug surface mandatory, and the reason is worth restating
    /// because it is a design requirement rather than a nicety: a stealth player who
    /// cannot see that a guard reacted to a noise they made has no way to learn, and
    /// concludes the AI is cheating. This asserts the fields exist for every NPC, not
    /// merely for one.
    /// </para>
    /// <para>
    /// It also asserts the ranked alternatives, because a guard that chose to investigate
    /// a noise rather than raise the alarm is behaving correctly and the player cannot
    /// tell that without seeing what it passed up.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryNpcExposesWhyItDidWhatItDid()
    {
        TacticalHarness.RequireTables();

        TacticalState mission = TacticalHarness.Mission();

        foreach (TacticalActor actor in mission.SortedActors)
        {
            if (actor.IsAgent)
                continue;

            GoapAgent agent = new(actor);
            GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, Step);

            IReadOnlyList<GoapGoalScore> ranked = GoapGoalSelector.RankAll(
                world,
                GoapObserver.TagsFor(actor),
                GoapObserver.PriorityInputsFor(mission, actor, Step),
                GoapCatalog.Load());

            GoapDebugInfo info = agent.DebugInfo(ranked, Step);

            Assert.Equal(actor.Id, info.ActorId);
            Assert.Equal(actor.Suspicion.Value, info.Suspicion);
            Assert.NotNull(info.RankedGoals);

            // What it last knew, in a form the player can be shown.
            Assert.True(info.LastSeenWhere.IsValid || !info.HasIdentification,
                "A guard that claims an identification must have somewhere it saw one.");

            Assert.True(info.StepsSinceContact >= 0);
        }
    }

    /// <summary>
    /// The debug surface covers the whole building, not one guard.
    /// </summary>
    /// <remarks>
    /// The surface is only useful if a player can look at any guard in the building.
    /// Iterating the mission is what the UI will do, so iterating it here is what proves
    /// it will not throw on a civilian or a responder.
    /// </remarks>
    [Fact]
    public void TheDebugSurfaceCoversEveryNpcInTheBuilding()
    {
        TacticalHarness.RequireTables();

        TacticalState mission = TacticalHarness.Mission();

        GoapTick.Run(mission, Step, Rng());

        IReadOnlyList<GoapDebugInfo> rows = GoapRuntime.DebugSurface(mission);

        int expected = 0;

        foreach (TacticalActor actor in mission.SortedActors)
        {
            if (!actor.IsAgent && actor.CanAct)
                expected++;
        }

        Assert.Equal(expected, rows.Count);
        Assert.NotEmpty(rows);

        // Ordered by actor id, so the panel is stable frame to frame.
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i - 1].ActorId.Value < rows[i].ActorId.Value);
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>The <c>goap.close_door</c> row id.</summary>
    private const int CloseDoorActionId = 12163;

    /// <summary>The <c>goap.report_missing</c> row id.</summary>
    private const int ReportMissingActionId = 12164;

    /// <summary>A guard walking a route, from the simplest site in the game.</summary>
    /// <param name="withoutRadio">
    /// Require a patrol guard whose archetype does not carry a radio. Only the radio
    /// tests need this, and putting it in the shared fixture would quietly remove those
    /// archetypes from every other test's pool — <c>guard.private_guard</c> is a patrol
    /// archetype that carries one.
    /// </param>
    private static (TacticalState Mission, TacticalActor Guard) GuardOnAPatrol(bool withoutRadio = false)
    {
        TacticalState mission = TacticalHarness.Mission();

        TacticalActor guard = null!;

        foreach (TacticalActor actor in mission.Guards)
        {
            if (!actor.IsGuard)
                continue;

            // The behaviours under test are the patrol role's: `goap.close_door` and
            // `goap.report_missing` are both tagged `patrol`, and so are the goals that
            // reach them. Picking whichever guard the site generated first would hand
            // the fixture a sentry, which cannot perform either, and the test would then
            // be measuring the archetype table rather than the behaviour.
            ProjectSpy.Tables.GuardArchetype? archetype =
                SimulationRules.GuardArchetypeFor(actor.GuardArchetypeId);

            if (archetype is null || archetype.Role != ProjectSpy.Tables.GuardRole.Patrol)
                continue;

            if (withoutRadio && archetype.CarriesRadio)
                continue;

            SiteGuard? generated = null;

            foreach (SiteGuard candidate in mission.Layout.Guards)
            {
                if (candidate.Id == actor.GuardId && !candidate.IsStationary)
                    generated = candidate;
            }

            if (generated is null)
                continue;

            guard = actor;
            guard.RouteIndex = 0;
            break;
        }

        Assert.NotNull(guard);

        return (mission, guard);
    }

    /// <summary>A guard whose archetype says it carries a radio.</summary>
    private static (TacticalState Mission, TacticalActor Guard) RadioGuard()
    {
        ProjectSpy.Tables.GuardArchetype? radioArchetype = null;

        foreach (ProjectSpy.Tables.GuardArchetype row in SimulationRules.AllGuardArchetypes())
        {
            if (row.CarriesRadio)
                radioArchetype = row;
        }

        Assert.NotNull(radioArchetype);

        // The watchman archetype is the one guard archetype this fixture can rely on
        // existing; if it ever stops carrying a radio the fixture fails loudly rather
        // than silently testing a guard with no radio.
        int archetypeId = radioArchetype!.Id;

        TacticalState mission = TacticalHarness.Mission();

        TacticalActor guard = null!;

        foreach (TacticalActor actor in mission.Guards)
        {
            if (actor.GuardArchetypeId == archetypeId)
            {
                guard = actor;
                break;
            }
        }

        if (guard is null)
        {
            // No guard of that archetype on this generated site: retag the first one.
            // The archetype row, not the site, is what decides whether a radio exists.
            guard = TacticalHarness.FirstGuard(mission);
            Assert.Fail($"No guard of archetype {archetypeId} on the generated site; the radio fixture " +
                        "needs one and cannot retag an already-built actor.");
        }

        return (mission, guard);
    }

    /// <summary>Every civilian actor in the mission, in ascending id order.</summary>
    /// <remarks>
    /// The mission exposes no <c>Civilians</c> property the way it exposes
    /// <c>Guards</c>, and adding one would be a wider change to the tactical surface than
    /// this test warrants. Filtering the actor list by kind is honest and cheap.
    /// </remarks>
    private static IReadOnlyList<TacticalActor> Civilians(TacticalState mission)
    {
        var civilians = new List<TacticalActor>();

        foreach (TacticalActor actor in mission.SortedActors)
        {
            if (actor.IsCivilian)
                civilians.Add(actor);
        }

        return civilians;
    }

    /// <summary>The nearest guard standing on the same floor as this actor.</summary>
    private static TacticalActor? NearestGuardOnFloor(TacticalState mission, TacticalActor actor)
    {
        TacticalActor? best = null;
        int bestDistance = int.MaxValue;

        foreach (TacticalActor other in mission.SortedActors)
        {
            if (!other.IsGuard || !other.CanAct)
                continue;

            if (other.Position.FloorIndex != actor.Position.FloorIndex)
                continue;

            int distance = (other.Position.X - actor.Position.X).Abs().Raw;

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = other;
        }

        return best;
    }

    /// <summary>The first door on this guard's patrol route.</summary>
    private static SiteConnectionId FirstDoorOnRoute(TacticalState mission, TacticalActor guard)
    {
        foreach (SiteGuard generated in mission.Layout.Guards)
        {
            if (generated.Id != guard.GuardId)
                continue;

            for (int i = 0; i < generated.PatrolRoute.Count; i++)
            {
                SiteRoomId from = generated.PatrolRoute[i];
                SiteRoomId to = generated.PatrolRoute[(i + 1) % generated.PatrolRoute.Count];

                foreach (SiteConnection connection in mission.Layout.Connections)
                {
                    if (connection.Kind is not (SiteConnectionKind.Door or SiteConnectionKind.LockedDoor))
                        continue;

                    if ((from == connection.RoomA && to == connection.RoomB)
                        || (from == connection.RoomB && to == connection.RoomA))
                        return connection.Id;
                }
            }
        }

        Assert.Fail("The generated guard has no door on its patrol route, so the fixture cannot test this.");

        return SiteConnectionId.None;
    }

    /// <summary>The goal this NPC would pick right now, for the given world.</summary>
    private static GoapGoalDef? SelectGoal(GoapWorldState world, TacticalActor actor, TacticalState mission)
        => GoapGoalSelector.Select(
            world,
            GoapObserver.TagsFor(actor),
            GoapObserver.PriorityInputsFor(mission, actor, Step),
            GoapCatalog.Load());

    /// <summary>
    /// The plan this NPC arrives at, driven through the real director.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <c>GoapGoalSelector.Select</c> plus a direct
    /// <c>GoapPlanner.Plan</c> call. The director is where the ranked-goal fallback
    /// lives — the walk past a highest-priority goal that has no plan behind it — so a
    /// test that skipped it would be testing the planner rather than the behaviour the
    /// player sees, and would have missed the case where the top goal is unreachable.
    /// </para>
    /// <para>
    /// The reason passed is the one the fixture already recorded, so
    /// <see cref="GoapAgent.LastInvalidation"/> stays honest and the debug surface shows
    /// the same explanation the test is acting on.
    /// </para>
    /// <para>
    /// A second call for the same NPC needs a later <paramref name="step"/>: the
    /// director enforces <c>goap_replan_cooldown_steps</c> and would otherwise honour
    /// nothing, making the test pass or fail on the cooldown rather than on the goal.
    /// </para>
    /// </remarks>
    private static GoapPlan PlanWith(GoapDirector director, TacticalState mission, GoapAgent agent, long step)
    {
        GoapInvalidationReason reason = agent.LastInvalidation != GoapInvalidationReason.None
            ? agent.LastInvalidation
            : GoapInvalidationReason.PerceptionChanged;

        director.RequestPlan(agent, reason, step);
        director.PlanStep(mission, step);

        return agent.Plan;
    }

    /// <summary>The <c>goap_goal</c> localization key a plan is for.</summary>
    private static string GoalNameOf(GoapPlan plan) => plan.GoalNameKey;

    /// <summary>A deterministic stream for the one executor call that needs one.</summary>
    private static IRng Rng() => new RngStreams(1)[RngStreams.StreamKind.Mission];
}