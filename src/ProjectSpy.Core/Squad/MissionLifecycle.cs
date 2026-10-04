using ProjectSpy.Core.Tactical;

using TableResolveClass = ProjectSpy.Tables.ResolveClass;
using TacticalOutcome = ProjectSpy.Core.Tactical.MissionOutcome;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// Where a mission is in the lifecycle the brief lays out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch → Travel → Insert → Tactical → Resolve → Return → Debrief.</b> The
/// interesting boundary is Travel: it is the only phase where a mission exists but
/// nobody is in the building, and the only one the base clock moves through. Rule 13
/// freezes the clock during the tactical mission, so the two cannot be confused — a
/// mission in <see cref="Tactical"/> has already converted its steps exactly once.
/// </para>
/// <para>
/// <b>The strategic <see cref="ProjectSpy.Core.MissionOutcome"/> and this one are
/// different enums on purpose.</b> The tactical one says how the fight went; this one
/// says where in the administrative process the mission is. A mission can be tactically
/// exfiltrated and still be in <see cref="Resolve"/> for two ticks while the debrief is
/// compiled, and a mission that ends in <see cref="Aborted"/> may still be in
/// <see cref="Return"/> for longer.
/// </para>
/// </remarks>
public enum MissionPhase
{
    /// <summary>Being composed on the dispatch screen. Not yet committed.</summary>
    Dispatch = 0,

    /// <summary>Traveling to the site. Strategic ticks pass.</summary>
    Travel = 1,

    /// <summary>At the site, about to go in. One tick.</summary>
    Insert = 2,

    /// <summary>Being played. The base clock is frozen.</summary>
    Tactical = 3,

    /// <summary>Classified and paid. One tick.</summary>
    Resolve = 4,

    /// <summary>Coming home. Strategic ticks pass.</summary>
    Return = 5,

    /// <summary>The report has been produced. Nothing further happens.</summary>
    Debrief = 6,

    /// <summary>The mission is over and nothing is left to do.</summary>
    Closed = 7,
}

/// <summary>
/// How a mission ended, in the vocabulary the debrief and the rewards both speak.
/// </summary>
/// <remarks>
/// <para>
/// <b>Five classes, and the middle one is the point.</b> The brief names them
/// CleanSuccess, Success, Compromised, Aborted and Disaster, and their existence in that
/// order is what makes aborting a legitimate choice rather than a failure state to be
/// avoided: a Compromised mission pays real money and a Disaster pays nothing, and the
/// difference is entirely in whether the team came home and how loudly it left.
/// </para>
/// <para>
/// <b>Derived, never declared.</b> Nothing in the code says "this mission was
/// Compromised"; <see cref="ResolveSystem.Classify"/> works it out from the tactical
/// outcome, the alarm band, and whether anybody was taken. A classification that could
/// be set by hand would eventually be set by hand.
/// </para>
/// </remarks>
public enum ResolveClass
{
    /// <summary>Not resolved.</summary>
    Unresolved = 0,

    /// <summary>Objective done, team out, site never knew.</summary>
    CleanSuccess = 1,

    /// <summary>Objective done, team out, site noticed something.</summary>
    Success = 2,

    /// <summary>The team came out having been made. Some of it worked.</summary>
    Compromised = 3,

    /// <summary>The team came out without the objective. Deliberately.</summary>
    Aborted = 4,

    /// <summary>The team did not come out.</summary>
    Disaster = 5,
}

/// <summary>
/// The result of resolving a finished tactical mission.
/// </summary>
public sealed record MissionResolution
{
    /// <summary>How it ended.</summary>
    public ResolveClass Class { get; init; } = ResolveClass.Unresolved;

    /// <summary>Localization key for the class name.</summary>
    public string ClassNameKey { get; init; } = string.Empty;

    /// <summary>The tactical outcome the class was derived from.</summary>
    public TacticalOutcome TacticalOutcome { get; init; } = TacticalOutcome.InProgress;

    /// <summary>True when the objective was achieved.</summary>
    public bool ObjectiveComplete { get; init; }

    /// <summary>True when every agent who went in came out.</summary>
    public bool TeamExtracted { get; init; }

    /// <summary>How many agents were captured or killed.</summary>
    public int Lost { get; init; }

    /// <summary>The alarm band the mission ended in.</summary>
    public AlarmBand EndBand { get; init; }

    /// <summary>Heat this mission will add.</summary>
    public int Heat { get; init; }

    /// <summary>Evidence this mission will leave behind.</summary>
    public int Evidence { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Class} (lost {Lost}, {EndBand})";
}

/// <summary>
/// Turns a finished tactical mission into a class, and a class into consequences.
/// </summary>
/// <remarks>
/// <para>
/// Every multiplier comes from <c>resolve_rule</c>. That is the whole point of the
/// table: "what does getting caught actually cost" is the single most-tuned number in
/// the game, and putting it in Core would mean a code change to change it.
/// </para>
/// <para>
/// <b>Classification order matters and is fixed.</b> A mission with casualties is a
/// Disaster whatever else is true, because a team that did not come home did not get
/// paid on the strength of what it managed to do before it went wrong.
/// </para>
/// </remarks>
public static class ResolveSystem
{
    /// <summary>
    /// Works out how a finished mission ended.
    /// </summary>
    /// <param name="state">The finished tactical mission.</param>
    /// <param name="outcome">The objective's own result.</param>
    /// <param name="aborted">True when the player chose to abort rather than it being forced.</param>
    public static MissionResolution Classify(
        TacticalState state, ObjectiveOutcome outcome, bool aborted)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (outcome is null) throw new ArgumentNullException(nameof(outcome));

        int lost = CountLost(state);
        bool extracted = lost == 0;
        bool complete = outcome.IsComplete;

        ResolveClass result = Decide(
            state, lost, extracted, complete, aborted);

        ProjectSpy.Tables.ResolveRule? rule = SimulationRules.ResolveRuleFor(Table(result));

        return new MissionResolution
        {
            Class = result,
            ClassNameKey = rule?.NameKey ?? $"resolve.{result.ToString().ToLowerInvariant()}",
            TacticalOutcome = state.Outcome,
            ObjectiveComplete = complete,
            TeamExtracted = extracted,
            Lost = lost,
            EndBand = state.Alarm.Band,
            Heat = ResolveHeat(state, rule),
            Evidence = ResolveEvidence(state, rule),
        };
    }

    /// <summary>
    /// The classification, as a fixed decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Disaster first.</b> Casualties dominate: the mission's other qualities are
    /// things that happened to a team that is no longer intact, and paying a bonus for
    /// them would make the game reward the run that gets everyone killed.
    /// </para>
    /// <para>
    /// <b>Then Aborted, before anything about success.</b> A deliberate abort that
    /// brought everybody home is Aborted whatever the objective said, because the player
    /// chose the risk knowingly and the debrief should show them that choice rather than
    /// flattering it.
    /// </para>
    /// <para>
    /// <b>Then Compromised on evidence of contact</b> — alarm above the calm band, or
    /// somebody was shot, or the team did not all come home (already handled above).
    /// <b>CleanSuccess</b> is the residue: objective done, nobody hurt, and the site
    /// never got above suspicious.
    /// </para>
    /// </remarks>
    private static ResolveClass Decide(
        TacticalState state, int lost, bool extracted, bool complete, bool aborted)
    {
        if (lost > 0)
            return ResolveClass.Disaster;

        if (aborted || state.Outcome == TacticalOutcome.Aborted)
            return ResolveClass.Aborted;

        if (!complete)
            return ResolveClass.Aborted;

        bool noisy = state.Alarm.Band > AlarmBand.Calm
                     || state.LethalActs > 0
                     || state.EvidenceLevel > 0;

        return noisy ? ResolveClass.Success : ResolveClass.CleanSuccess;
    }

    /// <summary>
    /// How many agents are dead, captured, or too hurt to have got out.
    /// </summary>
    /// <remarks>
    /// <b>Downed counts.</b> A member who was carried out unconscious has not lost
    /// anything permanent, but the mission's success class is about whether the team came
    /// home <em>able-bodied</em>, and the injury is applied to the agent afterwards. A
    /// classification that ignored casualties entirely would make a team that walked out
    /// carrying two of its own indistinguishable from one that walked out clean.
    /// </remarks>
    private static int CountLost(TacticalState state)
    {
        int lost = 0;

        foreach (TacticalActor actor in state.Squad)
        {
            if (actor.Condition is ActorCondition.Dead or ActorCondition.Captured
                or ActorCondition.Downed)
            {
                lost++;
            }
        }

        return lost;
    }

    /// <summary>The heat this mission adds, scaled by the class.</summary>
    private static int ResolveHeat(TacticalState state, ProjectSpy.Tables.ResolveRule? rule)
    {
        if (rule is null)
            return state.HeatGained;

        int base_ = Math.Max(state.HeatGained, state.Alarm.Level);
        return SimulationRules.PercentOf(base_, rule.HeatPercent);
    }

    /// <summary>The evidence this mission leaves, scaled by the class.</summary>
    private static int ResolveEvidence(TacticalState state, ProjectSpy.Tables.ResolveRule? rule)
        => rule is null
            ? state.EvidenceLevel
            : SimulationRules.PercentOf(state.EvidenceLevel, rule.EvidencePercent);

    /// <summary>The table enum a resolution class maps to.</summary>
    public static TableResolveClass Table(ResolveClass result) => result switch
    {
        ResolveClass.CleanSuccess => TableResolveClass.CleanSuccess,
        ResolveClass.Success => TableResolveClass.Success,
        ResolveClass.Compromised => TableResolveClass.Compromised,
        ResolveClass.Aborted => TableResolveClass.Aborted,
        ResolveClass.Disaster => TableResolveClass.Disaster,
        _ => throw new NotImplementedException(
            $"TODO(stage-4e): resolve class {result} has no reward row."),
    };

    /// <summary>
    /// Applies a resolution's heat to the world.
    /// </summary>
    /// <remarks>
    /// Only heat, because heat is the one consequence that is unambiguously additive and
    /// has no failure mode worth gating. Funds, intel and loot depend on what was carried
    /// out, which <see cref="MissionReport"/> knows and this does not.
    /// </remarks>
    public static void ApplyHeat(WorldState world, MissionResolution resolution)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (resolution is null) throw new ArgumentNullException(nameof(resolution));

        world.Resources = world.Resources.AddHeat(resolution.Heat);
    }
}