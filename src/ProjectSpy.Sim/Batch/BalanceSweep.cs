using ProjectSpy.Core;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using TacticalOutcome = ProjectSpy.Core.Tactical.MissionOutcome;

namespace ProjectSpy.Sim.Batch;

/// <summary>
/// One cell of the balance table: a policy against a site template, over many seeds.
/// </summary>
/// <remarks>
/// <para>
/// A cell rather than a single run because a single mission is mostly luck. A black site
/// has sixteen guards on four floors; whether one squad of four gets caught depends
/// enormously on which room the guards started in, and a report built from one run per
/// policy would be a report about seed 11013.
/// </para>
/// <para>
/// <b>Every policy is played the same fixture.</b> The sweep builds one fixture per
/// (template, seed) and plays it four times, once per policy, so a difference between two
/// columns is a difference between the policies rather than between the buildings. This
/// is the single most important property of the whole harness and it is why the fixture
/// is built outside the policy loop.
/// </para>
/// </remarks>
public sealed class SweepCell
{
    public required SquadPolicy Policy { get; init; }

    public required int TemplateId { get; init; }

    public required string TemplateNameKey { get; init; }

    public required int Tier { get; init; }

    public required int Runs { get; init; }

    public required int ObjectivesAchieved { get; init; }

    public required int CleanSuccesses { get; init; }

    public required int Compromised { get; init; }

    public required int Aborted { get; init; }

    public required int Disasters { get; init; }

    public required int Burned { get; init; }

    public required int NeverIdentified { get; init; }

    public required int IdentifiedRuns { get; init; }

    public required int TotalLost { get; init; }

    public required long TotalSteps { get; init; }

    public required long TotalHeat { get; init; }

    public required long TotalEvidence { get; init; }

    public required int TotalLethal { get; init; }

    public required long TotalExtractTime { get; init; }

    public required int ExtractTimeSamples { get; init; }

    public required int IllegalOrders { get; init; }

    /// <summary>Orders refused because a door would not open.</summary>
    public required int BlockedSkips { get; init; }

    /// <summary>Peak band per run, as a count per band, indexed by the band's value.</summary>
    public required IReadOnlyList<int> BandHistogram { get; init; }

    /// <summary>
    /// Runs where nobody was ever identified and the objective was still achieved.
    /// </summary>
    /// <remarks>
    /// The number that says whether the stealth layer is load-bearing. "No
    /// identifications at all" would mean guards cannot see anything and the whole
    /// perception system is decoration; "no clean successes anywhere" would mean it
    /// cannot be done. This band in between is the design working.
    /// </remarks>
    public required int PerfectRuns { get; init; }

    public double SuccessRate => Runs == 0 ? 0 : (double)ObjectivesAchieved / Runs;

    public double CleanRate => Runs == 0 ? 0 : (double)CleanSuccesses / Runs;

    public double StealthRate => Runs == 0 ? 0 : (double)NeverIdentified / Runs;

    public double LossRate => Runs == 0 ? 0 : (double)TotalLost / Runs;

    public double MeanSteps => Runs == 0 ? 0 : (double)TotalSteps / Runs;

    public double MeanExtractSteps => ExtractTimeSamples == 0 ? 0 : (double)TotalExtractTime / ExtractTimeSamples;
}

/// <summary>
/// Runs the whole mission sweep and hands back the table.
/// </summary>
public static class BalanceSweep
{
    /// <summary>
    /// Plays every policy over every site template.
    /// </summary>
    /// <param name="seedsPerTemplate">Seeds per template. The report's percentages are
    /// only worth quoting above a few hundred missions in total.</param>
    /// <param name="objectives">Which objectives to send the squads after.</param>
    public static IReadOnlyList<SweepCell> Run(
        int seedsPerTemplate,
        IReadOnlyList<TableObjectiveType>? objectives = null)
    {
        if (seedsPerTemplate < 1)
            throw new ArgumentOutOfRangeException(nameof(seedsPerTemplate));

        IReadOnlyList<TableObjectiveType> types = objectives
            ?? new[] { TableObjectiveType.StealData, TableObjectiveType.Recon };

        var rows = new List<SweepCell>();

        foreach (ProjectSpy.Tables.SiteTemplate template in SimulationRules.AllSiteTemplates())
        {
            foreach (SquadPolicy policy in Enum.GetValues<SquadPolicy>())
                rows.Add(RunCell(template, policy, seedsPerTemplate, types));
        }

        return rows;
    }

    private static SweepCell RunCell(
        ProjectSpy.Tables.SiteTemplate template,
        SquadPolicy policy,
        int seedsPerTemplate,
        IReadOnlyList<TableObjectiveType> objectives)
    {
        int achieved = 0;
        int clean = 0;
        int compromised = 0;
        int aborted = 0;
        int disasters = 0;
        int burned = 0;
        int neverIdentified = 0;
        int identifiedRuns = 0;
        int perfect = 0;
        int lost = 0;
        long steps = 0;
        long heat = 0;
        long evidence = 0;
        long extractTotal = 0;
        int extractSamples = 0;
        int lethal = 0;
        int illegal = 0;
        int blocked = 0;

        var bands = new int[Enum.GetValues<AlarmBand>().Length];

        for (int index = 0; index < seedsPerTemplate; index++)
        {
            // The objective cycles with the seed so a cell is not quietly a
            // StealData-only measurement dressed up as a general one.
            TableObjectiveType objective = objectives[index % objectives.Count];
            ulong seed = 0x5EED_0000UL + (ulong)index * 7919UL + (ulong)template.Id;
            int missionId = index + 1;

            MissionFixture fixture = MissionFixture.Create(
                seed, template.Id, template.Tier, objective, missionId,
                MissionFixture.RolesFor(objective));

            MissionRun run = MissionRun.Play(fixture, policy);

            if (run.ObjectiveComplete)
                achieved++;

            switch (run.Class)
            {
                case ResolveClass.CleanSuccess:
                    clean++;
                    break;
                case ResolveClass.Success:
                    break;
                case ResolveClass.Compromised:
                    compromised++;
                    break;
                case ResolveClass.Aborted:
                    aborted++;
                    break;
                case ResolveClass.Disaster:
                    disasters++;
                    break;
            }

            if (run.Outcome == TacticalOutcome.Burned)
                burned++;

            if (run.WasIdentified)
                identifiedRuns++;
            else
                neverIdentified++;

            if (run.WasIdentified == false && run.ObjectiveComplete)
                perfect++;

            lost += run.Lost;
            steps += run.Steps;
            heat += run.Heat;
            evidence += run.Evidence;
            lethal += run.LethalActs;
            illegal += run.IllegalOrders;
            blocked += run.BlockedSkips;

            if (run.TimeToExtraction >= 0)
            {
                extractTotal += run.TimeToExtraction;
                extractSamples++;
            }

            bands[(int)run.PeakAlarmBand]++;
        }

        return new SweepCell
        {
            Policy = policy,
            TemplateId = template.Id,
            TemplateNameKey = template.NameKey,
            Tier = template.Tier,
            Runs = seedsPerTemplate,
            ObjectivesAchieved = achieved,
            CleanSuccesses = clean,
            Compromised = compromised,
            Aborted = aborted,
            Disasters = disasters,
            Burned = burned,
            NeverIdentified = neverIdentified,
            IdentifiedRuns = identifiedRuns,
            PerfectRuns = perfect,
            TotalLost = lost,
            TotalSteps = steps,
            TotalHeat = heat,
            TotalEvidence = evidence,
            TotalLethal = lethal,
            TotalExtractTime = extractTotal,
            ExtractTimeSamples = extractSamples,
            IllegalOrders = illegal,
            BlockedSkips = blocked,
            BandHistogram = bands,
        };
    }
}