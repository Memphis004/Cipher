namespace ProjectSpy.Core;

/// <summary>
/// How much the player knows about a mission node.
/// </summary>
/// <remarks>
/// <para>
/// Ordered, and only ever moved upward. The ordering is the rule: a node that has
/// been <see cref="Revealed"/> is never knocked back down to
/// <see cref="Silhouette"/> when the team walks away, because knowledge does not
/// un-happen.
/// </para>
/// <para>
/// <b>Hidden</b> is the important one. It does not mean "drawn dark" — it means the
/// node's interior has never been generated, because
/// <see cref="FogOfWar.TryGetContents"/> refuses to produce it. See the class
/// remarks on <see cref="FogOfWar"/> for why that is enforced rather than merely
/// intended.
/// </para>
/// </remarks>
public enum MissionVisibility
{
    /// <summary>Nothing is known. The interior does not exist yet.</summary>
    Hidden = 0,

    /// <summary>The node is known to be there; what is in it is not.</summary>
    Silhouette = 1,

    /// <summary>Broadly scouted: the player knows the kind of place it is.</summary>
    Scouted = 2,

    /// <summary>Observed. The interior has been generated and can be read.</summary>
    Revealed = 3,
}

/// <summary>
/// Everything the player is allowed to know about one node at its current visibility.
/// </summary>
/// <remarks>
/// <para>
/// Presentation renders from this, not from <see cref="MissionNode"/>. That is the
/// whole point: a view that carries the same fields for a
/// <see cref="MissionVisibility.Hidden"/> node as for a revealed one would be a leak
/// wearing a disguise, whereas a view that blanks them cannot be.
/// </para>
/// <para>
/// <see cref="Layer"/> is always present even when Hidden. The player can see that
/// there are rooms to go through without knowing what is in them — that is exactly
/// what a site plan is, and hiding it would leave the map unplayable.
/// </para>
/// </remarks>
public readonly record struct NodeSight(
    MissionNodeId NodeId,
    MissionVisibility State,
    int Layer,
    string NameKey,
    IReadOnlyList<string> Tags,
    bool HasLoot)
{
    /// <summary>A view of a node that has never been seen.</summary>
    public static NodeSight Unknown(MissionNodeId id, int layer)
        => new(id, MissionVisibility.Hidden, layer, string.Empty, Array.Empty<string>(), false);

    /// <summary>True when the node's interior may exist and be read.</summary>
    public bool HasInterior => State == MissionVisibility.Revealed;
}

/// <summary>
/// Tracks what the player knows about a mission map, and owns the lazy generation of
/// node interiors.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hidden-content guarantee.</b> An unobserved node's
/// <see cref="RoomContents"/> is not computed and is not merely kept private: the
/// object does not exist. It is generated the first time the node is observed, from
/// a private generator seeded by <c>MapSeed + nodeId</c>, so it is identical every
/// time regardless of when or in what order it was generated. Generating it up front
/// and hiding it behind an access check was the alternative; lazy generation was
/// chosen because a value that exists in memory can leak through a debugger, a
/// serializer, an exception dump or a future API nobody audited, and the guarantee is
/// supposed to hold without every future reader remembering it.
/// </para>
/// <para>
/// Visibility only moves upward, and every reveal is expressed as a function of the
/// team's infiltration and the <c>fog_rule</c> table, so what the player sees is a
/// rule rather than a UI decision.
/// </para>
/// </remarks>
public sealed class FogOfWar
{
    private readonly MissionMap _map;
    private readonly Dictionary<MissionNodeId, MissionVisibility> _states = new();

    /// <summary>
    /// Interiors that have actually been generated, keyed by node.
    /// </summary>
    /// <remarks>
    /// A node absent from here has no contents anywhere in the program — not in a
    /// cache, not on the mission, not in a pending draw. This is the field the leak
    /// test checks.
    /// </remarks>
    private readonly Dictionary<MissionNodeId, RoomContents> _contents = new();

    /// <summary>Starts every node <see cref="MissionVisibility.Hidden"/>.</summary>
    public FogOfWar(MissionMap map)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));

        foreach (MissionNode node in map.Nodes)
            _states[node.Id] = MissionVisibility.Hidden;

        // The team is standing in the entry node at mission start, so the entry node
        // is observed from the first tick rather than waiting to be walked into.
        _states[map.EntryNodeId] = MissionVisibility.Revealed;
        _contents[map.EntryNodeId] = NodeInteriorGenerator.Generate(
            _map.Find(map.EntryNodeId)!, _map.MapSeed);
    }

    /// <summary>
    /// The map this fog covers.
    /// </summary>
    /// <remarks>
    /// Exposed because the mission runner resolves events and moves teams against
    /// the graph. Presentation should render from <see cref="Sight"/> instead: the map
    /// carries committed data for nodes the player has not seen.
    /// </remarks>
    public MissionMap Map => _map;

    /// <summary>How many nodes have been observed, and therefore have interiors.</summary>
    public int ObservedNodeCount => _contents.Count;

    /// <summary>The current visibility of one node.</summary>
    public MissionVisibility StateOf(MissionNodeId id)
        => _states.TryGetValue(id, out MissionVisibility state) ? state : MissionVisibility.Hidden;

    /// <summary>
    /// What the player may know about one node at its current visibility.
    /// </summary>
    public NodeSight Sight(MissionNodeId id)
    {
        MissionVisibility state = StateOf(id);
        MissionNode? node = _map.Find(id);

        if (node is null)
            return NodeSight.Unknown(id, 0);

        if (state == MissionVisibility.Hidden)
        {
            // Only the layer survives. A hidden node's name, tags and loot presence
            // are exactly what the player is not supposed to know yet.
            return NodeSight.Unknown(id, node.Layer);
        }

        bool showDetails = state >= MissionVisibility.Scouted;

        return new NodeSight(
            id,
            state,
            node.Layer,
            showDetails ? node.NameKey : string.Empty,
            showDetails ? node.Tags : Array.Empty<string>(),
            showDetails && node.HasLoot);
    }

    /// <summary>
    /// The node's interior, or false when it has never been generated.
    /// </summary>
    /// <remarks>
    /// <b>This never generates.</b> It is the only read path to contents, and it
    /// refuses for anything below <see cref="MissionVisibility.Revealed"/>. Asking
    /// twice for the same observed node returns the same instance, so mutations a
    /// command made are still there.
    /// </remarks>
    public bool TryGetContents(MissionNodeId id, out RoomContents contents)
    {
        if (_contents.TryGetValue(id, out RoomContents? found))
        {
            contents = found;
            return true;
        }

        contents = null!;
        return false;
    }

    /// <summary>
    /// Marks a node observed, generating its interior if this is the first time.
    /// </summary>
    /// <remarks>
    /// Idempotent, and monotonic: observing an already-observed node returns the
    /// contents already generated rather than rolling a new interior.
    /// </remarks>
    public RoomContents Observe(MissionNodeId id)
    {
        if (_contents.TryGetValue(id, out RoomContents? existing))
        {
            _states[id] = MissionVisibility.Revealed;
            return existing;
        }

        MissionNode? node = _map.Find(id)
            ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such node on this map.");

        RoomContents generated = NodeInteriorGenerator.Generate(node, _map.MapSeed);
        _contents[id] = generated;
        _states[id] = MissionVisibility.Revealed;
        return generated;
    }

    /// <summary>
    /// Raises a node to <see cref="MissionVisibility.Scouted"/> without generating
    /// its interior.
    /// </summary>
    /// <remarks>
    /// What a gadget or a hacked terminal does: it tells you what kind of place a
    /// node is from somewhere else on the map. Deliberately does not reach
    /// <see cref="Revealed"/> and does not generate contents — learning a room
    /// exists is not the same as standing in it, and collapsing the two would hand
    /// the player the interior for free.
    /// </remarks>
    /// <returns>True when the node's state actually changed.</returns>
    public bool Scout(MissionNodeId id)
    {
        MissionNode? node = _map.Find(id);
        if (node is null)
            return false;

        if (StateOf(id) >= MissionVisibility.Scouted)
            return false;

        _states[id] = MissionVisibility.Scouted;
        return true;
    }

    /// <summary>
    /// Propagates knowledge outwards from <paramref name="origin"/> by hop distance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distance is hop count along the mission graph, not a geometric distance: Core
    /// has no coordinates, and a route that goes round three rooms is genuinely
    /// further away for a team trying to stay unseen.
    /// </para>
    /// <para>
    /// Nodes within the scout radius become <see cref="MissionVisibility.Scouted"/>,
    /// nodes within the reveal radius become <see cref="MissionVisibility.Silhouette"/>,
    /// and the origin itself becomes observed. Nothing beyond the radius changes.
    /// </para>
    /// </remarks>
    /// <param name="origin">Where knowledge is spreading from.</param>
    /// <param name="revealRadius">Full radius, in hops.</param>
    /// <param name="scoutRadius">Radius at which a node counts as scouted, in hops.</param>
    public void RevealAround(MissionNodeId origin, int revealRadius, int scoutRadius)
    {
        if (revealRadius < 0) return;

        // Observing the origin is what generates its interior; it is the one node the
        // propagation pass is standing in.
        Observe(origin);

        foreach ((MissionNodeId id, int distance) in DistancesFrom(origin))
        {
            if (distance > revealRadius)
                continue;

            MissionVisibility granted = distance <= scoutRadius
                ? MissionVisibility.Scouted
                : MissionVisibility.Silhouette;

            if (StateOf(id) < granted)
                _states[id] = granted;
        }
    }

    /// <summary>
    /// The team's reveal radius, in hops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The best infiltrator contributes at full weight and the team average at a
    /// smaller one, so a team built around one specialist sees meaningfully further
    /// than a team of the same average skill. That is the whole reason to bring a
    /// specialist rather than five interchangeable agents.
    /// </para>
    /// <para>
    /// Integer arithmetic throughout, per knowledge.md rule 6. The weights live in
    /// <c>fog_rule</c>.
    /// </para>
    /// </remarks>
    public static int ComputeRevealRadius(IReadOnlyList<SkillSet> teamSkills)
    {
        if (teamSkills is null) throw new ArgumentNullException(nameof(teamSkills));

        int baseRadius = SimulationRules.Fog("fog_base_reveal_radius", FogFallbacks.BaseRevealRadius);

        if (teamSkills.Count == 0)
            return baseRadius;

        int best = 0;
        long total = 0;

        foreach (SkillSet skills in teamSkills)
        {
            best = Math.Max(best, skills.Infiltration);
            total += skills.Infiltration;
        }

        int average = (int)(total / teamSkills.Count);

        int bestPart = PercentOf(best, SimulationRules.Fog(
            "fog_best_infiltration_weight_percent", FogFallbacks.BestInfiltrationWeightPercent));

        int averagePart = PercentOf(average, SimulationRules.Fog(
            "fog_average_infiltration_weight_percent", FogFallbacks.AverageInfiltrationWeightPercent));

        return baseRadius + bestPart + averagePart;
    }

    /// <summary>
    /// The hop distance within which a node counts as scouted rather than merely
    /// present, given a reveal radius.
    /// </summary>
    public static int ScoutRadiusFor(int revealRadius)
        => PercentOf(revealRadius, SimulationRules.Fog(
            "fog_scout_radius_percent", FogFallbacks.ScoutRadiusPercent));

    /// <summary>
    /// Extra reveal radius a gadget grants when used to scout.
    /// </summary>
    public static int GadgetScoutBonus()
        => SimulationRules.Fog("fog_gadget_scout_radius_bonus", FogFallbacks.GadgetScoutBonus);

    /// <summary>
    /// Extra reveal radius a hacked terminal grants.
    /// </summary>
    public static int TerminalScoutBonus()
        => SimulationRules.Fog("fog_terminal_scout_radius_bonus", FogFallbacks.TerminalScoutBonus);

    /// <summary>
    /// Every node within <paramref name="radius"/> hops of <paramref name="origin"/>,
    /// with its distance.
    /// </summary>
    /// <remarks>
    /// Traversal ignores direction, because a team being watched from behind is a
    /// real thing and edge direction is a rule about movement, not about sight.
    /// </remarks>
    private IEnumerable<(MissionNodeId Id, int Distance)> DistancesFrom(MissionNodeId origin)
    {
        var seen = new Dictionary<MissionNodeId, int> { [origin] = 0 };
        var frontier = new Queue<MissionNodeId>();
        frontier.Enqueue(origin);

        while (frontier.Count > 0)
        {
            MissionNodeId current = frontier.Dequeue();
            int distance = seen[current];

            foreach (MissionNodeId next in _map.OutboundFrom(current))
            {
                if (seen.ContainsKey(next))
                    continue;

                seen[next] = distance + 1;
                frontier.Enqueue(next);
            }

            foreach (MissionNodeId previous in _map.InboundTo(current))
            {
                if (seen.ContainsKey(previous))
                    continue;

                seen[previous] = distance + 1;
                frontier.Enqueue(previous);
            }
        }

        foreach ((MissionNodeId id, int distance) in seen)
            yield return (id, distance);
    }

    private static int PercentOf(int value, int percent)
        => SimulationRules.PercentOf(value, percent);

    /// <summary>
    /// Documented fallbacks used only when <c>fog_rule</c> is unavailable.
    /// </summary>
    /// <remarks>
    /// Not balance numbers — they are what fog does when the table that would tell it
    /// otherwise is missing, which the table validator is meant to prevent. They
    /// mirror the shipped <c>fog_rule.csv</c> so a missing table degrades to sensible
    /// scouting rather than total blindness.
    /// </remarks>
    internal static class FogFallbacks
    {
        internal const int BaseRevealRadius = 1;
        internal const int BestInfiltrationWeightPercent = 100;
        internal const int AverageInfiltrationWeightPercent = 50;
        internal const int ScoutRadiusPercent = 50;
        internal const int GadgetScoutBonus = 2;
        internal const int TerminalScoutBonus = 2;
    }
}
