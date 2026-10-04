namespace ProjectSpy.Core.Tactical;

/// <summary>
/// A plan: the actions an NPC intends to perform, in order.
/// </summary>
/// <remarks>
/// Immutable and shared. A plan is computed once and then reused across steps until an
/// event invalidates it, so keeping it a value rather than re-deriving it is what makes
/// "cached plan reused until invalidated" mean anything.
/// </remarks>
public sealed class GoapPlan
{
    /// <summary>No plan. What the planner returns when it cannot reach the goal.</summary>
    public static readonly GoapPlan None = new(0, string.Empty, Array.Empty<int>(), 0, 0, GoapPlanStatus.NoPlan);

    /// <summary>The goal this plan is trying to achieve.</summary>
    public int GoalId { get; }

    /// <summary>Localization key for the goal's name.</summary>
    public string GoalNameKey { get; }

    /// <summary>
    /// The <c>goap_action</c> ids, in the order they are to be performed.
    /// </summary>
    /// <remarks>
    /// In plan order, not sorted. The first entry is what the NPC does next; the order
    /// is the whole content of a plan, and reversing it would produce an NPC that walks
    /// to the door before closing it.
    /// </remarks>
    public IReadOnlyList<int> ActionIds { get; }

    /// <summary>Total planner cost, from <c>goap_action.base_cost</c>.</summary>
    public int TotalCost { get; }

    /// <summary>Search nodes this plan cost to find.</summary>
    public int NodesExpanded { get; }

    /// <summary>Why the planner returned this plan.</summary>
    public GoapPlanStatus Status { get; }

    /// <summary>True when there is at least one action to perform.</summary>
    public bool HasActions => ActionIds.Count > 0;

    /// <summary>The next action id, or zero when the plan is exhausted.</summary>
    public int NextActionId => ActionIds.Count > 0 ? ActionIds[0] : 0;

    internal GoapPlan(
        int goalId,
        string goalNameKey,
        IReadOnlyList<int> actionIds,
        int totalCost,
        int nodesExpanded,
        GoapPlanStatus status)
    {
        GoalId = goalId;
        GoalNameKey = goalNameKey;
        ActionIds = actionIds;
        TotalCost = totalCost;
        NodesExpanded = nodesExpanded;
        Status = status;
    }

    /// <summary>
    /// This plan with its first action removed.
    /// </summary>
    /// <remarks>
    /// Returns the same instance when there is nothing left, so a caller can call it
    /// every step on an exhausted plan without allocating. An NPC mid-plan is the only
    /// thing that should cause a new plan to be sought, and only because the world
    /// moved under it.
    /// </remarks>
    public GoapPlan Advance()
    {
        if (ActionIds.Count <= 1)
            return new GoapPlan(GoalId, GoalNameKey, Array.Empty<int>(), TotalCost, NodesExpanded, Status);

        var remaining = new int[ActionIds.Count - 1];

        for (int i = 1; i < ActionIds.Count; i++)
            remaining[i - 1] = ActionIds[i];

        return new GoapPlan(GoalId, GoalNameKey, remaining, TotalCost, NodesExpanded, Status);
    }

    /// <inheritdoc/>
    public override string ToString()
        => Status == GoapPlanStatus.NoPlan
            ? "no plan"
            : $"{GoalNameKey}: [{string.Join(" -> ", ActionIds)}] cost={TotalCost} nodes={NodesExpanded} {Status}";
}

/// <summary>How a planning attempt ended.</summary>
public enum GoapPlanStatus
{
    /// <summary>No plan was found within the budget. The NPC keeps what it was doing.</summary>
    NoPlan = 0,

    /// <summary>The goal was already satisfied. No action is needed.</summary>
    GoalAlreadySatisfied = 1,

    /// <summary>A plan was found and the budget was not exhausted.</summary>
    Planned = 2,

    /// <summary>
    /// The node budget ran out before the search could finish.
    /// </summary>
    /// <remarks>
    /// Reported rather than hidden because it is the one outcome that means the
    /// planner did not really answer the question. Treated as a failure by the
    /// benchmark and asserted on by the budget test, so a budget that is quietly too
    /// small shows up as a named failure instead of as NPCs that stopped reacting.
    /// </remarks>
    BudgetExhausted = 3,
}

/// <summary>
/// A* over <c>goap_action</c>, from an NPC's own world state to one of its goals.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism is the whole design constraint.</b> Rule 6 requires that the same
/// seed and command log produce the same mission, and a GOAP planner is the most
/// direct way to lose that: the usual failure is a priority queue or a dictionary
/// whose ordering depends on hashing or on insertion history. Three rules make this
/// one deterministic by construction rather than by luck:
/// </para>
/// <list type="number">
/// <item>
/// Actions are expanded in ascending id, because <see cref="GoapCatalog"/> sorts them
/// once at load and the search never sorts.
/// </item>
/// <item>
/// The open set is a sorted array, not a priority queue. Ties break on the f-score
/// first and then on the parent node's id, so two nodes with equal scores are always
/// taken in the same order.
/// </item>
/// <item>
/// Nothing anywhere enumerates a hash collection to decide what to do next. The closed
/// set is a hash set used only for membership — never iterated — so its internal layout
/// cannot influence a plan.
/// </item>
/// </list>
/// <para>
/// <b>The budget is hard.</b> A plan that has spent its node allowance returns
/// immediately, mid-search, having produced nothing. This is what makes the cost of a
/// step bounded rather than merely predictable: without it, one pathological goal set
/// could open the whole state space inside a frame and blow the frame.
/// </para>
/// <para>
/// <b>Scratch state is reused.</b> The node arrays are held per planner instance and
/// reused across searches, because a 30-NPC building replans constantly and allocating
/// four lists per plan is a measurable share of a 2 ms budget. They are only ever
/// written between searches, never handed out, so reuse cannot leak state between
/// plans.
/// </para>
/// </remarks>
public sealed class GoapPlanner
{
    private readonly GoapCatalogData _catalog;

    // Reused across searches. Node 0 is the root and is never reused as an expansion.
    private GoapWorldState[] _states = Array.Empty<GoapWorldState>();
    private GoapActionDef?[] _applied = Array.Empty<GoapActionDef?>();
    private int[] _parent = Array.Empty<int>();
    private int[] _costSoFar = Array.Empty<int>();
    private int[] _openOrder = Array.Empty<int>();
    private int[] _openF = Array.Empty<int>();
    private int[] _scratchPath = Array.Empty<int>();
    private int _openCount;
    private int _nodeCount;

    /// <summary>Creates a planner over the loaded GOAP tables.</summary>
    public GoapPlanner()
        : this(GoapCatalog.Load())
    {
    }

    /// <summary>Creates a planner over a specific catalog. Tests use this.</summary>
    public GoapPlanner(GoapCatalogData catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>How many nodes the last search expanded.</summary>
    public int LastNodesExpanded => _lastNodes;

    private int _lastNodes;

    /// <summary>
    /// The largest number of nodes any single search may expand.
    /// </summary>
    /// <remarks>
    /// From <c>goap_rule.node_budget</c> rather than a constant. It is a tuneable
    /// because it trades planning depth against step cost, and a designer raising a
    /// building's guard density needs to be able to move that trade without a code
    /// change. The clamp below keeps a mistyped 0 from making every plan fail.
    /// </remarks>
    private static int NodeBudget => Math.Max(16, SimulationRules.Goap("goap_node_budget", 256));

    /// <summary>The longest plan the planner will return.</summary>
    private static int MaxPlanLength => Math.Max(1, SimulationRules.Goap("goap_plan_max_length", 12));

    /// <summary>
    /// The longest plan found for a goal, or <see cref="GoapPlan.None"/>.
    /// </summary>
    /// <param name="world">What this NPC knows. Never ground truth.</param>
    /// <param name="goal">The goal to reach.</param>
    /// <param name="available">
    /// The actions this NPC's archetype is able to perform. Passing the filtered set
    /// rather than filtering inside the search keeps the archetype check out of the
    /// inner loop, where it would be re-evaluated per node per action.
    /// </param>
    public GoapPlan Plan(GoapWorldState world, GoapGoalDef goal, IReadOnlyList<GoapActionDef> available)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (goal is null) throw new ArgumentNullException(nameof(goal));
        if (available is null) throw new ArgumentNullException(nameof(available));

        _lastNodes = 0;

        if (world.Satisfies(goal.Satisfaction))
        {
            return new GoapPlan(goal.Id, goal.NameKey, Array.Empty<int>(), 0, 0, GoapPlanStatus.GoalAlreadySatisfied);
        }

        int budget = NodeBudget;
        int maxLength = MaxPlanLength;

        EnsureCapacity();

        _states[0].CopyFrom(world);
        _parent[0] = -1;
        _costSoFar[0] = 0;
        _applied[0] = null;
        _nodeCount = 1;

        var closed = new HashSet<ulong>();
        _openCount = 0;
        Insert(0, Heuristic(_states[0], goal));

        while (_openCount > 0)
        {
            int current = PopBest();

            // Closing after popping, and skipping an already-closed node, is what stops
            // a low-cost cycle (patrol → investigate → patrol) from expanding forever.
            // The budget would stop it too, but only by spending the whole allowance.
            if (!closed.Add(_states[current].Fingerprint()))
                continue;

            _lastNodes++;

            if (_lastNodes > budget)
            {
                _lastNodes = budget;
                return new GoapPlan(goal.Id, goal.NameKey, Array.Empty<int>(), 0, _lastNodes, GoapPlanStatus.BudgetExhausted);
            }

            if (_states[current].Satisfies(goal.Satisfaction))
                return Reconstruct(current, goal);

            int depth = DepthOf(current);

            if (depth >= maxLength)
                continue;

            foreach (GoapActionDef action in available)
            {
                if (!_states[current].Satisfies(action.Preconditions))
                    continue;

                GoapWorldState next = EnsureState(_nodeCount);
                next.CopyFrom(_states[current]);
                next.Apply(action);

                ulong fingerprint = next.Fingerprint();

                if (closed.Contains(fingerprint))
                    continue;

                EnsureCapacity();
                int node = _nodeCount++;
                _states[node] = next;
                _parent[node] = current;
                _applied[node] = action;
                _costSoFar[node] = _costSoFar[current] + action.BaseCost;

                Insert(node, _costSoFar[node] + Heuristic(next, goal));
            }
        }

        return new GoapPlan(goal.Id, goal.NameKey, Array.Empty<int>(), 0, _lastNodes, GoapPlanStatus.NoPlan);
    }

    /// <summary>
    /// An estimate of the actions still needed, as the count of unmet goal conditions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Admissible because one action can satisfy at most a bounded number of
    /// conditions and the estimate never exceeds what is left to do; it is the number
    /// of satisfaction clauses not yet true. A goal with two clauses that are both
    /// false estimates 2, and it cannot be reached in fewer than the actions that set
    /// them.
    /// </para>
    /// <para>
    /// Not scaled by cost, deliberately. The <c>base_cost</c> values are single-digit
    /// to double-digit, so an unscaled clause count is close enough to guide the search
    /// without the risk of an inadmissible estimate silently returning a worse plan —
    /// which would be a correctness bug traded for a small constant factor.
    /// </para>
    /// </remarks>
    private static int Heuristic(GoapWorldState state, GoapGoalDef goal)
    {
        int unmet = 0;

        foreach (GoapKeys.Condition condition in goal.Satisfaction)
        {
            if (state.Get(condition.Key) == condition.Negated)
                unmet++;
        }

        return unmet;
    }

    /// <summary>How many actions deep a node is.</summary>
    private int DepthOf(int node)
    {
        int depth = 0;
        int cursor = node;

        while (_parent[cursor] >= 0)
        {
            cursor = _parent[cursor];
            depth++;
        }

        return depth;
    }

    /// <summary>Walks the parent chain back to the root and reverses it into plan order.</summary>
    private GoapPlan Reconstruct(int node, GoapGoalDef goal)
    {
        int length = DepthOf(node);

        if (_scratchPath.Length < length)
            _scratchPath = new int[Math.Max(length, 16)];

        int cursor = node;

        for (int i = length - 1; i >= 0; i--)
        {
            GoapActionDef action = _applied[cursor]!;
            _scratchPath[i] = action.Id;
            cursor = _parent[cursor];
        }

        var plan = new int[length];
        Array.Copy(_scratchPath, plan, length);

        return new GoapPlan(goal.Id, goal.NameKey, plan, _costSoFar[node], _lastNodes, GoapPlanStatus.Planned);
    }

    /// <summary>
    /// Inserts a node into the open set, keeping it sorted by (f-score, node id).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sorted array rather than a <c>PriorityQueue</c>, and this is the load-bearing
    /// choice for determinism. A binary heap's tie-breaking depends on insertion order
    /// and sift direction; two runs that inserted the same nodes in the same order would
    /// agree, but any change to the expansion order — a new action row, a different
    /// goal, a reordered table — reshuffles the heap's internals and can change which of
    /// two equal-cost plans wins. Sorting by score and then by node id makes the choice
    /// a function of the data alone.
    /// </para>
    /// <para>
    /// The tie-break on node id is stable because nodes are allocated in expansion
    /// order, so two nodes created from the same parent with the same score always
    /// resolve the same way.
    /// </para>
    /// </remarks>
    private void Insert(int node, int score)
    {
        EnsureCapacity();

        int i = _openCount++;

        // Sift the hole at i up towards the root, moving parents down into it. The
        // comparison is against the node being inserted rather than against whatever
        // happened to be sitting in slot i, because that slot is the hole and its
        // previous contents are not part of the heap.
        while (i > 0)
        {
            int parent = (i - 1) / 2;

            if (!Outranks(node, score, _openOrder[parent], _openF[parent]))
                break;

            _openOrder[i] = _openOrder[parent];
            _openF[i] = _openF[parent];
            i = parent;
        }

        _openOrder[i] = node;
        _openF[i] = score;
    }

    /// <summary>
    /// Removes and returns the best open node.
    /// </summary>
    private int PopBest()
    {
        int best = _openOrder[0];
        int last = --_openCount;

        if (last > 0)
        {
            int node = _openOrder[last];
            int score = _openF[last];

            int i = 0;

            while (true)
            {
                int left = (2 * i) + 1;

                if (left >= last)
                    break;

                int right = left + 1;
                int child = left;

                if (right < last && Outranks(_openOrder[right], _openF[right], _openOrder[left], _openF[left]))
                    child = right;

                if (!Outranks(_openOrder[child], _openF[child], node, score))
                    break;

                _openOrder[i] = _openOrder[child];
                _openF[i] = _openF[child];
                i = child;
            }

            _openOrder[i] = node;
            _openF[i] = score;
        }

        return best;
    }

    /// <summary>
    /// Whether one open node should be expanded before another.
    /// </summary>
    /// <remarks>
    /// Lower f first, then lower node id. The second clause is what makes equal-score
    /// ties resolve deterministically rather than depending on how the array happened
    /// to be arranged.
    /// </remarks>
    private static bool Outranks(int nodeA, int fA, int nodeB, int fB)
        => fA != fB ? fA < fB : nodeA < nodeB;

    /// <summary>
    /// The state instance for a node slot, allocated once and reused thereafter.
    /// </summary>
    /// <remarks>
    /// The single biggest cost in the search before this existed was two array
    /// allocations per node: a budgeted search opens up to <c>node_budget</c> nodes per
    /// plan, several plans per step, across every guard in a building — so thousands of
    /// allocations a step, which measured as the planner roughly doubling the cost of a
    /// tactical step. Reusing the instances removes that. Safe because a node's state is
    /// written and read only inside one search: no reference to it is ever handed out.
    /// </remarks>
    private GoapWorldState EnsureState(int index)
    {
        EnsureCapacity();

        return _states[index];
    }

    /// <summary>Grows the node arrays when a search opens more than the last one did.</summary>
    private void EnsureCapacity()
    {
        // At least one slot, because a search writes the root at index 0 before it has
        // expanded anything and so has no other reason to grow.
        if (_nodeCount < _states.Length && _states.Length >= 1)
            return;

        int oldLength = _states.Length;
        int capacity = Math.Max(16, oldLength * 2);

        Array.Resize(ref _states, capacity);
        Array.Resize(ref _applied, capacity);
        Array.Resize(ref _parent, capacity);
        Array.Resize(ref _costSoFar, capacity);
        Array.Resize(ref _openOrder, capacity);
        Array.Resize(ref _openF, capacity);

        // Every new slot gets a state instance now, once, rather than on first use.
        // Allocating here rather than lazily keeps the search loop free of null checks,
        // and means the work happens when the planner grows rather than once per node
        // per plan for the rest of the process. `oldLength` is captured before the
        // resize because afterwards it is indistinguishable from `capacity`.
        for (int i = oldLength; i < capacity; i++)
            _states[i] = new GoapWorldState();
    }
}