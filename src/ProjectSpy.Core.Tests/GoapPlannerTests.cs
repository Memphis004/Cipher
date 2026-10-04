using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;
using Xunit.Abstractions;

// The generated row types are read directly here so the key-vocabulary test can see
// exactly what the CSV said, before Core resolves it.
using GoapActionRow = ProjectSpy.Tables.GoapAction;
using GoapGoalRow = ProjectSpy.Tables.GoapGoal;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The properties the planner must have whatever else it does.
/// </summary>
/// <remarks>
/// <para>
/// Four things are asserted here, and they are the four that are expensive to discover
/// later: determinism, the node budget, the absence of omniscience, and the key
/// vocabulary matching the data. The emergent behaviours live in
/// <c>GoapEmergentBehaviourTests</c>; this file is about the machinery.
/// </para>
/// <para>
/// <b>Determinism first.</b> It is tested first because everything else is only useful
/// if a mission replays. A GOAP planner that produces a different — even equally good —
/// plan on a replay invalidates every recorded run, and it does so silently.
/// </para>
/// </remarks>
public class GoapPlannerTests
{
    private readonly ITestOutputHelper _output;

    public GoapPlannerTests(ITestOutputHelper output) => _output = output;

    // ---- determinism ---------------------------------------------------------

    /// <summary>
    /// The same inputs produce a byte-identical plan, every time.
    /// </summary>
    /// <remarks>
    /// Run repeatedly rather than twice: a single comparison would pass against a
    /// planner whose ordering was unstable in a way that only showed up under different
    /// heap states. Twenty iterations also exercises the planner's reused scratch
    /// buffers, which is where a stale-state bug would live.
    /// </remarks>
    [Fact]
    public void IdenticalInputsProduceAnIdenticalPlan()
    {
        TacticalHarness.RequireTables();

        for (int iteration = 0; iteration < 20; iteration++)
        {
            (TacticalState mission, TacticalActor guard) = GuardWithASighting();

            GoapWorldState world = GoapRuntime.BuildWorld(mission, new GoapAgent(guard), mission.Step);

            GoapCatalogData catalog = GoapCatalog.Load();
            GoapGoalDef? goal = SelectGoal(world, guard, catalog);

            Assert.NotNull(goal);

            var planner = new GoapPlanner(catalog);
            GoapPlan first = planner.Plan(world, goal!, AvailableActions(guard));

            for (int repeat = 0; repeat < 5; repeat++)
            {
                // A second planner, so the result cannot be an artefact of the first
                // planner's reused node buffers surviving from the previous call.
                var fresh = new GoapPlanner(catalog);
                GoapPlan again = fresh.Plan(world, goal!, AvailableActions(guard));

                Assert.Equal(first.ActionIds, again.ActionIds);
                Assert.Equal(first.TotalCost, again.TotalCost);
                Assert.Equal(first.NodesExpanded, again.NodesExpanded);
                Assert.Equal(first.GoalId, again.GoalId);
            }
        }
    }

    /// <summary>
    /// Two planners built separately, from separately parsed goals, agree exactly.
    /// </summary>
    /// <remarks>
    /// A second determinism check that would catch hash-order dependence specifically. If
    /// the open set were a <c>Dictionary</c> or the actions were iterated from a hash
    /// collection, two planners over identical data would frequently still agree — but
    /// not always, and "usually identical" is not a property a replay system can use.
    /// </remarks>
    [Fact]
    public void TwoCatalogsAgreeOnTheSamePlan()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardWithASighting();

        GoapWorldState world = GoapRuntime.BuildWorld(mission, new GoapAgent(guard), mission.Step);

        var plannerA = new GoapPlanner(GoapCatalog.Load());
        var plannerB = new GoapPlanner(GoapCatalog.Load());

        GoapGoalDef? goal = SelectGoal(world, guard, GoapCatalog.Load());

        Assert.NotNull(goal);

        GoapPlan a = plannerA.Plan(world, goal!, AvailableActions(guard));
        GoapPlan b = plannerB.Plan(world, goal!, AvailableActions(guard));

        Assert.Equal(a.ActionIds, b.ActionIds);
    }

    /// <summary>
    /// Replanning the same mission twice gives every NPC the same goal and the same
    /// plan, in the same order.
    /// </summary>
    /// <remarks>
    /// The end-to-end version. The unit-level checks above prove the search is
    /// deterministic given a world state; this proves the whole path is, including goal
    /// selection and the order NPCs are visited in — which is where a "sort by priority
    /// with an unstable tie-break" would hide.
    /// </remarks>
    [Fact]
    public void TwoRunsOfTheSameMissionPlanIdentically()
    {
        List<string> firstRun = PlanSignature(0x1234UL);
        List<string> secondRun = PlanSignature(0x1234UL);

        Assert.Equal(firstRun, secondRun);

        _output.WriteLine($"npcs planned        {firstRun.Count}");

        Assert.NotEmpty(firstRun);
    }

    /// <summary>
    /// Different knowledge produces different plans.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterweight to the determinism tests, and stated in the only way that is
    /// actually relevant: a planner that returned the same plan no matter what it knew
    /// would satisfy "identical inputs produce identical plans" perfectly while being
    /// completely useless.
    /// </para>
    /// <para>
    /// Written within one mission rather than across two seeds, because a seed changes
    /// the building but not what any given guard knows — two guards on two different
    /// sites who have both seen nothing are genuinely in the same situation and should
    /// plan the same way. What must differ is two guards in the <em>same</em> building
    /// where one saw the squad and one did not.
    /// </para>
    /// </remarks>
    [Fact]
    public void GuardsWhoKnowDifferentThingsPlanDifferently()
    {
        TacticalHarness.RequireTables();

        TacticalState mission = TacticalHarness.Mission();
        IReadOnlyList<TacticalActor> guards = mission.Guards;

        Assert.True(guards.Count >= 2, "The fixture needs two guards to compare.");

        TacticalActor ignorant = guards[0];
        TacticalActor informed = guards[1];

        // One guard has positively identified the squad; the other has seen nothing at
        // all. Nothing else about them differs.
        informed.Memory.NotePerception(
            new TacticalActorId(1),
            new TacticalPosition(informed.Position.FloorIndex, informed.Position.X),
            PerceptionLevel.Identified,
            mission.Step);

        informed.Suspicion.Set(90);
        ignorant.Suspicion.Set(0);

        var a = new GoapAgent(ignorant);
        var b = new GoapAgent(informed);

        GoapWorldState ignorantWorld = GoapRuntime.BuildWorld(mission, a, mission.Step);
        GoapWorldState informedWorld = GoapRuntime.BuildWorld(mission, b, mission.Step);

        Assert.False(ignorantWorld.Get(GoapKey.HasIntruder));
        Assert.True(informedWorld.Get(GoapKey.HasIntruder));

        GoapCatalogData catalog = GoapCatalog.Load();

        GoapGoalDef? ignorantGoal = SelectGoal(ignorantWorld, ignorant, catalog);
        GoapGoalDef? informedGoal = SelectGoal(informedWorld, informed, catalog);

        Assert.NotNull(informedGoal);
        Assert.NotEqual(ignorantGoal?.Id ?? 0, informedGoal!.Id);
    }

    // ---- the node budget -----------------------------------------------------

    /// <summary>
    /// No plan ever expands more nodes than the budget allows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted across every goal and every starting state a mission can present, not
    /// on one hand-built case. A budget that holds for the easy goal and fails for the
    /// one that needs five actions is not a budget.
    /// </para>
    /// <para>
    /// The check is on the reported <see cref="GoapPlan.NodesExpanded"/> and on the
    /// planner's own counter, because those are two different numbers and only one of
    /// them being right would mean a caller is being told the wrong thing.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPlanExceedsTheNodeBudget()
    {
        TacticalHarness.RequireTables();

        GoapCatalogData catalog = GoapCatalog.Load();
        IReadOnlyList<GoapActionDef> actions = catalog.Actions;

        int budget = SimulationRules.Goap("goap_node_budget", 256);

        // Every goal, against a world that satisfies nothing: the worst case, and the
        // only one that can actually exhaust a budget.
        var hostile = new GoapWorldState();

        foreach (GoapGoalDef goal in catalog.Goals)
        {
            hostile.Set(goal.Satisfaction[0].Key, !goal.Satisfaction[0].Negated);

            var planner = new GoapPlanner(catalog);
            GoapPlan plan = planner.Plan(hostile, goal, actions);

            Assert.True(plan.NodesExpanded <= budget,
                $"Goal {goal.Id} ({goal.NameKey}) expanded {plan.NodesExpanded} nodes, over the budget of {budget}.");

            Assert.True(planner.LastNodesExpanded <= budget,
                $"Goal {goal.Id} ({goal.NameKey}) reported {planner.LastNodesExpanded} nodes on the planner, " +
                $"over the budget of {budget}.");
        }
    }

    /// <summary>
    /// A budget too small to search with is reported, not silently ignored.
    /// </summary>
    /// <remarks>
    /// The failure mode a budget is supposed to prevent is a planner that quietly spends
    /// the whole frame. Reporting <see cref="GoapPlanStatus.BudgetExhausted"/> makes
    /// that visible; returning a plausible-looking partial plan would not.
    /// </remarks>
    [Fact]
    public void AnUnreachableGoalIsReportedRatherThanFaked()
    {
        TacticalHarness.RequireTables();

        GoapCatalogData catalog = GoapCatalog.Load();

        // Raise the alarm needs a contact; with no action that establishes one there is
        // genuinely no route to it.
        GoapGoalDef goal = catalog.Goals.First(g => g.NameKey == "goal.raise_alarm");

        var impossible = new GoapWorldState();
        var planner = new GoapPlanner(catalog);

        GoapPlan plan = planner.Plan(impossible, goal, Array.Empty<GoapActionDef>());

        Assert.Equal(GoapPlanStatus.NoPlan, plan.Status);
        Assert.Empty(plan.ActionIds);
    }

    // ---- the no-omniscience property -----------------------------------------

    /// <summary>
    /// Corrupting the squad's real position leaves every NPC's plan unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sharpest test in the stage.</b> The squad is moved across the building, to
    /// the far end of it, and back again, with every NPC's knowledge untouched. If any
    /// NPC could read where the squad actually is, its plan would follow them around and
    /// this comparison would fail.
    /// </para>
    /// <para>
    /// <b>What is deliberately not corrupted, and why.</b> The alarm, the doors, and each
    /// NPC's own memory are all left alone, because they are not hidden information:
    /// </para>
    /// <list type="bullet">
    /// <item>the alarm is public — a siren is something a guard can hear from anywhere,
    /// and reading it is not cheating;</item>
    /// <item>a door on a guard's own route is something it walks past, and reacting to
    /// an open door is correct behaviour rather than foresight;</item>
    /// <item>an NPC's memory is what it <em>believes</em>. Wiping or rewriting it and
    /// expecting the plan not to move would be asserting that a guard cannot react to
    /// learning something.</item>
    /// </list>
    /// <para>
    /// The naive version of this test — corrupting everything, including the alarm — does
    /// pass or fail for reasons that have nothing to do with omniscience, and fails
    /// against a correct implementation for the reasons above.
    /// </para>
    /// </remarks>
    [Fact]
    public void NpcsNeverActOnInformationTheyDoNotPossess()
    {
        const long Step = 100;

        TacticalState mission = TacticalHarness.Mission(seed: 0xD00DUL);

        List<string> before = KnowledgeSignature(mission, Step);

        Assert.NotEmpty(before);

        MoveTheSquad(mission, far: true);
        MoveTheSquad(mission, far: false);

        List<string> after = KnowledgeSignature(mission, Step);

        Assert.Equal(before, after);
    }

    /// <summary>
    /// One NPC learning something does not change any other NPC's plan.
    /// </summary>
    /// <remarks>
    /// The second half of the property, and the one that is easiest to get wrong. A
    /// design where one guard's reaction feeds the whole building is not cheating in the
    /// player's sense — nobody ever sees the guard react to nothing — but it destroys
    /// stealth just as effectively, because one glimpse re-ranks every guard's goals.
    /// </remarks>
    [Fact]
    public void OneGuardSightingTheSquadDoesNotChangeAnotherGuardsPlan()
    {
        const long Step = 100;

        TacticalState mission = TacticalHarness.Mission(seed: 0xD00EUL);
        IReadOnlyList<TacticalActor> guards = mission.Guards;

        Assert.True(guards.Count >= 2, "The fixture needs two guards to compare.");

        List<string> before = SignatureOf(guards[1], mission, Step);

        guards[0].Memory.NotePerception(
            new TacticalActorId(1),
            new TacticalPosition(guards[0].Position.FloorIndex, guards[0].Position.X),
            PerceptionLevel.Identified,
            mission.Step);

        guards[0].Suspicion.Set(100);

        List<string> after = SignatureOf(guards[1], mission, Step);

        Assert.Equal(before, after);
    }

    /// <summary>
    /// The same test is not vacuous: a guard that <em>has</em> seen the squad does
    /// change its plan.
    /// </remarks>
    /// <remarks>
    /// The control for the two tests above. If a guard's plan never moved regardless of
    /// what it knew, both no-omniscience tests would pass for the wrong reason. This one
    /// asserts the knowledge channel is actually connected.
    /// </remarks>
    [Fact]
    public void AGuardThatSeesTheSquadDoesChangeItsPlan()
    {
        const long Step = 100;

        TacticalState mission = TacticalHarness.Mission(seed: 0xD00FUL);

        List<string> before = AllSignatures(mission, Step);

        // Every guard on the floor now positively identifies the squad.
        foreach (TacticalActor guard in mission.Guards)
        {
            guard.Memory.NotePerception(
                new TacticalActorId(1),
                new TacticalPosition(guard.Position.FloorIndex, guard.Position.X),
                PerceptionLevel.Identified,
                mission.Step);

            guard.Suspicion.Set(95);
        }

        List<string> after = AllSignatures(mission, Step);

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// The same corruption does not change the mission's own view of the world.
    /// </summary>
    /// <remarks>
    /// The control for the test above, and it matters: if <c>MoveTheSquad</c> silently
    /// did nothing — a bad seed, a room that did not exist — then the no-omniscience test
    /// would pass vacuously. This asserts the corruption actually changed the mission, so
    /// a passing omniscience test means something.
    /// </remarks>
    [Fact]
    public void TheGroundTruthCorruptionActuallyChangesTheMission()
    {
        TacticalState mission = TacticalHarness.Mission(seed: 0xD00DUL);

        string quiet = AlarmAndPositionSignature(mission);

        MoveTheSquad(mission, far: true);

        string loud = AlarmAndPositionSignature(mission);

        Assert.NotEqual(quiet, loud);
    }

    /// <summary>
    /// A guard's world state depends only on what it perceived, not on where anyone is.
    /// </summary>
    /// <remarks>
    /// The same property at the level of one NPC, which gives a far more useful failure
    /// message than the whole-building comparison: it names the specific key that leaked.
    /// </remarks>
    [Fact]
    public void AGuardsWorldStateIsUnchangedByWhereTheSquadActuallyIs()
    {
        TacticalHarness.RequireTables();

        (TacticalState mission, TacticalActor guard) = GuardWithASighting();

        var agent = new GoapAgent(guard);
        GoapWorldState before = GoapRuntime.BuildWorld(mission, agent, mission.Step);

        // Put the whole squad somewhere else entirely.
        foreach (TacticalActor actor in mission.SortedActors)
        {
            if (actor.IsAgent)
                actor.Position = new TacticalPosition(actor.Position.FloorIndex + 3, new Fixed32(9999));
        }

        GoapWorldState after = GoapRuntime.BuildWorld(mission, agent, mission.Step);

        Assert.Equal(before.TrueFlags(), after.TrueFlags());
        Assert.Equal(before.Fingerprint(), after.Fingerprint());
    }

    // ---- the key vocabulary --------------------------------------------------

    /// <summary>
    /// Every key the GOAP tables name is declared, and every declared key is used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load-time check in <see cref="GoapCatalog"/> already refuses to start on an
    /// undeclared key. This asserts the other direction too: a key declared in
    /// <see cref="GoapKey"/> that no row mentions is dead vocabulary, and dead
    /// vocabulary is where a designer's next "why does this never fire" question comes
    /// from.
    /// </para>
    /// <para>
    /// The exception list is explicit and short. Every entry names a fact Core derives
    /// for the planner that the tables have no column for — the "steps since contact"
    /// counters, which are arithmetic over memory rather than a designer-set flag.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryKeyTheTablesNameIsDeclaredAndEveryDeclaredKeyIsUsed()
    {
        TacticalHarness.RequireTables();

        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (GoapActionRow action in SimulationRules.AllGoapActions())
        {
            foreach (string token in SplitTokens(action.Preconditions))
                used.Add(token);

            foreach (string token in SplitTokens(action.Effects))
                used.Add(token);
        }

        foreach (GoapGoalRow goal in SimulationRules.AllGoapGoals())
        {
            foreach (string token in SplitTokens(goal.SatisfactionCondition))
                used.Add(token);
        }

        foreach (string token in used)
        {
            Assert.True(GoapKeys.IsDeclared(token),
                $"The tables name the world-state key '{token}', which is not declared in GoapKey.");
        }

        foreach (GoapKeyDef def in GoapKeys.Definitions)
        {
            if (DerivedKeys.Contains(def.Name))
                continue;

            Assert.True(used.Contains(def.Name),
                $"GoapKey declares '{def.Name}', which no goap_action or goap_goal row mentions. " +
                "Either a row should use it or the declaration should go.");
        }
    }

    /// <summary>
    /// Keys Core derives for the planner that the tables have no column for.
    /// </summary>
    /// <remarks>
    /// Listed by name rather than matched by a naming convention, so that renaming one
    /// is a deliberate act that fails the test instead of a silent behaviour change.
    /// </remarks>
    private static readonly HashSet<string> DerivedKeys = new(StringComparer.Ordinal)
    {
        nameof(GoapKey.StepsSinceNoise),
        nameof(GoapKey.StepsSinceContact),
        nameof(GoapKey.SuspiciousDoorsSeen),
        nameof(GoapKey.ColleaguesMissingCount),
    };

    /// <summary>The declared key layout is internally consistent.</summary>
    [Fact]
    public void TheKeyLayoutIsSound()
    {
        Assert.Null(GoapKeys.ValidateLayout());

        // Flags and counters live in separate bands, and the world state's sizing must
        // agree with the declaration or a counter would overwrite a flag's bit.
        foreach (GoapKeyDef def in GoapKeys.Definitions)
        {
            if (def.Kind == GoapKeyKind.Flag)
                Assert.True(def.Slot < GoapKeys.CounterBandStart, $"'{def.Name}' is flagged but sits in the counter band.");
            else
                Assert.True(def.Slot >= GoapKeys.CounterBandStart, $"'{def.Name}' is a counter but sits in the flag band.");
        }
    }

    /// <summary>A fingerprint distinguishes states and is stable.</summary>
    [Fact]
    public void FingerprintsAreStableAndDistinguishing()
    {
        var a = new GoapWorldState();
        var b = new GoapWorldState();

        Assert.Equal(a.Fingerprint(), b.Fingerprint());

        b.Set(GoapKey.HasIntruder, true);

        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());

        b.Set(GoapKey.HasIntruder, false);
        Assert.Equal(a.Fingerprint(), b.Fingerprint());

        b.SetCount(GoapKey.StepsSinceContact, 3);
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());

        // Negative counts clamp to zero rather than storing a nonsense value that a
        // "steps since" condition would then read as "just now".
        b.SetCount(GoapKey.StepsSinceContact, -5);
        Assert.Equal(0, b.GetCount(GoapKey.StepsSinceContact));
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>A mission with one guard who has positively identified the squad.</summary>
    private static (TacticalState Mission, TacticalActor Guard) GuardWithASighting()
    {
        TacticalState mission = TacticalHarness.Mission();
        TacticalActor guard = TacticalHarness.FirstGuard(mission);

        guard.Memory.NotePerception(
            TacticalActorId.None,
            new TacticalPosition(guard.Position.FloorIndex, guard.Position.X),
            PerceptionLevel.Identified,
            mission.Step);

        guard.Suspicion.Set(90);

        return (mission, guard);
    }

    /// <summary>The actions this NPC's archetype may perform.</summary>
    private static IReadOnlyList<GoapActionDef> AvailableActions(TacticalActor actor)
    {
        GoapCatalogData catalog = GoapCatalog.Load();
        var allowed = new List<GoapActionDef>();

        foreach (GoapActionDef action in catalog.Actions)
        {
            if (action.RequiredTags.Count == 0)
            {
                allowed.Add(action);
                continue;
            }

            bool usable = false;

            foreach (string need in action.RequiredTags)
            {
                foreach (string have in GoapObserver.TagsFor(actor))
                {
                    if (string.Equals(need, have, StringComparison.Ordinal))
                    {
                        usable = true;
                        break;
                    }
                }
            }

            if (usable)
                allowed.Add(action);
        }

        return allowed;
    }

    /// <summary>The highest-priority goal an NPC would pick right now.</summary>
    private static GoapGoalDef? SelectGoal(GoapWorldState world, TacticalActor actor, GoapCatalogData catalog)
        => GoapGoalSelector.Select(
            world,
            GoapObserver.TagsFor(actor),
            new GoapPriorityInputs
            {
                Alarm = 60,
                Curiosity = actor.Suspicion.Value,
                SelfPreservation = 50,
                Composure = 20,
                Ally = 0,
                Order = 100,
                Compliance = 0,
            },
            catalog);

    /// <summary>
    /// Every NPC's goal and plan, as comparable text.
    /// </summary>
    private static List<string> PlanSignature(ulong seed)
    {
        TacticalState mission = TacticalHarness.Mission(seed: seed);
        GoapDirector director = new();

        // Give every NPC something to think about, so the signature reflects real plans
        // rather than thirty guards all idling.
        foreach (TacticalActor actor in mission.SortedActors)
        {
            if (!actor.IsAgent)
                actor.Suspicion.Set(70);
        }

        // A step well past the replan cooldown. At step zero the cooldown would swallow
        // every request — an agent's PlannedOnStep starts at zero, so zero minus zero
        // looks like "just planned" — and the signature would be a list of empty plans
        // that compare equal for every seed, passing a divergence test vacuously.
        const long Step = 100;

        GoapStepFacts facts = GoapStepFacts.Collect(mission);

        foreach (TacticalActor actor in facts.Actors)
        {
            if (!actor.IsAgent && actor.CanAct)
                director.RequestPlan(director.AgentFor(actor), GoapInvalidationReason.PerceptionChanged, Step);
        }

        director.PlanStep(mission, Step);

        var signature = new List<string>();

        foreach (GoapAgent agent in director.AgentsInOrder())
            signature.Add($"{agent.Actor.Id}:{agent.Plan.GoalId}:{string.Join(",", agent.Plan.ActionIds)}");

        return signature;
    }

    /// <summary>
    /// Every NPC's goal and plan, computed from what it knows, for this mission.
    /// </summary>
    /// <remarks>
    /// Reads the mission's own director rather than making a new one. The plans live on
    /// the agent that planned them, so a fresh director would report nothing and the
    /// comparison would be between two empty lists.
    /// </remarks>
    private static List<string> KnowledgeSignature(TacticalState mission, long step)
    {
        GoapDirector director = GoapRuntime.DirectorFor(mission);

        // One tick of notification, then a plan for everybody. This is exactly the
        // information the planner is permitted to use.
        GoapTick.Run(mission, step, new RngStreams(0xD00DUL)[RngStreams.StreamKind.Mission]);

        var signature = new List<string>();

        foreach (GoapAgent agent in director.AgentsInOrder())
        {
            GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, mission.Step);

            signature.Add(
                $"{agent.Actor.Id}:{agent.Plan.GoalId}:{string.Join(",", agent.Plan.ActionIds)}:" +
                $"{string.Join("|", world.TrueFlags())}");
        }

        return signature;
    }

    /// <summary>Every NPC's plan, for one mission at one step.</summary>
    private static List<string> AllSignatures(TacticalState mission, long step)
    {
        var signature = new List<string>();

        foreach (TacticalActor actor in mission.SortedActors)
        {
            if (!actor.IsAgent)
                signature.AddRange(SignatureOf(actor, mission, step));
        }

        return signature;
    }

    /// <summary>One NPC's world state and the goal it would pick, as comparable text.</summary>
    private static List<string> SignatureOf(TacticalActor actor, TacticalState mission, long step)
    {
        var agent = new GoapAgent(actor);

        GoapWorldState world = GoapRuntime.BuildWorld(mission, agent, step);

        GoapGoalDef? goal = GoapGoalSelector.Select(
            world,
            GoapObserver.TagsFor(actor),
            GoapObserver.PriorityInputsFor(mission, actor, step),
            GoapCatalog.Load());

        return new List<string>
        {
            $"{actor.Id}:{goal?.Id ?? 0}:{string.Join("|", world.TrueFlags())}",
        };
    }

    /// <summary>A short description of the mission's own ground truth.</summary>
    private static string AlarmAndPositionSignature(TacticalState mission)
    {
        var parts = new List<string> { $"alarm:{mission.Alarm.Level}" };

        foreach (TacticalActor actor in mission.SortedActors)
            parts.Add($"{actor.Id}@{actor.Position}");

        foreach (KeyValuePair<SiteConnectionId, ConnectionState> door in mission.Doors)
            parts.Add($"{door.Key}:{door.Value}");

        return string.Join(",", parts);
    }

    /// <summary>
    /// Moves the squad to the far end of the building, or back to a corner.
    /// </summary>
    /// <remarks>
    /// The only hidden ground truth in a stealth mission is where the squad actually is
    /// and what the other NPCs have worked out. This corrupts the first; the second is
    /// covered by
    /// <see cref="OneGuardSightingTheSquadDoesNotChangeAnotherGuardsPlan"/>. Both move
    /// actors to somewhere they demonstrably were not, so a planner reading either would
    /// produce a visibly different plan.
    /// </remarks>
    private static void MoveTheSquad(TacticalState mission, bool far)
    {
        foreach (TacticalActor actor in mission.SortedActors)
        {
            if (!actor.IsAgent)
                continue;

            actor.Position = far
                ? new TacticalPosition(actor.Position.FloorIndex + 9, new Fixed32(99999))
                : new TacticalPosition(actor.Position.FloorIndex, new Fixed32(1));
        }
    }

    /// <summary>Splits a condition cell into bare key names.</summary>
    private static IEnumerable<string> SplitTokens(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
            yield break;

        foreach (string comma in cell.Split(','))
        {
            foreach (string conjunct in comma.Split(new[] { "&&" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = conjunct.Trim().TrimStart('!').Trim();

                if (token.Length > 0)
                    yield return token;
            }
        }
    }
}