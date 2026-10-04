using System.Diagnostics;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;
using Xunit.Abstractions;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Five thousand headless missions, run to termination or to a step ceiling, checking
/// that the simulation's invariants hold on every one.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that finds the bugs nobody thought to write a case for. Every other
/// tactical test checks a behaviour somebody already decided should happen; this one
/// runs the whole pipeline over every site template and asserts only that nothing broke
/// — no actor off the map, no negative health, no alarm outside its scale, a mission
/// that terminates. Four of the bugs fixed during stage 4c were found this way or by the
/// tests it is built out of: agents spawning inside a guard's identification range, the
/// squad accumulating suspicion of the site's own guards, the alarm counting the
/// infiltrators among the things the site was aware of, and a noise loud enough to be
/// heard in its own maker's ears.
/// </para>
/// <para>
/// <b>Scripted policies, not random ones.</b> A policy is a function of the seed, so the
/// sweep is reproducible and a failure can be re-run from the seed in the message. Three
/// policies, because they fail differently: a patient one that creeps to the objective,
/// a reckless one that sprints and opens doors, and a passive one that gives no orders at
/// all. The passive policy is the one that exposed the deployment bugs — a mission
/// nobody touched should be a mission the player can still lose on their own terms, and
/// when it burned in thirty-four steps that was a bug rather than a difficulty setting.
/// </para>
/// <para>
/// <b>The distributions are printed, not asserted.</b> An outcome histogram is a stage-4c
/// deliverable in its own right: it is how you see that nine tenths of missions end the
/// same way, and it is in the test log rather than in a comment that goes stale.
/// </para>
/// </remarks>
public class TacticalFuzzSweepTests
{
    /// <summary>Missions run in the sweep.</summary>
    private const int MissionCount = 5_000;

    /// <summary>
    /// Steps a mission gets before the sweep stops watching it.
    /// </summary>
    /// <remarks>
    /// Sized against <c>TacticalMission.DefaultObjectiveSteps</c> (900), so a mission
    /// gets enough runway to walk to its objective, be noticed, react and — for the
    /// reckless policy — get itself into trouble. A taller ceiling buys very little and
    /// costs a great deal: the whole sweep is 5,000 missions, and a full-length mission
    /// on a tier-4 site is around 1.3 seconds of simulation, which turns a five-minute
    /// sweep into a three-quarter-hour one. What this ceiling cannot cover is the long
    /// tail of a mission nobody ends, and the fuzz sweep's job is the invariants, not the
    /// endgame distribution — the outcome histogram below is read with that in mind.
    /// </remarks>
    private const int StepCeiling = 1_200;

    /// <summary>Missions run before the timed sweep, to pay for JIT.</summary>
    private const int WarmupCount = 25;

    private readonly ITestOutputHelper _output;

    public TacticalFuzzSweepTests(ITestOutputHelper output) => _output = output;

    /// <summary>The four ways a player can behave.</summary>
    private enum Policy
    {
        /// <summary>Creeps to the objective in a crouch.</summary>
        Patient,

        /// <summary>Sprints, opens doors, and is heard doing it.</summary>
        Reckless,

        /// <summary>Gives no orders at all.</summary>
        Passive,

        /// <summary>Walks at the nearest guard and hits it, lethal and otherwise.</summary>
        Aggressive,
    }

    [Fact]
    public void FiveThousandMissionsAllHoldTheirInvariants()
    {
        TacticalHarness.RequireTables();

        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();
        Assert.NotEmpty(templates);

        var outcomes = new Dictionary<ProjectSpy.Core.Tactical.MissionOutcome, int>();
        var alarmBands = new Dictionary<AlarmBand, int>();
        var policies = new Dictionary<Policy, int>();

        int totalSteps = 0;
        int terminated = 0;
        int heatTotal = 0;
        int lethalTotal = 0;
        int evidenceMax = 0;
        int longestMission = 0;
        int longestSeed = 0;
        long slowestMicroseconds = 0;
        int slowestIndex = 0;

        for (int index = 0; index < MissionCount + WarmupCount; index++)
        {
            ProjectSpy.Tables.SiteTemplate template = templates[index % templates.Count];
            Policy policy = (Policy)(index % 4);
            ulong seed = 0xF005UL + (ulong)index;
            int missionId = index;

            ulong mapSeed = SiteGenerator.DeriveMapSeed(seed, missionId);

            var watch = new Stopwatch();
            watch.Restart();

            SiteLayout layout = SiteGenerator.Generate(template.Id, template.Tier, missionId, seed, mapSeed);

            WorldState world = TacticalHarness.WorldWithSquad(seed);
            TacticalState mission = TacticalMission.Create(layout, SquadIn(world), missionId, Tick.Zero);
            var runner = new TacticalMissionRunner(mission, new RngStreams(seed));

            int steps = RunOne(runner, policy, seed, index >= WarmupCount);

            watch.Stop();

            if (index < WarmupCount)
                continue;

            totalSteps += steps;

            if (steps >= StepCeiling)
                terminated++;
            else if (steps > longestMission)
            {
                longestMission = steps;
                longestSeed = missionId;
            }

            long elapsed = watch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            if (elapsed > slowestMicroseconds)
            {
                slowestMicroseconds = elapsed;
                slowestIndex = missionId;
            }

            outcomes.TryGetValue(mission.Outcome, out int seenOutcomes);
            outcomes[mission.Outcome] = seenOutcomes + 1;

            alarmBands.TryGetValue(mission.Alarm.Band, out int seenBands);
            alarmBands[mission.Alarm.Band] = seenBands + 1;

            policies.TryGetValue(policy, out int seenPolicies);
            policies[policy] = seenPolicies + 1;

            heatTotal += mission.HeatGained;
            lethalTotal += mission.LethalActs;

            if (mission.EvidenceLevel > evidenceMax)
                evidenceMax = mission.EvidenceLevel;

            // The invariants. Each one is a thing the mission itself promises, so a
            // failure here is a mission that broke its own contract rather than a test
            // that wanted something the design does not promise.
            Assert.True(mission.ValidateActors(out string problem),
                $"Mission {missionId} (seed {seed}, template {template.Id}, {policy}) broke an actor invariant at " +
                $"step {steps}: {problem}");

            Assert.InRange(mission.Alarm.Level, 0, AlarmState.Max);

            Assert.InRange(mission.Outcome, ProjectSpy.Core.Tactical.MissionOutcome.InProgress,
                ProjectSpy.Core.Tactical.MissionOutcome.Burned);

            Assert.True(mission.Step <= StepCeiling,
                $"Mission {missionId} ran past the step ceiling at {mission.Step}.");
        }

        // Every policy must have been exercised, or the sweep silently tested one way of
        // playing three thousand times and reported the total as coverage.
        foreach (Policy policy in Enum.GetValues<Policy>())
        {
            policies.TryGetValue(policy, out int count);
            Assert.True(count > 0, $"Policy {policy} never ran.");
        }

        _output.WriteLine($"missions            {MissionCount}");
        _output.WriteLine($"steps total         {totalSteps:N0}");
        _output.WriteLine($"mean steps          {totalSteps / (double)MissionCount:F0}");
        _output.WriteLine($"hit step ceiling    {terminated}");
        _output.WriteLine($"longest ending      {longestMission} steps (mission {longestSeed})");
        _output.WriteLine($"slowest mission     {slowestMicroseconds / 1000.0:F1}ms (mission {slowestIndex})");
        _output.WriteLine($"heat total          {heatTotal:N0}");
        _output.WriteLine($"lethal acts         {lethalTotal:N0}");
        _output.WriteLine($"max evidence        {evidenceMax}");

        _output.WriteLine(" ");
        _output.WriteLine("outcomes:");

        foreach (KeyValuePair<ProjectSpy.Core.Tactical.MissionOutcome, int> entry in outcomes.OrderBy(e => e.Key.ToString()))
            _output.WriteLine($"  {entry.Key,-14} {entry.Value,6}  {Percent(entry.Value)}");

        _output.WriteLine(" ");
        _output.WriteLine("final alarm band:");

        foreach (KeyValuePair<AlarmBand, int> entry in alarmBands.OrderBy(e => e.Key.ToString()))
            _output.WriteLine($"  {entry.Key,-14} {entry.Value,6}  {Percent(entry.Value)}");

        _output.WriteLine(" ");
        _output.WriteLine("policy:");

        foreach (KeyValuePair<Policy, int> entry in policies.OrderBy(e => e.Key.ToString()))
            _output.WriteLine($"  {entry.Key,-14} {entry.Value,6}  {Percent(entry.Value)}");

        // A sweep where nothing ever succeeds is a sweep that found a rule that cannot be
        // satisfied. Asserting the spread rather than a target outcome keeps this honest:
        // the balance is a designer's business, but "every mission ends Burned" is a bug
        // report wearing a balance sheet.
        Assert.True(
            outcomes.Count > 1,
            "Every mission ended the same way, so one of the outcomes is unreachable.");

        Assert.True(
            alarmBands.Keys.Any(band => band != AlarmBand.Calm),
            "No mission ever raised the alarm, so the alarm system is not being exercised.");
    }

    /// <summary>
    /// Runs one mission, returning how many steps it took.
    /// </summary>
    /// <param name="check">
    /// Whether to assert the mid-run invariants. The warm-up missions skip them so that
    /// a JIT-warmed-but-unverified first run cannot be mistaken for a sweep result.
    /// </param>
    private static int RunOne(TacticalMissionRunner runner, Policy policy, ulong seed, bool check)
    {
        TacticalState mission = runner.State;
        int step = 0;

        for (step = 1; step <= StepCeiling; step++)
        {
            foreach (TacticalOrder order in OrdersFor(mission, policy, seed, step))
                mission.PendingOrders.Add(order);

            runner.Step();

            if (check && step % 100 == 0)
            {
                Assert.True(mission.ValidateActors(out string problem),
                    $"Mission {mission.MissionId} broke an actor invariant at step {step}: {problem}");

                Assert.InRange(mission.Alarm.Level, 0, AlarmState.Max);
            }

            if (mission.Outcome != ProjectSpy.Core.Tactical.MissionOutcome.InProgress)
                break;
        }

        return step;
    }

    /// <summary>
    /// The orders one step of one policy consists of.
    /// </summary>
    /// <remarks>
    /// Takes the mission as well as the seed because the aggressive policy has to name
    /// a real target: guard ids are minted by the generator and differ from site to site,
    /// so an order list built from the seed alone could not point at anything. The other
    /// three policies ignore it.
    /// </remarks>
    private static IReadOnlyList<TacticalOrder> OrdersFor(TacticalState mission, Policy policy, ulong seed, int step)
    {
        switch (policy)
        {
            case Policy.Patient:
            case Policy.Reckless:
            {
                IReadOnlyList<TacticalOrder> scripted =
                    policy == Policy.Patient ? ScriptedOrders.Patient(seed) : ScriptedOrders.Reckless(seed);

                var issued = new List<TacticalOrder>(scripted.Count);

                // Each squad member is re-issued on its own offset, so the four of them do
                // not all act on the same step.
                for (int i = 0; i < scripted.Count; i++)
                    issued.Add(ScriptedOrders.AtStep(i, step, scripted[i]));

                return issued;
            }

            case Policy.Aggressive:
                return MeleeNearestGuard(mission, step);

            default:
                return Array.Empty<TacticalOrder>();
        }
    }

    /// <summary>
    /// Every agent who is free hits the nearest guard on their own floor, alternating
    /// between the lethal and non-lethal melee rows.
    /// </summary>
    /// <remarks>
    /// The policy exists to sweep the half of the simulation the movement ones cannot
    /// reach: damage, downing, bleeding out, capture, and the heat and evidence counters
    /// a lethal act writes. A sweep whose scripted players never swing anything reports
    /// a total of zero lethal acts across five thousand missions, which is a true number
    /// about a game nobody played.
    /// </remarks>
    private static IReadOnlyList<TacticalOrder> MeleeNearestGuard(TacticalState mission, int step)
    {
        var orders = new List<TacticalOrder>(mission.Squad.Count);

        foreach (TacticalActor agent in mission.Squad)
        {
            if (!agent.CanAct)
                continue;

            TacticalActor? target = NearestGuardOn(mission, agent);

            if (target is null)
                continue;

            int actionId = (step / ScriptedOrders.ReissueInterval) % 2 == 0 ? LethalMelee : Melee;

            orders.Add(new TacticalOrder(
                agent.Id,
                actionId,
                TargetActorId: target.Id,
                Target: target.Position));
        }

        return orders;
    }

    /// <summary>The closest active guard on the same floor as <paramref name="actor"/>.</summary>
    private static TacticalActor? NearestGuardOn(TacticalState mission, TacticalActor actor)
    {
        TacticalActor? nearest = null;
        int best = int.MaxValue;

        foreach (TacticalActor guard in mission.Guards)
        {
            if (guard.Position.FloorIndex != actor.Position.FloorIndex)
                continue;

            int distance = Fixed32.Distance(actor.Position.X, guard.Position.X).Raw;

            // Ascending guard id breaks a tie, so "the nearest" is a function of the
            // mission rather than of the order the guards happen to sit in a list.
            if (distance < best || (distance == best && nearest is not null && guard.Id.Value < nearest.Id.Value))
            {
                best = distance;
                nearest = guard;
            }
        }

        return nearest;
    }

    /// <summary><c>action.combat_melee</c>, the non-lethal row.</summary>
    private const int Melee = 12421;

    /// <summary><c>action.combat_melee_lethal</c>, the lethal row.</summary>
    private const int LethalMelee = 12423;

    /// <summary>The squad a freshly built world holds, in roster order.</summary>
    private static IReadOnlyList<Agent> SquadIn(WorldState world)
    {
        var squad = new List<Agent>();

        foreach (Agent agent in world.Agents.Values.OrderBy(a => a.Id.Value))
            squad.Add(agent);

        return squad;
    }

    private static string Percent(int count)
        => $"{count * 100.0 / MissionCount,6:F2}%";
}