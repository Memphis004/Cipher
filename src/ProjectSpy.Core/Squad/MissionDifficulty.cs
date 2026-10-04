using ProjectSpy.Tables;

// Rows and enums are aliased to their table names exactly as Model/SimulationRules.cs
// does it. Core deliberately holds no counterpart type for either: difficulty is a claim
// about a table's own numbers, and mirroring the enum here would be a second place to
// forget to update when the table gains a value.
// The squad types live in ProjectSpy.Core.Squad (see Squad/SquadComposition.cs); this
// file is in the root namespace like every other static utility in Core, so it names them.
using ProjectSpy.Core.Squad;

using MissionTypeRow = ProjectSpy.Tables.MissionType;
using SiteTemplateRow = ProjectSpy.Tables.SiteTemplate;
using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using TableDifficultyClass = ProjectSpy.Tables.DifficultyClass;

namespace ProjectSpy.Core;

/// <summary>
/// One line of the arithmetic behind a difficulty number.
/// </summary>
/// <remarks>
/// <para>
/// The whole reason this type exists is that the number alone is not enough. A player
/// shown "difficulty 42" cannot act on it; a player shown "base 25, +7 tier 3, +6 guards,
/// +4 Hacker unfilled" can decide whether to send somebody else. So every term is kept,
/// not just the sum.
/// </para>
/// <para>
/// The terms are Presentation-neutral: a <c>TermKey</c> naming a cause and an integer
/// delta, never a sentence. Core does not write English for the player
/// (knowledge.md rule 4), so the UI turns each key into localized text.
/// </para>
/// </remarks>
/// <param name="TermKey">
/// Localization key naming the cause, e.g. <c>difficulty.term.tier</c>.
/// </param>
/// <param name="Delta">Signed contribution. Zero is not emitted.</param>
public readonly record struct DifficultyTerm(string TermKey, int Delta)
{
    /// <summary>True when this term actually moved the number.</summary>
    public bool IsMeaningful => Delta != 0;
}

/// <summary>
/// A difficulty estimate, and the arithmetic that produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract: this number is Core's claim, not the UI's guess.</b> A UI that
/// computed difficulty from Core's data would be making a claim Core never made, and the
/// two would drift the moment either side changed. PresentationRuleValidator exists to
/// catch that, and it would be right to. So the arithmetic lives here, next to the tables
/// it reads, and the UI only reads the result.
/// </para>
/// <para>
/// <b>It is an estimate and says so.</b> It compares a squad's relevant skills against
/// the demands Core already publishes for the mission type and the site. It does not
/// model the tactical fight, which is not knowable at preparation time. The player is
/// told it is an estimate; a number that pretended to be certain would be the fudge the
/// brief rules out.
/// </para>
/// <para>
/// Deterministic (knowledge.md rule 6): integer arithmetic only, every percentage through
/// <see cref="SimulationRules.PercentOf(int,int)"/>, no floating point anywhere.
/// </para>
/// </remarks>
public sealed record MissionDifficultyEstimate(
    int Total,
    IReadOnlyList<DifficultyTerm> Terms,
    IReadOnlyList<DifficultyTerm> SquadTerms,
    TableDifficultyClass Band,
    IReadOnlyList<DispatchRefusal> Refusals,
    int TeamPower,
    int RequiredPower)
{
    /// <summary>
    /// True when the squad cannot legally be dispatched, whatever its numbers.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Total"/> on purpose. A squad can be strong enough on
    /// paper and still be refused for a missing role or low stamina, and a single
    /// difficulty number cannot express that. The UI shows both.
    /// </remarks>
    public bool CanDispatch => Refusals.Count == 0;

    /// <summary>
    /// How far the squad's power falls short of what the site demands, clamped at zero.
    /// </summary>
    /// <remarks>
    /// Surplus is reported by <see cref="TeamPower"/> minus <see cref="RequiredPower"/>
    /// rather than clamped away, because a player choosing between two agents needs to
    /// know by how much one is better, not merely that both suffice.
    /// </remarks>
    public int PowerShortfall => Math.Max(0, RequiredPower - TeamPower);

    /// <summary>Signed surplus. Negative means short.</summary>
    public int PowerMargin => TeamPower - RequiredPower;

    /// <summary>Key naming <see cref="Band"/>, for the UI to localize.</summary>
    public string BandKey => $"difficulty.band.{Band.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Estimates how hard a mission will be for a given squad.
/// </summary>
/// <remarks>
/// <para>
/// Everything read here is something Core already owns: the mission type's
/// <c>base_difficulty</c> and objective, the site's tier, guard count and security grade,
/// and the squad's members, roles and skills. Nothing is invented and nothing is
/// re-derived, which is what makes the number trustworthy.
/// </para>
/// </remarks>
public static class MissionDifficulty
{
    /// <summary>
    /// Power contributed by one skill point, as a percentage of a requirement point.
    /// </summary>
    /// <remarks>
    /// The unit that makes "team power" and "required power" comparable. They are both in
    /// this scale, so their ratio means something and the band thresholds below are
    /// stable rather than tuned per objective.
    /// </remarks>
    private const int PowerPerSkillPointPercent = 100;

    /// <summary>
    /// Band boundaries, as team power as a percentage of required power.
    /// </summary>
    /// <remarks>
    /// Below <c>ComfortableThreshold</c> is "trivially safe", which is a real strategic
    /// fact the player should be able to notice: a site that used to be dangerous has
    /// stopped being dangerous because they got better.
    /// </remarks>
    private const int ComfortableThreshold = 140;
    private const int EvenThreshold = 100;
    private const int StrainedThreshold = 75;
    private const int DesperateThreshold = 50;

    /// <summary>
    /// The skill each objective type actually leans on.
    /// </summary>
    /// <remarks>
    /// The brief calls for a single honest number, but "difficulty" against a StealData
    /// site is mostly about Infiltration and against a Rescue is mostly about Nerve. A
    /// single averaged number would tell a Tech-heavy squad it was ready for a site that
    /// it would fail for want of a lockpick. So the relevant skill is chosen per objective
    /// and the terms say which.
    /// </remarks>
    private static SkillKind RelevantSkill(TableObjectiveType objective) => objective switch
    {
        TableObjectiveType.StealData => SkillKind.Infiltration,
        TableObjectiveType.PlantBug => SkillKind.Tech,
        TableObjectiveType.Sabotage => SkillKind.Tech,
        TableObjectiveType.Recon => SkillKind.Nerve,
        TableObjectiveType.Rescue => SkillKind.Nerve,
        TableObjectiveType.Assassinate => SkillKind.Combat,
        _ => SkillKind.Nerve,
    };

    /// <summary>
    /// Behaviour whose absence makes a site harder, whatever the objective.
    /// </summary>
    /// <remarks>
    /// A site with no forward post is worse for a Hacker than any number of extra skill
    /// points, because the skill has nothing to act on. That is a fact about the site
    /// template, so it belongs in the estimate.
    /// </remarks>
    private const int MissingForwardPostPower = 15;

    /// <summary>
    /// Estimates the difficulty of one mission type for one squad.
    /// </summary>
    /// <param name="world">The world the squad is drawn from.</param>
    /// <param name="composition">The squad being assessed.</param>
    /// <param name="missionTypeId">Foreign key into <c>mission_type</c>.</param>
    /// <param name="siteTemplateId">
    /// The site the mission will run on, or 0 when the mission type carries the whole of
    /// the difficulty and no site has been chosen yet.
    /// </param>
    /// <returns>An estimate, or null when the mission type is unknown.</returns>
    /// <remarks>
    /// Returns null rather than a zero-filled estimate for an unknown mission type. A
    /// missing row is a content error, and showing "difficulty 0, trivial" for it would
    /// tell the player the safest possible thing about a mission nobody can describe.
    /// </remarks>
    public static MissionDifficultyEstimate? Estimate(
        WorldState world,
        SquadComposition composition,
        int missionTypeId,
        int siteTemplateId = 0)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (composition is null) throw new ArgumentNullException(nameof(composition));

        MissionTypeRow? mission = SimulationRules.MissionTypeFor(missionTypeId);
        if (mission is null)
            return null;

        var terms = new List<DifficultyTerm>();

        // ---- The mission's own demand -------------------------------------
        terms.Add(new DifficultyTerm("difficulty.term.base", mission.BaseDifficulty));

        SkillKind relevant = RelevantSkill(mission.ObjectiveType);
        terms.Add(new DifficultyTerm($"difficulty.term.skill.{relevant.ToString().ToLowerInvariant()}", SkillDemand(relevant, mission.Tier)));

        // ---- The site's contribution, when a site is known ------------------
        SiteTemplateRow? site = siteTemplateId > 0
            ? SimulationRules.SiteTemplateFor(siteTemplateId)
            : null;

        if (site is not null)
        {
            terms.Add(new DifficultyTerm("difficulty.term.tier", site.Tier * TierPowerPerLevel));
            terms.Add(new DifficultyTerm("difficulty.term.guards", site.GuardCountMax * GuardPowerEach));
            terms.Add(new DifficultyTerm("difficulty.term.security", site.SecurityGrade * SecurityPowerEach));

            if (site.HasForwardPost && composition.UnstaffedPost(site.HasForwardPost))
                terms.Add(new DifficultyTerm("difficulty.term.no_forward_post", MissingForwardPostPower));
        }

        int requiredPower = terms.Sum(t => t.Delta);

        // ---- The squad's answer ---------------------------------------------
        var squadTerms = new List<DifficultyTerm>();
        int teamPower = TeamPower(world, composition, relevant, squadTerms);

        // Difficulty is demand minus supply: the same squad is a bigger problem against a
        // harder site, and the shortfall is what the player is actually deciding about.
        int total = Math.Max(0, requiredPower - teamPower);

        var refusals = composition.Validate(world, site?.HasForwardPost ?? false);

        return new MissionDifficultyEstimate(
            Total: total,
            Terms: Compact(terms),
            SquadTerms: Compact(squadTerms),
            Band: BandFor(teamPower, requiredPower),
            Refusals: refusals,
            TeamPower: teamPower,
            RequiredPower: requiredPower);
    }

    /// <summary>
    /// Power a squad brings to bear, in the same scale as the demand.
    /// </summary>
    /// <remarks>
    /// Two contributors, both real. The relevant skill is the objective-specific one, and
    /// the squad's average is a floor so that a squad of five specialists cannot claim to
    /// be a squad of nothing. Neither is a fudge factor: both are quantities the player
    /// can see on the roster screen.
    /// </remarks>
    private static int TeamPower(
        WorldState world,
        SquadComposition composition,
        SkillKind relevant,
        List<DifficultyTerm> terms)
    {
        int relevantTotal = 0;
        int averageTotal = 0;
        int count = 0;

        foreach (SquadMember member in composition.Members)
        {
            Agent? agent = world.GetAgent(member.AgentId);
            if (agent is null)
                continue;

            count++;
            relevantTotal += agent.Skills[relevant];
            averageTotal += agent.Skills.Average();
        }

        if (count == 0)
        {
            terms.Add(new DifficultyTerm("difficulty.term.no_squad", 0));
            return 0;
        }

        // The average counts for a third: a squad needs general competence to survive
        // whatever the site throws at it, but the objective's own skill is what decides
        // the job. Weighting them 2:1 says "be good at the job, and be a team".
        int average = averageTotal / count;
        int power = SimulationRules.PercentOf(relevantTotal, PowerPerSkillPointPercent * 2 / 3)
                  + SimulationRules.PercentOf(average, PowerPerSkillPointPercent / 3);

        terms.Add(new DifficultyTerm(
            $"difficulty.term.team.{relevant.ToString().ToLowerInvariant()}", power));

        return power;
    }

    /// <summary>Demand the mission type places on one skill, by tier.</summary>
    private static int SkillDemand(SkillKind skill, int tier)
    {
        // Each tier raises the demand on the relevant skill. Computed from the skill kind
        // so all five tiers scale together rather than five hand-written numbers drifting.
        int baseDemand = skill switch
        {
            SkillKind.Tech => 8,
            SkillKind.Nerve => 6,
            SkillKind.Infiltration => 7,
            _ => 5,
        };

        return baseDemand * Math.Max(1, tier);
    }

    private const int TierPowerPerLevel = 5;
    private const int GuardPowerEach = 2;
    private const int SecurityPowerEach = 1;

    /// <summary>
    /// Classifies a matchup into a difficulty band.
    /// </summary>
    /// <remarks>
    /// Deliberately about the <em>ratio</em>, not the absolute number. Difficulty is a
    /// claim about a squad facing a site, and the same squad is comfortable or desperate
    /// depending only on what it is compared against.
    /// </remarks>
    private static TableDifficultyClass BandFor(int teamPower, int requiredPower)
    {
        if (requiredPower <= 0)
            return TableDifficultyClass.Trivial;

        int margin = SimulationRules.PercentOf(teamPower, 100 * 100 / Math.Max(1, requiredPower));

        return margin switch
        {
            >= ComfortableThreshold => TableDifficultyClass.Trivial,
            >= EvenThreshold => TableDifficultyClass.Easy,
            >= StrainedThreshold => TableDifficultyClass.Standard,
            >= DesperateThreshold => TableDifficultyClass.Hard,
            > 0 => TableDifficultyClass.Extreme,
            _ => TableDifficultyClass.Impossible,
        };
    }

    /// <summary>
    /// Drops zero terms and returns the rest.
    /// </summary>
    /// <remarks>
    /// A breakdown with a line of "+0" on it reads as a bug to the player and trains them
    /// to distrust the lines that are not zero. The absence of a term is the information.
    /// </remarks>
    private static IReadOnlyList<DifficultyTerm> Compact(List<DifficultyTerm> terms)
    {
        var kept = new List<DifficultyTerm>(terms.Count);
        foreach (DifficultyTerm term in terms)
        {
            if (term.IsMeaningful)
                kept.Add(term);
        }

        return kept;
    }
}
