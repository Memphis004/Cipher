using ProjectSpy.Tables;

namespace ProjectSpy.Core;

/// <summary>
/// One line of the arithmetic behind a skill number.
/// </summary>
/// <remarks>
/// <para>
/// A localization key naming the cause and a signed delta, never a sentence — Core does
/// not write English for the player (knowledge.md rule 4).
/// </para>
/// <para>
/// The brief asks for a full modifier breakdown ("Infiltration 62 = base 50 +8 training
/// +10 Ghost -6 fatigue"). This type carries what Core can actually account for, and
/// nothing else. See <see cref="SkillBreakdown"/> for why the list is short.
/// </para>
/// </remarks>
/// <param name="TermKey">Localization key naming the cause.</param>
/// <param name="Delta">Signed contribution. Zero is not emitted.</param>
public readonly record struct SkillTerm(string TermKey, int Delta)
{
    /// <summary>True when this term actually moved the number.</summary>
    public bool IsMeaningful => Delta != 0;
}

/// <summary>
/// Explains where an agent's skill number came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>This deliberately reports two terms, not five.</b> The brief's example implies a
/// trait bonus and a fatigue penalty on the same number. Core has neither: skills are
/// written once at recruitment (class base, plus the variance and quality rolls) and
/// afterwards only ever increase, through <see cref="Agent.AddSkillExp"/>. Traits such as
/// <c>InfiltrationBonus</c> exist in <c>trait.csv</c> and are read by the tactical
/// systems, but no code path adds them to an agent's stored skill value, and nothing
/// anywhere subtracts for fatigue.
/// </para>
/// <para>
/// So a breakdown claiming "+10 Ghost" would be a number the UI invented, in a game whose
/// central promise is that the UI does not invent things. When traits really do modify
/// skills, the term appears here and every tooltip picks it up with no UI change — which
/// is the only way the breakdown stays trustworthy.
/// </para>
/// <para>
/// <b>Known absences, stated rather than hidden.</b> <see cref="UnmodelledReasons"/> names
/// the modifiers a player might expect and Core does not apply. The tooltip shows them as
/// explicitly zero rather than omitting them, because "there is no fatigue penalty" is
/// information and silence would read as "the tooltip is incomplete".
/// </para>
/// </remarks>
public sealed record SkillBreakdown(
    SkillKind Skill,
    int Total,
    IReadOnlyList<SkillTerm> Terms,
    IReadOnlyList<SkillTerm> UnmodelledReasons)
{
    /// <summary>
    /// Explains one of an agent's five skills.
    /// </summary>
    /// <param name="agent">The agent. Never null.</param>
    /// <param name="skill">Which skill.</param>
    /// <remarks>
    /// <b>Recruitment is a die roll, so the base is the roll, not the class average.</b>
    /// <see cref="RecruitmentSystem.GenerateCandidate"/> draws each skill as the class
    /// base plus a variance roll plus a quality bonus. Reporting the class base here and
    /// calling the rest "training" would be a lie about where the number came from, so the
    /// recruited value is recovered as the class base adjusted by the agent's level — the
    /// only quantities Core stored at that moment — and everything above it is training.
    /// </remarks>
    public static SkillBreakdown For(Agent agent, SkillKind skill)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        int classBase = AgentClasses.BaseSkillsFor(agent.ClassId)[skill];
        int total = agent.Skills[skill];

        // Level is the one thing Core kept from the recruitment roll, so the rolled start
        // is recoverable to within the variance the roll actually applied. Reported as a
        // single "base" term because the player thinks of it as one number: "this is what
        // they walked in with".
        int recruited = classBase > 0
            ? SimulationRules.PercentOf(classBase, 100 + agent.Level - 1)
            : total;

        var terms = new List<SkillTerm>(2);
        terms.Add(new SkillTerm("skill.term.base", recruited));

        int training = total - recruited;
        terms.Add(new SkillTerm("skill.term.training", training));

        // The two modifiers a player will look for and Core does not apply. Reported at
        // zero so their absence is visible rather than inferred from a short list.
        var unmodelled = new List<SkillTerm>(2)
        {
            new("skill.term.trait_bonus", 0),
            new("skill.term.fatigue", 0),
        };

        return new SkillBreakdown(
            Skill: skill,
            Total: total,
            Terms: Compact(terms),
            UnmodelledReasons: unmodelled);
    }

    /// <summary>Explains all five skills.</summary>
    public static IReadOnlyList<SkillBreakdown> ForAll(Agent agent)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        var all = new List<SkillBreakdown>(SkillSet.Kinds.Length);
        foreach (SkillKind kind in SkillSet.Kinds)
            all.Add(For(agent, kind));

        return all;
    }

    /// <summary>
    /// Localization key naming this skill, for the tooltip heading.
    /// </summary>
    public string SkillKey => $"skill.name.{Skill.ToString().ToLowerInvariant()}";

    /// <summary>
    /// Drops zero terms.
    /// </summary>
    /// <remarks>
    /// A breakdown line reading "+0" trains the player to distrust the lines that are
    /// not zero. The absence of a term is the information — except for the deliberately
    /// zero-valued <see cref="UnmodelledReasons"/>, which is reported separately for
    /// exactly that reason.
    /// </remarks>
    private static IReadOnlyList<SkillTerm> Compact(List<SkillTerm> terms)
    {
        var kept = new List<SkillTerm>(terms.Count);
        foreach (SkillTerm term in terms)
        {
            if (term.IsMeaningful)
                kept.Add(term);
        }

        return kept;
    }
}
