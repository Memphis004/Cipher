using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Holds the one <see cref="GoapDirector"/> a mission plans through.
/// </summary>
/// <remarks>
/// <para>
/// The director is per-mission state, and it has to be reachable from the step pipeline
/// without every phase signature changing. <see cref="TacticalState"/> is the natural
/// owner — it already holds the mission's other mutable state — but a tactical state is
/// also hashed, serialized and compared by tests, and hanging a planner off it would put
/// a search's scratch arrays into every state hash. So it lives beside the state, keyed
/// by the state instance.
/// </para>
/// <para>
/// <b>A weak-keyed table, not a field.</b> A conditional-weak table means a finished
/// mission's director and its node arrays are collectable without anything having to
/// remember to drop them, which matters because the alternative is a leak on every
/// mission played.
/// </para>
/// </remarks>
public static class GoapRuntime
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TacticalState, GoapDirector> Directors = new();

    /// <summary>The director for a mission, created on first use.</summary>
    public static GoapDirector DirectorFor(TacticalState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        return Directors.GetValue(state, static _ => new GoapDirector());
    }

    /// <summary>
    /// The debug surface for a mission: every NPC's goal, action and knowledge.
    /// </summary>
    /// <remarks>
    /// <b>Mandatory.</b> The brief calls this out as a requirement rather than a nicety,
    /// and the reasoning is sound: a stealth player who cannot tell that a guard reacted
    /// to a noise they made has no way to learn what to do differently, and concludes the
    /// AI is cheating. Keys and numbers only — Presentation localizes.
    /// </remarks>
    public static IReadOnlyList<GoapDebugInfo> DebugSurface(TacticalState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        GoapDirector director = DirectorFor(state);
        var rows = new List<GoapDebugInfo>();

        foreach (GoapAgent agent in director.AgentsInOrder())
        {
            TacticalActor actor = agent.Actor;

            if (!actor.CanAct)
                continue;

            IReadOnlyList<GoapGoalScore> ranked = GoapGoalSelector.RankAll(
                BuildWorld(state, agent, state.Step),
                NpcPlanner.TagsFor(actor),
                NpcPlanner.PriorityInputsFor(state, actor, state.Step),
                GoapCatalog.Load());

            rows.Add(agent.DebugInfo(ranked, state.Step));
        }

        return rows;
    }

    /// <summary>The world state an NPC would plan against right now.</summary>
    /// <remarks>
    /// Exposed so the debug surface and the tests read exactly the state the planner
    /// read. A second implementation here would be a second answer to "what does this
    /// NPC know", which is the question the whole no-omniscience property turns on.
    /// </remarks>
    public static GoapWorldState BuildWorld(TacticalState state, GoapAgent agent, long step)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        return GoapWorldStateBuilder.Build(
            agent.Actor,
            agent.Findings,
            step,
            GoapObserver.ObserveLocally(state, agent.Actor));
    }
}