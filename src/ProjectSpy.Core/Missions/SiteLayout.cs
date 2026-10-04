// The stage-2 tactical `room_template` bean is the width authority for a site room.
// Aliased because Core's own `SiteRoom` would otherwise be the only other thing in this
// file named "room template", and the two must never be confused at a call site.
using TacticalRoomTemplate = ProjectSpy.Tables.RoomTemplate;

// A tactical position is looked up in this file because it is the crossing point
// between "a point on a floor" and "a room in a building". The type itself lives in
// Core.Tactical, which rule 10's exception scopes; nothing about it leaks out.
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Core.Missions;

/// <summary>
/// One room's contents, generated the first time the room is observed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type does not exist until somebody looks.</b> A generated site contains
/// rooms, and a room contains an interval and a loot-table id. What the loot table
/// actually yields, and where the furniture stands, are decided here — and this object
/// is only ever constructed by <see cref="SiteLayout.ObserveRoom"/>, which is the one
/// method that marks the room observed on the way past.
/// </para>
/// <para>
/// Everything is a pure function of <c>(MapSeed, roomId)</c>, so the object does not
/// have to be saved: re-observing after a load produces exactly the same contents. That
/// is what lets the save format carry a seed instead of every roll.
/// </para>
/// <para>
/// <b>Observation, not entry.</b> Walking past a closed door reveals nothing, so the
/// decision to observe is the caller's and is driven by what the team can perceive, not
/// by where the team happens to be standing.
/// </para>
/// </remarks>
public sealed class SiteRoomDetail
{
    /// <summary>The room these contents belong to.</summary>
    public SiteRoomId RoomId { get; init; }

    /// <summary>
    /// What the room's containers actually hold, rolled on first observation.
    /// </summary>
    /// <remarks>
    /// Empty for a room whose template declares no loot table. That is a real answer
    /// rather than a pending one, which is why this is a list and not a nullable.
    /// </remarks>
    public IReadOnlyList<SiteLootEntry> Loot { get; init; } = Array.Empty<SiteLootEntry>();

    /// <summary>Where the furniture stands, in room-relative centimetres.</summary>
    public IReadOnlyList<SiteProp> Props { get; init; } = Array.Empty<SiteProp>();

    /// <inheritdoc/>
    public override string ToString()
        => $"{RoomId}: loot={Loot.Count} props={Props.Count}";
}

/// <summary>One item stack a room's container turned out to hold.</summary>
/// <param name="LootTableId">The table it was rolled from.</param>
/// <param name="ItemId">The item.</param>
/// <param name="Count">How many.</param>
public readonly record struct SiteLootEntry(int LootTableId, int ItemId, int Count);

/// <summary>A piece of furniture, standing at a point on the room's floor.</summary>
/// <param name="PropId">
/// A <c>room_template.prop_set_id</c> value, meaning "a member of this set". Which
/// specific mesh that becomes is Presentation's business.
/// </param>
/// <param name="X">Where it stands, in centimetres from the room's left edge.</param>
public readonly record struct SiteProp(int PropId, Fixed32 X);

/// <summary>
/// A generated building: floors, the rooms across them, the connections between those
/// rooms, and everything placed inside them.
/// </summary>
/// <remarks>
/// <para>
/// This is the stage-4a answer to knowledge.md rule 16 — a mission is a continuous
/// building, not a graph of abstract nodes. Its rooms are walkable intervals on floors
/// and its adjacency is explicit connection data, so a door can be locked and a vent can
/// be too small for a guard without either fact being a lie about the walls.
/// </para>
/// <para>
/// <b>Pure structure and committed data.</b> Everything here is fixed by
/// <see cref="MapSeed"/> and the tables: no per-mission progress, no agent positions, no
/// door states. A save stores the seed and regenerates, which is why
/// <see cref="Save"/> is four numbers and a room list rather than several thousand
/// fields. What has been <em>observed</em> is recorded, because a reloaded site must not
/// present a room the team has already searched as unknown.
/// </para>
/// <para>
/// <b>Coordinates are centimetres</b> and only ever that — the tactical-space exception
/// in rule 10, scoped to this namespace. The generator's own inputs are lane units and
/// are converted once, on the way in.
/// </para>
/// </remarks>
public sealed class SiteLayout
{
    /// <summary>Foreign key into <c>site_template</c>.</summary>
    public int SiteTemplateId { get; init; }

    /// <summary>The site's tier, from the <c>site_template</c> row.</summary>
    public int Tier { get; init; }

    /// <summary>Mission this site was generated for.</summary>
    public int MissionId { get; init; }

    /// <summary>The world seed the mission seed was derived from.</summary>
    public ulong WorldSeed { get; init; }

    /// <summary>
    /// The seed every deterministic decision about this building was drawn from.
    /// </summary>
    /// <remarks>
    /// Not the world's Mission stream. A private generator seeded from <c>MapSeed</c>
    /// means mission 7 of a world is the same building in the simulator, in a replay and
    /// in the shipping game, and a save reloaded at a different moment in the run rebuilds
    /// it exactly (rule 6). See <see cref="SiteGenerator.DeriveMapSeed"/>.
    /// </remarks>
    public ulong MapSeed { get; init; }

    /// <summary>The building itself.</summary>
    public SiteRegion MainSite { get; init; } = new() { Kind = SiteRegionKind.Site };

    /// <summary>
    /// The forward command post, when the site template offers one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null for a site without one. A separate region — its own floors, rooms and
    /// connections — joined to the building by a single <see cref="SiteConnection"/>
    /// whose <see cref="SiteConnection.TraverseSteps"/> is
    /// <c>site_gen_rule.site_forward_post_travel_steps</c>. That connection is what makes
    /// travelling to the post cost steps <em>and</em> makes it something the pathfinder
    /// can route to.
    /// </para>
    /// <para>
    /// <b>The post's floors are indexed past the building's.</b> A
    /// <see cref="TacticalPosition"/> is a floor index and an x, so two regions whose
    /// floors share an index are two places that cannot be told apart: a point at floor 0,
    /// x 500 is inside some room in both. The post therefore starts at
    /// <c>MainSite.Floors.Count</c>, which is what lets
    /// <see cref="RoomContaining(TacticalPosition)"/> answer unambiguously for anyone
    /// standing in the post.
    /// </para>
    /// </remarks>
    public SiteRegion? ForwardPost { get; init; }

    /// <summary>Where the team lands. Always in the building.</summary>
    public SiteRoomId EntranceRoomId { get; init; }

    /// <summary>What the mission is for. Always in the building.</summary>
    public SiteRoomId ObjectiveRoomId { get; init; }

    /// <summary>Where the team can leave. May include the entrance, usually does not.</summary>
    public IReadOnlyList<SiteRoomId> ExtractionRoomIds { get; init; } = Array.Empty<SiteRoomId>();

    /// <summary>The forward post's own room, or <see cref="SiteRoomId.None"/>.</summary>
    public SiteRoomId ForwardPostRoomId { get; init; }

    // ---- pass-throughs -------------------------------------------------------
    //
    // The brief describes a site as "Floors, each with an ordered list of Rooms ...
    // plus a Connection list", and that is what nearly every caller wants. The region
    // type exists so the forward post can be somewhere else, not to make every caller
    // reach through two levels to find a room.

    /// <summary>The building's floors, bottom to top.</summary>
    public IReadOnlyList<SiteFloor> Floors => MainSite.Floors;

    /// <summary>The building's connections.</summary>
    public IReadOnlyList<SiteConnection> Connections => MainSite.Connections;

    /// <summary>Light emitters across the whole mission.</summary>
    public IReadOnlyList<SiteLight> Lights => Combine(MainSite.Lights, ForwardPost?.Lights);

    /// <summary>Sight blockers across the whole mission.</summary>
    public IReadOnlyList<SiteOccluder> Occluders => Combine(MainSite.Occluders, ForwardPost?.Occluders);

    /// <summary>Placed interactables across the whole mission.</summary>
    public IReadOnlyList<SiteInteractable> Interactables => Combine(MainSite.Interactables, ForwardPost?.Interactables);

    /// <summary>Guards across the whole mission.</summary>
    public IReadOnlyList<SiteGuard> Guards => Combine(MainSite.Guards, ForwardPost?.Guards);

    /// <summary>Civilians across the whole mission.</summary>
    public IReadOnlyList<SiteCivilian> Civilians => Combine(MainSite.Civilians, ForwardPost?.Civilians);

    /// <summary>The building's rooms, floor by floor, left to right.</summary>
    public IReadOnlyList<SiteRoom> Rooms => Flatten(MainSite);

    /// <summary>Every room in the mission, building first then forward post.</summary>
    public IReadOnlyList<SiteRoom> AllRooms
        => ForwardPost is null ? Rooms : Combine(Rooms, Flatten(ForwardPost));

    /// <summary>Every region of this site, building first.</summary>
    public IEnumerable<SiteRegion> Regions
    {
        get
        {
            yield return MainSite;
            if (ForwardPost is not null)
                yield return ForwardPost;
        }
    }

    private static List<SiteRoom> Flatten(SiteRegion region)
    {
        var rooms = new List<SiteRoom>(region.RoomCount);
        foreach (SiteFloor floor in region.Floors)
            rooms.AddRange(floor.Rooms);
        return rooms;
    }

    private static IReadOnlyList<T> Combine<T>(IReadOnlyList<T> first, IReadOnlyList<T>? second)
    {
        if (second is null || second.Count == 0)
            return first;

        var all = new List<T>(first.Count + second.Count);
        all.AddRange(first);
        all.AddRange(second);
        return all;
    }

    private static IReadOnlyList<T> Combine<T>(List<T> first, List<T>? second)
        => second is null || second.Count == 0 ? first : Combine((IReadOnlyList<T>)first, second);

    // ---- indices -------------------------------------------------------------
    //
    // Built once and cached. The twenty-thousand site test validates every layout it
    // generates, so an index rebuilt per query would dominate the very measurement it
    // exists to take.

    private Dictionary<SiteRoomId, SiteRoom>? _roomsById;
    private Dictionary<SiteRoomId, List<SiteRoomId>>? _neighbours;
    private Dictionary<SiteRoomId, List<SiteConnection>>? _connectionsByRoom;
    private Dictionary<string, SiteConnection>? _connectionByPair;
    private Dictionary<SiteConnectionId, SiteConnection>? _connectionsById;

    /// <summary>Looks up a room, or null.</summary>
    public SiteRoom? Find(SiteRoomId id)
    {
        _roomsById ??= BuildRoomIndex();
        return _roomsById.TryGetValue(id, out SiteRoom? room) ? room : null;
    }

    /// <summary>
    /// The room a point on a floor falls inside, or null when it falls in no room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place tactical space is turned back into architecture. A
    /// <c>TacticalPosition</c> is a floor and a number; every rule that needs to know
    /// <em>which room</em> that is asks here, and there is deliberately no cached room
    /// id sitting beside the position to disagree with it. Two sources of truth about
    /// where something is would eventually diverge, and the failure would look like a
    /// guard standing in a wall.
    /// </para>
    /// <para>
    /// Scans the floor rather than indexing by floor because the number of rooms per
    /// floor is single digits and an index would be a second thing to keep correct. A
    /// position outside every room's half-open interval returns null — which is a real
    /// answer, not a corner case, and callers treat it as "off the map" rather than
    /// quietly snapping to the nearest room.
    /// </para>
    /// </remarks>
    public SiteRoom? RoomContaining(TacticalPosition at)
    {
        if (!at.IsValid)
            return null;

        foreach (SiteFloor floor in MainSite.Floors)
        {
            if (floor.Index != at.FloorIndex)
                continue;

            foreach (SiteRoom room in floor.Rooms)
            {
                if (room.Contains(at.X))
                    return room;
            }

            return null;
        }

        // The forward post is a separate region with its own floors, so it needs its own
        // pass. The building is searched first because it is where a mission spends
        // almost all of its time, and the two can never both contain the same point
        // because they are separate places.
        if (ForwardPost is not null)
        {
            foreach (SiteFloor floor in ForwardPost.Floors)
            {
                if (floor.Index != at.FloorIndex)
                    continue;

                foreach (SiteRoom room in floor.Rooms)
                {
                    if (room.Contains(at.X))
                        return room;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The room a connection stands in on its lower floor, which is where a traversal
    /// starts from.
    /// </summary>
    /// <remarks>
    /// A named accessor rather than repeated <c>Find(connection.RoomA)</c> because the
    /// question is asked at every traversal and getting it backwards — arriving in
    /// <see cref="SiteConnection.RoomA"/> when the entity set off from
    /// <see cref="SiteConnection.RoomB"/> — is a movement bug that would only show up
    /// on the subset of connections whose A side is the upper floor.
    /// </remarks>
    public SiteRoom? RoomAtFootOf(SiteConnection connection)
        => connection is null ? null : Find(connection.RoomA);

    /// <summary>The connection's landing room, given the room the entity set off from.</summary>
    public SiteRoom? RoomAcross(SiteConnection connection, SiteRoomId from)
        => connection is null ? null : Find(connection.Other(from));

    /// <summary>
    /// A connection by id, or null when the id names nothing on this site.
    /// </summary>
    /// <remarks>
    /// The tactical layer holds connection ids — on doors, on pending actions, on
    /// orders — and needs to get back to the connection without knowing which room it
    /// touches. Without this, every one of those lookups would either walk the whole
    /// connection list or require the caller to already know a room, and the first
    /// would cost a scan on every door check in the step pipeline.
    /// </remarks>
    public SiteConnection? FindConnectionFor(SiteConnectionId id)
    {
        EnsureIndex();
        return _connectionsById!.TryGetValue(id, out SiteConnection? connection) ? connection : null;
    }

    /// <summary>Rooms directly connected to <paramref name="id"/>, in generation order.</summary>
    public IReadOnlyList<SiteRoomId> Neighbours(SiteRoomId id)
    {
        EnsureIndex();
        return _neighbours!.TryGetValue(id, out List<SiteRoomId>? list) ? list : (IReadOnlyList<SiteRoomId>)Array.Empty<SiteRoomId>();
    }

    /// <summary>Connections that touch a room, in generation order.</summary>
    public IReadOnlyList<SiteConnection> ConnectionsAt(SiteRoomId id)
    {
        EnsureIndex();
        return _connectionsByRoom!.TryGetValue(id, out List<SiteConnection>? list)
            ? list
            : (IReadOnlyList<SiteConnection>)Array.Empty<SiteConnection>();
    }

    /// <summary>True when some connection joins exactly these two rooms.</summary>
    public bool AreConnected(SiteRoomId a, SiteRoomId b) => FindConnection(a, b) is not null;

    /// <summary>The first connection joining two rooms, or null.</summary>
    public SiteConnection? FindConnection(SiteRoomId a, SiteRoomId b)
    {
        EnsureIndex();
        return _connectionByPair!.TryGetValue(PairKey(a, b), out SiteConnection? connection) ? connection : null;
    }

    private Dictionary<SiteRoomId, SiteRoom> BuildRoomIndex()
    {
        var index = new Dictionary<SiteRoomId, SiteRoom>();
        foreach (SiteRoom room in AllRooms)
            index[room.Id] = room;
        return index;
    }

    private void EnsureIndex()
    {
        if (_neighbours is not null)
            return;

        var neighbours = new Dictionary<SiteRoomId, List<SiteRoomId>>();
        var byRoom = new Dictionary<SiteRoomId, List<SiteConnection>>();
        var byPair = new Dictionary<string, SiteConnection>(StringComparer.Ordinal);
        var byId = new Dictionary<SiteConnectionId, SiteConnection>();

        foreach (SiteRegion region in Regions)
        {
            foreach (SiteConnection connection in region.Connections)
            {
                byId[connection.Id] = connection;

                // Two connections between the same pair are legal — a door and a window
                // between an office and a corridor is ordinary — so the first indexed
                // wins for FindConnection and the rest stay reachable through
                // ConnectionsAt. Validate rejects duplicates of the *same* kind.
                string key = PairKey(connection.RoomA, connection.RoomB);
                if (!byPair.ContainsKey(key))
                    byPair[key] = connection;

                Link(neighbours, connection.RoomA, connection.RoomB);
                Link(neighbours, connection.RoomB, connection.RoomA);
                Track(byRoom, connection.RoomA, connection);
                Track(byRoom, connection.RoomB, connection);
            }
        }

        _neighbours = neighbours;
        _connectionsByRoom = byRoom;
        _connectionByPair = byPair;
        _connectionsById = byId;
    }

    private static void Link(Dictionary<SiteRoomId, List<SiteRoomId>> index, SiteRoomId from, SiteRoomId to)
    {
        if (!index.TryGetValue(from, out List<SiteRoomId>? list))
        {
            list = new List<SiteRoomId>();
            index[from] = list;
        }

        list.Add(to);
    }

    private static void Track(Dictionary<SiteRoomId, List<SiteConnection>> index, SiteRoomId room, SiteConnection connection)
    {
        if (!index.TryGetValue(room, out List<SiteConnection>? list))
        {
            list = new List<SiteConnection>();
            index[room] = list;
        }

        list.Add(connection);
    }

    /// <summary>Order-independent key for a room pair.</summary>
    private static string PairKey(SiteRoomId a, SiteRoomId b)
        => a.Value <= b.Value
            ? a.Value.ToString() + ":" + b.Value
            : b.Value.ToString() + ":" + a.Value;

    // ---- graph queries -------------------------------------------------------

    /// <summary>
    /// Every room reachable from <paramref name="start"/>, itself included.
    /// </summary>
    /// <remarks>
    /// Connections are two-way, so this is a plain undirected flood fill with no
    /// direction handling at all — which is also why <see cref="ReachableFrom"/> doubles
    /// as "can reach <paramref name="start"/>", and why the route count below does not
    /// need a reverse search.
    /// </remarks>
    public HashSet<SiteRoomId> ReachableFrom(SiteRoomId start)
    {
        var seen = new HashSet<SiteRoomId>();
        if (!start.IsValid)
            return seen;

        var frontier = new Queue<SiteRoomId>();
        seen.Add(start);
        frontier.Enqueue(start);

        while (frontier.Count > 0)
        {
            SiteRoomId current = frontier.Dequeue();
            foreach (SiteRoomId next in Neighbours(current))
            {
                if (seen.Add(next))
                    frontier.Enqueue(next);
            }
        }

        return seen;
    }

    /// <summary>True when <paramref name="target"/> can be walked to from <paramref name="start"/>.</summary>
    public bool CanReach(SiteRoomId start, SiteRoomId target) => ReachableFrom(start).Contains(target);

    /// <summary>Rooms in the building that cannot be walked to from the entrance.</summary>
    public IReadOnlyList<SiteRoomId> RoomsUnreachableFromEntrance()
    {
        HashSet<SiteRoomId> seen = ReachableFrom(EntranceRoomId);
        var orphans = new List<SiteRoomId>();

        foreach (SiteFloor floor in MainSite.Floors)
        {
            foreach (SiteRoom room in floor.Rooms)
            {
                if (!seen.Contains(room.Id))
                    orphans.Add(room.Id);
            }
        }

        return orphans;
    }

    /// <summary>
    /// A shortest route from the entrance to the objective, as connection ids.
    /// </summary>
    /// <remarks>
    /// Breadth-first, so the route found is a shortest one. Ties break on the neighbour
    /// generation order, which is itself deterministic — which matters, because the route
    /// count is defined relative to <em>this</em> route and would otherwise wobble
    /// between runs on the same layout.
    /// </remarks>
    public IReadOnlyList<SiteConnectionId> ShortestRouteToObjective()
    {
        if (EntranceRoomId == ObjectiveRoomId)
            return Array.Empty<SiteConnectionId>();

        var cameFrom = new Dictionary<SiteRoomId, SiteRoomId>();
        var cameBy = new Dictionary<SiteRoomId, SiteConnectionId>();
        var seen = new HashSet<SiteRoomId> { EntranceRoomId };
        var frontier = new Queue<SiteRoomId>();
        frontier.Enqueue(EntranceRoomId);

        while (frontier.Count > 0)
        {
            SiteRoomId current = frontier.Dequeue();

            foreach (SiteConnection connection in ConnectionsAt(current))
            {
                SiteRoomId next = connection.Other(current);
                if (!seen.Add(next))
                    continue;

                cameFrom[next] = current;
                cameBy[next] = connection.Id;

                if (next == ObjectiveRoomId)
                    return TraceBack(cameFrom, cameBy);

                frontier.Enqueue(next);
            }
        }

        return Array.Empty<SiteConnectionId>();
    }

    private IReadOnlyList<SiteConnectionId> TraceBack(
        Dictionary<SiteRoomId, SiteRoomId> cameFrom,
        Dictionary<SiteRoomId, SiteConnectionId> cameBy)
    {
        var route = new List<SiteConnectionId>();
        SiteRoomId cursor = ObjectiveRoomId;

        // TryGetValue rather than an indexer. The seed room is the one room in the tree
        // with no predecessor, so a loop written as `while (cursor != cameFrom[cursor])`
        // indexes the dictionary for the seed *before* the comparison that would have
        // ended it — which throws on every single layout.
        while (cameFrom.TryGetValue(cursor, out SiteRoomId parent) && cursor != parent)
        {
            route.Add(cameBy[cursor]);
            cursor = parent;
        }

        route.Reverse();
        return route;
    }

    /// <summary>
    /// How many different ways there are to reach the objective, saturating at
    /// <paramref name="cap"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counting distinct simple paths is exponential and, on a building with forty
    /// connections, far out of range for an int. The question the stage-4a brief actually
    /// asks is the boolean one — <em>at least two routes that do not share every
    /// connection</em> — so this counts that instead and defines it precisely:
    /// </para>
    /// <para>
    /// Take the shortest route <c>P</c>. Every connection outside <c>P</c> that a route
    /// could use — one end reachable from the entrance, the other able to reach the
    /// objective — contributes one. The returned number is therefore "how many
    /// connections offer a way through that the shortest route does not take", plus one
    /// for the shortest route itself.
    /// </para>
    /// <para>
    /// Because the graph is undirected, "can reach the objective" is just
    /// <see cref="ReachableFrom"/> of the objective, so the whole count is two flood
    /// fills and a scan rather than a search per connection.
    /// </para>
    /// <para>
    /// This is not "distinct paths" and is not claimed to be. It is a monotone measure of
    /// how much redundancy the building has; the guarantee the generator makes and the
    /// tests assert is only that it is never 1.
    /// </para>
    /// </remarks>
    public long CountRoutesToObjective(long cap)
    {
        if (cap <= 0)
            return 0;

        if (EntranceRoomId == ObjectiveRoomId)
            return 0;

        HashSet<SiteRoomId> fromEntrance = ReachableFrom(EntranceRoomId);
        if (!fromEntrance.Contains(ObjectiveRoomId))
            return 0;

        // Undirected, so the set that can reach the objective is the set reachable from it.
        HashSet<SiteRoomId> toObjective = ReachableFrom(ObjectiveRoomId);

        var onShortest = new HashSet<SiteConnectionId>(ShortestRouteToObjective());
        long total = 1;

        foreach (SiteConnection connection in Connections)
        {
            if (onShortest.Contains(connection.Id))
                continue;

            bool usable =
                (fromEntrance.Contains(connection.RoomA) && toObjective.Contains(connection.RoomB))
                || (fromEntrance.Contains(connection.RoomB) && toObjective.Contains(connection.RoomA));

            if (!usable)
                continue;

            if (++total >= cap)
                return cap;
        }

        return total;
    }

    /// <summary>
    /// True when two routes reach the objective that do not share every connection.
    /// </summary>
    /// <remarks>
    /// The invariant the stage-4a brief calls "at least one alternate route". Structural,
    /// never a dice roll: the generator closes a loop on purpose and then checks this
    /// rather than hoping a random layout happened to produce one.
    /// </remarks>
    public bool HasAlternateRouteToObjective => CountRoutesToObjective(2) >= 2;

    /// <summary>
    /// Floors that hold at least one room but no connection leading off them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A floor with rooms and no way up or down is a floor the team can enter and never
    /// leave. The generator links every floor to its neighbour explicitly, so this comes
    /// back empty by construction — which is why it is asserted rather than repaired.
    /// </para>
    /// <para>
    /// A <b>single-floor</b> building always returns empty, and that is not a loophole. It
    /// has no floor above or below to connect to, and a one-storey warehouse with a stair
    /// going up to a roof that is not there would be worse than the honest answer. The
    /// brief's "every floor with a room has at least one vertical connection" is a claim
    /// about how floors are joined; with one floor there are no floors to join. The other
    /// invariants still bind — a one-storey site still has to offer two routes to the
    /// objective — so nothing is actually let through by this.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> FloorsWithoutVerticalConnection()
    {
        if (MainSite.Floors.Count < 2)
            return Array.Empty<int>();

        var touching = new HashSet<int>();

        foreach (SiteConnection connection in Connections)
        {
            if (!connection.IsVertical)
                continue;

            touching.Add(connection.FloorIndexA);
            touching.Add(connection.FloorIndexB);
        }

        var stranded = new List<int>();
        foreach (SiteFloor floor in MainSite.Floors)
        {
            if (floor.Rooms.Count > 0 && !touching.Contains(floor.Index))
                stranded.Add(floor.Index);
        }

        return stranded;
    }

    /// <summary>Guards whose patrol route a guard could not actually walk.</summary>
    public IReadOnlyList<SiteGuardId> GuardsWithUnwalkableRoutes()
    {
        var broken = new List<SiteGuardId>();

        foreach (SiteGuard guard in Guards)
        {
            if (!IsRouteTraversable(guard))
                broken.Add(guard.Id);
        }

        return broken;
    }

    /// <summary>
    /// True when a guard could walk its own route.
    /// </summary>
    /// <remarks>
    /// Two requirements, both structural. Every hop must cross a real connection, so the
    /// route is contiguous rather than a list of rooms that happen to share a floor. And
    /// every hop must cross one an NPC can use — <c>usable_by_npc</c> is false for vents,
    /// windows and holes, which is what stops a patrol being routed through a crawlspace
    /// nobody can fit through.
    /// </remarks>
    public bool IsRouteTraversable(SiteGuard guard)
    {
        if (guard is null || guard.PatrolRoute.Count == 0)
            return false;

        if (guard.PatrolRoute[0] != guard.HomeRoomId)
            return false;

        for (int i = 1; i < guard.PatrolRoute.Count; i++)
        {
            if (CanTraverse(guard.PatrolRoute[i - 1], guard.PatrolRoute[i]))
                continue;

            return false;
        }

        return true;
    }

    /// <summary>
    /// True when some connection joins two rooms that an NPC can use.
    /// </summary>
    /// <remarks>
    /// Scans the connections at the first room rather than asking
    /// <see cref="FindConnection"/> for "the" connection between a pair. Two rooms can
    /// legitimately be joined twice — a door and an interior window — and only one of them
    /// may be NPC-usable. Looking up the first and testing that one made a guard's route
    /// through a doored pair look untraversable whenever the window happened to be indexed
    /// first, which is a false negative on a perfectly walkable patrol.
    /// </remarks>
    private bool CanTraverse(SiteRoomId from, SiteRoomId to)
    {
        foreach (SiteConnection connection in ConnectionsAt(from))
        {
            if (connection.Other(from) == to && connection.UsableByNpc)
                return true;
        }

        return false;
    }

    /// <summary>Extraction points reachable from the objective.</summary>
    public IReadOnlyList<SiteRoomId> ReachableExtractionPoints()
    {
        HashSet<SiteRoomId> fromObjective = ReachableFrom(ObjectiveRoomId);
        var usable = new List<SiteRoomId>();

        foreach (SiteRoomId id in ExtractionRoomIds)
        {
            if (fromObjective.Contains(id))
                usable.Add(id);
        }

        return usable;
    }

    // ---- lazy contents -------------------------------------------------------
    //
    // Observation state and cached contents are kept apart, and they have to be. A room
    // that was observed before the save must still count as observed after the load, but
    // its contents are regenerated on demand rather than carried in the save — so the
    // "already observed" record exists with no detail attached, and a single dictionary
    // holding nulls would hand ObserveRoom a null cache hit and return nothing at all.

    private readonly HashSet<SiteRoomId> _observed = new();
    private readonly Dictionary<SiteRoomId, SiteRoomDetail> _details = new();

    /// <summary>
    /// Generates and returns a room's contents, marking the room observed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single door to <see cref="SiteRoomDetail"/>. Contents are a pure function of
    /// <c>(MapSeed, roomId, "contents")</c>, so calling this twice for the same room
    /// returns the same object, and a room observed in an earlier session observes
    /// identically after a reload.
    /// </para>
    /// <para>
    /// There is deliberately no "all rooms" overload. A method that generated every
    /// room's contents would let a caller ask for a room nobody has entered, which is the
    /// leak knowledge.md rule 11 forbids; <c>SiteLazyContentTests</c> asserts over the
    /// compiled public surface that no such member exists.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The room is not part of this site.</exception>
    public SiteRoomDetail ObserveRoom(SiteRoomId roomId)
    {
        if (Find(roomId) is null)
        {
            throw new InvalidOperationException(
                $"Room {roomId} is not part of this site, so it has no contents to observe.");
        }

        if (_details.TryGetValue(roomId, out SiteRoomDetail? cached))
            return cached;

        SiteRoomDetail detail = SiteRoomDetailGenerator.Generate(this, roomId);
        _observed.Add(roomId);
        _details[roomId] = detail;
        return detail;
    }

    /// <summary>True when this room has been observed.</summary>
    public bool IsRoomObserved(SiteRoomId roomId) => _observed.Contains(roomId);

    /// <summary>
    /// The contents of an already-observed room, or null.
    /// </summary>
    /// <remarks>
    /// The read side of observation, for callers checking what they already know. It
    /// cannot produce the contents of an unobserved room, which is the entire difference
    /// between it and <see cref="ObserveRoom"/>.
    /// </remarks>
    public SiteRoomDetail? ObservedRoom(SiteRoomId roomId)
        => _details.TryGetValue(roomId, out SiteRoomDetail? detail) ? detail : null;

    /// <summary>Rooms observed so far, in ascending id order.</summary>
    public IReadOnlyList<SiteRoomId> ObservedRoomIds
    {
        get
        {
            var ids = new List<SiteRoomId>(_observed);
            ids.Sort();
            return ids;
        }
    }

    /// <summary>How many rooms have been observed.</summary>
    public int ObservedRoomCount => _observed.Count;

    /// <summary>
    /// Records a room as observed without generating its contents.
    /// </summary>
    /// <remarks>
    /// Load-time only. A save that recorded "this room was searched" has to come back
    /// with the room still marked observed, but the contents are not in the save: they
    /// regenerate from the seed, identically. So there is nothing to load but the flag.
    /// </remarks>
    internal void MarkRoomObserved(SiteRoomId roomId) => _observed.Add(roomId);

    // ---- save and load -------------------------------------------------------

    /// <summary>Captures everything needed to rebuild this site exactly.</summary>
    /// <remarks>
    /// The seed, not the building. That is the payoff of the generator being a pure
    /// function of <see cref="MapSeed"/>: the save is five numbers and a list of room
    /// ids, and rebuilding is a re-run rather than a deserialization of several thousand
    /// fields that could quietly disagree with what the generator would produce.
    /// </remarks>
    public SiteLayoutSaveData Save() => new(
        SiteTemplateId,
        Tier,
        MissionId,
        WorldSeed,
        MapSeed,
        ObservedRoomIds);

    /// <summary>Rebuilds a site saved by <see cref="Save"/>.</summary>
    public static SiteLayout Load(SiteLayoutSaveData data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        SiteLayout layout = SiteGenerator.Generate(
            data.SiteTemplateId,
            data.Tier,
            data.MissionId,
            data.WorldSeed,
            data.MapSeed);

        foreach (SiteRoomId roomId in data.ObservedRoomIds)
        {
            // The save names the template, so a well-formed save can only name rooms this
            // site has. A save that names anything else was written against a different
            // building — accepting it would leave the observed set holding ids nothing can
            // ever resolve, and the "this room is empty because nobody has been in it"
            // answer would become unreachable for it forever.
            if (layout.Find(roomId) is null)
                throw new InvalidOperationException(
                    $"Save data names room {roomId.Value} which site template {data.SiteTemplateId} does not contain.");

            layout.MarkRoomObserved(roomId);
        }

        return layout;
    }

    // ---- integrity -----------------------------------------------------------

    /// <summary>
    /// Checks every invariant the stage-4a brief promises, and names the first failure.
    /// </summary>
    /// <remarks>
    /// Exhaustive rather than cheap, and on purpose. This runs once inside generation —
    /// so a generator bug fails loudly where it happened instead of handing a player an
    /// unwinnable building — and again across every site in the distribution test. The
    /// problem is a ReasonCode key, not prose (rule 4); the id it names is what a bug
    /// report needs.
    /// </remarks>
    public bool Validate(out string problem)
    {
        problem = string.Empty;

        if (MainSite.Floors.Count == 0)
        {
            problem = "SiteHasNoFloors";
            return false;
        }

        string? geometry = ValidateGeometry(MainSite);
        if (geometry is not null)
        {
            problem = geometry;
            return false;
        }

        if (Find(EntranceRoomId) is null)
        {
            problem = $"SiteHasNoEntrance:{EntranceRoomId}";
            return false;
        }

        if (Find(ObjectiveRoomId) is null)
        {
            problem = $"SiteHasNoObjective:{ObjectiveRoomId}";
            return false;
        }

        string? roles = ValidateRoles();
        if (roles is not null)
        {
            problem = roles;
            return false;
        }

        string? connections = ValidateConnections(MainSite);
        if (connections is not null)
        {
            problem = connections;
            return false;
        }

        IReadOnlyList<SiteRoomId> orphans = RoomsUnreachableFromEntrance();
        if (orphans.Count > 0)
        {
            problem = $"SiteRoomOrphaned:{orphans[0]}";
            return false;
        }

        IReadOnlyList<int> strandedFloors = FloorsWithoutVerticalConnection();
        if (strandedFloors.Count > 0)
        {
            problem = $"SiteFloorHasNoVerticalConnection:{strandedFloors[0]}";
            return false;
        }

        if (!HasAlternateRouteToObjective)
        {
            problem = "SiteHasNoAlternateRouteToObjective";
            return false;
        }

        if (ReachableExtractionPoints().Count == 0)
        {
            problem = "SiteObjectiveCannotReachExtraction";
            return false;
        }

        IReadOnlyList<SiteGuardId> broken = GuardsWithUnwalkableRoutes();

        // Ids must be unique across the whole site, not per region. A forward post that
        // numbered its guards from one produced the same ids as the building it was
        // attached to, and every lookup keyed by guard id then resolved to whichever
        // guard the search reached first — a wrong patrol report, or the wrong guard
        // standing watch, with nothing in the layout to show for it.
        var guardIds = new HashSet<SiteGuardId>();
        var civilianIds = new HashSet<SiteCivilianId>();

        foreach (SiteGuard guard in Guards)
        {
            if (!guardIds.Add(guard.Id))
            {
                problem = $"SiteGuardIdDuplicate:{guard.Id}";
                return false;
            }

            if (Find(guard.HomeRoomId) is null)
            {
                problem = $"SiteGuardHomeRoomMissing:{guard.Id}";
                return false;
            }
        }

        foreach (SiteCivilian civilian in Civilians)
        {
            if (!civilianIds.Add(civilian.Id))
            {
                problem = $"SiteCivilianIdDuplicate:{civilian.Id}";
                return false;
            }

            if (Find(civilian.RoomId) is null)
            {
                problem = $"SiteCivilianRoomMissing:{civilian.Id}";
                return false;
            }
        }
        if (broken.Count > 0)
        {
            problem = $"SiteGuardRouteNotTraversable:{broken[0]}";
            return false;
        }

        string? travel = ValidateTravel();
        if (travel is not null)
        {
            problem = travel;
            return false;
        }

        if (ForwardPost is not null)
        {
            string? postGeometry = ValidateGeometry(
                ForwardPost, postFloorIndexOffset());
            if (postGeometry is not null)
            {
                problem = postGeometry;
                return false;
            }
        }

        return true;
    }

    /// <param name="region">The region to check.</param>
    /// <param name="floorIndexOffset">
    /// The floor index this region's first floor sits at. Zero for the building, whose
    /// floors run 0..n-1 from the ground up. Non-zero for the forward post, which is
    /// indexed past the building so that a <see cref="TacticalPosition"/> says which
    /// region it is in — without that, floor 0 x 500 is a room in both and
    /// <see cref="RoomContaining(TacticalPosition)"/> cannot answer.
    /// </param>
    /// <remarks>
    /// Everything else about a floor is checked the same way in either region: rooms tile
    /// it from zero, do not overlap, and do not hang off the end. A region's geometry is
    /// geometry whatever it is attached to.
    /// </remarks>
    private string? ValidateGeometry(SiteRegion region, int floorIndexOffset = 0)
    {
        var seenIds = new HashSet<int>();

        for (int index = 0; index < region.Floors.Count; index++)
        {
            int expected = floorIndexOffset + index;
            SiteFloor floor = region.Floors[index];

            if (floor.Index != expected)
                return $"SiteFloorIndexMismatch:{expected}:{floor.Index}";

            // Ground is a property of the first floor a region owns, not of floor index
            // zero: the post's first floor is index 4 and is still the ground it stands on.
            if (index == 0 && floor.Kind != SiteFloorKind.Ground)
                return $"SiteGroundFloorHasWrongKind:{floor.Kind}";

            if (index > 0 && floor.Kind == SiteFloorKind.Ground)
                return "SiteUpperFloorIsGround";

            Fixed32 cursor = Fixed32.Zero;

            for (int roomIndex = 0; roomIndex < floor.Rooms.Count; roomIndex++)
            {
                SiteRoom room = floor.Rooms[roomIndex];

                if (!room.Id.IsValid)
                    return $"SiteRoomIdInvalid:F{index}R{roomIndex}";

                if (!seenIds.Add(room.Id.Value))
                    return $"SiteRoomIdDuplicate:{room.Id}";

                if (room.FloorIndex != expected)
                    return $"SiteRoomOnWrongFloor:{room.Id}:{room.FloorIndex}";

                // Half-open intervals, ordered left to right and touching exactly. A gap
                // is legal — a party wall has thickness — an overlap is not, and neither
                // is an interval that runs backwards or hangs off the end of its floor.
                if (room.EndX <= room.StartX)
                    return $"SiteRoomEmpty:{room.Id}";

                if (room.StartX < cursor)
                    return $"SiteRoomOverlaps:{room.Id}";

                if (room.EndX > floor.Span)
                    return $"SiteRoomPastFloorEnd:{room.Id}";

                string? width = IsNarrowerThanTemplate(room);
                if (width is not null)
                    return width;

                cursor = room.EndX;
            }
        }

        return null;
    }

    /// <summary>
    /// The floor index the forward post's first floor sits at.
    /// </summary>
    /// <remarks>
    /// Past every floor the building owns, so the two regions cannot claim the same
    /// <see cref="TacticalPosition"/>. Read off the building rather than stored, so it
    /// cannot disagree with <see cref="MainSite"/>.
    /// </remarks>
    private int postFloorIndexOffset() => MainSite.Floors.Count;

    /// <summary>
    /// Checks a room is at least as wide as its template demands.
    /// </summary>
    /// <remarks>
    /// The invariant the brief states as "no room is narrower than its template
    /// minimum". A room narrower than its own minimum is a room whose contents do not
    /// fit — a desk that needs four metres in a two-metre office — so this is a
    /// correctness property, not a cosmetic one.
    /// </remarks>
    private static string? IsNarrowerThanTemplate(SiteRoom room)
    {
        TacticalRoomTemplate? template = SimulationRules.TacticalRoomTemplateFor(room.RoomTemplateId);
        if (template is null)
            return null;

        int minimumCm = Fixed32.FromMetres(template.WidthMin).Raw;
        return room.Span.Raw < minimumCm
            ? $"SiteRoomNarrowerThanTemplate:{room.Id}:{room.Span.Raw}<{minimumCm}"
            : null;
    }

    private string? ValidateRoles()
    {
        int entrances = 0, objectives = 0, extractions = 0;

        foreach (SiteRoom room in Rooms)
        {
            // Flag tests rather than a switch: a room can carry more than one role, and a
            // switch on the combined value would match none of the cases.
            if (room.Role.HasFlag(SiteRoomRole.Entrance))
            {
                entrances++;
                if (room.Id != EntranceRoomId)
                    return $"SiteEntranceRoleMismatch:{room.Id}";
            }

            if (room.Role.HasFlag(SiteRoomRole.Objective))
            {
                objectives++;
                if (room.Id != ObjectiveRoomId)
                    return $"SiteObjectiveRoleMismatch:{room.Id}";
            }

            if (room.Role.HasFlag(SiteRoomRole.Extraction))
            {
                extractions++;
                if (!ExtractionRoomIds.Contains(room.Id))
                    return $"SiteExtractionRoleMismatch:{room.Id}";
            }
        }

        if (entrances != 1)
            return $"SiteEntranceCount:{entrances}";

        if (objectives != 1)
            return $"SiteObjectiveCount:{objectives}";

        if (extractions < 1)
            return $"SiteExtractionCount:{extractions}";

        foreach (SiteRoomId id in ExtractionRoomIds)
        {
            SiteRoom? room = Find(id);
            if (room is null)
                return $"SiteExtractionRoomMissing:{id}";

            if (!room.Role.HasFlag(SiteRoomRole.Extraction))
                return $"SiteExtractionRoomNotMarked:{id}";
        }

        return null;
    }

    private string? ValidateConnections(SiteRegion region)
    {
        var seenPairs = new HashSet<string>();

        foreach (SiteConnection connection in region.Connections)
        {
            if (!connection.Id.IsValid)
                return "SiteConnectionIdInvalid";

            SiteRoom? a = Find(connection.RoomA);
            SiteRoom? b = Find(connection.RoomB);

            if (a is null || b is null)
                return $"SiteConnectionRoomMissing:{connection.Id}";

            if (a.Id == b.Id)
                return $"SiteConnectionSelfLoop:{connection.Id}";

            // At most one floor per hop. A two-floor span would let a route skip the
            // vertical cut, and a "vertical" connection inside one floor is a
            // contradiction rather than a stair.
            if (Math.Abs(connection.FloorIndexB - connection.FloorIndexA) > 1)
                return $"SiteConnectionSpansTooManyFloors:{connection.Id}";

            if (connection.FloorIndexA != a.FloorIndex || connection.FloorIndexB != b.FloorIndex)
                return $"SiteConnectionFloorMismatch:{connection.Id}";

            if (connection.IsVertical)
            {
                // The foot and the landing both have to be inside the rooms they belong
                // to, or the agent arrives inside a wall.
                if (!a.Contains(connection.X))
                    return $"SiteConnectionXOutsideRoom:{connection.Id}";

                if (!b.Contains(connection.UpperX))
                    return $"SiteConnectionUpperXOutsideRoom:{connection.Id}";
            }
            else
            {
                // A horizontal connection sits in the wall the two rooms share, which
                // only exists if they actually touch. Taking the *maximum* of the two
                // right edges — the first version of this check — is wrong: for adjacent
                // rooms the wall is the left room's EndX, which is the *smaller* of the
                // two, so the check rejected every correctly-placed door in the game.
                Fixed32? wall = null;

                if (a.EndX == b.StartX)
                    wall = a.EndX;
                else if (b.EndX == a.StartX)
                    wall = b.EndX;

                if (wall is null)
                    return $"SiteHorizontalConnectionRoomsNotAdjacent:{connection.Id}";

                if (connection.X != wall.Value)
                    return $"SiteHorizontalConnectionNotOnSharedWall:{connection.Id}";
            }

            // Two connections of the same kind between the same rooms is a data bug
            // reading as two doors. Two of different kinds — a door and a window between
            // an office and a corridor — are ordinary, and stay legal.
            string key = connection.Kind + "|" + PairKey(connection.RoomA, connection.RoomB);
            if (!seenPairs.Add(key))
                return $"SiteDuplicateConnection:{connection.Id}";
        }

        return null;
    }

    /// <summary>
    /// The forward post must be connected to the building by exactly one hop, and the hop
    /// must cost steps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hop is a <see cref="SiteConnection"/> rather than a separate edge type, so this
    /// is checking the same thing the old travel-edge check checked — one way in, a
    /// positive cost, both rooms real — against the connection that now carries it.
    /// </para>
    /// <para>
    /// <b>Both directions are required, not one.</b> A post with no connection is the bug
    /// this whole rule exists to catch: a Handler ordered to hold it would stand in the
    /// building and no test would notice, because every query about the post still
    /// answers correctly — it simply answers about somewhere the team cannot walk to.
    /// </para>
    /// </remarks>
    private string? ValidateTravel()
    {
        if (ForwardPost is null)
            return null;

        SiteRoom? arrival = Find(ForwardPostRoomId);

        if (arrival is null)
            return "SiteForwardPostRoomMissing";

        int crossings = 0;

        foreach (SiteRegion region in Regions)
        {
            foreach (SiteConnection connection in region.Connections)
            {
                if (connection.RoomA != ForwardPostRoomId && connection.RoomB != ForwardPostRoomId)
                    continue;

                // The post's own rooms are chained together like any other floor's, so
                // most of the connections touching the post room are internal. Only a
                // connection to the *building* is a crossing — counting the internal
                // ones would report two hops for a one-room post.
                SiteRoomId other = connection.RoomA == ForwardPostRoomId
                    ? connection.RoomB
                    : connection.RoomA;

                if (IsInPost(other))
                    continue;

                crossings++;

                if (connection.TraverseSteps < 1)
                    return $"SiteForwardPostHopHasNoStepCost:{connection.Id}";
            }
        }

        return crossings == 1 ? null : $"SiteForwardPostHopCount:{crossings}";
    }

    /// <summary>
    /// True when a room belongs to the forward post region.
    /// </summary>
    /// <remarks>
    /// Public because "is this room part of the building" is not only a validation
    /// question. The mission-end test has to know whether a member is still inside, and
    /// the honest answer for somebody standing in the post is no — the post is joined to
    /// the building by one stairwell and is otherwise outside it.
    /// </remarks>
    public bool IsInPost(SiteRoomId roomId)
    {
        if (ForwardPost is null)
            return false;

        foreach (SiteRoom room in ForwardPost.AllRooms)
        {
            if (room.Id == roomId)
                return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"Site {SiteTemplateId} t{Tier} seed={MapSeed} floors={MainSite.Floors.Count} rooms={MainSite.RoomCount}";
}

/// <summary>
/// Everything needed to rebuild a site: the seed, plus which rooms were observed.
/// </summary>
/// <param name="SiteTemplateId">Foreign key into <c>site_template</c>.</param>
/// <param name="Tier">The site's tier.</param>
/// <param name="MissionId">Mission the site belongs to.</param>
/// <param name="WorldSeed">The world seed the map seed was derived from.</param>
/// <param name="MapSeed">The seed the building was generated from.</param>
/// <param name="ObservedRoomIds">Rooms whose contents had been generated when saved.</param>
public sealed record SiteLayoutSaveData(
    int SiteTemplateId,
    int Tier,
    int MissionId,
    ulong WorldSeed,
    ulong MapSeed,
    IReadOnlyList<SiteRoomId> ObservedRoomIds) : IEquatable<SiteLayoutSaveData>
{
    /// <summary>Value equality, room list included.</summary>
    /// <remarks>
    /// A record's generated equality compares a list-typed member by <em>reference</em>,
    /// so the obvious implementation — let the compiler write it — produces a type where
    /// two saves of the same building, taken at the same tick with the same rooms
    /// visited, are unequal because they hold two different lists of the same rooms.
    /// Save data is compared constantly: a determinism test wants to say "reloading twice
    /// gives the same save", and a migration wants to know whether a save has actually
    /// changed. So the list is compared element by element.
    /// </remarks>
    public bool Equals(SiteLayoutSaveData? other)
    {
        if (ReferenceEquals(this, other))
            return true;

        if (other is null)
            return false;

        return SiteTemplateId == other.SiteTemplateId
               && Tier == other.Tier
               && MissionId == other.MissionId
               && WorldSeed == other.WorldSeed
               && MapSeed == other.MapSeed
               && ObservedRoomIds.SequenceEqual(other.ObservedRoomIds);
    }

    /// <summary>Hash over every field, so equal saves land in the same bucket.</summary>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = SiteTemplateId;
            hash = (hash * 397) ^ Tier;
            hash = (hash * 397) ^ MissionId;
            hash = (hash * 397) ^ WorldSeed.GetHashCode();
            hash = (hash * 397) ^ MapSeed.GetHashCode();

            foreach (SiteRoomId roomId in ObservedRoomIds)
                hash = (hash * 397) ^ roomId.GetHashCode();

            return hash;
        }
    }
}