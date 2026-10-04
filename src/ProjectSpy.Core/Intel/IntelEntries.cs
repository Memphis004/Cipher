using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core;

/// <summary>
/// One piece of what the player believes about a site.
/// </summary>
/// <remarks>
/// <para>
/// A closed hierarchy rather than one wide record with optional members, because the
/// failure this system can have is silently telling the player something that is not
/// true — and the easiest way to do that is to have a single struct with a
/// <c>PatrolRoute</c> field that is empty because nobody filled it in, rather than
/// because the answer is "no patrols here". A distinct type per kind of fact means a
/// caller asking for patrols gets patrols, and a snapshot cannot hold a half-populated
/// connection.
/// </para>
/// <para>
/// <b>No entry carries a room's interior.</b> knowledge.md rule 11 governs unobserved
/// room contents, and rule 11 says explicitly that intel reveals layout, room types and
/// connections — not the loot and props inside a room nobody has entered. Nothing here
/// may grow a field that could hold one, and <c>SiteLazyContentTests</c> asserts it.
/// </para>
/// </remarks>
public abstract record IntelEntry
{
    /// <summary>How the player came to know this, and how much to trust it.</summary>
    public IntelConfidence Confidence { get; init; }

    /// <summary>Who told us.</summary>
    public IntelSource Source { get; init; }

    /// <summary>
    /// True when the entry carries no content of its own and exists only to say that
    /// something is known — the floor count, for instance, which is a property of the
    /// snapshot rather than of any one room.
    /// </summary>
    public virtual bool IsEmpty => false;

    /// <summary>Which sort of fact this is.</summary>
    /// <remarks>
    /// Named <c>FactKind</c> rather than <c>Kind</c> because
    /// <see cref="IntelConnectionEntry"/> has a kind of its own: the base property answers
    /// "what sort of claim is this", the connection's answers "what sort of way through
    /// is this", and overloading one name for both is how the wrong one gets read.
    /// </remarks>
    public abstract IntelFactKind FactKind { get; }
}

/// <summary>Which sort of fact an entry carries.</summary>
/// <remarks>
/// Present so that code holding a bare <see cref="IntelEntry"/> — the save format, the
/// event log, a UI diffing two snapshots — can say what it is looking at without
/// re-testing the type. Also what <see cref="IntelContradicted"/> names, since the
/// tactical layer contradicts a room or a connection and nothing else.
/// </remarks>
public enum IntelFactKind
{
    /// <summary>A room.</summary>
    Room = 0,

    /// <summary>A connection between rooms.</summary>
    Connection = 1,

    /// <summary>A guard's patrol route.</summary>
    Patrol = 2,

    /// <summary>Where a prisoner is held.</summary>
    HoldingRoom = 3,
}

/// <summary>What is known about one room.</summary>
public sealed record IntelRoomEntry : IntelEntry
{
    /// <inheritdoc/>
    public override IntelFactKind FactKind => IntelFactKind.Room;
    /// <summary>Which room this describes.</summary>
    public SiteRoomId RoomId { get; init; }

    /// <summary>Floor the room is on.</summary>
    public int FloorIndex { get; init; }

    /// <summary>Left edge of the room, in centimetres along the floor.</summary>
    public Fixed32 StartX { get; init; }

    /// <summary>Right edge of the room, in centimetres along the floor.</summary>
    public Fixed32 EndX { get; init; }

    /// <summary>
    /// Localization key for the room's name, or empty when the band does not reveal
    /// room types.
    /// </summary>
    public string NameKey { get; init; } = string.Empty;

    /// <summary>The room template, or zero when the band does not reveal room types.</summary>
    /// <remarks>
    /// Zero rather than a nullable: it is a foreign key, and "no foreign key" is what a
    /// missing one looks like everywhere else in the codebase. The band is on the
    /// snapshot, so a caller always knows whether the zero is meaningful.
    /// </remarks>
    public int RoomTemplateId { get; init; }

    /// <summary>Guards believed to be here, or -1 when the band does not say.</summary>
    public int GuardCount { get; init; } = -1;

    /// <summary>Civilians believed to be here, or -1 when the band does not say.</summary>
    public int CivilianCount { get; init; } = -1;

    /// <summary>
    /// How lit the room is believed to be. Meaningless unless
    /// <see cref="LightLevelKnown"/> is true.
    /// </summary>
    public SiteLightLevel LightLevel { get; init; }

    /// <summary>True when the band reveals light levels.</summary>
    public bool LightLevelKnown { get; init; }

    /// <summary>Special roles the player has been told this room has.</summary>
    public IntelKnownRole Roles { get; init; }

    /// <summary>True when the sleeper's report is that nothing is in this room.</summary>
    public override bool IsEmpty => RoomTemplateId == 0;
}

/// <summary>What is known about one connection between two rooms.</summary>
public sealed record IntelConnectionEntry : IntelEntry
{
    /// <inheritdoc/>
    public override IntelFactKind FactKind => IntelFactKind.Connection;
    /// <summary>Which connection this describes.</summary>
    public SiteConnectionId ConnectionId { get; init; }

    /// <summary>One side.</summary>
    public SiteRoomId RoomA { get; init; }

    /// <summary>Floor of <see cref="RoomA"/>.</summary>
    public int FloorIndexA { get; init; }

    /// <summary>The other side.</summary>
    public SiteRoomId RoomB { get; init; }

    /// <summary>Floor of <see cref="RoomB"/>.</summary>
    public int FloorIndexB { get; init; }

    /// <summary>What kind of way through this is.</summary>
    public SiteConnectionKind Kind { get; init; }

    /// <summary>True when this joins two floors rather than two rooms.</summary>
    public bool IsVertical { get; init; }

    /// <summary>
    /// Whether the player believes this is locked.
    /// </summary>
    /// <remarks>
    /// The field a poisoned operation lies about most often, and the reason the
    /// guarantee "poisoned intel is never unwinnable" is worth stating carefully: a lie
    /// here costs the team time and a route, so the poison is built so that at least one
    /// route to the claimed objective survives every lie it tells.
    /// </remarks>
    public bool IsLocked { get; init; }
}

/// <summary>What is known about where one guard walks.</summary>
public sealed record IntelPatrolEntry : IntelEntry
{
    /// <inheritdoc/>
    public override IntelFactKind FactKind => IntelFactKind.Patrol;
    /// <summary>Which guard this describes.</summary>
    public SiteGuardId GuardId { get; init; }

    /// <summary>The guard archetype, so the UI can name the kind of guard.</summary>
    public int ArchetypeId { get; init; }

    /// <summary>What the guard is for.</summary>
    public SiteGuardRole Role { get; init; }

    /// <summary>
    /// The rooms the guard is believed to visit, in order.
    /// </summary>
    /// <remarks>
    /// Deliberately a list of rooms and not a list of connections. A route a player can
    /// read is a sequence of places; the doors between them are already known from the
    /// connection entries, and duplicating them here would give poisoning a second,
    /// redundant way to invent a door that is not there.
    /// </remarks>
    public IReadOnlyList<SiteRoomId> Route { get; init; } = Array.Empty<SiteRoomId>();

    /// <summary>The room the guard is believed to sit in when not walking.</summary>
    public SiteRoomId HomeRoomId { get; init; }
}

/// <summary>Where a captured agent is being held.</summary>
public sealed record IntelHoldingEntry : IntelEntry
{
    /// <inheritdoc/>
    public override IntelFactKind FactKind => IntelFactKind.HoldingRoom;
    /// <summary>The missing agent.</summary>
    public AgentId AgentId { get; init; }

    /// <summary>The room they are believed to be in.</summary>
    public SiteRoomId RoomId { get; init; }

    /// <summary>
    /// Ticks left before they are lost.
    /// </summary>
    /// <remarks>
    /// Carried on the entry rather than looked up, because the countdown keeps running
    /// while the team searches and the base screen has to show it ticking beside the
    /// room number. A rescue that revealed only the room would leave the player unable
    /// to tell a rescue worth mounting from one that is already too late.
    /// </remarks>
    public int TicksUntilLost { get; init; }
}