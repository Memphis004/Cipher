using ProjectSpy.Core.Missions;
using ProjectSpy.Tables;

// SimulationRules keeps its generated-bean aliases file-local so the two "room template"
// tables can never be confused. This file reads site_template and sleeper_op, so it
// repeats the two aliases it needs rather than reaching for types SimulationRules does
// not expose.
using SiteTemplateRow = ProjectSpy.Tables.SiteTemplate;
using SleeperOpRow = ProjectSpy.Tables.SleeperOp;

namespace ProjectSpy.Core;

/// <summary>
/// Pre-mission intelligence: the ticking half of the sleeper system.
/// </summary>
/// <remarks>
/// <para>
/// Runs on the strategic clock, once per tick, inside <c>EventChecks</c>. It does three
/// things in a fixed order, and the order is the design: accrue what the agent has
/// learned, check whether the site has noticed, then decide what the player is told.
/// Discovery after accrual means an agent found on this tick still reported once, which
/// is what makes a poisoned operation able to lie at all — there has to be a last
/// report to poison.
/// </para>
/// <para>
/// Every number here comes from <c>sleeper_op.csv</c> or <c>intel_rule</c>. The formula
/// for the discovery chance is written out at <see cref="DiscoveryChancePercent"/>,
/// which is public and pure so the distribution test can state its expectation
/// independently of the code under test.
/// </para>
/// </remarks>
public static class SleeperSystem
{
    /// <summary>
    /// The map seed of the building at a site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from the world seed and the <em>site</em>, not from a mission. A site is a
    /// place that exists whether or not anyone ever raids it; a mission is a visit to one.
    /// That distinction is not pedantry — it is the only way the intel can be about the
    /// building the player is actually going into. If the site were seeded from a mission
    /// id that does not exist until the player accepts a contract, then the report a
    /// sleeper filed last week would describe a building generated from a different seed,
    /// and every fact in it would be wrong in a way no amount of poisoning explains.
    /// </para>
    /// <para>
    /// Whoever dispatches a mission onto a site must call this, rather than deriving a
    /// seed of its own, so the layout the team walks into is the layout the report was
    /// written about.
    /// </para>
    /// </remarks>
    public static ulong SiteMapSeed(WorldState world, int siteId)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        return SiteGenerator.DeriveMapSeed(world.Seed, siteId);
    }

    /// <summary>The building at a site, regenerated from its seed.</summary>
    public static SiteLayout LayoutFor(WorldState world, int siteId)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        SiteTemplateRow? site = SimulationRules.SiteTemplateFor(siteId)
            ?? throw new InvalidOperationException($"No site template with id {siteId}.");

        return SiteGenerator.Generate(siteId, site.Tier, siteId, world.Seed, SiteMapSeed(world, siteId));
    }

    /// <summary>Hundredths of a percent accrued per tick.</summary>
    /// <remarks>
    /// A tick is worth exactly one hundredth of a percent whatever the rate, and the
    /// table says how many ticks make a whole one. See
    /// <see cref="SleeperOperation.IntelProgressHundredths"/> for why the remainder is
    /// carried rather than truncated away.
    /// </remarks>
    private const int HundredthsPerTick = 100;

    /// <summary>
    /// The top of the percentage scale the chance rolls on.
    /// </summary>
    /// <remarks>
    /// Named for what it is rather than "PercentScale": rule 10 bans a member named
    /// <c>Scale</c>, on the grounds that a scale is a thing Unity renders things by.
    /// This is a divisor, and the ban is right to make me name it precisely.
    /// </remarks>
    private const int PercentDenominator = 100;

    /// <summary>
    /// Inserts an agent into a site.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The agent is unavailable, or the site has no rule row for its tier.
    /// </exception>
    public static SleeperOperation Start(
        WorldState world,
        AgentId agentId,
        int siteId,
        Tick tick,
        IEventSink events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (events is null) throw new ArgumentNullException(nameof(events));

        Agent? agent = world.GetAgent(agentId)
            ?? throw new InvalidOperationException($"Agent {agentId} is not on the roster.");

        if (agent.Status == AgentStatus.Captured || agent.Status == AgentStatus.OnMission)
        {
            throw new InvalidOperationException(
                $"Agent {agentId} is {agent.Status} and cannot be inserted into a site.");
        }

        SiteTemplateRow? site = SimulationRules.SiteTemplateFor(siteId)
            ?? throw new InvalidOperationException($"No site template with id {siteId}.");

        if (SimulationRules.SleeperOpFor(site.Tier) is null)
            throw new InvalidOperationException($"sleeper_op has no row for site tier {site.Tier}.");

        var operation = new SleeperOperation
        {
            AgentId = agentId,
            SiteId = siteId,
            StartedOnTick = tick,
            Status = SleeperStatus.Inserting,
        };

        world.SleeperOperations.Add(operation);

        events.Publish(new SleeperStarted(tick, agentId, siteId));
        return operation;
    }

    /// <summary>
    /// Pulls a sleeper out early, keeping the intel gathered so far.
    /// </summary>
    /// <remarks>
    /// Free, and deliberately so. If recalling cost anything the player would simply run
    /// the operation to the last tick and hope, and the whole question the system poses —
    /// how much is a week of exposure worth against one more percent — would collapse
    /// into "never stop". What recall costs is the intel that never accrues.
    /// </remarks>
    /// <returns>False when the operation was not running.</returns>
    public static bool Recall(WorldState world, SleeperOperation operation, Tick tick, IEventSink events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (events is null) throw new ArgumentNullException(nameof(events));

        if (!operation.IsActive)
            return false;

        operation.Status = SleeperStatus.Extracted;
        operation.SnapshotOnTick = tick;

        events.Publish(new SleeperRecalled(tick, operation.AgentId, operation.SiteId, operation.IntelPercent));
        return true;
    }

    /// <summary>
    /// Advances every running operation and every prisoner one tick.
    /// </summary>
    public static void Tick(WorldState world, Tick tick, IEventSink events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (events is null) throw new ArgumentNullException(nameof(events));

        foreach (SleeperOperation operation in world.SleeperOperations.ToArray())
        {
            if (!operation.IsActive)
                continue;

            SiteTemplateRow? site = SimulationRules.SiteTemplateFor(operation.SiteId);
            if (site is null)
                continue;

            // ToArray: discovery publishes events and a handler may legitimately inspect
            // the operation list, and a list being mutated mid-enumeration is the kind of
            // crash that only happens in front of a player.
            Accrue(world, operation, site, tick, events);

            if (!IsStillRunning(operation))
                continue;

            RollDiscovery(world, operation, site, tick, events);

            if (!IsStillRunning(operation))
                continue;

            RefreshSnapshot(world, operation, tick, events);
        }

        AdvanceCaptures(world, tick, events);
    }

    private static bool IsStillRunning(SleeperOperation operation) => operation.IsActive;

    // ---- accrual -------------------------------------------------------------

    /// <summary>
    /// Adds one tick's worth of intel and promotes the operation once it is embedded.
    /// </summary>
    private static void Accrue(
        WorldState world,
        SleeperOperation operation,
        SiteTemplateRow site,
        Tick tick,
        IEventSink events)
    {
        SleeperOpRow? rules = SimulationRules.SleeperOpFor(site.Tier);
        if (rules is null || rules.TicksPerIntelPercent < 1)
            return;

        if (operation.IntelPercent >= 100)
            return;

        operation.IntelProgressHundredths += HundredthsPerTick;

        int perPercent = rules.TicksPerIntelPercent * HundredthsPerTick;
        bool gained = false;

        // A whole percent may complete more than once if the rate is fast enough that a
        // single tick carries several, so this loops rather than stepping once.
        while (operation.IntelProgressHundredths >= perPercent && operation.IntelPercent < 100)
        {
            operation.IntelProgressHundredths -= perPercent;
            operation.IntelPercent++;
            gained = true;
        }

        if (!gained)
            return;

        events.Publish(new SleeperIntelGained(
            tick, operation.AgentId, operation.SiteId, operation.IntelPercent, operation.Band));

        if (operation.Status == SleeperStatus.Inserting
            && operation.IntelPercent >= SimulationRules.Intel("intel_embedded_at_percent", 25))
        {
            operation.Status = SleeperStatus.Embedded;

            events.Publish(new SleeperEmbedded(
                tick, operation.AgentId, operation.SiteId, operation.IntelPercent));
        }
    }

    // ---- discovery -----------------------------------------------------------

    /// <summary>
    /// The chance, in percent, that the site notices this agent on this tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The formula, all integer, all data-driven:
    /// </para>
    /// <code>
    /// chance = base_discovery_chance_per_tick
    ///        + site_security_grade / intel_security_grade_divisor
    ///        - (Infiltration / intel_infiltration_points_per_reduction) * infiltration_modifier
    ///        - (Social       / intel_social_points_per_reduction)      * social_modifier
    /// clamped to [intel_discovery_chance_min, intel_discovery_chance_max]
    /// </code>
    /// <para>
    /// Subtraction rather than division because a division-based reduction turns every
    /// skilled agent into a zero-risk one the moment their skills clear a threshold,
    /// and the player would be told "safe" for a site that would eat them alive. Here a
    /// better infiltrator lowers the number by the same step at every level, so the risk
    /// stays visible on the screen and stays meaningful as skills rise.
    /// </para>
    /// <para>
    /// <b>Integer division on the skills is deliberate and load-bearing.</b> It quantises
    /// a skill into whole steps, so an operation's risk for a given agent and site is
    /// one number rather than a per-tick rounding wobble — the figure the player reads
    /// is exactly the figure that is rolled.
    /// </para>
    /// <para>
    /// The floor is a table value rather than zero so that "no risk" is an explicit
    /// choice by the designer, and so the distribution test has a non-degenerate case at
    /// the bottom of the range.
    /// </para>
    /// </remarks>
    public static int DiscoveryChancePercent(SleeperOperation operation, Agent agent, SiteTemplateRow site)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        if (site is null) throw new ArgumentNullException(nameof(site));

        SleeperOpRow? rules = SimulationRules.SleeperOpFor(site.Tier);
        if (rules is null)
            return 0;

        int divisor = Math.Max(1, SimulationRules.Intel("intel_security_grade_divisor", 10));
        int infiltrationSteps = Math.Max(
            1, SimulationRules.Intel("intel_infiltration_points_per_reduction", 8));
        int socialSteps = Math.Max(
            1, SimulationRules.Intel("intel_social_points_per_reduction", 10));

        int chance = rules.BaseDiscoveryChancePerTick
                     + site.SecurityGrade / divisor
                     - (agent.Skills.Infiltration / infiltrationSteps) * rules.InfiltrationModifier
                     - (agent.Skills.Social / socialSteps) * rules.SocialModifier;

        return Math.Clamp(
            chance,
            SimulationRules.Intel("intel_discovery_chance_min", 0),
            Math.Min(PercentDenominator, SimulationRules.Intel("intel_discovery_chance_max", 95)));
    }

    /// <summary>Rolls discovery and, if it lands, burns the operation and raises Heat.</summary>
    private static void RollDiscovery(
        WorldState world,
        SleeperOperation operation,
        SiteTemplateRow site,
        Tick tick,
        IEventSink events)
    {
        Agent? agent = world.GetAgent(operation.AgentId);
        if (agent is null)
            return;

        int chance = DiscoveryChancePercent(operation, agent, site);
        if (chance <= 0)
            return;

        IRng rng = world.RngStreams[RngStreams.StreamKind.Sleeper];
        if (rng.NextInt(1, PercentDenominator + 1) > chance)
            return;

        Discover(world, operation, site, tick, events);
    }

    /// <summary>
    /// Resolves a discovery: Heat, the agent's fate, and whether the last report will lie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation passes through <see cref="SleeperStatus.Discovered"/> and settles in
    /// the same tick on <see cref="SleeperStatus.Burned"/> or
    /// <see cref="SleeperStatus.Poisoned"/>. <c>Discovered</c> is not a resting state
    /// because a tick is atomic and nothing can observe it between the two — it is the
    /// moment, carried on <see cref="SleeperDiscovered.FromStatus"/> so a replay log can
    /// show that the site found them even after the operation has already resolved.
    /// </para>
    /// <para>
    /// The operation keeps reporting when poisoned and stops when burned, and that is
    /// the entire difference between the two outcomes as far as the player is concerned:
    /// one loses an agent, the other keeps a map that lies.
    /// </para>
    /// </remarks>
    private static void Discover(
        WorldState world,
        SleeperOperation operation,
        SiteTemplateRow site,
        Tick tick,
        IEventSink events)
    {
        SleeperOpRow? rules = SimulationRules.SleeperOpFor(site.Tier);
        SleeperStatus from = operation.Status;

        operation.DiscoveredOnTick = tick;

        IRng rng = world.RngStreams[RngStreams.StreamKind.Sleeper];
        bool poisoned = rules is not null
                        && rng.NextInt(1, PercentDenominator + 1)
                           <= rules.PoisonChanceOnDiscovery;

        operation.Status = poisoned ? SleeperStatus.Poisoned : SleeperStatus.Burned;

        int heat = rules?.HeatOnDiscovery ?? 0;
        if (heat > 0)
        {
            Resources before = world.Resources;
            world.Resources = world.Resources.AddHeat(heat);
            events.Publish(new ResourcesChanged(tick, before, world.Resources));
        }

        events.Publish(new SleeperDiscovered(tick, operation.AgentId, operation.SiteId, from, operation.Status, heat));

        if (poisoned)
            events.Publish(new SleeperPoisoned(tick, operation.AgentId, operation.SiteId));
    }

    // ---- reporting -----------------------------------------------------------

    /// <summary>
    /// Rebuilds the site's report when the operation has something new to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Regenerated from the seed rather than patched, so a report can never drift out of
    /// step with the building it describes. Regenerating costs a full site generation,
    /// which is why it is gated on the report actually having changed: at full intel it
    /// is refreshed every tick, because a prisoner's countdown is in the report and it
    /// has to stay current for the rescue decision to be an informed one. Below that, a
    /// new report is only worth building when the intel percentage moved or when the
    /// operation has just started lying.
    /// </para>
    /// <para>
    /// The poisoned-state change matters and is easy to miss. Poisoning happens on a
    /// discovery tick with the intel percentage unchanged, so a gate that only watched
    /// the percentage would leave the last honest report in place — and the player would
    /// be spared the consequences of the one event they are never told about.
    /// </para>
    /// </remarks>
    private static void RefreshSnapshot(
        WorldState world,
        SleeperOperation operation,
        Tick tick,
        IEventSink events)
    {
        bool complete = operation.Band == IntelBand.Complete;
        bool poisoned = operation.Status == SleeperStatus.Poisoned;

        bool had = world.IntelSnapshots.TryGetValue(operation.SiteId, out IntelSnapshot? previous);

        bool changed = !had
                       || previous!.IntelPercent != operation.IntelPercent
                       || previous.IsPoisoned != poisoned;

        if (!complete && !changed)
            return;

        SiteLayout layout = LayoutFor(world, operation.SiteId);

        IntelSnapshot snapshot = IntelSnapshotBuilder.Build(
            layout,
            operation,
            tick,
            world.Captures.Count > 0 ? world.Captures : null);

        world.IntelSnapshots[operation.SiteId] = snapshot;
        operation.SnapshotOnTick = tick;

        events.Publish(new IntelSnapshotRefreshed(
            tick, operation.SiteId, snapshot.IntelPercent, snapshot.Band));
    }

    // ---- prisoners -----------------------------------------------------------

    /// <summary>
    /// Counts every prisoner's clock down, and reports the ones who did not make it.
    /// </summary>
    private static void AdvanceCaptures(WorldState world, Tick tick, IEventSink events)
    {
        foreach (CaptureRecord capture in world.Captures.ToArray())
        {
            if (!capture.AdvanceTick())
            {
                world.Captures.Remove(capture);

                Agent? agent = world.GetAgent(capture.AgentId);
                if (agent is not null)
                    agent.Status = AgentStatus.Dead;

                events.Publish(new AgentLost(tick, capture.AgentId, capture.HostSiteId));
            }
        }
    }
}