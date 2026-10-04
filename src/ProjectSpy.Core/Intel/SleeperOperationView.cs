using ProjectSpy.Core.Missions;

// Generated table beans, aliased the same way SimulationRules does so a "room template"
// can never be confused with the node-interior one. This file reads site_template and
// sleeper_op only.
using SiteTemplateRow = ProjectSpy.Tables.SiteTemplate;
using SleeperOpRow = ProjectSpy.Tables.SleeperOp;

namespace ProjectSpy.Core;

/// <summary>
/// Everything the base-side sleeper screen needs, as data.
/// </summary>
/// <remarks>
/// <para>
/// The stage-4b deliverable for the strategic UI is a contract, not a screen. Unity is
/// forbidden from reaching into Core's state to work out what the player is allowed to
/// see (knowledge.md rule 4), so the numbers a screen draws have to arrive pre-computed
/// and pre-decided, or the first person to write the screen will start recomputing the
/// discovery risk in C# and the two copies will disagree.
/// </para>
/// <para>
/// <b>No prose.</b> Every string here is a key or an enum, never a sentence. The screen
/// builds its own text from localization.
/// </para>
/// </remarks>
/// <param name="AgentId">The inserted agent.</param>
/// <param name="SiteId">The site being spied on.</param>
/// <param name="SiteTier">The site's tier, which selects the accrual and risk rows.</param>
/// <param name="Status">Where the operation stands.</param>
/// <param name="IntelPercent">Intel gathered, 0 to 100.</param>
/// <param name="Band">The band that percentage unlocks.</param>
/// <param name="BandContents">What the current band reveals.</param>
/// <param name="ElapsedTicks">Ticks since insertion.</param>
/// <param name="EstimatedRemainingTicks">
/// Ticks to reach full intel at the current rate, or -1 once full intel is reached.
/// </param>
/// <param name="DiscoveryRiskPerTickPercent">
/// The chance the site notices this agent on the next tick.
/// </param>
/// <param name="NextBand">The band above the current one, or null at the top.</param>
/// <param name="PercentToNextBand">
/// Intel still owed before the next band, or -1 at the top of the scale.
/// </param>
/// <param name="EstimatedTicksToNextBand">Ticks until that, or -1.</param>
/// <param name="NextBandContents">
/// What the next band would add, or null at the top. This is the "what am I buying"
/// line the player needs before committing the days.
/// </param>
/// <param name="CanRecall">Whether the player may pull the agent out right now.</param>
/// <param name="HeatIfDiscovered">Heat this discovery would cost.</param>
/// <param name="RecallKeepsIntelPercent">Intel kept by recalling now.</param>
public sealed record SleeperOperationView(
    AgentId AgentId,
    int SiteId,
    int SiteTier,
    SleeperStatus Status,
    int IntelPercent,
    IntelBand Band,
    IntelBandContents BandContents,
    int ElapsedTicks,
    int EstimatedRemainingTicks,
    int DiscoveryRiskPerTickPercent,
    IntelBand? NextBand,
    int PercentToNextBand,
    int EstimatedTicksToNextBand,
    IntelBandContents? NextBandContents,
    bool CanRecall,
    int HeatIfDiscovered,
    int RecallKeepsIntelPercent)
{
    /// <summary>True once the operation has reached everything the report can say.</summary>
    public bool IsCompleteIntel => Band == IntelBand.Complete;

    /// <summary>
    /// Builds the view for one operation.
    /// </summary>
    /// <remarks>
    /// The remaining-tick estimates are a straight extrapolation of the current rate,
    /// with no allowance for the fact that the operation may be found before it gets
    /// there. That is deliberate: a number that included the chance of failure would be a
    /// probability of success dressed as a duration, and the player can already see the
    /// per-tick risk one field away. The screen shows both and lets the player weigh them.
    /// </remarks>
    public static SleeperOperationView Build(
        WorldState world,
        SleeperOperation operation,
        Tick now)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (operation is null) throw new ArgumentNullException(nameof(operation));

        SiteTemplateRow? site = SimulationRules.SiteTemplateFor(operation.SiteId);
        int tier = site?.Tier ?? 0;

        SleeperOpRow? rules = SimulationRules.SleeperOpFor(tier);
        Agent? agent = world.GetAgent(operation.AgentId);

        int risk = agent is null || site is null
            ? 0
            : SleeperSystem.DiscoveryChancePercent(operation, agent, site);

        int ticksPerPercent = Math.Max(1, rules?.TicksPerIntelPercent ?? 1);
        int progress = operation.IntelProgressHundredths;

        IntelBand? next = IntelBands.NextAbove(operation.Band);
        bool complete = next is null;

        int toFull = Math.Max(0, 100 - operation.IntelPercent);
        int toNext = next is null ? -1 : Math.Max(0, IntelBands.MinPercentFor(next.Value) - operation.IntelPercent);

        return new SleeperOperationView(
            AgentId: operation.AgentId,
            SiteId: operation.SiteId,
            SiteTier: tier,
            Status: operation.Status,
            IntelPercent: operation.IntelPercent,
            Band: operation.Band,
            BandContents: IntelBands.ContentsOf(operation.Band),
            ElapsedTicks: operation.TicksRunning(now),
            EstimatedRemainingTicks: complete ? -1 : toFull * ticksPerPercent - TicksOfProgress(progress, ticksPerPercent),
            DiscoveryRiskPerTickPercent: risk,
            NextBand: next,
            PercentToNextBand: toNext,
            EstimatedTicksToNextBand: next is null ? -1 : toNext * ticksPerPercent - TicksOfProgress(progress, ticksPerPercent),
            NextBandContents: next is null ? null : IntelBands.ContentsOf(next.Value),
            CanRecall: operation.IsActive,
            HeatIfDiscovered: rules?.HeatOnDiscovery ?? 0,
            RecallKeepsIntelPercent: operation.IntelPercent);
    }

    /// <summary>Builds a view for every running operation, in insertion order.</summary>
    public static IReadOnlyList<SleeperOperationView> BuildAll(WorldState world, Tick now)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        var views = new List<SleeperOperationView>(world.SleeperOperations.Count);

        foreach (SleeperOperation operation in world.SleeperOperations)
        {
            if (!operation.IsActive)
                continue;

            views.Add(Build(world, operation, now));
        }

        return views;
    }

    /// <summary>
    /// Ticks already banked toward the next percent, so a percentage that just moved does
    /// not read as though no progress had been made.
    /// </summary>
    private static int TicksOfProgress(int hundredths, int ticksPerPercent)
        => hundredths / 100;
}