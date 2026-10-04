using System.Globalization;
using System.Text;

using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Sim.Batch;

/// <summary>
/// One thing the sweep noticed that a designer should look at.
/// </summary>
/// <remarks>
/// <para>
/// A warning is not a failure. Nothing here breaks the build, because a balance pass is
/// supposed to find things and a harness that refuses to run until the game is balanced
/// can only ever be run on a finished game.
/// </para>
/// <para>
/// Every warning carries the number that triggered it and the threshold that was
/// crossed, so a reader can disagree with the threshold rather than having to guess what
/// "too dominant" meant.
/// </para>
/// </remarks>
public sealed record BalanceWarning(string Severity, string Key, string Detail);

/// <summary>
/// Turns <see cref="SweepCell"/>s into the tables the brief asks for, and into the
/// warnings.
/// </summary>
public static class BalanceReport
{
    /// <summary>A tier above this success rate reads as uninteresting.</summary>
    public const double HighSuccessWarn = 0.90;

    /// <summary>A tier below this success rate reads as unfair.</summary>
    public const double LowSuccessWarn = 0.25;

    /// <summary>A strategy this many times better than the next is the only strategy.</summary>
    public const double DominanceFactor = 2.0;

    /// <summary>
    /// Every warning the sweep raises, in a stable order.
    /// </summary>
    /// <remarks>
    /// Asserted on rather than ignored: the brief is explicit that Speedrun failing on
    /// high tiers is the control that proves the design works, and a report that stayed
    /// silent when Speedrun won would not be a neutral report.
    /// </remarks>
    public static IReadOnlyList<BalanceWarning> Warnings(IReadOnlyList<SweepCell> cells)
    {
        List<BalanceWarning> warnings = new();

        foreach (SweepCell cell in cells)
        {
            if (cell.SuccessRate > HighSuccessWarn)
            {
                warnings.Add(new BalanceWarning(
                    "high",
                    "tier_too_easy",
                    $"{cell.Policy} on {cell.TemplateNameKey} (tier {cell.Tier}) succeeds "
                    + $"{Percent(cell.SuccessRate)} of the time, above the {Percent(HighSuccessWarn)} "
                    + "threshold. There is no decision on this site."));
            }

            if (cell.Runs > 0 && cell.SuccessRate < LowSuccessWarn)
            {
                warnings.Add(new BalanceWarning(
                    "high",
                    "tier_too_hard",
                    $"{cell.Policy} on {cell.TemplateNameKey} (tier {cell.Tier}) succeeds only "
                    + $"{Percent(cell.SuccessRate)} of the time, below the {Percent(LowSuccessWarn)} "
                    + "threshold. A squad that usually fails teaches the player nothing."));
            }

            if (cell.IllegalOrders > 0)
            {
                warnings.Add(new BalanceWarning(
                    "critical",
                    "policy_issued_illegal_order",
                    $"{cell.Policy} on {cell.TemplateNameKey} produced {cell.IllegalOrders} orders "
                    + "the action system refused. A policy must never ask for something illegal."));
            }

            // Not a policy fault: the building has a locked door on the route and no
            // policy knows how to open one. Worth its own warning because it caps what
            // any policy can score on the template, and a balance report that blamed
            // the policy for it would point the next edit at the wrong file.
            if (cell.BlockedSkips > 0)
            {
                warnings.Add(new BalanceWarning(
                    "high",
                    "policy_blocked_by_door",
                    $"{cell.Policy} on {cell.TemplateNameKey} was refused {cell.BlockedSkips} times "
                    + "by a door it could not open. No policy forces a lock, so a site whose "
                    + "route is locked can cap every score on it."));
            }
        }

        foreach (BalanceWarning dominance in Dominance(cells))
            warnings.Add(dominance);

        warnings.Add(SpeedrunControlGroup(cells));

        return warnings;
    }

    /// <summary>
    /// Strategies whose success rate is more than twice the next best on the same site.
    /// </summary>
    private static IEnumerable<BalanceWarning> Dominance(IReadOnlyList<SweepCell> cells)
    {
        foreach (IGrouping<int, SweepCell> site in cells.GroupBy(c => c.TemplateId))
        {
            List<SweepCell> ordered = site.OrderByDescending(c => c.SuccessRate).ToList();

            if (ordered.Count < 2)
                continue;

            SweepCell best = ordered[0];
            SweepCell second = ordered[1];

            if (second.SuccessRate <= 0)
                continue;

            double factor = best.SuccessRate / second.SuccessRate;

            if (factor <= DominanceFactor)
                continue;

            yield return new BalanceWarning(
                "high",
                "dominant_strategy",
                $"{best.Policy} succeeds {factor:F1}x as often as {second.Policy} on "
                + $"{best.TemplateNameKey}. Above {DominanceFactor:F1}x the other strategies are "
                + "not choices, they are alternatives the player is expected to ignore.");
        }
    }

    /// <summary>
    /// Whether Speedrun — the control group — actually loses on the top tiers.
    /// </summary>
    /// <remarks>
    /// The brief's condition on the whole tactical layer: if walking straight at the
    /// objective and ignoring every noise is a viable strategy on a hard site, then noise
    /// and sightlines are not costs. This is the one warning that is about the
    /// <em>shape</em> of the design rather than the numbers in it, so it is reported even
    /// when it passes, as a line in the table.
    /// </remarks>
    private static BalanceWarning SpeedrunControlGroup(IReadOnlyList<SweepCell> cells)
    {
        var topTier = cells.Where(c => c.Tier >= 4).ToList();

        if (topTier.Count == 0)
            return new BalanceWarning("info", "control_group.skipped", "No tier-4 sites in the sweep.");

        double speedrun = topTier.Where(c => c.Policy == SquadPolicy.Speedrun)
            .Select(c => c.SuccessRate)
            .DefaultIfEmpty(0)
            .Average();

        double stealth = topTier.Where(c => c.Policy == SquadPolicy.Stealth)
            .Select(c => c.SuccessRate)
            .DefaultIfEmpty(0)
            .Average();

        if (speedrun > stealth)
        {
            return new BalanceWarning(
                "critical",
                "control_group_failed",
                $"On tier-4 sites Speedrun succeeds {Percent(speedrun)} against Stealth's "
                + $"{Percent(stealth)}. Ignoring noise is supposed to be worse than avoiding it; "
                + "if it is not, the alarm and the sightlines are not charging the player for anything.");
        }

        return new BalanceWarning(
            "ok",
            "control_group_holds",
            $"On tier-4 sites Stealth succeeds {Percent(stealth)} against Speedrun's "
            + $"{Percent(speedrun)}. The control group behaves as the design requires.");
    }

    /// <summary>The markdown table the brief asks for.</summary>
    public static string ToMarkdown(IReadOnlyList<SweepCell> cells, IReadOnlyList<BalanceWarning> warnings)
    {
        var sb = new StringBuilder(32 * 1024);

        sb.AppendLine("# Balance sweep");
        sb.AppendLine();
        sb.AppendLine("Mission-level, per policy and per site template. Every policy is played the");
        sb.AppendLine("same fixture, so a difference between two rows is a difference between the");
        sb.AppendLine("policies and not between the buildings.");
        sb.AppendLine();

        sb.AppendLine("## Outcomes by policy and site");
        sb.AppendLine();
        sb.AppendLine("| policy | site | tier | runs | objective | clean | never identified | "
            + "lost | mean steps | mean exit |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|");

        foreach (SweepCell cell in Ordered(cells))
        {
            sb.AppendLine(
                $"| {cell.Policy} | {cell.TemplateNameKey} | {cell.Tier} | {cell.Runs} "
                + $"| {Percent(cell.SuccessRate)} | {Percent(cell.CleanRate)} "
                + $"| {Percent(cell.StealthRate)} | {cell.LossRate:F2} "
                + $"| {cell.MeanSteps:F0} | {cell.MeanExtractSteps:F0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Resolve class by policy");
        sb.AppendLine();
        sb.AppendLine("| policy | clean | success | compromised | aborted | disaster | burned |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (IGrouping<SquadPolicy, SweepCell> group in cells.GroupBy(c => c.Policy))
        {
            List<SweepCell> rows = group.ToList();
            int runs = rows.Sum(r => r.Runs);

            sb.AppendLine(
                $"| {group.Key} | {Percent(runs == 0 ? 0 : (double)rows.Sum(r => r.CleanSuccesses) / runs)} "
                + $"| {Percent(runs == 0 ? 0 : (double)rows.Sum(r => r.ObjectivesAchieved - r.CleanSuccesses) / runs)} "
                + $"| {Percent(runs == 0 ? 0 : (double)rows.Sum(r => r.Compromised) / runs)} "
                + $"| {Percent(runs == 0 ? 0 : (double)rows.Sum(r => r.Aborted) / runs)} "
                + $"| {Percent(runs == 0 ? 0 : (double)rows.Sum(r => r.Disasters) / runs)} "
                + $"| {Percent(runs == 0 ? 0 : (double)rows.Sum(r => r.Burned) / runs)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Peak alarm band by policy");
        sb.AppendLine();
        sb.AppendLine("The band each mission finished in, as a share of that policy's runs.");
        sb.AppendLine();
        sb.AppendLine("| policy | calm | suspicious | alert | lockdown | burned |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");

        foreach (IGrouping<SquadPolicy, SweepCell> group in cells.GroupBy(c => c.Policy))
        {
            long[] totals = new long[Enum.GetValues<AlarmBand>().Length];

            foreach (SweepCell row in group)
            {
                for (int band = 0; band < totals.Length; band++)
                    totals[band] += row.BandHistogram[band];
            }

            long runs = totals.Sum();

            sb.Append("| " + group.Key);

            for (int band = 0; band < totals.Length; band++)
                sb.Append(" | " + Percent(runs == 0 ? 0 : (double)totals[band] / runs));

            sb.AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine("## Warnings");
        sb.AppendLine();

        if (warnings.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            sb.AppendLine("| severity | what | detail |");
            sb.AppendLine("|---|---|---|");

            foreach (BalanceWarning warning in warnings.OrderByDescending(w => SeverityRank(w.Severity))
                         .ThenBy(w => w.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"| {warning.Severity} | `{warning.Key}` | {warning.Detail} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## What these numbers are not");
        sb.AppendLine();
        sb.AppendLine("- Agency-scale economy. Funds, salaries and bankruptcy are measured by the");
        sb.AppendLine("  agency loop, not by this table.");
        sb.AppendLine("- Sleeper economics. Band timings and the intel-vs-success difference are");
        sb.AppendLine("  measured by the sleeper harness.");
        sb.AppendLine("- Anything about rooms, traits or archetypes, which this sweep does not vary.");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>Every cell as one CSV row.</summary>
    public static string ToCsv(IReadOnlyList<SweepCell> cells)
    {
        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine("policy,template_id,template,tier,runs,objective_rate,clean_rate,"
            + "never_identified_rate,perfect_rate,compromised,aborted,disasters,burned,"
            + "lost_total,mean_steps,mean_exit_steps,heat_total,evidence_total,lethal_total,"
            + "illegal_orders");

        foreach (SweepCell cell in Ordered(cells))
        {
            sb.AppendLine(string.Join(",",
                cell.Policy,
                cell.TemplateId,
                cell.TemplateNameKey,
                cell.Tier,
                cell.Runs,
                Num(cell.SuccessRate),
                Num(cell.CleanRate),
                Num(cell.StealthRate),
                Num(cell.Runs == 0 ? 0 : (double)cell.PerfectRuns / cell.Runs),
                cell.Compromised,
                cell.Aborted,
                cell.Disasters,
                cell.Burned,
                cell.TotalLost,
                Num(cell.MeanSteps),
                Num(cell.MeanExtractSteps),
                cell.TotalHeat,
                cell.TotalEvidence,
                cell.TotalLethal,
                cell.IllegalOrders));
        }

        return sb.ToString();
    }

    private static IEnumerable<SweepCell> Ordered(IReadOnlyList<SweepCell> cells)
        => cells.OrderBy(c => (int)c.Policy).ThenBy(c => c.TemplateId);

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "ok" => 2,
        _ => 3,
    };

    private static string Percent(double value)
        => (value * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A number for the CSV, in the invariant culture.
    /// </summary>
    /// <remarks>
    /// Culture-invariant because the CSV is read by whatever the next tool happens to be,
    /// including something on a machine where the decimal separator is a comma — which
    /// would turn one column into two and quietly corrupt a table that was fine.
    /// </remarks>
    private static string Num(double value)
        => value.ToString("F4", CultureInfo.InvariantCulture);
}