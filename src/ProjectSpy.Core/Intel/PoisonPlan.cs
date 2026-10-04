using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core;

/// <summary>
/// The lies a discovered operation tells, and the guarantee it is not allowed to break.
/// </summary>
/// <remarks>
/// <para>
/// A poisoned operation keeps reporting plausible, specific, wrong facts. The player is
/// never told, because an announced poison removes the decision the system exists to
/// create. What the system owes the player instead is that a poisoned report is
/// <em>survivable</em>: it costs time, routes and nerve, never the mission.
/// </para>
/// <para>
/// <b>The topology is never touched.</b> Poisoning rewrites what is said about doors,
/// patrols and the objective — never which rooms join to which. A building the player
/// cannot walk is not a hard mission, it is a broken one, and it is the kind of broken
/// that looks like the game cheating.
/// </para>
/// <para>
/// <b>One route is always described truthfully.</b> Once the lie about the objective is
/// settled, the plan walks the <em>real</em> route from the entrance to whichever room it
/// now claims, and refuses to lie about any door on that route. So whatever else the
/// report gets wrong, the most direct way to the room it points at is described exactly
/// as it is. If a door on that route is locked, the report says locked, and the player can
/// see they have a problem and deal with it.
/// </para>
/// <para>
/// That is deliberately weaker than "there is a route with no locked doors on it", which
/// would be nicer to claim and is not true. The generator guarantees an alternate route,
/// not an alternate unlocked one; a site may legitimately have every short path to the
/// objective behind a lock that the player can pick, gadget or go round. Promising an
/// unlocked path would be promising something the building never agreed to, and the gap
/// would show up in play as the one mission where a poisoned report is fatal.
/// </para>
/// <para>
/// The guarantee is structural rather than statistical, which is what lets
/// <c>IntelPoisonTests</c> sweep thousands of poisoned reports and assert on every one: no
/// seed can produce a report that misdescribes the route to its own claimed objective, so
/// the test cannot be flaky and cannot be merely probable.
/// </para>
/// <para>
/// <b>All the lies are drawn before any of them is needed.</b> The objective claim has to be
/// settled before the protected route is known, and the protected route before any door
/// can be chosen. So the plan settles all three before a single entry is written, rather
/// than deciding each lie at the moment it is read — deciding as it went meant the first
/// version produced reports that lied about the objective and about nothing else, which
/// looked like it worked right up until you counted.
/// </para>
/// </remarks>
internal sealed class PoisonPlan
{
    private readonly SiteLayout _layout;
    private readonly IRng _rng;
    private readonly bool _active;

    private readonly HashSet<SiteConnectionId> _liedLocks = new();
    private readonly Dictionary<SiteGuardId, IReadOnlyList<SiteRoomId>> _liedPatrols = new();

    private SiteRoomId _objective;
    private bool _objectiveResolved;
    private bool _settled;

    public PoisonPlan(SiteLayout layout, IRng rng, bool active)
    {
        _layout = layout;
        _rng = rng;
        _active = active;
        _objective = layout.ObjectiveRoomId;
    }

    /// <summary>The room the report claims holds the objective.</summary>
    /// <remarks>
    /// Settled on first read rather than in the constructor, because the choice needs
    /// the intel percentage to gate whether the objective is mentioned at all. Only
    /// called when the band reveals it.
    /// </remarks>
    public SiteRoomId PeekObjective()
    {
        if (!_objectiveResolved)
        {
            _objective = _active ? ChooseWrongObjective() : _layout.ObjectiveRoomId;
            _objectiveResolved = true;
        }

        return _objective;
    }

    /// <summary>The room the finished report claims holds the objective.</summary>
    public SiteRoomId ClaimedObjective
    {
        get
        {
            PeekObjective();
            return _objective;
        }
    }

    /// <summary>True when the report claims this connection is locked the wrong way round.</summary>
    public bool LiesAboutLock(SiteConnectionId connectionId) => _liedLocks.Contains(connectionId);

    /// <summary>True when the report gives this guard an invented route.</summary>
    public bool LiesAboutPatrol(SiteGuardId guardId, out IReadOnlyList<SiteRoomId>? invented)
    {
        if (_liedPatrols.TryGetValue(guardId, out IReadOnlyList<SiteRoomId>? route))
        {
            invented = route;
            return true;
        }

        invented = null;
        return false;
    }

    /// <summary>
    /// Settles every choice: the objective claim, the protected route, and the lies.
    /// </summary>
    /// <remarks>
    /// Called once, before any entry is built. The protected route is walked over the real
    /// layout rather than over a list of claims, so there is nothing to wait for — and
    /// calling this last would be a silent bug, because every lie would be "chosen" after
    /// the entry that consumes it had already been written.
    /// </remarks>
    public void Settle(SiteLayout layout)
    {
        if (!_active || _settled)
            return;

        _settled = true;

        // The objective claim FIRST. Protecting the route to the real objective and only
        // then moving the claim would leave the lie standing on a route nothing had
        // bothered to protect, which is how the first version shipped: every poisoned
        // report had a door it had misdescribed on the way to the room it was pointing at.
        SiteRoomId claimed = PeekObjective();

        var protectedConnections = WalkRealRoute(layout, _layout.EntranceRoomId, claimed);

        ChooseLockLies(protectedConnections);
        ChoosePatrolLies();
    }

    // ---- the three lies ------------------------------------------------------

    /// <summary>
    /// Points the report at the wrong room.
    /// </summary>
    /// <remarks>
    /// Only ever a room the player could plausibly have been told about: a real room,
    /// reachable, and not the entrance — the entrance is known to be the entrance, so
    /// claiming it holds the objective would be a lie the player can disprove on sight
    /// rather than one they have to walk to find out.
    /// </remarks>
    private SiteRoomId ChooseWrongObjective()
    {
        if (_rng.NextInt(1, 101)
            > SimulationRules.Intel("intel_poison_objective_percent", 45))
        {
            return _layout.ObjectiveRoomId;
        }

        var candidates = new List<SiteRoomId>();

        foreach (SiteRoom room in _layout.Rooms)
        {
            if (room.Id == _layout.ObjectiveRoomId || room.Id == _layout.EntranceRoomId)
                continue;

            candidates.Add(room.Id);
        }

        if (candidates.Count == 0)
            return _layout.ObjectiveRoomId;

        return candidates[_rng.NextInt(0, candidates.Count)];
    }

    /// <summary>
    /// Claims the wrong lock state for a handful of doors, sparing the protected route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Doors that really are locked are lied about first, because that is the expensive
    /// direction: the player walks to a door they were told was open and finds it shut.
    /// That is the lie the stage brief asks for and the one that costs a search of the
    /// building. Only when the site has no locked door at all does the plan fall back to
    /// claiming an open one is shut, which is annoying but merely wasted time.
    /// </para>
    /// <para>
    /// The count is capped twice over — by a percentage of doors and by an absolute
    /// maximum — because a percentage alone scales with the building. A forty-room site
    /// would then get a wall of false locks and a two-room site none, and the same
    /// operation would feel completely different in each.
    /// </para>
    /// </remarks>
    private void ChooseLockLies(HashSet<SiteConnectionId> protectedConnections)
    {
        var lieable = new List<SiteConnectionId>();
        var locked = new List<SiteConnectionId>();

        foreach (SiteConnection connection in _layout.Connections)
        {
            if (protectedConnections.Contains(connection.Id))
                continue;

            lieable.Add(connection.Id);

            if (connection.IsLocked)
                locked.Add(connection.Id);
        }

        if (lieable.Count == 0)
            return;

        int budget = Math.Min(
            SimulationRules.Intel("intel_poison_max_locked_doors", 2),
            SimulationRules.PercentOf(lieable.Count, SimulationRules.Intel("intel_poison_door_percent", 20)));

        if (budget <= 0)
            return;

        var pool = locked.Count > 0 ? locked : lieable;

        for (int taken = 0; taken < budget && pool.Count > 0; taken++)
        {
            int pick = _rng.NextInt(0, pool.Count);
            _liedLocks.Add(pool[pick]);
            pool.RemoveAt(pick);
        }
    }

    /// <summary>
    /// Invents a patrol route for some of the guards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invented route is a random walk of the same length through real rooms and
    /// real joins. That matters: a route naming rooms that do not exist, or two rooms
    /// with no door between them, would be visibly nonsense, and a nonsense patrol report
    /// teaches the player that the map is lying about everything. The route has to look
    /// like a route so that finding it wrong costs something.
    /// </para>
    /// <para>
    /// Stationary guards are left alone. A one-room route is not a route, and there is
    /// no version of "wrong about where the sentry stands" that is both plausible and
    /// different from the truth.
    /// </para>
    /// </remarks>
    private void ChoosePatrolLies()
    {
        int budget = SimulationRules.PercentOf(
            _layout.Guards.Count,
            SimulationRules.Intel("intel_poison_patrol_percent", 30));

        if (budget <= 0)
            return;

        var patrolling = new List<SiteGuard>();

        foreach (SiteGuard guard in _layout.Guards)
        {
            if (!guard.IsStationary)
                patrolling.Add(guard);
        }

        for (int taken = 0; taken < budget && patrolling.Count > 0; taken++)
        {
            int pick = _rng.NextInt(0, patrolling.Count);
            SiteGuard guard = patrolling[pick];
            patrolling.RemoveAt(pick);

            IReadOnlyList<SiteRoomId>? walked = InventRoute(guard.PatrolRoute.Count);
            if (walked is not null)
                _liedPatrols[guard.Id] = walked;
        }
    }

    /// <summary>
    /// A plausible walk of <paramref name="length"/> rooms, or null when there is nowhere to walk.
    /// </summary>
    private IReadOnlyList<SiteRoomId>? InventRoute(int length)
    {
        if (length < 2 || _layout.Rooms.Count == 0)
            return null;

        var walk = new List<SiteRoomId>(length) { _layout.Rooms[_rng.NextInt(0, _layout.Rooms.Count)].Id };

        while (walk.Count < length)
        {
            IReadOnlyList<SiteRoomId> options = _layout.Neighbours(walk[walk.Count - 1]);

            if (options.Count == 0)
                break;

            walk.Add(options[_rng.NextInt(0, options.Count)]);
        }

        // A walk that ran out of doors early is not a patrol route of this length, and
        // reporting a two-room route for a guard the player will see walking four rooms
        // is a lie that costs the player nothing.
        return walk.Count == length ? walk : null;
    }

    // ---- routing -------------------------------------------------------------

    /// <summary>
    /// The real connections on a shortest path between two rooms.
    /// </summary>
    /// <remarks>
    /// Shortest because the route only has to exist: protecting every path would leave
    /// nothing to lie about on a site with many ways in, and protecting the shortest one
    /// is the promise that matters — it is the route the player is overwhelmingly likely
    /// to try first.
    /// </remarks>
    private HashSet<SiteConnectionId> WalkRealRoute(
        SiteLayout layout,
        SiteRoomId from,
        SiteRoomId to)
    {
        var onRoute = new HashSet<SiteConnectionId>();

        if (from == to)
            return onRoute;

        var cameBy = new Dictionary<SiteRoomId, SiteConnectionId>();
        var cameFrom = new Dictionary<SiteRoomId, SiteRoomId>();
        var queue = new Queue<SiteRoomId>();

        queue.Enqueue(from);
        cameFrom[from] = from;

        while (queue.Count > 0)
        {
            SiteRoomId current = queue.Dequeue();

            foreach (SiteConnection connection in layout.ConnectionsAt(current))
            {
                SiteRoomId next = connection.RoomA == current ? connection.RoomB : connection.RoomA;

                if (cameFrom.ContainsKey(next))
                    continue;

                cameFrom[next] = current;
                cameBy[next] = connection.Id;
                queue.Enqueue(next);
            }
        }

        if (!cameFrom.ContainsKey(to))
            return onRoute;

        SiteRoomId cursor = to;

        // The seed room was never given a predecessor, so a loop that assumed one would
        // step off the end of the dictionary on every single-hop route.
        while (cursor != from && cameBy.TryGetValue(cursor, out SiteConnectionId edge))
        {
            onRoute.Add(edge);

            if (!cameFrom.TryGetValue(cursor, out SiteRoomId previous))
                break;

            cursor = previous;
        }

        return onRoute;
    }
}