namespace ProjectSpy.Core;

/// <summary>
/// Stable identifier for a node on one mission map.
/// </summary>
/// <remarks>
/// Ids are per-mission and are handed out in generation order. They are what a
/// command names (<c>Advance(edge)</c>, <c>EnterNode(nodeId)</c>) and what the
/// after-action report refers to, so a replay and a player's notes mean the same
/// node. They are deliberately not world-global: two missions may both have node 7.
/// </remarks>
public readonly record struct MissionNodeId(int Value) : IComparable<MissionNodeId>
{
    public static readonly MissionNodeId None = new(0);

    public bool IsValid => Value > 0;

    public int CompareTo(MissionNodeId other) => Value.CompareTo(other.Value);

    public override string ToString() => $"N{Value:D3}";
}

/// <summary>
/// A node's role in the layered graph.
/// </summary>
/// <remarks>
/// <para>
/// The graph is Entry -> transit layers -> Objective -> Extraction. A
/// <see cref="DeadEnd"/> is a node in a transit layer that leads nowhere: it is a
/// place worth going that does not advance the mission, and it is the map's loot
/// incentive.
/// </para>
/// <para>
/// A dead end is the one kind of node with no outbound edge. Every other node must
/// have one, which is what makes "no orphan" a structural property rather than
/// something the generator has to remember to check.
/// </para>
/// </remarks>
public enum MissionNodeKind
{
    /// <summary>Where the team lands. Exactly one.</summary>
    Entry = 0,

    /// <summary>An intermediate layer node.</summary>
    Transit = 1,

    /// <summary>What the mission is for. Exactly one.</summary>
    Objective = 2,

    /// <summary>Where the team leaves. Exactly one.</summary>
    Extraction = 3,

    /// <summary>Reachable, but leads nowhere.</summary>
    DeadEnd = 4,
}

/// <summary>
/// One node on a mission map: its graph position, the room it was assigned, and the
/// content it may hold.
/// </summary>
/// <remarks>
/// <para>
/// The interior of a node is <em>not</em> here. It is generated lazily on first
/// observation and lives in a <see cref="RoomContents"/>, because a node the player
/// has never seen must not have contents that anything could read (see
/// <c>FogOfWar</c>). What lives here is the map skeleton the generator commits to:
/// which room the node is, how secure it is, and whether it holds loot.
/// </para>
/// <para>
/// <b>No coordinates.</b> A node's place in the world is its <see cref="Layer"/> and
/// the explicit edge set. Presentation decides how any of that is drawn
/// (knowledge.md rule 10).
/// </para>
/// </remarks>
public sealed class MissionNode
{
    /// <summary>Stable id, unique within the owning map.</summary>
    public MissionNodeId Id { get; init; }

    /// <summary>This node's role in the graph.</summary>
    public MissionNodeKind Kind { get; init; }

    /// <summary>
    /// Depth in the layered graph: 0 is Entry, increasing toward Extraction.
    /// </summary>
    /// <remarks>
    /// Edges only ever run from layer <c>n</c> to layer <c>n+1</c>, which is what
    /// makes the graph acyclic and every "does a route exist" question a dynamic
    /// program rather than a search.
    /// </remarks>
    public int Layer { get; init; }

    /// <summary>Foreign key into <c>node_room</c>.</summary>
    public int RoomTypeId { get; init; }

    /// <summary>Localization key for the node's name. Core never produces prose.</summary>
    public string NameKey { get; init; } = string.Empty;

    /// <summary>Tags from <c>node_room</c>, split on commas. Drives event selection.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Base security from <c>node_room</c>.</summary>
    public int BaseSecurity { get; init; }

    /// <summary>Noise the node makes from <c>node_room</c>. Feeds the alarm meter.</summary>
    public int NoiseModifier { get; init; }

    /// <summary>
    /// Candidate mission events from <c>node_room</c>, already filtered to this tier.
    /// </summary>
    /// <remarks>
    /// Which one fires is a runtime roll. The candidate list is part of the
    /// generator's committed skeleton rather than the node's hidden interior,
    /// because a node's <em>tags</em> are already observable from a silhouette.
    /// </remarks>
    public IReadOnlyList<int> CandidateEventIds { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Loot table this node exposes, or 0 when it holds none.
    /// </summary>
    /// <remarks>
    /// Zero is the absence, which is why this is an int rather than a nullable
    /// reference to a table object: Core stores ids and asks
    /// <c>SimulationRules</c> for the rows, so a retired table id degrades instead of
    /// leaving a dangling object in a save.
    /// </remarks>
    public int LootTableId { get; private set; }

    /// <summary>True when the node's tags include <c>security</c>.</summary>
    public bool IsSecurity => Tags.Contains(TagSecurity);

    /// <summary>True when the node was assigned a loot table.</summary>
    public bool HasLoot => LootTableId > 0;

    /// <summary>True when the node leads nowhere.</summary>
    public bool IsDeadEnd => Kind == MissionNodeKind.DeadEnd;

    /// <summary>The <c>security</c> tag, as it appears in <c>node_room</c>.</summary>
    public const string TagSecurity = "security";

    /// <summary>
    /// Sets this node's loot table. Generator-time only.
    /// </summary>
    /// <remarks>
    /// Internal rather than a public setter: which nodes hold loot is part of the
    /// generated skeleton, so no rule or command should be able to invent loot on a
    /// node the map generator did not put it in.
    /// </remarks>
    internal void AssignLoot(int lootTableId) => LootTableId = lootTableId;

    /// <summary>True when this node carries every tag in <paramref name="required"/>.</summary>
    public bool HasAllTags(IEnumerable<string> required)
    {
        foreach (string tag in required)
        {
            if (!Tags.Contains(tag))
                return false;
        }

        return true;
    }
}

/// <summary>
/// One directed connection between adjacent layers.
/// </summary>
/// <remarks>
/// Edges are always one-way and always span exactly one layer, so the map is a DAG
/// and "how many routes are there" is a counting problem rather than an enumeration.
/// <see cref="IsClosed"/> exists for the alarm bands: a rising alarm closes edges,
/// which is what can strand a sub-team on the wrong side of the map.
/// </remarks>
public sealed class MissionEdge
{
    /// <summary>The node the edge leaves.</summary>
    public MissionNodeId From { get; init; }

    /// <summary>The node the edge enters.</summary>
    public MissionNodeId To { get; init; }

    /// <summary>
    /// True when the alarm meter has closed this edge.
    /// </summary>
    /// <remarks>
    /// Mutable mission state, so this is a settable flag rather than part of the
    /// generated skeleton. It is reset on regeneration because a fresh
    /// <see cref="MissionMap"/> starts with an alarm of zero.
    /// </remarks>
    public bool IsClosed { get; set; }
}

/// <summary>
/// A generated mission map: a layered graph plus the guarantees the generator made
/// about it.
/// </summary>
/// <remarks>
/// <para>
/// The map is pure structure and committed data — ids, layers, tags, and the edge
/// set. It contains no per-mission progress: the team's position, the alarm meter
/// and each node's interior live on the mission's runtime state (stage 4's
/// <c>MissionRunState</c>), so a map can be regenerated from
/// <see cref="MapSeed"/> alone and compared against a loaded save.
/// </para>
/// <para>
/// Every query here is a pure function of the graph, which is what lets the
/// generator assert its own guarantees and the invariant tests re-assert them on
/// ten thousand generated maps without re-running generation logic.
/// </para>
/// </remarks>
public sealed class MissionMap
{
    /// <summary>Mission this map was generated for.</summary>
    public int MissionId { get; init; }

    /// <summary>Tier the map was generated at, from <c>mission_type</c>.</summary>
    public int Tier { get; init; }

    /// <summary>
    /// The seed every deterministic decision about this map was drawn from.
    /// </summary>
    /// <remarks>
    /// Regenerating with this seed reproduces the map exactly, which is what makes
    /// a save able to reload a mission it was in the middle of (knowledge.md rule 6).
    /// </remarks>
    public ulong MapSeed { get; init; }

    /// <summary>Every node, in generation order.</summary>
    public List<MissionNode> Nodes { get; } = new();

    /// <summary>Every edge, in generation order.</summary>
    public List<MissionEdge> Edges { get; } = new();

    /// <summary>Where the team lands.</summary>
    public MissionNodeId EntryNodeId { get; internal set; }

    /// <summary>What the mission is for.</summary>
    public MissionNodeId ObjectiveNodeId { get; internal set; }

    /// <summary>Where the team leaves.</summary>
    public MissionNodeId ExtractionNodeId { get; internal set; }

    /// <summary>
    /// Number of transit layers between Entry and Objective.
    /// </summary>
    /// <remarks>
    /// The layer count the map actually built, which can differ from the rule's
    /// value only if the rule row was missing and a fallback was used.
    /// </remarks>
    public int TransitLayerCount { get; init; }

    /// <summary>Highest layer index in use — the Extraction layer.</summary>
    public int DeepestLayer => TransitLayerCount + 2;

    private Dictionary<MissionNodeId, MissionNode>? _nodesById;
    private Dictionary<MissionNodeId, List<MissionNodeId>>? _outbound;
    private Dictionary<MissionNodeId, List<MissionNodeId>>? _inbound;
    private Dictionary<int, List<MissionNode>>? _byLayer;

    /// <summary>Looks up a node, or null.</summary>
    public MissionNode? Find(MissionNodeId id)
    {
        _nodesById ??= BuildNodeIndex();
        return _nodesById.TryGetValue(id, out MissionNode? node) ? node : null;
    }

    /// <summary>
    /// Nodes in a layer, in generation order.
    /// </summary>
    /// <remarks>
    /// Includes dead ends, because a dead end belongs to the layer it hangs off —
    /// Presentation draws it there, and fog propagation treats it as a neighbour of
    /// its parent.
    /// </remarks>
    public IReadOnlyList<MissionNode> NodesInLayer(int layer)
    {
        _byLayer ??= BuildLayerIndex();
        return _byLayer.TryGetValue(layer, out List<MissionNode>? nodes)
            ? nodes
            : Array.Empty<MissionNode>();
    }

    /// <summary>Ids this node has an edge to, in generation order.</summary>
    public IReadOnlyList<MissionNodeId> OutboundFrom(MissionNodeId id)
    {
        EnsureEdgeIndex();
        return _outbound!.TryGetValue(id, out List<MissionNodeId>? to) ? to : Array.Empty<MissionNodeId>();
    }

    /// <summary>Ids with an edge into this node, in generation order.</summary>
    public IReadOnlyList<MissionNodeId> InboundTo(MissionNodeId id)
    {
        EnsureEdgeIndex();
        return _inbound!.TryGetValue(id, out List<MissionNodeId>? from) ? from : Array.Empty<MissionNodeId>();
    }

    /// <summary>True when an edge connects exactly these two nodes.</summary>
    public bool HasEdge(MissionNodeId from, MissionNodeId to)
    {
        foreach (MissionNodeId candidate in OutboundFrom(from))
        {
            if (candidate == to)
                return true;
        }

        return false;
    }

    /// <summary>Number of dead-end nodes on the map.</summary>
    public int DeadEndCount
    {
        get
        {
            int count = 0;
            foreach (MissionNode node in Nodes)
            {
                if (node.IsDeadEnd)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Counts distinct Entry-to-Extraction routes, saturating at
    /// <paramref name="cap"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A layered DAG makes this a dynamic program: the number of routes reaching a
    /// node is the sum over its inbound neighbours. That is deliberately not an
    /// enumeration — a tier-3 map can have more Entry-to-Extraction routes than fit
    /// in a long, and the only question ever asked of this number is "are there at
    /// least as many as the rule promised".
    /// </para>
    /// <para>
    /// Dead ends contribute nothing: they have no outbound edges, so no route passes
    /// through them.
    /// </para>
    /// </remarks>
    public long CountRoutesToExtraction(long cap)
    {
        if (cap <= 0) return 0;

        // ways[n] = number of Entry->n routes, saturating at cap so a map with an
        // absurd number of routes cannot overflow the accumulator.
        var ways = new Dictionary<MissionNodeId, long>();

        foreach (MissionNode node in Nodes.OrderBy(n => n.Layer))
            ways[node.Id] = node.Id == EntryNodeId ? 1L : 0L;

        foreach (MissionNode node in Nodes.OrderBy(n => n.Layer))
        {
            long here = ways[node.Id];
            if (here <= 0)
                continue;

            foreach (MissionNodeId next in OutboundFrom(node.Id))
            {
                long updated = ways[next] + here;
                ways[next] = updated >= cap ? cap : updated;
            }
        }

        return ways.TryGetValue(ExtractionNodeId, out long total) ? total : 0L;
    }

    /// <summary>
    /// True when no route from Entry to the Objective avoids every security node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the GDD's "at least one Security node on every route to the
    /// Objective", expressed the only way it can be <em>checked</em>: strip the
    /// security nodes out of the graph and confirm Entry can no longer reach the
    /// Objective. Checking it per-node instead ("is there a security node
    /// somewhere?") would pass on a map whose only guard room sits behind a wall
    /// nobody walks.
    /// </para>
    /// <para>
    /// The generator enforces this structurally, by making the layer immediately
    /// before the Objective a security cut — so this query is expected to be true
    /// and is asserted rather than repaired.
    /// </para>
    /// </remarks>
    public bool EveryRouteToObjectiveCrossesSecurity
    {
        get
        {
            foreach (MissionNode node in Nodes)
            {
                if (node.IsSecurity || node.Id == ObjectiveNodeId)
                    continue;

                if (ReachesAvoidingSecurity(node.Id))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="startId"/> reaches the Objective without passing
    /// through a security node.
    /// </summary>
    private bool ReachesAvoidingSecurity(MissionNodeId startId)
    {
        var seen = new HashSet<MissionNodeId>();
        var frontier = new Stack<MissionNodeId>();
        frontier.Push(startId);

        while (frontier.Count > 0)
        {
            MissionNodeId current = frontier.Pop();
            if (!seen.Add(current))
                continue;

            foreach (MissionNodeId next in OutboundFrom(current))
            {
                if (next == ObjectiveNodeId)
                    return true;

                MissionNode? nextNode = Find(next);
                if (nextNode is not null && nextNode.IsSecurity)
                    continue;

                frontier.Push(next);
            }
        }

        return false;
    }

    /// <summary>
    /// Nodes that cannot be reached from Entry — the orphans the generator promises
    /// not to produce.
    /// </summary>
    public IReadOnlyList<MissionNodeId> NodesUnreachableFromEntry()
    {
        var seen = new HashSet<MissionNodeId>();
        var frontier = new Stack<MissionNodeId>();
        frontier.Push(EntryNodeId);

        while (frontier.Count > 0)
        {
            MissionNodeId current = frontier.Pop();
            if (!seen.Add(current))
                continue;

            foreach (MissionNodeId next in OutboundFrom(current))
                frontier.Push(next);
        }

        var orphans = new List<MissionNodeId>();
        foreach (MissionNode node in Nodes)
        {
            if (!seen.Contains(node.Id))
                orphans.Add(node.Id);
        }

        return orphans;
    }

    /// <summary>
    /// Non-dead-end nodes that cannot reach Extraction.
    /// </summary>
    /// <remarks>
    /// Dead ends are excluded because reaching Extraction is not something they
    /// offer; a dead end that <em>could</em> reach Extraction would not be one.
    /// </remarks>
    public IReadOnlyList<MissionNodeId> NodesStrandedFromExtraction()
    {
        var stranded = new List<MissionNodeId>();

        foreach (MissionNode node in Nodes)
        {
            if (node.IsDeadEnd || node.Id == ExtractionNodeId)
                continue;

            if (!ReachesExtraction(node.Id))
                stranded.Add(node.Id);
        }

        return stranded;
    }

    private bool ReachesExtraction(MissionNodeId startId)
    {
        var seen = new HashSet<MissionNodeId>();
        var frontier = new Stack<MissionNodeId>();
        frontier.Push(startId);

        while (frontier.Count > 0)
        {
            MissionNodeId current = frontier.Pop();
            if (current == ExtractionNodeId)
                return true;

            if (!seen.Add(current))
                continue;

            foreach (MissionNodeId next in OutboundFrom(current))
                frontier.Push(next);
        }

        return false;
    }

    // ---- integrity -----------------------------------------------------------

    /// <summary>
    /// Checks the structural invariants that must hold on any generated map.
    /// Returns false and names the first problem when the graph is malformed.
    /// </summary>
    /// <remarks>
    /// Deliberately exhaustive rather than cheap: this is what the ten-thousand-map
    /// test runs on every generated map, and a generator bug that produced an
    /// unreachable objective has to be caught here rather than by a player who
    /// softlocks their mission.
    /// </remarks>
    public bool Validate(out string problem)
    {
        problem = string.Empty;

        if (Nodes.Count == 0)
        {
            problem = "MapHasNoNodes";
            return false;
        }

        if (Find(EntryNodeId) is null)
        {
            problem = $"MapHasNoEntry:{EntryNodeId}";
            return false;
        }

        if (Find(ObjectiveNodeId) is null)
        {
            problem = $"MapHasNoObjective:{ObjectiveNodeId}";
            return false;
        }

        if (Find(ExtractionNodeId) is null)
        {
            problem = $"MapHasNoExtraction:{ExtractionNodeId}";
            return false;
        }

        int entries = 0, objectives = 0, extractions = 0;
        var seenIds = new HashSet<int>();
        var seenPairs = new HashSet<(int, int)>();

        foreach (MissionNode node in Nodes)
        {
            if (!node.Id.IsValid)
            {
                problem = $"NodeIdInvalid:{node.Kind}";
                return false;
            }

            if (!seenIds.Add(node.Id.Value))
            {
                problem = $"DuplicateNodeId:{node.Id}";
                return false;
            }

            switch (node.Kind)
            {
                case MissionNodeKind.Entry: entries++; break;
                case MissionNodeKind.Objective: objectives++; break;
                case MissionNodeKind.Extraction: extractions++; break;
            }

            if (node.Layer < 0 || node.Layer > DeepestLayer)
            {
                problem = $"NodeLayerOutOfRange:{node.Id}:{node.Layer}";
                return false;
            }

            // Two kinds of node legitimately lead nowhere: a dead end, and the
            // extraction point itself. Anything else without an outbound edge is
            // stranded mid-map and the mission becomes unclearable.
            bool leadsNowhere = node.IsDeadEnd || node.Id == ExtractionNodeId;
            if (!leadsNowhere && OutboundFrom(node.Id).Count == 0)
            {
                problem = $"NodeHasNoOutboundEdge:{node.Id}";
                return false;
            }
        }

        if (entries != 1)
        {
            problem = $"EntryCount:{entries}";
            return false;
        }

        if (objectives != 1)
        {
            problem = $"ObjectiveCount:{objectives}";
            return false;
        }

        if (extractions != 1)
        {
            problem = $"ExtractionCount:{extractions}";
            return false;
        }

        foreach (MissionEdge edge in Edges)
        {
            MissionNode? from = Find(edge.From);
            MissionNode? to = Find(edge.To);

            if (from is null || to is null)
            {
                problem = $"EdgeReferencesMissingNode:{edge.From}->{edge.To}";
                return false;
            }

            // One layer at a time is what keeps the graph acyclic. An edge that
            // skipped a layer would also let a route dodge the security cut.
            if (to.Layer != from.Layer + 1)
            {
                problem = $"EdgeSkipsLayer:{edge.From}->{edge.To}:{from.Layer}->{to.Layer}";
                return false;
            }

            if (!seenPairs.Add((edge.From.Value, edge.To.Value)))
            {
                problem = $"DuplicateEdge:{edge.From}->{edge.To}";
                return false;
            }
        }

        IReadOnlyList<MissionNodeId> orphans = NodesUnreachableFromEntry();
        if (orphans.Count > 0)
        {
            problem = $"NodeOrphaned:{orphans[0]}";
            return false;
        }

        IReadOnlyList<MissionNodeId> stranded = NodesStrandedFromExtraction();
        if (stranded.Count > 0)
        {
            problem = $"NodeStrandedFromExtraction:{stranded[0]}";
            return false;
        }

        if (CountRoutesToExtraction(1) < 1)
        {
            problem = "NoRouteToExtraction";
            return false;
        }

        if (!EveryRouteToObjectiveCrossesSecurity)
        {
            problem = "RouteToObjectiveAvoidsSecurity";
            return false;
        }

        return true;
    }

    // ---- index construction --------------------------------------------------

    private Dictionary<MissionNodeId, MissionNode> BuildNodeIndex()
    {
        var index = new Dictionary<MissionNodeId, MissionNode>();
        foreach (MissionNode node in Nodes)
            index[node.Id] = node;
        return index;
    }

    private Dictionary<int, List<MissionNode>> BuildLayerIndex()
    {
        var index = new Dictionary<int, List<MissionNode>>();
        foreach (MissionNode node in Nodes)
        {
            if (!index.TryGetValue(node.Layer, out List<MissionNode>? layer))
            {
                layer = new List<MissionNode>();
                index[node.Layer] = layer;
            }

            layer.Add(node);
        }

        return index;
    }

    private void EnsureEdgeIndex()
    {
        if (_outbound is not null && _inbound is not null)
            return;

        var outbound = new Dictionary<MissionNodeId, List<MissionNodeId>>();
        var inbound = new Dictionary<MissionNodeId, List<MissionNodeId>>();

        foreach (MissionEdge edge in Edges)
        {
            if (!outbound.TryGetValue(edge.From, out List<MissionNodeId>? to))
            {
                to = new List<MissionNodeId>();
                outbound[edge.From] = to;
            }

            to.Add(edge.To);

            if (!inbound.TryGetValue(edge.To, out List<MissionNodeId>? from))
            {
                from = new List<MissionNodeId>();
                inbound[edge.To] = from;
            }

            from.Add(edge.From);
        }

        _outbound = outbound;
        _inbound = inbound;
    }

    private void InvalidateIndexes()
    {
        _nodesById = null;
        _outbound = null;
        _inbound = null;
        _byLayer = null;
    }

    /// <summary>Adds an edge. Used by the generator while building the graph.</summary>
    internal void AddEdge(MissionNodeId from, MissionNodeId to)
    {
        Edges.Add(new MissionEdge { From = from, To = to });
        InvalidateIndexes();
    }

    /// <summary>Adds a node. Used by the generator while building the graph.</summary>
    internal void AddNode(MissionNode node)
    {
        Nodes.Add(node);
        InvalidateIndexes();
    }
}
