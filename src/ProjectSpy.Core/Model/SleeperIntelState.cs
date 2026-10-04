using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core;

/// <summary>How a sleeper operation is going.</summary>
public enum SleeperStatus
{
    /// <summary>Insertion under way; intel is still accruing.</summary>
    Inserting = 0,

    /// <summary>Embedded and reporting.</summary>
    Embedded = 1,

    /// <summary>The site found the agent. The agent is gone.</summary>
    Discovered = 2,

    /// <summary>Burned: no longer usable at this site.</summary>
    Burned = 3,

    /// <summary>Recalled by the player, keeping the intel accrued so far.</summary>
    Extracted = 4,

    /// <summary>
    /// Compromised. Still reporting, but the information is deliberately false.
    /// </summary>
    /// <remarks>
    /// The player is not told. A poisoned operation reporting plausible, survivably
    /// wrong facts is the entire point of the risk; announcing it would remove the
    /// decision the system exists to create. See knowledge.md rule 17.
    /// </remarks>
    Poisoned = 5,
}

/// <summary>
/// An agent inserted into a target site to gather intelligence before a mission.
/// </summary>
/// <remarks>
/// <para>
/// Advances on the strategic clock, not the tactical one (knowledge.md rule 13), so
/// an operation accrues while the player builds, trains and waits — which is what
/// makes it a real cost rather than a menu click.
/// </para>
/// <para>
/// The per-tick accrual and discovery roll are <see cref="SleeperSystem"/>, which reads
/// <c>sleeper_op.csv</c>.
/// </para>
/// </remarks>
public sealed class SleeperOperation
{
    /// <summary>The inserted agent. Foreign key into the roster.</summary>
    public AgentId AgentId { get; init; }

    /// <summary>The site being spied on. Foreign key into site_template (stage 2).</summary>
    public int SiteId { get; init; }

    /// <summary>Strategic tick the insertion began on.</summary>
    public Tick StartedOnTick { get; init; }

    /// <summary>
    /// Intel gathered so far, 0 to 100.
    /// </summary>
    /// <remarks>
    /// Determines how much of the building is pre-revealed at mission start, and how
    /// much of it may be wrong. The bands are in knowledge.md rule 17; they are not
    /// encoded here because stage 4b filters the snapshot through them, and a second
    /// copy of the thresholds in Core would be a second thing to keep in sync.
    /// </remarks>
    public int IntelPercent { get; set; }

    /// <summary>Current state of the operation.</summary>
    public SleeperStatus Status { get; set; } = SleeperStatus.Inserting;

    /// <summary>
    /// Progress toward the next whole percent, in hundredths of a percent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accumulation cannot simply be <c>IntelPercent += 100 / ticks_per_intel_percent</c>.
    /// Integer division truncates, so at 24 ticks per percent a rate of 4 truncates to 4
    /// and a rate of 7 truncates to 4 — a slower tier would accrue exactly as fast as a
    /// faster one, and the table's central number would stop meaning anything. Carrying
    /// the remainder keeps the rate exact: 100 units a tick, and a percent every time
    /// 100 times <c>ticks_per_intel_percent</c> units have gone by.
    /// </para>
    /// <para>
    /// Public and serialized because it is state. A save that dropped it would restart
    /// every running operation a few ticks short of where it was, and the player would
    /// watch their intel percentage stall and resume for no visible reason.
    /// </para>
    /// </remarks>
    public int IntelProgressHundredths { get; set; }

    /// <summary>The tick this operation most recently produced a snapshot.</summary>
    public Tick? SnapshotOnTick { get; set; }

    /// <summary>The tick discovery happened on, or null while undiscovered.</summary>
    public Tick? DiscoveredOnTick { get; set; }

    /// <summary>The band this operation's intel percentage currently unlocks.</summary>
    public IntelBand Band => IntelBands.For(IntelPercent);

    /// <summary>Ticks elapsed since insertion.</summary>
    public int TicksRunning(Tick now) => Math.Max(0, (int)(now.Value - StartedOnTick.Value));

    /// <summary>True when the operation is running and reporting.</summary>
    /// <remarks>
    /// <see cref="SleeperStatus.Poisoned"/> counts as active. A poisoned operation
    /// keeps reporting — that is what makes it dangerous rather than merely unlucky,
    /// and if it stopped reporting the player would learn something was wrong for free.
    /// </remarks>
    public bool IsActive => Status is SleeperStatus.Inserting
        or SleeperStatus.Embedded
        or SleeperStatus.Poisoned;
}

/// <summary>
/// What the player believes about a site at a point in time.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot, not a live view: it is stamped with the tick it was taken and the
/// tactical simulation is free to contradict it. That gap is deliberate and is the
/// central idea of the sleeper system (knowledge.md rule 17) — a map that silently
/// corrects itself when it turns out to be wrong would remove the reason to spend
/// ticks on intelligence at all.
/// </para>
/// <para>
/// The individual facts, their confidence and their poisoning are
/// <see cref="IntelEntry"/> objects built by <see cref="IntelSnapshotBuilder"/>.
/// </para>
/// </remarks>
public sealed class IntelSnapshot
{
    /// <summary>Creates an empty snapshot.</summary>
    public IntelSnapshot() => Entries = Array.Empty<IntelEntry>();

    /// <summary>The site this describes. One snapshot per site.</summary>
    public int SiteId { get; init; }

    /// <summary>
    /// The strategic tick the snapshot was taken on.
    /// </summary>
    /// <remarks>
    /// A stale snapshot is one whose facts were true when it was taken and may not be
    /// now. Without the timestamp there is no way to tell those apart from a snapshot
    /// that was wrong from the start, and the UI has to render the two differently.
    /// </remarks>
    public Tick TakenOnTick { get; init; }

    /// <summary>Intel percentage the snapshot was filtered at, 0 to 100.</summary>
    public int IntelPercent { get; init; }

    /// <summary>Which band <see cref="IntelPercent"/> unlocks.</summary>
    public IntelBand Band { get; init; }

    /// <summary>
    /// The seed the building was generated from, so the snapshot can be rebuilt rather
    /// than stored.
    /// </summary>
    /// <remarks>
    /// Same argument as <see cref="SiteLayoutSaveData"/>: the snapshot is a claim about a
    /// building the generator can reproduce exactly, so keeping the seed is enough to
    /// reproduce it and keeping the claims would be a second copy that could disagree
    /// with the generator.
    /// </remarks>
    public ulong MapSeed { get; init; }

    /// <summary>
    /// True when this snapshot's contents were deliberately falsified.
    /// </summary>
    /// <remarks>
    /// Tracked but never surfaced to the player. It exists so the debrief and the
    /// tests can assert that poisoned intel stays <em>solvable</em> rather than
    /// unwinnable — an assertion that is impossible to write if nothing records that
    /// the snapshot was poisoned.
    /// </remarks>
    public bool IsPoisoned { get; init; }

    /// <summary>
    /// Everything the player has been told, in a stable order.
    /// </summary>
    /// <remarks>
    /// Sorted on construction so that two snapshots built from the same building and
    /// intel percentage are byte-identical, which is what lets a determinism test
    /// compare them directly. Exposed as a read-only list: callers filter through the
    /// typed accessors rather than re-deriving which kind of entry is which.
    /// </remarks>
    public IReadOnlyList<IntelEntry> Entries { get; init; }

    /// <summary>The room the player believes the team enters by, or null.</summary>
    public SiteRoomId? ClaimedEntrance { get; init; }

    /// <summary>
    /// The room the player believes holds the objective, or null.
    /// </summary>
    /// <remarks>
    /// Named "claimed" everywhere in this type because at low bands it is not merely
    /// unverified, it may be absent entirely, and at <see cref="IntelBand.Complete"/> on
    /// a poisoned operation it may be confidently wrong. The real objective lives on
    /// <see cref="Missions.SiteLayout"/> and only the tactical layer may promote a
    /// claim to a fact.
    /// </remarks>
    public SiteRoomId? ClaimedObjective { get; init; }

    /// <summary>The rooms the player believes are ways out.</summary>
    public IReadOnlyList<SiteRoomId> ClaimedExtraction { get; init; } = Array.Empty<SiteRoomId>();

    /// <summary>Room entries, in ascending room id order.</summary>
    public IEnumerable<IntelRoomEntry> Rooms => Entries.OfType<IntelRoomEntry>();

    /// <summary>Connection entries, in ascending connection id order.</summary>
    public IEnumerable<IntelConnectionEntry> Connections => Entries.OfType<IntelConnectionEntry>();

    /// <summary>Patrol entries, in ascending guard id order.</summary>
    public IEnumerable<IntelPatrolEntry> Patrols => Entries.OfType<IntelPatrolEntry>();

    /// <summary>Holding-room entries, in ascending agent id order.</summary>
    public IEnumerable<IntelHoldingEntry> Holdings => Entries.OfType<IntelHoldingEntry>();

    /// <summary>The entry for one room, or null if the room is not in the snapshot.</summary>
    public IntelRoomEntry? Room(SiteRoomId roomId)
    {
        foreach (IntelRoomEntry room in Rooms)
        {
            if (room.RoomId == roomId)
                return room;
        }

        return null;
    }

    /// <summary>The entry for one connection, or null.</summary>
    public IntelConnectionEntry? Connection(SiteConnectionId connectionId)
    {
        foreach (IntelConnectionEntry connection in Connections)
        {
            if (connection.ConnectionId == connectionId)
                return connection;
        }

        return null;
    }

    /// <summary>
    /// A route from the claimed entrance to the claimed objective, using only what this
    /// snapshot says — the graph as the player believes it, not the real one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what "can the team act on this intel" means, and it is why the poisoning
    /// has a solvability obligation attached to it. A route computed over the real
    /// layout would prove nothing: a poisoned snapshot is free to claim a door is open
    /// when it is not, and the interesting question is whether the player, believing
    /// what they were told, could still find a way through.
    /// </para>
    /// <para>
    /// Breadth-first over the claimed connections, so a returned route is a genuinely
    /// walkable sequence of joins rather than a straight line that happens to intersect
    /// them. Null when the claims do not connect the two rooms, which is the condition
    /// the poison tests assert never happens.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IntelConnectionEntry>? RouteToClaimedObjective()
    {
        if (ClaimedEntrance is not SiteRoomId entrance || ClaimedObjective is not SiteRoomId objective)
            return null;

        if (entrance == objective)
            return Array.Empty<IntelConnectionEntry>();

        var claimed = new List<IntelConnectionEntry>(Connections);
        var cameFrom = new Dictionary<SiteRoomId, int>();

        var queue = new Queue<SiteRoomId>();
        queue.Enqueue(entrance);
        cameFrom[entrance] = -1;

        while (queue.Count > 0)
        {
            SiteRoomId current = queue.Dequeue();

            for (int index = 0; index < claimed.Count; index++)
            {
                IntelConnectionEntry connection = claimed[index];

                SiteRoomId? next = null;
                if (connection.RoomA == current)
                    next = connection.RoomB;
                else if (connection.RoomB == current)
                    next = connection.RoomA;

                if (next is not SiteRoomId neighbour || cameFrom.ContainsKey(neighbour))
                    continue;

                cameFrom[neighbour] = index;

                if (neighbour == objective)
                    return TraceBack(claimed, cameFrom, entrance, objective);

                queue.Enqueue(neighbour);
            }
        }

        return null;
    }

    private static IReadOnlyList<IntelConnectionEntry> TraceBack(
        List<IntelConnectionEntry> claimed,
        Dictionary<SiteRoomId, int> cameFrom,
        SiteRoomId start,
        SiteRoomId goal)
    {
        var route = new List<IntelConnectionEntry>();
        SiteRoomId cursor = goal;

        while (cursor != start)
        {
            // The seed room has no predecessor of its own, so a lookup that assumes one
            // walks off the end of the dictionary the moment the route is one hop long.
            if (!cameFrom.TryGetValue(cursor, out int index))
                return Array.Empty<IntelConnectionEntry>();

            route.Add(claimed[index]);
            cursor = claimed[index].RoomA == cursor ? claimed[index].RoomB : claimed[index].RoomA;
        }

        route.Reverse();
        return route;
    }
}