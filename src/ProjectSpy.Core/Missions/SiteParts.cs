namespace ProjectSpy.Core.Missions;

/// <summary>
/// Hands out guard and civilian ids across a whole site.
/// </summary>
/// <remarks>
/// <para>
/// One counter per <em>layout</em>, not per region. A site with a forward command post has
/// two regions, and numbering each from one produced G001 in both — so any lookup keyed by
/// guard id found whichever guard the search happened to reach first. That is invisible in
/// a generated layout and quietly wrong everywhere else: an intel report describing guard
/// G001's patrol would resolve to the main building's sentry rather than the post's.
/// </para>
/// <para>
/// Deliberately not part of <see cref="SiteLayout"/> or any of its regions. Ids are an
/// allocation detail of generation; making them layout state would mean a save could
/// disagree with the generator about what the next id is.
/// </para>
/// </remarks>
internal sealed class SitePartIds
{
    private int _nextGuard;
    private int _nextCivilian;

    /// <summary>The next guard id.</summary>
    public SiteGuardId NextGuard() => new(++_nextGuard);

    /// <summary>The next civilian id.</summary>
    public SiteCivilianId NextCivilian() => new(++_nextCivilian);
}

/// <summary>Stable identifier for a guard on one generated site.</summary>
public readonly record struct SiteGuardId(int Value) : IComparable<SiteGuardId>
{
    /// <summary>The absence of a guard.</summary>
    public static readonly SiteGuardId None = new(0);

    /// <summary>True for a real guard id.</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc/>
    public int CompareTo(SiteGuardId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    public override string ToString() => $"G{Value:D3}";
}

/// <summary>Stable identifier for a civilian on one generated site.</summary>
public readonly record struct SiteCivilianId(int Value) : IComparable<SiteCivilianId>
{
    /// <summary>The absence of a civilian.</summary>
    public static readonly SiteCivilianId None = new(0);

    /// <summary>True for a real civilian id.</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc/>
    public int CompareTo(SiteCivilianId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    public override string ToString() => $"C{Value:D3}";
}

/// <summary>
/// A light emitter inside a room.
/// </summary>
/// <remarks>
/// Lights are committed to the generated layout rather than deferred, because whether a
/// room is lit is a thing a player can see from the doorway — deferring it would mean
/// generating contents to answer a question about a room nobody has entered, which is
/// the leak knowledge.md rule 11 exists to prevent. What the light *does* to the vision
/// intervals is stage 4c's business; here it is only placed.
/// </remarks>
public sealed class SiteLight
{
    /// <summary>Stable id, unique within the site.</summary>
    public int Id { get; init; }

    /// <summary>The room this emitter sits in.</summary>
    public SiteRoomId RoomId { get; init; }

    /// <summary>Foreign key into <c>light_source</c>.</summary>
    public int LightSourceId { get; init; }

    /// <summary>Where the emitter sits in its room, in centimetres.</summary>
    public Fixed32 X { get; init; }

    /// <summary>How bright it is.</summary>
    public SiteLightLevel Level { get; init; }

    /// <summary>How far it reaches, in centimetres, from <c>light_source.radius_cm</c>.</summary>
    public int RadiusCm { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"light {Id} in {RoomId} @{X.Raw}cm {Level}";
}

/// <summary>
/// Something inside a room that blocks the sight line through it.
/// </summary>
/// <remarks>
/// Occluders are placed, not deferred, for the same reason lights are: whether a room
/// is a straight sightline or a series of short ones is visible from the doorway. They
/// do not block movement — a filing cabinet is not a wall — and how they clip a vision
/// interval is stage 4c's job.
/// </remarks>
public sealed class SiteOccluder
{
    /// <summary>Stable id, unique within the site.</summary>
    public int Id { get; init; }

    /// <summary>The room this occluder sits in.</summary>
    public SiteRoomId RoomId { get; init; }

    /// <summary>Where the occluder sits in its room, in centimetres.</summary>
    public Fixed32 X { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"occluder {Id} in {RoomId} @{X.Raw}cm";
}

/// <summary>
/// A placed interactable: the thing the team actually touches.
/// </summary>
/// <remarks>
/// Reuses Core's <see cref="InteractableType"/> rather than declaring a second enum for
/// the same eight ideas. The kinds a site may place are the same eight the abstract
/// node interiors use; what differs is that here each one has an X on a floor instead of
/// a slot index, and that the details of a container are lazy. Narrowing the set per
/// room is <c>interactable_type.allowed_room_tags</c>'s job, exactly as it already is
/// for node interiors.
/// </remarks>
public sealed class SiteInteractable
{
    /// <summary>Stable id, unique within the site.</summary>
    public int Id { get; init; }

    /// <summary>The room this sits in.</summary>
    public SiteRoomId RoomId { get; init; }

    /// <summary>What kind of thing it is.</summary>
    public InteractableType Kind { get; init; }

    /// <summary>Where it sits in its room, in centimetres.</summary>
    public Fixed32 X { get; init; }

    /// <summary>
    /// The loot table this exposes, or 0. Only a <see cref="InteractableType.Container"/>
    /// has one.
    /// </summary>
    /// <remarks>
    /// The promise that something is here, never the contents — those are lazy and are
    /// generated from the room's id on first observation.
    /// </remarks>
    public int LootTableId { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"interactable {Id} {Kind} in {RoomId} @{X.Raw}cm";
}

/// <summary>
/// A guard, and the route it walks.
/// </summary>
/// <remarks>
/// <para>
/// The route is committed to the layout rather than generated when the guard first
/// moves, for the same reason guards are on the mission map's skeleton at all: a
/// scouted room has to be able to promise a guard band, and it cannot do that by
/// generating the guard's contents.
/// </para>
/// <para>
/// <b>The route is traversable, by construction and by assertion.</b> Every hop is a
/// connection whose <c>usable_by_npc</c> is set, so a guard is never given a vent or a
/// window to walk. A route that cannot be walked is not a difficult patrol, it is a
/// guard stuck in a cupboard, and <see cref="SiteLayout.Validate"/> refuses to accept
/// one.
/// </para>
/// </remarks>
public sealed class SiteGuard
{
    /// <summary>Stable id, unique within the site.</summary>
    public SiteGuardId Id { get; init; }

    /// <summary>Foreign key into <c>guard_archetype</c>.</summary>
    public int ArchetypeId { get; init; }

    /// <summary>Localization key for the archetype's name. Core never produces prose.</summary>
    public string NameKey { get; init; } = string.Empty;

    /// <summary>What this guard is for.</summary>
    public SiteGuardRole Role { get; init; }

    /// <summary>
    /// The rooms this guard walks, in order. Always at least one.
    /// </summary>
    /// <remarks>
    /// A sentry's route is a single room, which is the honest encoding of "stands
    /// there": a two-room route would have it walking to a door and back forever, which
    /// reads as a patrol in the ascii dump and is not what the archetype is.
    /// </remarks>
    public IReadOnlyList<SiteRoomId> PatrolRoute { get; init; } = Array.Empty<SiteRoomId>();

    /// <summary>The room the route starts from, and the one it returns to.</summary>
    public SiteRoomId HomeRoomId { get; init; }

    /// <summary>True when this guard holds one room rather than walking.</summary>
    public bool IsStationary => PatrolRoute.Count <= 1;

    /// <inheritdoc/>
    public override string ToString()
        => $"{Id} {NameKey} {Role} route=[{string.Join(" ", PatrolRoute)}]";
}

/// <summary>
/// A civilian standing in a room.
/// </summary>
/// <remarks>
/// Deliberately thinner than <see cref="SiteGuard"/>. A civilian has no archetype, no
/// vision cone and no route in stage 4a: what matters about them for generation is that
/// the room they occupy is a room a guard patrols through, and that belongs to stage 4c's
/// alarm model. A route here would be invented authority over how they move.
/// </remarks>
public sealed class SiteCivilian
{
    /// <summary>Stable id, unique within the site.</summary>
    public SiteCivilianId Id { get; init; }

    /// <summary>The room they are standing in.</summary>
    public SiteRoomId RoomId { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Id} in {RoomId}";
}

// A journey between regions is a SiteConnection, not a separate edge type.
//
// It used to be a `SiteTravelEdge` — "one way, a step cost, and no place on any floor" —
// because the post was somewhere the pathfinder could not route to. That made the
// forward post decorative: `Pathfinder` builds its graph from `SiteConnection`s, so no
// route ever crossed into the post, `RoleBehaviours` could never send a Handler there,
// and a Handler told to hold the post stood in the doorway of the building instead.
//
// A separate edge type is also two sources of truth. The cost would live on the travel
// edge while the door state lived on a connection, and the two could disagree about
// whether the post was reachable at all. As a connection there is one hop, one step
// cost, one `UsableByNpc`, and everything downstream — routing, action legality,
// serialization, the ASCII dump — reads it without knowing the post is special.