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
/// A one-off upgrade to what the player knows, granted by something outside the team:
/// a gadget, a hacked terminal, an informant.
/// </summary>
/// <remarks>
/// <para>
/// The extension point stage 4b needs, and the reason a gadget does not simply call
/// <see cref="FogOfWar.Scout"/> with a bigger number. A gadget that reaches across the
/// map and raises a node to <see cref="MissionVisibility.Scouted"/> does the same thing
/// walking there would have done, so it goes through the same monotone bookkeeping:
/// knowledge only ever moves up, and scouting never generates an interior.
/// </para>
/// <para>
/// An implementation is handed the <see cref="FogOfWar"/> it may act on and cannot see
/// an interior doing it — no API reachable from here hands one over.
/// <see cref="FogOfWar.TryGetContents"/> is the only door out and it is keyed by a node
/// the player has already entered.
/// </para>
/// </remarks>
public interface IFogReveal
{
    /// <summary>Localization key for what this source is, e.g. <c>fog.source.gadget</c>.</summary>
    string SourceKey { get; }

    /// <summary>Extra hops of silhouette this source grants, from <c>fog_rule</c>.</summary>
    int RevealRadiusBonus { get; }

    /// <summary>Extra hops of scouting this source grants, from <c>fog_rule</c>.</summary>
    int ScoutRadiusBonus { get; }
}

/// <summary>
/// The reveal sources the tables define today: a carried gadget, and a terminal the
/// team has already hacked.
/// </summary>
/// <remarks>
/// A set rather than a general registry, because a gadget and a terminal differ only
/// in which two numbers they read. A third source is a new row in <c>fog_rule</c>, not a
/// new type.
/// </remarks>
public readonly record struct FogRevealSource(string SourceKey, int RevealRadiusBonus, int ScoutRadiusBonus)
    : IFogReveal
{
    /// <summary>The bonus a carried gadget grants.</summary>
    public static FogRevealSource Gadget { get; } = new(
        "fog.source.gadget",
        SimulationRules.Fog("fog_gadget_reveal_radius_bonus", FogOfWar.FogFallbacks.GadgetRevealBonus),
        SimulationRules.Fog("fog_gadget_scout_radius_bonus", FogOfWar.FogFallbacks.GadgetScoutBonus));

    /// <summary>The bonus a hacked terminal grants.</summary>
    public static FogRevealSource Terminal { get; } = new(
        "fog.source.terminal",
        SimulationRules.Fog("fog_terminal_reveal_radius_bonus", FogOfWar.FogFallbacks.TerminalRevealBonus),
        SimulationRules.Fog("fog_terminal_scout_radius_bonus", FogOfWar.FogFallbacks.TerminalScoutBonus));

    /// <summary>Every source this build knows about.</summary>
    public static IReadOnlyList<FogRevealSource> All { get; } = new[] { Gadget, Terminal };
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
/// <para>
/// A thin projection over <see cref="FogNodeView"/> for callers that only care about
/// one node. The whole-map surface Presentation reads is <see cref="FogView"/>.
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

    /// <summary>Projects a whole-map view down to this shape.</summary>
    internal static NodeSight From(FogNodeView view) => new(
        view.NodeId,
        view.State,
        view.Layer,
        view.NameKey,
        view.Tags,
        view.HasLoot);
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
    /// Exposed because the mission runner resolves events and moves teams against the
    /// graph. Presentation must render from <see cref="View"/> instead: the map carries
    /// committed data for nodes the player has not seen, so handing it to a view layer
    /// hands it everything. <c>FogOfWarLeakTests</c> asserts that a
    /// <see cref="FogView"/> cannot reach back to one.
    /// </remarks>
    public MissionMap Map => _map;

    /// <summary>How many nodes have been observed, and therefore have interiors.</summary>
    public int ObservedNodeCount => _contents.Count;

    /// <summary>The current visibility of one node.</summary>
    public MissionVisibility StateOf(MissionNodeId id)
        => _states.TryGetValue(id, out MissionVisibility state) ? state : MissionVisibility.Hidden;

    /// <summary>
    /// A snapshot of everything the player may know about this whole map.
    /// </summary>
    /// <remarks>
    /// <b>The surface Presentation reads.</b> It carries no reference back to this
    /// object, so a view holding one cannot observe a node it was not shown, cannot
    /// mutate anything, and cannot reach an interior. Rebuilt per refresh rather than
    /// cached, because a cached view outlives the fog state it described and would
    /// keep revealing rooms the player has since walked away from.
    /// </remarks>
    public FogView View()
    {
        var nodes = new List<FogNodeView>(_map.Nodes.Count);

        foreach (MissionNode node in _map.Nodes)
            nodes.Add(BuildSight(node));

        return new FogView(nodes, _map.EntryNodeId);
    }

    /// <summary>
    /// Builds one node's read-model view from its current visibility.
    /// </summary>
    /// <remarks>
    /// <b>The single place fog decides what a state reveals.</b> Every other read path
    /// (<see cref="Sight"/>, <see cref="View"/>) projects from here, so there is one
    /// function to audit when someone adds a field to <see cref="MissionNode"/> — rather
    /// than four independent filters that have to be kept in agreement by hand.
    /// </remarks>
    private FogNodeView BuildSight(MissionNode node)
    {
        MissionVisibility state = StateOf(node.Id);
        IReadOnlyList<MissionNodeId> edges = _map.OutboundFrom(node.Id);

        if (state == MissionVisibility.Hidden)
        {
            // Only the layer and where it leads survive. A hidden node's name, tags,
            // security and loot presence are exactly what the player is not supposed
            // to know yet — and edges carry ids only, so following one lands on that
            // node's own view, still hidden.
            return FogNodeView.Unknown(node.Id, node.Layer) with { EdgesTo = edges };
        }

        if (state == MissionVisibility.Silhouette)
        {
            // The room is on the plan. Its identity, its contents and its threat are
            // not.
            return new FogNodeView(
                node.Id,
                state,
                node.Layer,
                edges,
                string.Empty,
                Array.Empty<string>(),
                false,
                0,
                GuardBand.None);
        }

        // Scouted and Revealed both name the room and its threat. The difference is
        // not in this value: it is whether an interior exists, which
        // TryGetContents answers.
        return new FogNodeView(
            node.Id,
            state,
            node.Layer,
            edges,
            node.NameKey,
            node.Tags,
            node.HasLoot,
            node.BaseSecurity,
            node.GuardBand);
    }

    /// <summary>
    /// What the player may know about one node at its current visibility.
    /// </summary>
    /// <remarks>
    /// A convenience over <see cref="View"/> for callers that only want one node. It
    /// shares <see cref="BuildSight"/>, so it cannot drift from the whole-map view.
    /// </remarks>
    public NodeSight Sight(MissionNodeId id)
    {
        MissionNode? node = _map.Find(id);
        return node is null ? NodeSight.Unknown(id, 0) : NodeSight.From(BuildSight(node));
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
            "fog_team_avg_weight_pct", FogFallbacks.AverageInfiltrationWeightPercent));

        return baseRadius + bestPart + averagePart;
    }

    /// <summary>
    /// The hop distance within which a node counts as a mere silhouette — known to be
    /// there, not known to be anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief's divisor model: the team's reach is a budget of hops, and each grade
    /// of knowledge is a fraction of it. A larger divisor is a tighter circle.
    /// </para>
    /// <para>
    /// Floored at <c>fog_min_silhouette_radius</c> rather than at zero. Integer
    /// division would otherwise give a weak team a silhouette radius of 0, and "you
    /// can see the room the door leads into" is not a luxury — a team that cannot see
    /// even the next room cannot route anywhere, and the player is left guessing at
    /// the one decision the map exists to inform.
    /// </para>
    /// </remarks>
    public static int SilhouetteRadiusFor(int revealRadius)
    {
        int divisor = Math.Max(1, SimulationRules.Fog(
            "fog_silhouette_divisor", FogFallbacks.SilhouetteDivisor));

        int radius = revealRadius / divisor;

        return Math.Max(radius, SimulationRules.Fog(
            "fog_min_silhouette_radius", FogFallbacks.MinSilhouetteRadius));
    }

    /// <summary>
    /// The hop distance within which a node counts as scouted rather than merely
    /// present, given a reveal radius.
    /// </summary>
    /// <remarks>
    /// A second, tighter divisor rather than a percentage of the silhouette radius:
    /// two independent knobs means a designer can widen what is <em>known to exist</em>
    /// without also widening what is <em>understood</em>, which are different design
    /// decisions.
    /// </remarks>
    public static int ScoutRadiusFor(int revealRadius)
    {
        int divisor = Math.Max(1, SimulationRules.Fog(
            "fog_scouted_divisor", FogFallbacks.ScoutedDivisor));

        return Math.Max(0, revealRadius / divisor);
    }

    /// <summary>
    /// The hop distance within which a node counts as scouted, given a reveal radius
    /// <em>and</em> the bonus a reveal source adds.
    /// </summary>
    /// <remarks>
    /// The bonus is added to the resulting <em>radius</em>, not to the reach before the
    /// divisor is taken. That distinction is not cosmetic: the divisors are 25 and 40,
    /// so adding two hops to a reach of 200 moves 8 to 8 and a gadget would appear to do
    /// nothing at all to a competent team — precisely the players a gadget is bought
    /// for. Added to the radius, "+2 hops" means two hops at every skill level, which is
    /// what a designer reading the table expects.
    /// </remarks>
    public static int ScoutRadiusFor(int revealRadius, int scoutRadiusBonus)
        => ScoutRadiusFor(revealRadius) + Math.Max(0, scoutRadiusBonus);

    /// <summary>The silhouette radius after a reveal source's bonus.</summary>
    public static int SilhouetteRadiusFor(int revealRadius, int revealRadiusBonus)
        => SilhouetteRadiusFor(revealRadius) + Math.Max(0, revealRadiusBonus);

    /// <summary>
    /// Applies a reveal source from <paramref name="origin"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stage-4b entry point for gadgets and terminals. It widens the existing
    /// propagation rather than inventing a second one, so a node the team has already
    /// passed is still only as known as the better of the two passes — knowledge is
    /// monotone, and two paths to the same room must not disagree about what it is.
    /// </para>
    /// <para>
    /// Never reaches <see cref="MissionVisibility.Revealed"/>, and in particular never
    /// observes its own origin. That is the whole difference between this and
    /// <see cref="RevealAround"/>: walking a team somewhere puts them there and
    /// generates the interior they are standing in, whereas a gadget is a remote
    /// source that happens to be keyed off the team's position. Observing the origin
    /// here would mean a gadget could generate the interior of a room the team has never
    /// entered — which is the exact leak the lazy content rule exists to prevent, and
    /// the only reason this method exists separately rather than delegating.
    /// </para>
    /// </remarks>
    /// <returns>How many nodes this pass newly taught the player about.</returns>
    public int RevealFrom(MissionNodeId origin, IFogReveal source)
        => RevealFrom(origin, source, Array.Empty<SkillSet>());

    /// <summary>
    /// Applies a reveal source from <paramref name="origin"/>, scaled to the team.
    /// </summary>
    /// <remarks>
    /// The overload stage 4b will call. A reveal source is keyed off the team's own
    /// position and reach, so it inherits the team's radius and then extends it — rather
    /// than granting a flat radius that would be enormous for a weak team and negligible
    /// for a strong one. The parameterless-in-spirit overload above is for tests and for
    /// any caller that genuinely has no team to hand.
    /// </remarks>
    /// <returns>How many nodes this pass newly taught the player about.</returns>
    public int RevealFrom(
        MissionNodeId origin,
        IFogReveal source,
        IReadOnlyList<SkillSet> teamSkills)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (teamSkills is null) throw new ArgumentNullException(nameof(teamSkills));

        if (_map.Find(origin) is null)
            return 0;

        int before = VisibleNodeCount();

        int reach = ComputeRevealRadius(teamSkills);

        int radius = SilhouetteRadiusFor(reach, source.RevealRadiusBonus);
        int scout = ScoutRadiusFor(reach, source.ScoutRadiusBonus);

        if (radius <= 0)
            return 0;

        // Deliberately not Observe(origin): see the remarks.
        foreach ((MissionNodeId id, int distance) in DistancesFrom(origin))
        {
            if (distance == 0 || distance > radius)
                continue;

            MissionVisibility granted = distance <= scout
                ? MissionVisibility.Scouted
                : MissionVisibility.Silhouette;

            if (StateOf(id) < granted)
                _states[id] = granted;
        }

        return VisibleNodeCount() - before;
    }

    /// <summary>How many nodes the player knows exist.</summary>
    public int VisibleNodeCount()
    {
        int count = 0;
        foreach (MissionVisibility state in _states.Values)
        {
            if (state != MissionVisibility.Hidden)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Extra silhouette hops a gadget grants when used to scout.
    /// </summary>
    public static int GadgetRevealBonus() => FogRevealSource.Gadget.RevealRadiusBonus;

    /// <summary>
    /// Extra scouting hops a gadget grants when used to scout.
    /// </summary>
    public static int GadgetScoutBonus() => FogRevealSource.Gadget.ScoutRadiusBonus;

    /// <summary>
    /// Extra silhouette hops a hacked terminal grants.
    /// </summary>
    public static int TerminalRevealBonus() => FogRevealSource.Terminal.RevealRadiusBonus;

    /// <summary>
    /// Extra scouting hops a hacked terminal grants.
    /// </summary>
    public static int TerminalScoutBonus() => FogRevealSource.Terminal.ScoutRadiusBonus;

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

        /// <summary>Silhouettes reach this fraction of the team's hop budget.</summary>
        internal const int SilhouetteDivisor = 25;

        /// <summary>Scouted reaches this fraction — a tighter circle.</summary>
        internal const int ScoutedDivisor = 40;

        /// <summary>Even the weakest team sees the room the door leads into.</summary>
        internal const int MinSilhouetteRadius = 1;

        internal const int GadgetRevealBonus = 1;
        internal const int GadgetScoutBonus = 2;
        internal const int TerminalRevealBonus = 1;
        internal const int TerminalScoutBonus = 2;
    }
}
