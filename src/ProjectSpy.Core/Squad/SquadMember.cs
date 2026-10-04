namespace ProjectSpy.Core.Squad;

/// <summary>
/// One agent's place in a dispatched squad: who they are, what job they were given, and
/// what they are carrying.
/// </summary>
/// <remarks>
/// <para>
/// A class rather than a value because the assignment mutates as the mission runs — an
/// agent who was carrying a gadget drops it when they use it, and the Handler's role is
/// what the command post checks to decide whether support abilities are available. A
/// snapshot taken at dispatch and never updated would make the post look staffed by
/// whoever was in the van at the start of the mission.
/// </para>
/// <para>
/// <b>Gadgets are instances, not ids.</b> Two agents cannot both be carrying gadget 8501
/// even though the table has one row for it, so the assignment owns the id and hands out
/// a per-agent use budget. See <see cref="Gadgets"/>.
/// </para>
/// </remarks>
public sealed class SquadMember
{
    /// <summary>The operative.</summary>
    public AgentId AgentId { get; init; }

    /// <summary>
    /// The <c>agent_role</c> id this agent was assigned at dispatch.
    /// </summary>
    /// <remarks>
    /// The id, not the resolved <see cref="SquadRole"/>, because this is saved state: a
    /// role row that is retired in a later patch must not change what a loaded mission
    /// means. <see cref="Role"/> resolves it on demand and tolerates the id being gone.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>What this member carries, and how many uses of each are left.</summary>
    public Gadgets Gadgets { get; } = new();

    /// <summary>The resolved role, or null when the id no longer exists.</summary>
    public SquadRole? Role => SquadRole.For(RoleId);

    /// <summary>The behaviour this member runs with no order. None when the role is gone.</summary>
    public RoleBehaviour Behaviour => Role?.Behaviour ?? RoleBehaviour.None;

    /// <summary>True when this member's role only works at the command post.</summary>
    public bool RequiresCommandPost => Role?.RequiresCommandPost ?? false;

    /// <inheritdoc/>
    public override string ToString() => $"{AgentId} role {RoleId} ({Behaviour})";
}

/// <summary>
/// What one squad member is carrying.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small: an id, a number of uses left, and a weight. Not an inventory, not
/// a slot list, and no notion of value — because at this scale the only question the
/// simulation asks about a gadget is "does this agent have one, and how much does it
/// weigh", and a richer model would be a richer model with no rules attached.
/// </para>
/// <para>
/// <b>Weight is the dispatch constraint.</b> The brief requires total carry weight to be
/// validated before a team is sent, so the weight has to live with the gadget rather than
/// be recomputed from a table at validation time: the same gadget id can be assigned to
/// two agents, and the <em>pair</em> is what is heavy.
/// </para>
/// </remarks>
public sealed class Gadgets
{
    private readonly Dictionary<int, int> _usesByGadget = new();

    /// <summary>Gadget ids this agent has, with uses remaining, in ascending id order.</summary>
    public IReadOnlyList<(int GadgetId, int Uses)> Entries
    {
        get
        {
            var entries = new List<(int, int)>(_usesByGadget.Count);

            foreach (KeyValuePair<int, int> pair in _usesByGadget)
                entries.Add((pair.Key, pair.Value));

            entries.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
            return entries;
        }
    }

    /// <summary>How many distinct gadgets are assigned.</summary>
    public int Count => _usesByGadget.Count;

    /// <summary>
    /// Total carry weight in the table's abstract units.
    /// </summary>
    /// <remarks>
    /// Gadget weight comes from <c>item.weight</c> where the gadget names an item and
    /// from <c>gadget.craft_cost / CraftCostDivisor</c> otherwise — the only honest
    /// answer available without inventing a weight column the design does not have.
    /// <c>TableValidator</c> checks the divisor produces a positive weight for every
    /// gadget, so this cannot silently become zero and make weight irrelevant.
    /// </remarks>
    public int TotalWeight
    {
        get
        {
            int total = 0;

            foreach (KeyValuePair<int, int> pair in _usesByGadget)
                total += SimulationRules.PercentOf(WeightOf(pair.Key), pair.Value);

            return total;
        }
    }

    /// <summary>Gives an agent a gadget with its full use count from the table.</summary>
    /// <returns>False when the gadget id names no <c>gadget</c> row.</returns>
    public bool Assign(int gadgetId)
    {
        ProjectSpy.Tables.Gadget? row = SimulationRules.GadgetFor(gadgetId);
        if (row is null)
            return false;

        _usesByGadget[gadgetId] = row.Uses;
        return true;
    }

    /// <summary>True when this agent holds at least one of a gadget.</summary>
    public bool Has(int gadgetId) => _usesByGadget.ContainsKey(gadgetId);

    /// <summary>Uses of a gadget remaining. Zero when it is not held.</summary>
    public int UsesOf(int gadgetId)
        => _usesByGadget.TryGetValue(gadgetId, out int uses) ? uses : 0;

    /// <summary>
    /// Spends one use, dropping the gadget at zero.
    /// </summary>
    /// <returns>False when the agent did not hold it or had none left.</returns>
    public bool Consume(int gadgetId)
    {
        if (!_usesByGadget.TryGetValue(gadgetId, out int uses) || uses <= 0)
            return false;

        if (uses - 1 <= 0)
            _usesByGadget.Remove(gadgetId);
        else
            _usesByGadget[gadgetId] = uses - 1;

        return true;
    }

    /// <summary>Drops a gadget entirely.</summary>
    public void Remove(int gadgetId) => _usesByGadget.Remove(gadgetId);

    /// <summary>The carry weight of one use of a gadget.</summary>
    /// <remarks>
    /// The <c>gadget.weight</c> column, added in stage 4e. It was tried first from
    /// <c>item.weight</c>, but <c>item</c> has no weight column and <c>gadget</c> has no
    /// item reference — inventing the join would have meant Core guessing which of two
    /// plausible relationships was meant, which is exactly the hand-typed balance the
    /// project rules forbid. A designer now edits a number in a CSV.
    /// </remarks>
    public static int WeightOf(int gadgetId)
    {
        ProjectSpy.Tables.Gadget? gadget = SimulationRules.GadgetFor(gadgetId);
        if (gadget is null)
            return 0;

        return gadget.Weight;
    }

    /// <summary>
    /// Every gadget id in the table, ascending.
    /// </summary>
    /// <remarks>
    /// Exposed so the dispatch screen can list what exists without reaching into the
    /// table from the UI assembly.
    /// </remarks>
    public static IReadOnlyList<int> KnownIds()
    {
        IReadOnlyList<ProjectSpy.Tables.Gadget> all = SimulationRules.AllGadgets();
        var ids = new List<int>(all.Count);

        foreach (ProjectSpy.Tables.Gadget row in all)
            ids.Add(row.Id);

        ids.Sort();
        return ids;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"[{string.Join(", ", Entries.Select(e => $"{e.GadgetId}x{e.Uses}"))}] weight {TotalWeight}";
}