namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Runs the site's planning: who replans this step, what they plan, and what they then
/// do about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The budget is the reason this type exists.</b> Thirty NPCs each running A* every
/// 100 ms step would blow the brief's 2 ms on its own, so replanning is rationed: at
/// most <c>goap_max_plans_per_step</c> NPCs replan in any one step, chosen by a fair
/// round-robin rather than by urgency.
/// </para>
/// <para>
/// <b>Why round-robin and not "most urgent first".</b> Sorting the queue by priority
/// and taking the top N looks fairer and is not: when the building is busy every NPC has
/// a high priority, the ordering collapses to whatever tie-break applies, and the same
/// few NPCs win every step while the rest never replan at all. A guard that never
/// replans is a guard permanently stuck on its first impression, which in practice means
/// the guards nearest the entrance are the only ones that ever respond. The queue here
/// is strictly first-come, so a request cannot be starved behind newer, louder ones, and
/// <see cref="GoapAgent.RequestedOnStep"/> is what lets a test prove it.
/// </para>
/// <para>
/// <b>Determinism.</b> The queue is an ordered list of actor ids drained from the front,
/// appended to in ascending actor id order within a step. No hashing, no set iteration,
/// no priority sorting. Two runs of the same mission therefore replan the same NPCs in
/// the same order, which is what makes a recorded replay reproduce.
/// </para>
/// <para>
/// <b>Event-driven, not polled.</b> Nothing here decides to replan on a timer. The
/// queue is filled only by <see cref="GoapAgent.Invalidate"/>, which is called by
/// perception, noise, the alarm band and the systems that discover bodies and doors.
/// </para>
/// </remarks>
public sealed class GoapDirector
{
    private readonly GoapPlanner _planner = new();
    private readonly Dictionary<TacticalActorId, GoapAgent> _agents = new();

    /// <summary>NPCs awaiting a replan, in the order they asked.</summary>
    private readonly List<TacticalActorId> _queue = new();

    /// <summary>The actions each archetype may perform, resolved once.</summary>
    private readonly Dictionary<string, IReadOnlyList<GoapActionDef>> _actionsByTag = new(StringComparer.Ordinal);

    /// <summary>Every action that any archetype may perform.</summary>
    private readonly IReadOnlyList<GoapActionDef> _anyActions;

    private GoapCatalogData _catalog = null!;

    /// <summary>How many NPCs replanned on the most recent step.</summary>
    public int LastPlanCount { get; private set; }

    /// <summary>How many plans could not be found on the most recent step.</summary>
    public int LastNoPlanCount { get; private set; }

    /// <summary>
    /// The largest number of NPCs that may replan in one step.
    /// </summary>
    /// <remarks>
    /// From <c>goap_rule.max_plans_per_step</c>. Clamped to at least one, because zero
    /// would mean no NPC ever plans at all — a silent, total failure that would look
    /// like a site where guards simply do not react.
    /// </remarks>
    private static int PlanBudget => Math.Max(1, SimulationRules.Goap("goap_max_plans_per_step", 6));

    /// <summary>
    /// How long an NPC waits between unsolicited replans.
    /// </summary>
    /// <remarks>
    /// A floor on replanning frequency, not a schedule. An event still drives every
    /// replan; this stops one NPC that keeps getting poked from consuming the whole
    /// budget and starving a guard who saw something once.
    /// </remarks>
    private static int ReplanCooldownSteps
        => Math.Max(1, SimulationRules.Goap("goap_replan_cooldown_steps", 5));

    /// <summary>Prepares the director against a catalog. Tests use this.</summary>
    public GoapDirector(GoapCatalogData? catalog = null)
    {
        _catalog = catalog ?? GoapCatalog.Load();
        _anyActions = ResolveActions(string.Empty);
    }

    /// <summary>The agent for an NPC, created on first use.</summary>
    public GoapAgent AgentFor(TacticalActor actor)
    {
        if (actor is null) throw new ArgumentNullException(nameof(actor));

        if (!_agents.TryGetValue(actor.Id, out GoapAgent? agent))
        {
            agent = new GoapAgent(actor);
            _agents[actor.Id] = agent;
        }

        return agent;
    }

    /// <summary>Every agent, in ascending actor id order. For the debug surface.</summary>
    public IReadOnlyList<GoapAgent> AgentsInOrder()
    {
        var ordered = new List<GoapAgent>(_agents.Values);
        ordered.Sort(static (a, b) => a.Actor.Id.Value.CompareTo(b.Actor.Id.Value));
        return ordered;
    }

    /// <summary>
    /// Asks an NPC to replan, on whatever grounds the caller has.
    /// </summary>
    /// <remarks>
    /// The single entry point other systems use, so that the cooldown and the queue are
    /// enforced in one place. A system that wanted to bypass them could call
    /// <see cref="GoapAgent.Invalidate"/> directly, and that is exactly why this method
    /// exists: it is the one that decides whether the request is honoured now.
    /// </remarks>
    public void RequestPlan(GoapAgent agent, GoapInvalidationReason reason, long step)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        // The cooldown exempts reasons that are genuinely new information. A body is
        // new information every time it is found; "I am still suspicious" is not, and
        // honouring it every five steps forever is what would starve the building.
        bool urgent = reason is GoapInvalidationReason.BodyFound
            or GoapInvalidationReason.AlarmBandChanged
            or GoapInvalidationReason.ColleagueMissing
            or GoapInvalidationReason.ObjectiveTampered
            or GoapInvalidationReason.SuspiciousDoor;

        if (!urgent && step - agent.PlannedOnStep < ReplanCooldownSteps)
            return;

        agent.Invalidate(reason, step);

        if (!_queue.Contains(agent.Actor.Id))
            _queue.Add(agent.Actor.Id);
    }

    /// <summary>
    /// Services the replan queue for one step, within the budget.
    /// </summary>
    /// <param name="state">The mission, for tags and the alarm band.</param>
    /// <param name="step">The step being resolved.</param>
    /// <returns>How many NPCs replanned.</returns>
    public int PlanStep(TacticalState state, long step)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        int budget = PlanBudget;
        int planned = 0;
        int noPlan = 0;

        // Snapshot the budget's worth from the front of the queue before draining, so a
        // replan that enqueues more work cannot extend this step past its allowance.
        int take = Math.Min(budget, _queue.Count);

        for (int i = 0; i < take; i++)
        {
            TacticalActorId id = _queue[i];

            if (!_agents.TryGetValue(id, out GoapAgent? agent))
                continue;

            if (!agent.NeedsPlan)
                continue;

            GoapPlan plan = PlanFor(state, agent, step);

            agent.Commit(plan, step, plan.NodesExpanded);
            planned++;

            if (plan.Status == GoapPlanStatus.NoPlan || plan.Status == GoapPlanStatus.BudgetExhausted)
                noPlan++;
        }

        _queue.RemoveRange(0, take);

        LastPlanCount = planned;
        LastNoPlanCount = noPlan;
        return planned;
    }

    /// <summary>
    /// Selects a goal and searches for a plan to reach it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the ranked goal list and takes the first one it can actually plan for, not
    /// simply the most urgent. Priority alone does not mean a goal can be had: a guard
    /// that has just found a body is full of curiosity, and <c>goal.pursue_target</c> is
    /// weighted heavily on curiosity, but there is nobody left to chase — so the highest
    /// priority goal is unreachable and the honest alternatives (raise the alarm, call it
    /// in) are never considered. Without this walk, a building full of guards would find
    /// a corpse and every one of them would stand still being confused.
    /// </para>
    /// <para>
    /// The walk is bounded by <see cref="MaxGoalsTried"/>, and only ever considers
    /// goals valid for this NPC's archetype, so it is bounded by the goal table anyway;
    /// the cap is there so that adding goals cannot quietly turn it into an unbounded
    /// search inside a system with a per-step budget.
    /// </para>
    /// </remarks>
    private GoapPlan PlanFor(TacticalState state, GoapAgent agent, long step)
    {
        IReadOnlyList<string> tags = GoapObserver.TagsFor(agent.Actor);
        IReadOnlyList<GoapActionDef> actions = ResolveActionsFor(tags);

        GoapPriorityInputs inputs = GoapObserver.PriorityInputsFor(state, agent, step);

        GoapWorldState world = BuildWorld(state, agent, step);

        IReadOnlyList<GoapGoalDef> candidates = GoapGoalSelector.Candidates(world, tags, inputs, _catalog);

        int tried = 0;

        foreach (GoapGoalDef goal in candidates)
        {
            if (tried++ >= MaxGoalsTried)
                break;

            GoapPlan plan = _planner.Plan(world, goal, actions);

            if (plan.Status == GoapPlanStatus.Planned)
                return plan;

            // A goal that is already satisfied needs no actions; that is a real answer,
            // not a failure to plan, and it should not send us shopping down the list.
            if (plan.Status == GoapPlanStatus.GoalAlreadySatisfied)
                return plan;
        }

        return new GoapPlan(0, string.Empty, Array.Empty<int>(), 0, 0, GoapPlanStatus.NoPlan);
    }

    /// <summary>How many ranked goals one NPC may try before giving up on all of them.</summary>
    /// <remarks>
    /// <para>
    /// Large on purpose. For most goals in an archetype's ranked list there is no plan
    /// to be had at all: a guard on a quiet round cannot pursue a target it has not seen,
    /// and the goals weighted on <c>alarm</c> or <c>curiosity</c> are precisely the ones
    /// that rise above the single goal this NPC can actually act on. A cap of a handful
    /// therefore does not act as a rare fallback — it spends the whole walk on
    /// unreachable goals and returns <see cref="GoapPlanStatus.NoPlan"/> for a guard that
    /// had a perfectly good plan available at rank seven. That failure is silent: the
    /// guard simply stands there.
    /// </para>
    /// <para>
    /// The bound that actually protects the frame is not this number but the planner's
    /// per-goal node budget, which caps every individual attempt. This is only here so
    /// that a future goal table cannot turn the walk into an unbounded search wearing a
    /// planner's clothes, and it is comfortably above the goal table as it stands.
    /// </para>
    /// </remarks>
    private const int MaxGoalsTried = 24;

    /// <summary>
    /// Builds this NPC's world, filling in only what the NPC could see where it stands.
    /// </summary>
    /// <remarks>
    /// The call that hands the builder its surroundings, and the only place in Core that
    /// reads the site on an NPC's behalf. It reads the actor's <em>own</em> room and the
    /// connections touching it — not the site, not the player, not other guards — and
    /// passes the result as a plain observation record. Everything the NPC did not walk
    /// past stays out.
    /// </remarks>
    private GoapWorldState BuildWorld(TacticalState state, GoapAgent agent, long step)
        => GoapWorldStateBuilder.Build(
            agent.Actor,
            agent.Findings,
            step,
            GoapObserver.ObserveLocally(state, agent.Actor));

    /// <summary>
    /// The actions available to a set of tags: any action whose required tags are a
    /// subset of what this NPC has.
    /// </summary>
    /// <remarks>
    /// Resolved once per distinct tag set and cached, because the filter runs over every
    /// action row and the set of distinct tag combinations in a building is small
    /// (four guard roles plus civilians). Sorting is not needed: the catalog is already in
    /// ascending action id, and the filter preserves that order, which is the order the
    /// planner expands in.
    /// </remarks>
    private IReadOnlyList<GoapActionDef> ResolveActionsFor(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
            return _anyActions;

        string key = string.Join("|", tags);

        if (_actionsByTag.TryGetValue(key, out IReadOnlyList<GoapActionDef>? cached))
            return cached;

        var allowed = new List<GoapActionDef>();

        foreach (GoapActionDef action in _catalog.Actions)
        {
            if (HasAllTags(action.RequiredTags, tags))
                allowed.Add(action);
        }

        IReadOnlyList<GoapActionDef> result = allowed;
        _actionsByTag[key] = result;
        return result;
    }

    /// <summary>Actions anyone may perform, used when an NPC has no archetype.</summary>
    private IReadOnlyList<GoapActionDef> ResolveActions(string tag)
    {
        var allowed = new List<GoapActionDef>();

        foreach (GoapActionDef action in _catalog.Actions)
        {
            if (HasAllTags(action.RequiredTags, tag.Length == 0 ? Array.Empty<string>() : new[] { tag }))
                allowed.Add(action);
        }

        return allowed;
    }

    /// <summary>
    /// Whether an action's required tags are all present.
    /// </summary>
    /// <remarks>
    /// An action with no required tags belongs to everybody. <c>any</c> as a requirement
    /// is satisfied by any non-empty tag set, which is how the CSV's <c>any</c> reads.
    /// </remarks>
    private static bool HasAllTags(IReadOnlyList<string> required, IReadOnlyList<string> held)
    {
        for (int i = 0; i < required.Count; i++)
        {
            string need = required[i];

            if (need == GoapObserver.AnyTag && held.Count > 0)
                continue;

            bool found = false;

            foreach (string have in held)
            {
                if (string.Equals(need, have, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }
}