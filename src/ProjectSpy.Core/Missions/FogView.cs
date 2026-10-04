namespace ProjectSpy.Core;

/// <summary>
/// One node as a <see cref="MissionVisibility.Scouted"/> player may read it: the room
/// it is, how secure it is, and how heavily it is guarded.
/// </summary>
/// <remarks>
/// A separate type from <see cref="NodeSight"/> rather than extra fields on it,
/// because "there is a terminal in here" and "there are two guards in here" are
/// different grades of knowledge and collapsing them into one struct with mostly-empty
/// members is how a leak gets shipped.
/// </remarks>
public readonly record struct NodeIntel(
    MissionNodeId NodeId,
    string NameKey,
    int BaseSecurity,
    GuardBand Guards)
{
    /// <summary>What a player knows about a room they have scouted but not entered.</summary>
    public static NodeIntel Unknown(MissionNodeId id)
        => new(id, string.Empty, 0, GuardBand.None);
}

/// <summary>
/// What the player is allowed to know about one node at its current visibility.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately built rather than returned. A read model that were a bare property on
/// the live node would leak the moment anyone added a field to
/// <see cref="MissionNode"/> without auditing the four states; constructing a fresh
/// value per state means the only way to widen what is visible is to edit
/// <see cref="FogOfWar.BuildSight"/>, which is one function with four branches and is
/// covered by a leak test.
/// </para>
/// <para>
/// This type holds no interior and offers no way to reach one. Contents come from
/// <see cref="FogOfWar.TryGetContents"/>, which exists only for nodes that have
/// actually been entered.
/// </para>
/// </remarks>
public readonly record struct FogNodeView(
    MissionNodeId NodeId,
    MissionVisibility State,
    int Layer,
    IReadOnlyList<MissionNodeId> EdgesTo,

    // Populated only at Scouted and above.
    string NameKey,
    IReadOnlyList<string> Tags,
    bool HasLoot,
    int BaseSecurity,
    GuardBand Guards)
{
    /// <summary>
    /// A node the player knows exists but nothing else. Carries its layer, which a
    /// site plan is entitled to show, and no more.
    /// </summary>
    public static FogNodeView Unknown(MissionNodeId id, int layer)
        => new(
            id,
            MissionVisibility.Hidden,
            layer,
            Array.Empty<MissionNodeId>(),
            string.Empty,
            Array.Empty<string>(),
            false,
            0,
            GuardBand.None);

    /// <summary>
    /// True when this node has been entered, so its interior exists.
    /// </summary>
    /// <remarks>
    /// This says the interior exists; it does not hand it over. Reading it still goes
    /// through <see cref="FogOfWar.TryGetContents"/>.
    /// </remarks>
    public bool HasInterior => State == MissionVisibility.Revealed;

    /// <summary>True when the room has been scouted closely enough to plan an approach.</summary>
    public bool HasIntel => State >= MissionVisibility.Scouted;
}

/// <summary>
/// Everything the player may know about a whole mission map at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only thing Presentation may read about a mission map.</b> It is a
/// snapshot: it has no reference back to <see cref="FogOfWar"/>, no
/// <c>Map</c> property and no way to reach a <see cref="MissionNode"/>, so a UI holding
/// one cannot observe or mutate a node it has not been shown.
/// </para>
/// <para>
/// Edges are included because knowing which way you can go is what makes a map a map.
/// They carry no node data — the far end of an edge is an id, and resolving it yields
/// that node's own <see cref="FogNodeView"/> at whatever grade it is entitled to.
/// </para>
/// <para>
/// Built by <see cref="FogOfWar.View"/>. Every snapshot allocates, so it is a
/// per-refresh value rather than a cached one: a stale cached view is a fog-of-war
/// leak that outlives the state it described.
/// </para>
/// </remarks>
public sealed class FogView
{
    private readonly Dictionary<MissionNodeId, FogNodeView> _byId;

    /// <summary>
    /// Builds a view over a set of nodes, in the order supplied.
    /// </summary>
    /// <remarks>
    /// The index is private and there is no accessor by id that can return anything
    /// other than a <see cref="FogNodeView"/>. That is deliberate: the point of this
    /// type is that there is no way to get from it to a node.
    /// </remarks>
    internal FogView(IEnumerable<FogNodeView> nodes, MissionNodeId entryNodeId)
    {
        Nodes = nodes.ToArray();
        EntryNodeId = entryNodeId;

        _byId = new Dictionary<MissionNodeId, FogNodeView>(Nodes.Count);
        foreach (FogNodeView node in Nodes)
            _byId[node.NodeId] = node;
    }

    /// <summary>Every node on the map, at whatever grade each is entitled to.</summary>
    public IReadOnlyList<FogNodeView> Nodes { get; }

    /// <summary>Where the team landed.</summary>
    public MissionNodeId EntryNodeId { get; }

    /// <summary>How many nodes have been entered, and so have interiors.</summary>
    public int RevealedCount
    {
        get
        {
            int count = 0;
            foreach (FogNodeView node in Nodes)
            {
                if (node.HasInterior)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// One node's view, or <see cref="FogNodeView.Unknown"/> when the id is not on
    /// this map.
    /// </summary>
    public FogNodeView Node(MissionNodeId id)
        => _byId.TryGetValue(id, out FogNodeView node) ? node : FogNodeView.Unknown(id, 0);

    /// <summary>Every node the player has at least seen the existence of.</summary>
    public IEnumerable<FogNodeView> Visible
    {
        get
        {
            foreach (FogNodeView node in Nodes)
            {
                if (node.State != MissionVisibility.Hidden)
                    yield return node;
            }
        }
    }
}