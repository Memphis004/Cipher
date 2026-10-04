using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>What an NPC is currently trying to do.</summary>
/// <remarks>
/// <para>
/// <b>Now a display category, not a decision.</b> Stage 4c used these five tiers to
/// <em>choose</em> behaviour from suspicion and the alarm band. Stage 4d replaced that
/// with a GOAP search over <c>goap_goal</c> and <c>goap_action</c>, and this enum
/// survives only so that a debug panel, a minimap icon and the existing stage-4c tests
/// have something stable to read.
/// </para>
/// <para>
/// It is derived from the goal the planner chose, never the other way round. That
/// direction is the whole point of the change: as long as an intent could drive
/// behaviour, there would be two sources of truth about what an NPC wants, and the one
/// that was not in the table would quietly win whenever it disagreed.
/// </para>
/// </remarks>
public enum NpcIntent
{
    /// <summary>Nothing has registered. Walking the route.</summary>
    Patrol = 0,

    /// <summary>Something was heard or half-seen. Going to look at it.</summary>
    Investigate = 1,

    /// <summary>Somebody was identified. Going to where they were.</summary>
    Pursue = 2,

    /// <summary>Called to an alarm. Converging rather than standing at a post.</summary>
    Respond = 3,

    /// <summary>Panicked. Running for somewhere out of the way.</summary>
    Flee = 4,
}

/// <summary>
/// Reads and drives the site's NPC planning.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope, stated plainly.</b> This is no longer the thing that decides what NPCs do —
/// <see cref="GoapDirector"/> and <see cref="GoapExecutor"/> are. What remains here is
/// the vocabulary around them: the <see cref="NpcIntent"/> display mapping, and the
/// convenience entry point that lets a test or the headless simulator drive a mission's
/// NPCs without assembling a pipeline.
/// </para>
/// <para>
/// <b>One order per NPC per planning pass.</b> Carried over unchanged from stage 4c and
/// still load-bearing: a planner that emits a burst of orders for one actor would
/// resolve them all in the same step's <c>ApplyQueuedCommands</c> and the last would
/// silently overwrite the rest.
/// </para>
/// <para>
/// <b>Determinism.</b> NPCs are visited in ascending actor id, the planner draws no
/// random numbers at all, and the search's ordering is fixed by action id. The
/// randomness in a mission is perception, damage and skill checks, and putting more of
/// it in here would make a plan non-repeatable for no benefit.
/// </para>
/// </remarks>
public static class NpcPlanner
{
    /// <summary>
    /// The goal id each intent maps onto, from <c>goap_goal</c>.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from the enum, because these are table ids and a
    /// table can renumber a row while an enum member cannot. A test asserts every one of
    /// them still resolves to a real row.
    /// </remarks>
    public static int GoalIdFor(NpcIntent intent) => intent switch
    {
        NpcIntent.Patrol => 12201,      // goal.patrol
        NpcIntent.Investigate => 12202, // goal.investigate_noise
        NpcIntent.Pursue => 12204,      // goal.pursue_target
        NpcIntent.Respond => 12205,     // goal.call_for_backup
        NpcIntent.Flee => 12218,        // goal.flee
        _ => 12201,
    };

    /// <summary>
    /// The intent a chosen goal amounts to, for display.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="GoalIdFor"/>, with one addition: several goals map onto
    /// one intent, because a debug panel wants "that guard is pursuing" rather than
    /// "that guard is pursuing, and separately would also like to call for backup". The
    /// intent is a coarse label over a precise plan.
    /// </remarks>
    public static NpcIntent IntentForGoal(int goalId) => goalId switch
    {
        12202 or 12203 => NpcIntent.Investigate,          // investigate_noise, investigate_sighting
        12204 => NpcIntent.Pursue,                        // pursue_target
        12205 or 12206 => NpcIntent.Respond,              // call_for_backup, raise_alarm
        12209 or 12218 or 12219 => NpcIntent.Flee,        // flee_and_report, flee, hide
        _ => NpcIntent.Patrol,
    };

    /// <summary>
    /// Runs one planning pass over every NPC.
    /// </summary>
    /// <remarks>
    /// The convenience entry point: notify, plan within budget, run the radio timers and
    /// execute. The step pipeline does the same four things in
    /// <see cref="TacticalPhases"/>; this exists so a test or the headless simulator
    /// can drive NPCs without constructing a pipeline, and it is what keeps both paths
    /// honest about running identical logic.
    /// </remarks>
    public static int Plan(TacticalState state, long step, IRng rng)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        return GoapTick.Run(state, step, rng);
    }

    /// <summary>
    /// The coarse intent for an NPC, read from the goal it is currently pursuing.
    /// </summary>
    /// <remarks>
    /// Reads the plan rather than re-deriving from suspicion, so the label on a debug
    /// panel always describes what the NPC is actually doing. A derived-from-suspicion
    /// label would show "investigating" on a guard that is standing still because its
    /// plan ran out of reachable steps, which is the sort of small lie that makes a
    /// player stop trusting the debug overlay entirely.
    /// </remarks>
    public static NpcIntent IntentFor(TacticalState state, TacticalActor actor, long step)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (actor is null) throw new ArgumentNullException(nameof(actor));

        GoapAgent agent = GoapRuntime.DirectorFor(state).AgentFor(actor);

        return agent.Plan.GoalId > 0 ? IntentForGoal(agent.Plan.GoalId) : NpcIntent.Patrol;
    }

    /// <summary>The priority inputs an NPC's goals would be scored against.</summary>
    /// <remarks>
    /// Forwarded so the step pipeline and the debug surface cannot drift from the
    /// planner in how a priority is computed.
    /// </remarks>
    public static GoapPriorityInputs PriorityInputsFor(TacticalState state, TacticalActor actor, long step)
        => GoapObserver.PriorityInputsFor(state, actor, step);

    /// <summary>The archetype tags an NPC plans under.</summary>
    public static IReadOnlyList<string> TagsFor(TacticalActor actor) => GoapObserver.TagsFor(actor);

}
