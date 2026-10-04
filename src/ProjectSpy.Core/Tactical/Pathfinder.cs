using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// What a caller is willing to cross, and what crossing it will cost.
/// </summary>
/// <remarks>
/// <para>
/// A pathfinder that answers "can I get there" without saying what the answer costs is
/// useless for a game whose currency is steps, and one that refuses to route through a
/// locked door is useless for a game whose answer to a locked door is "pick it". The
/// options carry both halves: which kinds of connection are acceptable, and how many
/// extra steps each obstacle adds.
/// </para>
/// <para>
/// The extra-step numbers default to the cost of the action that clears each obstacle,
/// read from <c>tactical_action</c> — picking, forcing and clearing — so that "route
/// there and open the door" and "route there" are the same computation with one flag
/// rather than two different code paths that could disagree about where the door is.
/// </para>
/// </remarks>
public sealed record PathOptions
{
    /// <summary>
    /// Whether vents, windows and holes may be used. False for NPCs, who cannot fit.
    /// </summary>
    public bool AllowCrawlable { get; init; } = true;

    /// <summary>
    /// Whether a locked or barricaded connection may be part of a plan.
    /// </summary>
    /// <remarks>
    /// True when the caller intends to clear it and is asking what the whole thing
    /// costs. False when the question really is "can I walk that way right now", which
    /// is what a guard deciding whether to head for the extraction door needs.
    /// </remarks>
    public bool AllowLocked { get; init; } = true;

    /// <summary>Extra steps for a connection that is merely shut.</summary>
    public int ClosedDoorSteps { get; init; } = 10;

    /// <summary>Extra steps to pick a lock.</summary>
    public int LockedSteps { get; init; } = 15;

    /// <summary>Extra steps to force a barricade or a locked door.</summary>
    public int ForceSteps { get; init; } = 12;

    /// <summary>
    /// How a guard is routed: no crawlspaces, and locked doors still cost.
    /// </summary>
    /// <remarks>
    /// <b>Guards, and only guards.</b> A vent is a vent: a guard cannot fit through one,
    /// which is what makes vents a squad-only route and what stops every patrol on a
    /// site collapsing onto the same corridor. Nobody else should be using this, and
    /// using it for a squad member is not a small mistake: it removes the vent, the
    /// window and the hole from the graph entirely, so a building whose rooms are joined
    /// only by crawlspaces becomes a building the squad cannot move in.
    /// </remarks>
    public static PathOptions ForNpc => new() { AllowCrawlable = false };

    /// <summary>
    /// How a squad member is routed: crawlspaces included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default, named so that the choice is written down at the call site. A person
    /// is not a guard: they go through the vent, the window and the hole, which is the
    /// whole reason a site has them.
    /// </para>
    /// <para>
    /// This exists because the squad was being routed with
    /// <see cref="ForNpc"/>'s crawlspace ban by three separate call sites in
    /// <c>RoleBehaviours</c> — while a fourth call site in the same file, one screen
    /// away, carried a comment saying the ban was "deliberately not applied here". The
    /// code and its own documentation had disagreed, and the measurable result was that
    /// 12% of generated rooms could not be routed to at all: a member standing in one of
    /// them issued no order, forever, because the route it was looking for did not exist
    /// in the graph it was given.
    /// </para>
    /// </remarks>
    public static PathOptions ForAgent => new() { AllowCrawlable = true };
}

/// <summary>
/// A route through the building: the connections to cross, what it costs, and what has
/// to be dealt with on the way.
/// </summary>
/// <param name="Connections">
/// The connections in order. Empty when the two rooms are the same room, which is a
/// success and not a failure — the caller can tell the two apart by
/// <paramref name="ReachedTarget"/>.
/// </param>
/// <param name="TotalSteps">Steps the whole route costs, doors included.</param>
/// <param name="ReachedTarget">False when no route exists at all.</param>
public readonly record struct TacticalPath(
    IReadOnlyList<SiteConnectionId> Connections,
    int TotalSteps,
    bool ReachedTarget)
{
    /// <summary>A route that goes nowhere, because there is no route.</summary>
    public static TacticalPath Unreachable { get; } = new(Array.Empty<SiteConnectionId>(), 0, false);

    /// <summary>Number of connections the route crosses.</summary>
    public int Hops => Connections.Count;

    /// <inheritdoc/>
    public override string ToString()
        => ReachedTarget
            ? $"{Hops} hops, {TotalSteps} steps"
            : "unreachable";
}

/// <summary>
/// Fixed-point route finding over the room and connection graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism is the whole design constraint here.</b> Rule 6 requires that the same
/// building and the same door states always produce the same route, because the route is
/// what a recorded replay replays. Three things make that true and all three are
/// deliberate:
/// </para>
/// <list type="number">
/// <item>
/// <b>Integer costs throughout.</b> Walking costs centimetres converted to steps by a
/// rounding rule that is stated here rather than left to the language, so that a
/// platform whose integer division rounds differently cannot pick a different route.
/// </item>
/// <item>
/// <b>Ties break on room id, never on iteration order.</b> Two rooms reachable at
/// identical cost must always resolve the same way. A <c>Dictionary</c> or a
/// <c>HashSet</c> enumerates in an order that depends on insertion and hashing, and
/// insertion order depends on the order the generator happened to run, so relying on it
/// would make the route a function of history rather than of state.
/// </item>
/// <item>
/// <b>Selection scans the whole frontier for the minimum.</b> Not a priority queue,
/// which would need a tie-break rule of its own to stay stable. A building has tens of
/// rooms, not thousands, so the linear scan costs nothing worth saving and removes a
/// whole class of ordering bug.
/// </item>
/// </list>
/// </remarks>
public static class Pathfinder
{
    /// <summary>
    /// Centimetres walked per step when costing a route on foot.
    /// </summary>
    /// <remarks>
    /// The walking speed from <see cref="PostureRules"/>, and deliberately the walking
    /// speed: a route is a plan, and a plan is made while walking. Costing it at a
    /// sprint would make every route look like a panic and costing it at a crouch would
    /// make every route look like a siege.
    /// </remarks>
    private static readonly int WalkCmPerStep = PostureRules.WalkCmPerStep;

    /// <summary>
    /// Finds a route between two rooms.
    /// </summary>
    /// <param name="layout">The building.</param>
    /// <param name="doorStates">Current door states, or null for "all as generated".</param>
    /// <param name="from">Where the entity is.</param>
    /// <param name="to">Where it is trying to get to.</param>
    /// <param name="options">What it is willing to cross.</param>
    public static TacticalPath FindRoute(
        SiteLayout layout,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState>? doorStates,
        SiteRoomId from,
        SiteRoomId to,
        PathOptions? options = null)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        PathOptions rules = options ?? new PathOptions();

        if (from == to)
            return new TacticalPath(Array.Empty<SiteConnectionId>(), 0, true);

        if (layout.Find(from) is null || layout.Find(to) is null)
            return TacticalPath.Unreachable;

        // `best` is keyed by room and holds the cheapest known cost to arrive there.
        // `previous` is keyed by the room being arrived at, holding the room and the
        // connection it came through — so reconstruction walks back from the target
        // without needing the forward tree at all.
        var best = new Dictionary<SiteRoomId, int> { [from] = 0 };
        var previous = new Dictionary<SiteRoomId, (SiteRoomId From, SiteConnectionId Via)>();
        var settled = new HashSet<SiteRoomId>();

        while (true)
        {
            SiteRoomId? next = CheapestUnsettled(best, settled);
            if (next is null)
                break;

            settled.Add(next.Value);

            if (next.Value == to)
                break;

            int arrivedAt = best[next.Value];

            // Sorted rather than enumerated: `ConnectionsAt` is in generation order,
            // which is deterministic, but generation order is a property of the
            // building rather than of the two rooms being compared, and relying on it
            // here would make the tie-break depend on which room we happened to expand
            // first.
            foreach (SiteConnection connection in SortedConnections(layout, next.Value))
            {
                SiteRoomId across = connection.Other(next.Value);

                if (settled.Contains(across))
                    continue;

                if (!IsPassable(connection, layout, doorStates, rules))
                    continue;

                int stepCost = ConnectionSteps(connection, layout, doorStates, rules)
                               + WalkSteps(layout, across, LandingX(connection, across));

                int candidate = arrivedAt + stepCost;

                // Strictly less, so an equal-cost alternative never displaces a route
                // already found. Combined with scanning for the minimum by room id,
                // that makes the result a function of the graph alone.
                if (best.TryGetValue(across, out int existing) && existing <= candidate)
                    continue;

                best[across] = candidate;
                previous[across] = (next.Value, connection.Id);
            }
        }

        if (!best.TryGetValue(to, out int total))
            return TacticalPath.Unreachable;

        return new TacticalPath(Rebuild(previous, from, to), total, true);
    }

    /// <summary>True when one room can be walked to from another under these options.</summary>
    public static bool CanReach(
        SiteLayout layout,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState>? doorStates,
        SiteRoomId from,
        SiteRoomId to,
        PathOptions? options = null)
        => FindRoute(layout, doorStates, from, to, options).ReachedTarget;

    /// <summary>
    /// Finds a route from an exact point, charging for the walk to the first door.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="FindRoute"/> because standing in a doorway is not the
    /// same as standing in the middle of the room, and a route that ignored the opening
    /// walk would claim a team at the far end of a corridor could cross a room for free.
    /// </remarks>
    public static TacticalPath FindRouteFrom(
        SiteLayout layout,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState>? doorStates,
        TacticalPosition start,
        SiteRoomId to,
        PathOptions? options = null)
    {
        SiteRoom? startRoom = layout.RoomContaining(start);
        if (startRoom is null)
            return TacticalPath.Unreachable;

        TacticalPath tail = FindRoute(layout, doorStates, startRoom.Id, to, options);
        if (!tail.ReachedTarget || tail.Hops == 0)
            return tail;

        // The first hop leaves through a connection at a known x on the start room's
        // floor. Charging the walk out to it separately keeps the room-to-room search
        // itself pure — it never needs to know where inside a room the entity stood.
        SiteConnectionId firstId = tail.Connections[0];
        SiteConnection? first = null;

        foreach (SiteConnection candidate in layout.ConnectionsAt(startRoom.Id))
        {
            if (candidate.Id == firstId)
            {
                first = candidate;
                break;
            }
        }

        if (first is null)
            return TacticalPath.Unreachable;

        int openingWalk = WalkSteps(layout, startRoom.Id, start.X, first.X);
        return new TacticalPath(tail.Connections, tail.TotalSteps + openingWalk, true);
    }

    /// <summary>
    /// The room-to-room minimum cost of the connection itself, doors included.
    /// </summary>
    /// <remarks>
    /// Split out from the search so that "how expensive is this door" has one answer.
    /// The caller is usually the movement system deciding whether to spend the steps,
    /// and it must agree with what the pathfinder assumed or a plan will be costed
    /// differently from the walk that follows it.
    /// </remarks>
    public static int ConnectionSteps(
        SiteConnection connection,
        SiteLayout layout,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState>? doorStates,
        PathOptions options)
    {
        int cost = connection.TraverseSteps;

        if (doorStates is null || !doorStates.TryGetValue(connection.Id, out ConnectionState state))
        {
            // No live door state: treat a locked door as shut and openable, which is
            // what a plan drawn from intel should assume.
            return cost;
        }

        cost += state switch
        {
            ConnectionState.Open => 0,
            ConnectionState.Closed => options.ClosedDoorSteps,
            ConnectionState.Locked => options.LockedSteps,
            ConnectionState.Barricaded => options.ForceSteps,
            _ => 0,
        };

        return cost;
    }

    /// <summary>
    /// Whether a connection may be part of a route at all.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnectionState.Blocked"/> is refused outright and unconditionally: a
    /// collapsed stairwell is not a door and no action opens it, so a pathfinder that
    /// priced it would plan a route through a wall.
    /// </remarks>
    private static bool IsPassable(
        SiteConnection connection,
        SiteLayout layout,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState>? doorStates,
        PathOptions options)
    {
        if (!options.AllowCrawlable && !connection.UsableByNpc)
            return false;

        if (doorStates is not null && doorStates.TryGetValue(connection.Id, out ConnectionState state))
        {
            if (state == ConnectionState.Blocked)
                return false;

            if (!options.AllowLocked && state is ConnectionState.Locked or ConnectionState.Barricaded)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Steps to walk from a room's edge to a point inside it, rounded up.
    /// </summary>
    /// <remarks>
    /// Rounds up because a route that rounded down would promise a walk the entity
    /// cannot finish inside the steps it cost — and the shortfall would then be paid for
    /// out of an action the player never chose.
    /// </remarks>
    private static int WalkSteps(SiteLayout layout, SiteRoomId roomId, Fixed32 targetX)
    {
        SiteRoom? room = layout.Find(roomId);
        if (room is null)
            return 0;

        Fixed32 distance = Fixed32.Distance(room.StartX, targetX);
        if (distance.Raw <= 0)
            return 0;

        // Integer ceiling division, widened to long so a pathological layout cannot
        // overflow the add.
        return (int)(((long)distance.Raw + WalkCmPerStep - 1) / WalkCmPerStep);
    }

    /// <summary>Steps to walk from a point inside a room to a point inside it.</summary>
    private static int WalkSteps(SiteLayout layout, SiteRoomId roomId, Fixed32 fromX, Fixed32 toX)
    {
        Fixed32 distance = Fixed32.Distance(fromX, toX);
        return distance.Raw <= 0 ? 0 : (int)(((long)distance.Raw + WalkCmPerStep - 1) / WalkCmPerStep);
    }

    /// <summary>Where a connection lands in the room being arrived at.</summary>
    private static Fixed32 LandingX(SiteConnection connection, SiteRoomId destination)
        => destination == connection.RoomA ? connection.X : connection.UpperX;

    /// <summary>
    /// The unsettled room with the cheapest known cost, ties broken by room id.
    /// </summary>
    /// <remarks>
    /// The tie-break is the determinism guarantee, and it is written as an explicit
    /// comparison rather than left to whichever room the scan reached first. Without it
    /// two equally-cheap rooms resolve differently depending on which one the previous
    /// expansion happened to insert first.
    /// </remarks>
    private static SiteRoomId? CheapestUnsettled(
        Dictionary<SiteRoomId, int> best,
        HashSet<SiteRoomId> settled)
    {
        SiteRoomId? chosen = null;
        int chosenCost = 0;

        foreach (KeyValuePair<SiteRoomId, int> entry in best)
        {
            if (settled.Contains(entry.Key))
                continue;

            if (chosen is null || entry.Value < chosenCost ||
                (entry.Value == chosenCost && entry.Key.Value < chosen.Value.Value))
            {
                chosen = entry.Key;
                chosenCost = entry.Value;
            }
        }

        return chosen;
    }

    /// <summary>
    /// A room's connections in ascending connection-id order.
    /// </summary>
    /// <remarks>
    /// Sorted explicitly rather than trusting the layout's order. The expansion order
    /// of neighbours is what decides which of two equal-cost routes gets recorded, so
    /// it has to be a function of ids and not of however the generator happened to walk
    /// the building.
    /// </remarks>
    private static IEnumerable<SiteConnection> SortedConnections(SiteLayout layout, SiteRoomId room)
    {
        List<SiteConnection> connections = new(layout.ConnectionsAt(room));
        connections.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return connections;
    }

    /// <summary>Walks the predecessor chain back from the target to the start.</summary>
    private static IReadOnlyList<SiteConnectionId> Rebuild(
        Dictionary<SiteRoomId, (SiteRoomId From, SiteConnectionId Via)> previous,
        SiteRoomId from,
        SiteRoomId to)
    {
        var route = new List<SiteConnectionId>();
        SiteRoomId cursor = to;

        while (previous.TryGetValue(cursor, out (SiteRoomId From, SiteConnectionId Via) step) && step.From != cursor)
        {
            route.Add(step.Via);
            cursor = step.From;
        }

        route.Reverse();
        return route;
    }
}
