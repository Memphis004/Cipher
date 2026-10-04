namespace ProjectSpy.Core.Missions;

/// <summary>
/// What kind of floor a <see cref="SiteFloor"/> is.
/// </summary>
/// <remarks>
/// Core's own enum rather than the table's <c>FloorKind</c>, because the table's is a
/// <em>declaration</em> ("this room may go on any floor") while this is a <em>resolved
/// value</em> ("this room is on the basement"). The table's also carries an <c>Any</c>
/// member with no meaning for an actual floor, which is why the two are kept apart and
/// cross only through <see cref="SimulationRules.IsRoomAllowedOnFloor"/>.
/// </remarks>
public enum SiteFloorKind
{
    /// <summary>Floor 0, at street level.</summary>
    Ground = 0,

    /// <summary>A floor above the ground.</summary>
    Upper = 1,

    /// <summary>A floor below the ground.</summary>
    Basement = 2,
}

/// <summary>
/// What a <see cref="SiteConnection"/> joins.
/// </summary>
/// <remarks>
/// The table's <c>ConnectionKind</c> is converted into this on the way in
/// (<see cref="SimulationRules.ToCoreConnectionKind"/>) so a generated layout holds
/// resolved Core values, and renumbering either enum becomes a compile error at the one
/// crossing point instead of a building full of vents where the stairs should be.
/// </remarks>
public enum SiteConnectionKind
{
    /// <summary>An ordinary door. Two-way, passable by anyone.</summary>
    Door = 0,

    /// <summary>A door that starts locked and has to be opened, picked or forced.</summary>
    LockedDoor = 1,

    /// <summary>A stair between adjacent floors.</summary>
    Stair = 2,

    /// <summary>A ladder between adjacent floors.</summary>
    Ladder = 3,

    /// <summary>A vent. Crawlspace: slow, quiet, and too small for an NPC to use.</summary>
    Vent = 4,

    /// <summary>A window. Fast and unbarricaded, but one-way and not for NPCs.</summary>
    Window = 5,

    /// <summary>A hole in a wall. Quiet, awkward, and not for NPCs.</summary>
    Hole = 6,
}

/// <summary>How well lit a room or a spot is.</summary>
/// <remarks>
/// Mirrors the table's <c>LightLevel</c>. <see cref="Dim"/> is a real state rather than a
/// half-lit <see cref="Lit"/>: it is the level at which a guard can just about make out a
/// shape, which is the level most of a generated building spends its time at.
/// </remarks>
public enum SiteLightLevel
{
    /// <summary>No usable light.</summary>
    Dark = 0,

    /// <summary>Enough to see a silhouette, not a face.</summary>
    Dim = 1,

    /// <summary>Enough to be identified.</summary>
    Lit = 2,
}

/// <summary>What a guard is for.</summary>
public enum SiteGuardRole
{
    /// <summary>Walks a route.</summary>
    Patrol = 0,

    /// <summary>Holds one room.</summary>
    Sentry = 1,

    /// <summary>Waits to be called to an incident rather than standing somewhere.</summary>
    Responder = 2,

    /// <summary>Guards one specific thing: a vault, a terminal, a prisoner.</summary>
    Specialist = 3,
}

/// <summary>Which part of a mission a region is.</summary>
public enum SiteRegionKind
{
    /// <summary>The building itself.</summary>
    Site = 0,

    /// <summary>
    /// The forward command post: a small region away from the building that the team can
    /// travel to and back from.
    /// </summary>
    ForwardPost = 1,
}

/// <summary>What a room is for in the mission.</summary>
/// <remarks>
/// <para>
/// A <b>flags</b> enum because a small room genuinely can be two things at once. The
/// forced case is the entrance: a site with only two rooms has the team land in one and
/// take the other as the objective, leaving nowhere else to leave from — so the room they
/// arrived in has to be the extraction point as well. With a single-valued enum the
/// generator had to choose which of the two it was, and whichever it lost stopped existing
/// as far as validation was concerned. That is not a hypothetical: it is what every
/// two-room tier-1 site does, and it is why this is flags.
/// </para>
/// <para>
/// Exactly one room carries <see cref="Entrance"/>, exactly one carries
/// <see cref="Objective"/>, and at least one carries <see cref="Extraction"/>. Those are
/// still separate counts in <see cref="SiteLayout.Validate"/>, because "exactly one
/// entrance" is not the same claim as "at least one extraction point".
/// </para>
/// </remarks>
[Flags]
public enum SiteRoomRole
{
    /// <summary>Nothing special.</summary>
    None = 0,

    /// <summary>Where the team lands. Exactly one room.</summary>
    Entrance = 1 << 0,

    /// <summary>What the mission is for. Exactly one room.</summary>
    Objective = 1 << 1,

    /// <summary>Where the team can leave. At least one room.</summary>
    Extraction = 1 << 2,

    /// <summary>The command post itself.</summary>
    ForwardPost = 1 << 3,
}

/// <summary>Stable identifier for a room on one generated site.</summary>
/// <remarks>
/// Handed out in generation order, left to right and floor by floor, so room 7 is the
/// same room in the simulator, in a replay and in a player's notes. It is the key lazy
/// room contents are derived from (rule 11), so it has to be stable at a point where the
/// contents do not exist yet.
/// </remarks>
public readonly record struct SiteRoomId(int Value) : IComparable<SiteRoomId>
{
    /// <summary>The absence of a room.</summary>
    public static readonly SiteRoomId None = new(0);

    /// <summary>True for a real room id.</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc/>
    public int CompareTo(SiteRoomId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    public override string ToString() => $"R{Value:D3}";
}

/// <summary>Stable identifier for a connection on one generated site.</summary>
public readonly record struct SiteConnectionId(int Value) : IComparable<SiteConnectionId>
{
    /// <summary>The absence of a connection.</summary>
    public static readonly SiteConnectionId None = new(0);

    /// <summary>True for a real connection id.</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc/>
    public int CompareTo(SiteConnectionId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    public override string ToString() => $"C{Value:D3}";
}

/// <summary>
/// One floor of a generated site: a span of centimetres and the rooms laid across it.
/// </summary>
/// <remarks>
/// <b>Why centimetres.</b> Everything in Core is centimetres end-to-end (rule 15). The
/// CSVs are authored in lane units where one unit is one metre, because that is how a
/// designer thinks about a warehouse; the generator converts once, on the way in, and
/// nothing downstream has to remember which unit a number is in.
/// </remarks>
public sealed class SiteFloor
{
    /// <summary>Zero-based index. Index 0 is always <see cref="SiteFloorKind.Ground"/>.</summary>
    public int Index { get; init; }

    /// <summary>What kind of floor this is.</summary>
    public SiteFloorKind Kind { get; init; }

    /// <summary>The floor's total walkable extent in centimetres, from zero to here.</summary>
    public Fixed32 Span { get; init; }

    /// <summary>
    /// The rooms on this floor, ordered left to right by <see cref="SiteRoom.StartX"/>.
    /// </summary>
    /// <remarks>
    /// A list rather than a set because the order <em>is</em> the layout: two rooms on a
    /// floor are "adjacent" exactly when one follows the other, and Presentation draws them
    /// in that order. Sorting is never needed, because the generator only ever appends at
    /// the right-hand edge.
    /// </remarks>
    public List<SiteRoom> Rooms { get; } = new();

    /// <inheritdoc/>
    public override string ToString()
        => $"Floor {Index} ({Kind}) span={Span.Raw}cm rooms={Rooms.Count}";
}

/// <summary>
/// One room: a walkable half-open interval <c>[StartX, EndX)</c> on one floor.
/// </summary>
/// <remarks>
/// <para>
/// The interval is the whole point. A room in a mission is a place an agent can stand and
/// walk through, and everything downstream is interval maths on that line — whether a
/// guard can see the doorway, whether a thrown object reaches the far wall, how loud a
/// noise is by the time it crosses the room (rule 15). Nothing here knows how deep the
/// room is or what shape it is; Presentation decides that.
/// </para>
/// <para>
/// Half-open (<c>[StartX, EndX)</c>) rather than closed so that two adjacent rooms share
/// exactly one wall and never a slab of floor, which is what makes "no overlapping
/// intervals" checkable by one comparison per room.
/// </para>
/// <para>
/// <b>No contents.</b> What is actually in the room — the exact loot roll, the prop
/// layout — is generated on first observation and lives in a <see cref="SiteRoomDetail"/>,
/// which does not exist for a room nobody has entered (rule 11). See
/// <see cref="SiteLayout.ObserveRoom"/>.
/// </para>
/// </remarks>
public sealed class SiteRoom
{
    /// <summary>This room's stable id.</summary>
    public SiteRoomId Id { get; init; }

    /// <summary>Which floor this room sits on.</summary>
    public int FloorIndex { get; init; }

    /// <summary>Left edge of the room, in centimetres. Inclusive.</summary>
    public Fixed32 StartX { get; init; }

    /// <summary>Right edge of the room, in centimetres. Exclusive.</summary>
    public Fixed32 EndX { get; init; }

    /// <summary>
    /// How wide the room is, in centimetres. Always at least the template's declared
    /// minimum width.
    /// </summary>
    public Fixed32 Span => EndX - StartX;

    /// <summary>
    /// True when <paramref name="x"/> is inside this room: left edge included, right edge
    /// excluded.
    /// </summary>
    public bool Contains(Fixed32 x) => x >= StartX && x < EndX;

    /// <summary>
    /// The rightmost centimetre that is still inside this room.
    /// </summary>
    /// <remarks>
    /// <see cref="Contains"/> is half-open, so <see cref="EndX"/> itself belongs to no
    /// room — it is the first centimetre of whatever is next. Anything that puts an actor
    /// at a clamped position has to clamp to this rather than to <see cref="EndX"/>, or it
    /// will occasionally place somebody one centimetre outside the building and the
    /// mission's own validator will report them off the map.
    /// </remarks>
    public Fixed32 LastX => EndX - new Fixed32(1);

    /// <summary>Foreign key into <c>room_template</c>.</summary>
    public int RoomTemplateId { get; init; }

    /// <summary>Localization key for the room's name. Core never produces prose.</summary>
    public string NameKey { get; init; } = string.Empty;

    /// <summary>Tags from <c>room_template</c>, split on commas.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Static inhabitant classes allowed here, from <c>npc_tags</c>.</summary>
    public IReadOnlyList<string> NpcTags { get; init; } = Array.Empty<string>();

    /// <summary>True when a static inhabitant of the given class may stand here.</summary>
    public bool AllowsNpc(string npcTag) => NpcTags.Contains(npcTag);

    /// <summary>
    /// The loot table this room exposes, or 0 when it holds none.
    /// </summary>
    /// <remarks>
    /// The promise, not the contents. Which item ids that table yields is lazy content
    /// and does not exist until the room is observed — which is the whole point of the
    /// split, because a scouted room may honestly say "there is something to search here"
    /// without saying what.
    /// </remarks>
    public int LootTableId { get; init; }

    /// <summary>Light level a room with no working emitter sits at, from the template.</summary>
    public SiteLightLevel DefaultLightLevel { get; init; }

    /// <summary>How much this room damps noise crossing it, in percent.</summary>
    public int NoiseAbsorptionPercent { get; init; }

    /// <summary>
    /// The room's roles in the mission, when it has any.
    /// </summary>
    /// <remarks>
    /// A flag set rather than three separate id fields, because a room being both the
    /// entrance and the extraction point is ordinary — see <see cref="SiteRoomRole"/> — and
    /// because three nullable fields would let the generator record two objectives without
    /// anything noticing.
    /// </remarks>
    public SiteRoomRole Role { get; internal set; } = SiteRoomRole.None;

    /// <inheritdoc/>
    public override string ToString() => $"{Id} f{FloorIndex} [{StartX.Raw},{EndX.Raw}) {NameKey}";
}

/// <summary>
/// One way between two rooms.
/// </summary>
/// <remarks>
/// <para>
/// Connections are the building's adjacency, and they are explicit data rather than
/// something derived from geometry (rule 16). Two rooms being two metres apart means
/// nothing; a door between them is the whole fact, and it can be locked, barricaded, noisy
/// or blind to sight while the walls stay exactly where they are.
/// </para>
/// <para>
/// Every connection is two-way. A building with a one-way door is a puzzle box rather than
/// a building, and modelling direction would double the edge set for a distinction the
/// fiction does not need. Direction enters the model as a cost, in
/// <see cref="TraverseSteps"/>.
/// </para>
/// </remarks>
public sealed class SiteConnection
{
    /// <summary>This connection's stable id.</summary>
    public SiteConnectionId Id { get; init; }

    /// <summary>Room on the lower floor index, or the left-hand room for a horizontal one.</summary>
    public SiteRoomId RoomA { get; init; }

    /// <summary>Floor index of <see cref="RoomA"/>.</summary>
    public int FloorIndexA { get; init; }

    /// <summary>Room on the higher floor index, or the right-hand room for a horizontal one.</summary>
    public SiteRoomId RoomB { get; init; }

    /// <summary>Floor index of <see cref="RoomB"/>.</summary>
    public int FloorIndexB { get; init; }

    /// <summary>What kind of connection this is.</summary>
    public SiteConnectionKind Kind { get; init; }

    /// <summary>Foreign key into <c>connection_type</c>.</summary>
    public int ConnectionTypeId { get; init; }

    /// <summary>
    /// Where the connection sits on <see cref="RoomA"/>'s floor, in centimetres.
    /// </summary>
    /// <remarks>
    /// For a horizontal connection this is the shared wall — the point the two rooms
    /// touch — because that is where a door in a party wall is. For a vertical one it is
    /// the foot of the stair.
    /// </remarks>
    public Fixed32 X { get; init; }

    /// <summary>
    /// Where the connection lands on <see cref="RoomB"/>'s floor, in centimetres.
    /// </summary>
    /// <remarks>
    /// Equal to <see cref="X"/> for a horizontal connection, because both rooms are on one
    /// floor. It is a separate value for a vertical one because a stairwell's landing is
    /// not directly above its foot — which is both true of real buildings and what frees
    /// the generator from having to line up the room it lands in with the room it leaves.
    /// </remarks>
    public Fixed32 UpperX { get; init; }

    /// <summary>
    /// True when the two rooms are on different floors.
    /// </summary>
    /// <remarks>
    /// Vertical connections span exactly one floor, which is what keeps "can I get from
    /// here to there" a question about the graph rather than about whether a stairwell
    /// skipped a level. A generator bug that produced a two-floor span is caught by
    /// <see cref="SiteLayout.Validate"/>.
    /// </remarks>
    public bool IsVertical => FloorIndexB != FloorIndexA;

    /// <summary>Steps to traverse, from <c>connection_type.traverse_steps</c>.</summary>
    public int TraverseSteps { get; init; }

    /// <summary>True when the door starts locked. Locked doors are opened, not walked.</summary>
    public bool IsLocked { get; init; }

    /// <summary>True when the connection stops anyone looking through it.</summary>
    public bool BlocksVision { get; init; }

    /// <summary>True when an NPC can use it. Vents, windows and holes cannot.</summary>
    public bool UsableByNpc { get; init; }

    /// <summary>The other room, given one of this connection's rooms.</summary>
    /// <remarks>
    /// <para>
    /// Both directions, and that is the whole point of the method. It used to read
    /// <c>room == RoomA ? RoomB : room</c>, which answers correctly when the caller is
    /// standing in <see cref="RoomA"/> and returns <em>the room they were already
    /// standing in</em> when they are in <see cref="RoomB"/>.
    /// </para>
    /// <para>
    /// The damage is not obvious because <see cref="RoomA"/> is by construction the
    /// lower floor or the left-hand room, and callers mostly expand outwards from the
    /// entrance, so most of the time the branch taken is the right one. But the
    /// pathfinder's <c>settled.Contains(across)</c> check then skipped the edge and the
    /// search could never walk <em>into</em> a room that sat on the B side of the door
    /// leading to it: on a six-floor black site, ten of twenty-three rooms were
    /// unreachable from the entrance and the route finder said so honestly. Nothing
    /// failed loudly, because the graph it was walking was one-directional.
    /// </para>
    /// </remarks>
    public SiteRoomId Other(SiteRoomId room) => room == RoomA ? RoomB : RoomA;

    /// <inheritdoc/>
    public override string ToString() => $"{Id} {RoomA}-{RoomB} {Kind} @{X.Raw}cm";
}