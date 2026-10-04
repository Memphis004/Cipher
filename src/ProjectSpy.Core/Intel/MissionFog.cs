using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core;

/// <summary>
/// What the player knows about each room and connection of the mission they are in.
/// </summary>
/// <remarks>
/// <para>
/// Derived from an <see cref="IntelSnapshot"/> at mission start and promoted as the team
/// perceives. Two properties define it, and both exist because of knowledge.md rule 17:
/// </para>
/// <para>
/// <b>Knowledge only ever moves up.</b> There is no demotion path in the type at all —
/// the promotion method is the only mutator, and it refuses to move a room down. A map
/// that un-revealed itself as the team walked would flicker, and a player who cannot
/// trust the map to stay drawn is playing a different and much worse game.
/// </para>
/// <para>
/// <b>A claim may be contradicted, and saying so is the point.</b> When the team sees
/// something the report said differently, the room is promoted to
/// <see cref="FogState.Observed"/> — the observation is true and the claim was not — and
/// an <see cref="IntelContradicted"/> is raised. Silently reconciling the two would mean
/// the player never learns their source was wrong, and a report that could never be
/// caught being wrong could never have been worth anything.
/// </para>
/// </remarks>
public sealed class MissionFog
{
    private readonly Dictionary<SiteRoomId, FogState> _rooms = new();
    private readonly Dictionary<SiteConnectionId, FogState> _connections = new();

    /// <summary>Creates an empty fog: nothing known, because nothing has been told yet.</summary>
    public MissionFog()
    {
    }

    /// <summary>The site this fog describes.</summary>
    public int SiteId { get; private set; }

    /// <summary>The intel percentage the fog was seeded at, or -1 with no intel.</summary>
    public int IntelPercent { get; private set; } = -1;

    /// <summary>How many contradictions the team has run into so far.</summary>
    public int ContradictionCount { get; private set; }

    /// <summary>
    /// Builds the fog a mission starts with, from the report the player brought.
    /// </summary>
    /// <remarks>
    /// A null snapshot is a legitimate input — a mission the player walked into on no
    /// intel at all — and produces an all-Unknown fog rather than an error. Rooms the
    /// report named become <see cref="FogState.Reported"/>; everything else stays
    /// <see cref="FogState.Unknown"/>, which is how a partial map stays partial instead
    /// of quietly filling in.
    /// </remarks>
    public static MissionFog FromSnapshot(SiteLayout layout, IntelSnapshot? snapshot)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        var fog = new MissionFog { SiteId = layout.SiteTemplateId };

        if (snapshot is null)
            return fog;

        fog.IntelPercent = snapshot.IntelPercent;

        foreach (IntelRoomEntry room in snapshot.Rooms)
            fog._rooms[room.RoomId] = FogState.Reported;

        foreach (IntelConnectionEntry connection in snapshot.Connections)
            fog._connections[connection.ConnectionId] = FogState.Reported;

        return fog;
    }

    /// <summary>What is known about a room.</summary>
    public FogState RoomState(SiteRoomId roomId)
        => _rooms.TryGetValue(roomId, out FogState state) ? state : FogState.Unknown;

    /// <summary>What is known about a connection.</summary>
    public FogState ConnectionState(SiteConnectionId connectionId)
        => _connections.TryGetValue(connectionId, out FogState state) ? state : FogState.Unknown;

    /// <summary>Rooms in ascending id order with their state, for the map screen.</summary>
    public IReadOnlyList<KeyValuePair<SiteRoomId, FogState>> RoomStates
    {
        get
        {
            var all = new List<KeyValuePair<SiteRoomId, FogState>>(_rooms);
            all.Sort((left, right) => left.Key.CompareTo(right.Key));
            return all;
        }
    }

    /// <summary>Connections in ascending id order with their state.</summary>
    public IReadOnlyList<KeyValuePair<SiteConnectionId, FogState>> ConnectionStates
    {
        get
        {
            var all = new List<KeyValuePair<SiteConnectionId, FogState>>(_connections);
            all.Sort((left, right) => left.Key.CompareTo(right.Key));
            return all;
        }
    }

    /// <summary>Promotes a room, refusing to move it down.</summary>
    /// <returns>True when the state actually changed.</returns>
    public bool Promote(SiteRoomId roomId, FogState state)
    {
        FogState current = RoomState(roomId);

        if (state <= current)
            return false;

        _rooms[roomId] = state;
        return true;
    }

    /// <summary>Promotes a connection, refusing to move it down.</summary>
    /// <returns>True when the state actually changed.</returns>
    public bool Promote(SiteConnectionId connectionId, FogState state)
    {
        FogState current = ConnectionState(connectionId);

        if (state <= current)
            return false;

        _connections[connectionId] = state;
        return true;
    }

    /// <summary>
    /// Marks a room read without entering it — a gadget, a terminal, an informant.
    /// </summary>
    /// <remarks>
    /// The rung between a report and a visit. It exists as a distinct state because
    /// "someone told us what is in there" and "we saw what is in there" call for
    /// different behaviour on entry, and collapsing them would either let a gadget do a
    /// scout's job or make gadgets useless.
    /// </remarks>
    public bool Scout(SiteRoomId roomId) => Promote(roomId, FogState.Scouted);

    /// <summary>Marks a room searched.</summary>
    public bool Clear(SiteRoomId roomId) => Promote(roomId, FogState.Cleared);

    /// <summary>
    /// Records that the team has seen a room, and reports what the intel got wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the layout rather than a room because the most expensive lie is not visible
    /// from inside the room: it is the report saying this is where the objective is when
    /// the real objective is elsewhere in the building. Checking that needs the layout,
    /// and checking it is the whole point — a wasted assault on the wrong end of the
    /// building is the single most expensive thing a poisoned report can cost, and it is
    /// the one the player most needs to be told they have walked into.
    /// </para>
    /// <para>
    /// A correct report produces no event at all. The absence of contradictions is
    /// meaningful, so it must not be padded with events for rooms that matched.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IntelContradicted> ObserveRoom(
        SiteRoomId roomId,
        SiteLayout layout,
        IntelSnapshot? snapshot,
        Tick tick,
        IEventSink? events = null)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        SiteRoom? room = layout.Find(roomId)
            ?? throw new InvalidOperationException(
                $"Room {roomId} is not part of site {layout.SiteTemplateId}.");

        Promote(roomId, FogState.Observed);

        var contradictions = new List<IntelContradicted>();
        IntelRoomEntry? claim = snapshot?.Room(roomId);

        if (claim is null)
            return contradictions;

        // Only meaningful once the band named a type. Below that the claim said nothing
        // about the room, so agreeing with it is not a fact and differing from it is
        // not a lie.
        if (claim.RoomTemplateId != 0 && claim.RoomTemplateId != room.RoomTemplateId)
            contradictions.Add(Record(snapshot!, IntelFactKind.Room, roomId.Value, claim.Confidence, tick, events));

        if (claim.Roles.HasFlag(IntelKnownRole.Objective) && layout.ObjectiveRoomId != roomId)
            contradictions.Add(Record(snapshot!, IntelFactKind.Room, roomId.Value, claim.Confidence, tick, events));

        if (claim.Roles.HasFlag(IntelKnownRole.Entrance) && snapshot!.ClaimedEntrance != roomId)
            contradictions.Add(Record(snapshot, IntelFactKind.Room, roomId.Value, claim.Confidence, tick, events));

        return contradictions;
    }

    /// <summary>
    /// Records that the team has seen a connection, and reports a wrong lock state.
    /// </summary>
    public IReadOnlyList<IntelContradicted> ObserveConnection(
        SiteConnectionId connectionId,
        SiteLayout layout,
        IntelSnapshot? snapshot,
        Tick tick,
        IEventSink? events = null)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        SiteConnection? connection = Find(layout, connectionId)
            ?? throw new InvalidOperationException(
                $"Connection {connectionId} is not part of site {layout.SiteTemplateId}.");

        Promote(connectionId, FogState.Observed);

        var contradictions = new List<IntelContradicted>();
        IntelConnectionEntry? claim = snapshot?.Connection(connectionId);

        if (claim is not null && claim.IsLocked != connection.IsLocked)
        {
            contradictions.Add(Record(snapshot!, IntelFactKind.Connection, connectionId.Value, claim.Confidence, tick, events));
        }

        return contradictions;
    }

    /// <summary>
    /// Records that the team has seen a guard's round, and reports a wrong route.
    /// </summary>
    public IReadOnlyList<IntelContradicted> ObservePatrol(
        SiteGuardId guardId,
        SiteLayout layout,
        IntelSnapshot? snapshot,
        Tick tick,
        IEventSink? events = null)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        var contradictions = new List<IntelContradicted>();
        SiteGuard? guard = null;

        foreach (SiteGuard candidate in layout.Guards)
        {
            if (candidate.Id == guardId)
            {
                guard = candidate;
                break;
            }
        }

        if (guard is null)
            throw new InvalidOperationException(
                $"Guard {guardId} is not part of site {layout.SiteTemplateId}.");

        foreach (IntelPatrolEntry claim in SnapshotPatrols(snapshot, guardId))
        {
            if (SameRoute(claim.Route, guard.PatrolRoute))
                continue;

            contradictions.Add(Record(snapshot!, IntelFactKind.Patrol, guardId.Value, claim.Confidence, tick, events));
        }

        return contradictions;
    }

    private static SiteConnection? Find(SiteLayout layout, SiteConnectionId connectionId)
    {
        foreach (SiteConnection connection in layout.Connections)
        {
            if (connection.Id == connectionId)
                return connection;
        }

        return null;
    }

    private static IEnumerable<IntelPatrolEntry> SnapshotPatrols(IntelSnapshot? snapshot, SiteGuardId guardId)
    {
        if (snapshot is null)
            yield break;

        foreach (IntelPatrolEntry patrol in snapshot.Patrols)
        {
            if (patrol.GuardId == guardId)
                yield return patrol;
        }
    }

    private static bool SameRoute(IReadOnlyList<SiteRoomId> left, IReadOnlyList<SiteRoomId> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
                return false;
        }

        return true;
    }

    private IntelContradicted Record(
        IntelSnapshot snapshot,
        IntelFactKind kind,
        int factId,
        IntelConfidence claimedConfidence,
        Tick tick,
        IEventSink? events)
    {
        ContradictionCount++;

        var contradiction = new IntelContradicted(
            tick, snapshot.SiteId, kind, factId, claimedConfidence);

        events?.Publish(contradiction);
        return contradiction;
    }
}