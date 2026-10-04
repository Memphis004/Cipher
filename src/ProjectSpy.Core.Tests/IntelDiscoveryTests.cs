using System.Diagnostics;
using ProjectSpy.Tables;
using Xunit;
using Xunit.Abstractions;

using SiteTemplateRow = ProjectSpy.Tables.SiteTemplate;
using SleeperOpRow = ProjectSpy.Tables.SleeperOp;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Stage 4b's probability test: over ten thousand simulated operations, the rate at which
/// sites notice sleepers matches the table.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that would catch a discovery formula quietly diverging from
/// <c>sleeper_op.csv</c>. A formula is easy to get subtly wrong — off by one divisor, a
/// clamp applied to the wrong side of the subtraction, a percentage applied to a count
/// instead of to a chance — and every one of those failures is invisible in a unit test
/// that only checks "a good agent is safer than a bad one".
/// </para>
/// <para>
/// The expectation is recomputed here from the table rather than read back from
/// <see cref="SleeperSystem.DiscoveryChancePercent"/>. Calling the code under test to
/// produce the number it should match would make the test a tautology: a formula that
/// returned its own input would pass. What is shared with the implementation is the table
/// access and the arithmetic; what is re-derived independently is the formula itself.
/// </para>
/// <para>
/// Batched rather than one-world-per-trial: <see cref="SleeperSystem.Tick"/> walks every
/// operation in the world each tick, so a single world holding ten thousand operations
/// would make this test quadratic in its own sample size and turn a two-second check into
/// a two-minute one.
/// </para>
/// </remarks>
public class IntelDiscoveryTests
{
    /// <summary>Operations simulated, per the stage-4b brief.</summary>
    private const int OperationCount = 10_000;

    /// <summary>Ticks each operation is left running.</summary>
    private const int TicksPerOperation = 24;

    /// <summary>Operations per world, to keep the per-tick walk short.</summary>
    private const int BatchSize = 100;

    /// <summary>
    /// How many standard errors of the observed mean the test tolerates.
    /// </summary>
    /// <remarks>
    /// Four is loose enough that an unlucky run does not fail the build and tight enough
    /// that a real formula error — which moves the mean by a multiple of the tolerance,
    /// not a percent of it — cannot hide inside it.
    /// </remarks>
    private const double ToleranceInStandardErrors = 4.0;

    private readonly ITestOutputHelper _output;

    public IntelDiscoveryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DiscoveryHappensAsOftenAsTheTableSaysItShould()
    {
        IReadOnlyList<SiteTemplateRow> templates = SimulationRules.AllSiteTemplates();
        Assert.NotEmpty(templates);

        var watch = Stopwatch.StartNew();

        int discovered = 0;
        double expectedSum = 0;
        double varianceSum = 0;

        var riskBuckets = new SortedDictionary<int, (int Trials, int Found)>();

        int remaining = OperationCount;

        while (remaining > 0)
        {
            int size = Math.Min(BatchSize, remaining);
            remaining -= size;

            var session = new GameSession(0xD15C0DEUL + (ulong)discovered + (ulong)size);
            var sink = new RecordingSink();

            var operations = new List<SleeperOperation>();
            var chances = new List<int>();

            for (int index = 0; index < size; index++)
            {
                SiteTemplateRow site = templates[index % templates.Count];
                Agent agent = World.Hire(session);

                // Spread the agents across the whole skill range, including hopeless ones.
                // A sweep of only competent agents would land every trial on the clamp at
                // the bottom of the formula and prove that a roll of zero happens.
                agent.Skills = new SkillSet(
                    Infiltration: 5 + (index * 7) % 85,
                    Combat: 0,
                    Tech: 0,
                    Social: 5 + (index * 11) % 85,
                    Nerve: 0);

                SleeperOperation operation = SleeperSystem.Start(
                    session.World, agent.Id, site.Id, Tick.Zero, sink);

                operations.Add(operation);
                chances.Add(ExpectedChance(site, agent));
            }

            var before = operations.ToDictionary(
                o => o.AgentId,
                o => o.DiscoveredOnTick.HasValue);

            for (int tick = 1; tick <= TicksPerOperation; tick++)
                SleeperSystem.Tick(session.World, new Tick(tick), sink);

            for (int index = 0; index < size; index++)
            {
                bool wasDiscovered = before[operations[index].AgentId];
                bool isDiscovered = operations[index].DiscoveredOnTick.HasValue;

                Assert.False(wasDiscovered, "an operation started already discovered");

                int chance = chances[index];

                // Once found, an operation stops accruing and stops being rolled against,
                // so a discovery at any point in the window counts the same.
                if (isDiscovered)
                    discovered++;

                // P(at least one discovery in TicksPerOperation ticks at p per tick).
                double p = chance / 100.0;
                double expected = 1.0 - Math.Pow(1.0 - p, TicksPerOperation);

                expectedSum += expected;
                varianceSum += expected * (1.0 - expected);

                riskBuckets.TryGetValue(chance, out (int Trials, int Found) bucket);
                riskBuckets[chance] = (bucket.Trials + 1, bucket.Found + (isDiscovered ? 1 : 0));
            }
        }

        watch.Stop();

        double n = OperationCount;
        double observed = discovered / n;
        double expectedMean = expectedSum / n;
        double standardError = Math.Sqrt(varianceSum) / n;
        double tolerance = ToleranceInStandardErrors * standardError;

        _output.WriteLine($"operations            {OperationCount} x {TicksPerOperation} ticks");
        _output.WriteLine($"discovered            {discovered} ({observed:P3})");
        _output.WriteLine($"expected              {expectedMean:P3}");
        _output.WriteLine($"standard error        {standardError:P3}, tolerance +/-{tolerance:P3}");
        _output.WriteLine($"elapsed               {watch.ElapsedMilliseconds} ms");

        foreach (KeyValuePair<int, (int Trials, int Found)> bucket in riskBuckets)
        {
            double rate = (double)bucket.Value.Found / bucket.Value.Trials;
            double perTick = bucket.Key / 100.0;
            double predicted = 1.0 - Math.Pow(1.0 - perTick, TicksPerOperation);

            _output.WriteLine($"  risk {bucket.Key,3}%/tick   found {bucket.Value.Found,5} of {bucket.Value.Trials,5} = {rate:P3}"
                              + $"   predicted {predicted:P3}");
        }

        Assert.True(
            Math.Abs(observed - expectedMean) <= tolerance,
            $"Discovery ran at {observed:P3}, the table predicts {expectedMean:P3}, and the "
            + $"tolerance was +/-{tolerance:P3}. Over {OperationCount} operations that is a real "
            + "divergence between the formula and sleeper_op.csv, not sampling noise.");
    }

    [Fact]
    public void AWellPreparedAgentIsNeverFoundAtAnEasySite()
    {
        SiteTemplateRow easiest = null!;
        foreach (SiteTemplateRow candidate in SimulationRules.AllSiteTemplates())
        {
            if (easiest is null || candidate.SecurityGrade < easiest.SecurityGrade)
                easiest = candidate;
        }

        var session = new GameSession(0x51EFUL);
        Agent agent = World.Hire(session);
        agent.Skills = new SkillSet(Infiltration: 100, Combat: 0, Tech: 0, Social: 100, Nerve: 0);

        var operation = new SleeperOperation
        {
            AgentId = agent.Id,
            SiteId = easiest.Id,
            StartedOnTick = Tick.Zero,
        };

        int chance = SleeperSystem.DiscoveryChancePercent(operation, agent, easiest);

        Assert.Equal(0, chance);

        // Ten thousand ticks at a flat zero: the clamp has to hold, or a long operation
        // against a well-covered site would creep upwards one tick at a time.
        var sink = new RecordingSink();
        SleeperSystem.Start(session.World, agent.Id, easiest.Id, Tick.Zero, sink);

        for (int tick = 1; tick <= 10_000; tick++)
            SleeperSystem.Tick(session.World, new Tick(tick), sink);

        Assert.Null(session.World.SleeperOperations[0].DiscoveredOnTick);
    }

    [Fact]
    public void IntelAccruesAtExactlyTheRateTheTableSays()
    {
        // The reason for the hundredths accumulator: 100 / 24 is 4 in integer division, so
        // a naive implementation gives tier 1 (24 ticks per percent) and tier 2 (36)
        // the same speed whenever both truncate to the same number, and the table stops
        // meaning anything.
        foreach (SleeperOpRow rules in SimulationRules.AllSleeperOps())
        {
            SiteTemplateRow site = FirstSiteOfTier(rules.SiteTier);
            var session = new GameSession(0xACC0UL + (ulong)rules.SiteTier);
            Agent agent = World.Hire(session);

            // Skills high enough to sit on the zero clamp for the whole run, so the only
            // thing this measures is the accrual.
            agent.Skills = new SkillSet(Infiltration: 100, Combat: 0, Tech: 0, Social: 100, Nerve: 0);

            var sink = new RecordingSink();
            SleeperOperation operation = SleeperSystem.Start(
                session.World, agent.Id, site.Id, Tick.Zero, sink);

            for (int ticks = 1; ticks <= rules.TicksPerIntelPercent * 3; ticks++)
            {
                SleeperSystem.Tick(session.World, new Tick(ticks), sink);

                Assert.Equal(
                    ticks / rules.TicksPerIntelPercent,
                    operation.IntelPercent);

                Assert.True(operation.DiscoveredOnTick is null);
            }

            Assert.Equal(3, operation.IntelPercent);
        }
    }

    [Fact]
    public void DiscoveryRaisesHeatAndBurnsTheOperation()
    {
        SiteTemplateRow site = FirstSiteOfTier(1);
        SleeperOpRow rules = SimulationRules.SleeperOpFor(1)!;

        var session = new GameSession(0xBEEFUL);
        var sink = new RecordingSink();

        Agent agent = World.Hire(session);

        // Zero skills on an easy site: the base chance alone, which the table guarantees is
        // non-zero, so the roll lands within a tick or two.
        agent.Skills = SkillSet.Zero;

        SleeperOperation operation = SleeperSystem.Start(
            session.World, agent.Id, site.Id, Tick.Zero, sink);

        int heatBefore = session.World.Resources.Heat;

        for (int tick = 1; tick <= 400 && operation.DiscoveredOnTick is null; tick++)
            SleeperSystem.Tick(session.World, new Tick(tick), sink);

        Assert.NotNull(operation.DiscoveredOnTick);

        Assert.True(
            operation.Status is SleeperStatus.Burned or SleeperStatus.Poisoned,
            $"a discovered operation ended up {operation.Status}");

        Assert.False(operation.IsActive && operation.Status == SleeperStatus.Burned);

        Assert.Equal(heatBefore + rules.HeatOnDiscovery, session.World.Resources.Heat);
        Assert.True(sink.Saw(GameEventKind.SleeperDiscovered));

        // A poisoned operation keeps reporting; a burned one does not. That difference is
        // the whole consequence of the roll, so it is asserted rather than assumed.
        Assert.Equal(
            operation.Status == SleeperStatus.Poisoned,
            sink.Saw(GameEventKind.SleeperPoisoned));
    }

    [Fact]
    public void RecallingKeepsTheIntelGatheredSoFar()
    {
        SiteTemplateRow site = FirstSiteOfTier(1);
        var session = new GameSession(0xCA11UL);
        var sink = new RecordingSink();

        Agent agent = World.Hire(session);
        agent.Skills = new SkillSet(Infiltration: 100, Combat: 0, Tech: 0, Social: 100, Nerve: 0);

        SleeperOperation operation = SleeperSystem.Start(
            session.World, agent.Id, site.Id, Tick.Zero, sink);

        for (int tick = 1; tick <= 240; tick++)
            SleeperSystem.Tick(session.World, new Tick(tick), sink);

        int kept = operation.IntelPercent;
        Assert.True(kept > 0, "the operation should have accrued something to lose");

        Assert.True(SleeperSystem.Recall(session.World, operation, new Tick(241), sink));
        Assert.Equal(SleeperStatus.Extracted, operation.Status);
        Assert.Equal(kept, operation.IntelPercent);

        // ...and it stops accruing, and the report it already produced is still there.
        for (int tick = 242; tick <= 480; tick++)
            SleeperSystem.Tick(session.World, new Tick(tick), sink);

        Assert.Equal(kept, operation.IntelPercent);

        // Recalling twice is not an error, it is a no-op: the UI can afford to be unsure.
        Assert.False(SleeperSystem.Recall(session.World, operation, new Tick(481), sink));
    }

    [Fact]
    public void TheBaseSideViewTellsThePlayerWhatTheyWouldBeBuying()
    {
        SiteTemplateRow site = FirstSiteOfTier(2);
        var session = new GameSession(0xB0D1UL);
        var sink = new RecordingSink();

        Agent agent = World.Hire(session);
        agent.Skills = new SkillSet(Infiltration: 20, Combat: 5, Tech: 0, Social: 15, Nerve: 0);

        SleeperOperation operation = SleeperSystem.Start(
            session.World, agent.Id, site.Id, Tick.Zero, sink);

        SleeperOperationView view = SleeperOperationView.Build(session.World, operation, Tick.Zero);

        Assert.Equal(agent.Id, view.AgentId);
        Assert.Equal(site.Id, view.SiteId);
        Assert.Equal(site.Tier, view.SiteTier);
        Assert.Equal(IntelBand.EntranceOnly, view.Band);
        Assert.True(view.CanRecall);
        Assert.False(view.IsCompleteIntel);

        Assert.NotNull(view.NextBand);
        Assert.NotNull(view.NextBandContents);
        // From nothing, the layout band is 25 percent away, and the estimate agrees
        // with the table's rate rather than being an independent guess.
        SleeperOpRow rules = SimulationRules.SleeperOpFor(site.Tier)!;
        Assert.Equal(
            IntelBands.MinPercentFor(IntelBand.Layout) - view.IntelPercent,
            view.PercentToNextBand);
        Assert.Equal(25, view.PercentToNextBand);
        Assert.Equal(25 * rules.TicksPerIntelPercent, view.EstimatedTicksToNextBand);

        // The next-band preview must be a strict widening, never a narrowing. A preview
        // that offered less than the player already has would mean the bands are not
        // ordered, and the whole "pay more to know more" promise is void.
        IntelBandContents current = view.BandContents;
        IntelBandContents next = view.NextBandContents!;

        Assert.True(Implies(current, next));

        // ...and every band implies the ones below it.
        for (int index = 1; index < IntelBands.Ladder.Count; index++)
        {
            IntelBandContents lower = IntelBands.ContentsOf(IntelBands.Ladder[index - 1]);
            IntelBandContents higher = IntelBands.ContentsOf(IntelBands.Ladder[index]);
            Assert.True(Implies(lower, higher), $"{IntelBands.Ladder[index]} revealed less than {IntelBands.Ladder[index - 1]}");
        }

        // The risk shown is the risk taken, or the operation is useless as a contract.
        int risk = SleeperSystem.DiscoveryChancePercent(operation, agent, site);
        Assert.Equal(risk, view.DiscoveryRiskPerTickPercent);

        // A finished operation shows a finished estimate rather than a countdown to zero.
        operation.IntelPercent = 100;
        SleeperOperationView done = SleeperOperationView.Build(session.World, operation, Tick.Zero);

        Assert.True(done.IsCompleteIntel);
        Assert.Null(done.NextBand);
        Assert.Null(done.NextBandContents);
        Assert.Equal(-1, done.PercentToNextBand);
        Assert.Equal(-1, done.EstimatedTicksToNextBand);
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// Recomputes the discovery chance from the tables, without asking the implementation.
    /// </summary>
    private static int ExpectedChance(SiteTemplateRow site, Agent agent)
    {
        SleeperOpRow rules = SimulationRules.SleeperOpFor(site.Tier)!;

        int divisor = Math.Max(1, SimulationRules.Intel("intel_security_grade_divisor", 10));
        int infiltrationSteps = Math.Max(1, SimulationRules.Intel("intel_infiltration_points_per_reduction", 8));
        int socialSteps = Math.Max(1, SimulationRules.Intel("intel_social_points_per_reduction", 10));

        int chance = rules.BaseDiscoveryChancePerTick
                     + site.SecurityGrade / divisor
                     - (agent.Skills.Infiltration / infiltrationSteps) * rules.InfiltrationModifier
                     - (agent.Skills.Social / socialSteps) * rules.SocialModifier;

        return Math.Clamp(
            chance,
            SimulationRules.Intel("intel_discovery_chance_min", 0),
            Math.Min(100, SimulationRules.Intel("intel_discovery_chance_max", 95)));
    }

    private static SiteTemplateRow FirstSiteOfTier(int tier)
    {
        foreach (SiteTemplateRow site in SimulationRules.AllSiteTemplates())
        {
            if (site.Tier == tier)
                return site;
        }

        throw new InvalidOperationException($"No site template at tier {tier}.");
    }

    /// <summary>True when everything <paramref name="lower"/> reveals, higher reveals too.</summary>
    private static bool Implies(IntelBandContents lower, IntelBandContents higher)
        => (!lower.RevealsEntrance || higher.RevealsEntrance)
           && (!lower.RevealsFloorCount || higher.RevealsFloorCount)
           && (!lower.RevealsRoomPlacement || higher.RevealsRoomPlacement)
           && (!lower.RevealsRoomTypes || higher.RevealsRoomTypes)
           && (!lower.RevealsConnections || higher.RevealsConnections)
           && (!lower.RevealsLockStates || higher.RevealsLockStates)
           && (!lower.RevealsGuardCounts || higher.RevealsGuardCounts)
           && (!lower.RevealsLightLevels || higher.RevealsLightLevels)
           && (!lower.RevealsPatrolRoutes || higher.RevealsPatrolRoutes)
           && (!lower.RevealsObjective || higher.RevealsObjective)
           && (!lower.RevealsExtraction || higher.RevealsExtraction)
           && (!lower.RevealsHoldingRooms || higher.RevealsHoldingRooms);
}