namespace ProjectSpy.Core;

/// <summary>Stable identifier for a room.</summary>
public readonly record struct RoomId(int Value) : IComparable<RoomId>
{
    public static readonly RoomId None = new(0);
    public bool IsValid => Value > 0;
    public int CompareTo(RoomId other) => Value.CompareTo(other.Value);
    public override string ToString() => $"R{Value:D4}";
}

/// <summary>
/// A room module sitting in the base cutaway.
/// </summary>
/// <remarks>
/// <para>
/// <b>No coordinates (knowledge.md rule 10).</b> A room is described by the
/// abstract <see cref="SlotIndices"/> it occupies, the <see cref="Layer"/> it sits
/// in, and the <see cref="AdjacentRoomIds"/> it touches. There is no grid cell, no
/// width, no position. Presentation decides what layer 2 and slot 7 look like.
/// </para>
/// <para>
/// The previous version stored <c>GridX</c>/<c>GridY</c>/<c>Width</c> and derived
/// adjacency by comparing coordinates. That put a 2D presentation decision — "rooms
/// sit in a lattice, one row per depth layer" — inside the simulation, where it
/// could not be changed without changing game rules. Worse, adjacency became a
/// function of geometry, so a rule about merging rooms depended on arithmetic
/// nobody reading the rule would think to check.
/// </para>
/// <para>
/// <b>Layer survives deliberately.</b> Depth is a rule input, not a rendering one:
/// rooms in deeper layers cost more to build
/// (<see cref="BaseLayout.ApplyDepthCost"/>). Only the <em>interpretation</em> of a
/// layer is presentational.
/// </para>
/// </remarks>
public sealed class Room
{
    /// <summary>Condition floor below which a room needs repair.</summary>
    public const int MinCondition = 0;

    /// <summary>Condition of a brand-new room.</summary>
    public const int MaxCondition = 100;

    public RoomId Id { get; init; }

    /// <summary>Foreign key into the room_type table (stage 2).</summary>
    public int TypeId { get; set; }

    /// <summary>
    /// Abstract depth stratum. Deeper layers cost more; presentation decides
    /// whether that reads as further underground, further back, or higher up.
    /// </summary>
    public int Layer { get; set; }

    /// <summary>
    /// The abstract slots this room occupies.
    /// </summary>
    /// <remarks>
    /// A set rather than a contiguous span. "Contiguous" is a geometric claim, and
    /// whether a merged room's slots read as adjacent is presentation's business.
    /// Merging unions the sets; nothing else depends on their order.
    /// </remarks>
    public HashSet<int> SlotIndices { get; } = new();

    /// <summary>
    /// Rooms declared adjacent to this one.
    /// </summary>
    /// <remarks>
    /// Explicit data rather than derived geometry, and the single source of truth
    /// for merging. The layout maintains the set symmetrically: adding a room adds
    /// itself to its neighbours' sets and the reverse, so a one-sided declaration
    /// is a bug the validator can catch rather than a silent asymmetry that makes
    /// merging depend on which room the player clicked.
    /// </remarks>
    public HashSet<RoomId> AdjacentRoomIds { get; } = new();

    public int Level { get; set; } = 1;

    /// <summary>Structural condition, 0 (ruined) to 100 (as new).</summary>
    public int Condition { get; set; } = MaxCondition;

    /// <summary>Agents currently assigned here.</summary>
    public List<AgentId> AssignedAgentIds { get; } = new();

    public Tick BuiltOnTick { get; set; }

    /// <summary>Ticks of construction still remaining. Zero once finished.</summary>
    public int ConstructionTicksRemaining { get; set; }

    /// <summary>How many slots this room occupies.</summary>
    public int SlotCount => SlotIndices.Count;

    /// <summary>The lowest slot index occupied, or -1 when the room has no slots.</summary>
    public int LowestSlotIndex => SlotIndices.Count == 0 ? -1 : SlotIndices.Min();

    /// <summary>True while the room is still being built.</summary>
    public bool IsUnderConstruction => ConstructionTicksRemaining > 0;

    /// <summary>True when the room is damaged enough to need repair.</summary>
    public bool NeedsRepair => Condition < MaxCondition;

    /// <summary>True when this room occupies <paramref name="slotIndex"/>.</summary>
    public bool OccupiesSlot(int slotIndex) => SlotIndices.Contains(slotIndex);

    /// <summary>
    /// True when <paramref name="other"/> can be absorbed by this room: same type,
    /// same layer, same level, declared adjacent, and both finished building.
    /// </summary>
    /// <remarks>
    /// Level equality is deliberate — merging a level-2 room into a level-1 one
    /// would silently discard the upgrade, which is the sort of bug that is far
    /// cheaper to forbid here than to explain in a bug report later.
    /// </remarks>
    public bool CanMergeWith(Room other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return false;
        if (TypeId != other.TypeId) return false;
        if (Level != other.Level) return false;

        // Same layer only. Adjacency is already explicit, so without this a room
        // could absorb something the player placed directly above it, which was
        // never a legal merge in the grid version.
        if (Layer != other.Layer) return false;

        if (IsUnderConstruction || other.IsUnderConstruction) return false;

        return AdjacentRoomIds.Contains(other.Id);
    }

    /// <summary>
    /// True when this room declares <paramref name="other"/> adjacent.
    /// </summary>
    /// <remarks>
    /// Reads this side only. <see cref="BaseLayout"/> guarantees the relation is
    /// symmetric, and that is what keeps merging order-independent.
    /// </remarks>
    public bool IsAdjacentTo(Room other)
    {
        if (other is null) return false;
        return AdjacentRoomIds.Contains(other.Id);
    }

    /// <summary>Absorbs <paramref name="other"/>'s slots and adjacency, widening this room.</summary>
    public void Absorb(Room other)
    {
        if (other is null) return;

        foreach (int slot in other.SlotIndices)
            SlotIndices.Add(slot);

        foreach (RoomId neighbour in other.AdjacentRoomIds)
        {
            // Skip both the absorbed room and this room. Skipping only `other` left a
            // self-edge whenever the absorbed room listed us as a neighbour — and
            // since adjacency is symmetric it always does. A room adjacent to itself
            // passes every membership check while being meaningless.
            if (neighbour == other.Id || neighbour == Id)
                continue;

            AdjacentRoomIds.Add(neighbour);
        }

        // The merged room takes the worse of the two conditions.
        Condition = Math.Min(Condition, other.Condition);
    }

    /// <summary>Clamps level and condition into legal ranges.</summary>
    public void Normalize()
    {
        if (Level < 1) Level = 1;
        if (Layer < 0) Layer = 0;
        Condition = Math.Clamp(Condition, MinCondition, MaxCondition);
        if (ConstructionTicksRemaining < 0) ConstructionTicksRemaining = 0;
    }

    /// <summary>Applies wear, clamped at zero.</summary>
    public void ApplyWear(int amount)
    {
        Condition -= Math.Max(0, amount);
        Normalize();
    }

    /// <summary>Restores condition, clamped at <see cref="MaxCondition"/>.</summary>
    public void Repair(int amount)
    {
        Condition += Math.Max(0, amount);
        Normalize();
    }

    /// <summary>True when the room holds an assigned agent.</summary>
    public bool HasAgent(AgentId id) => AssignedAgentIds.Contains(id);
}
