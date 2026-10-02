namespace ProjectSpy.Core;

/// <summary>
/// Why a placement attempt failed. The UI maps these to localized text; Core never
/// produces an English sentence (knowledge.md rule 4).
/// </summary>
public enum PlacementError
{
    /// <summary>The placement is legal.</summary>
    None = 0,

    /// <summary>The room would sit in a layer that does not exist.</summary>
    LayerOutOfRange = 1,

    /// <summary>A named slot index is outside the layout's slot space.</summary>
    SlotOutOfRange = 2,

    /// <summary>One or more named slots are already taken.</summary>
    SlotOccupied = 3,

    /// <summary>No slots were named. A room must occupy at least one.</summary>
    NoSlots = 4,

    /// <summary>The room type is not in the table.</summary>
    UnknownRoomType = 5,

    /// <summary>The room type is not unlocked yet.</summary>
    LockedByStory = 6,

    /// <summary>The layer is shallower than this room type requires.</summary>
    LayerTooShallow = 7,

    /// <summary>A named neighbour does not exist, or is this room itself.</summary>
    UnknownNeighbour = 8,

    /// <summary>A named neighbour sits in a different layer, so it cannot touch.</summary>
    NeighbourInOtherLayer = 9,
}

/// <summary>
/// The base cutaway: rooms distributed across abstract slots and depth layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>No coordinates (knowledge.md rule 10).</b> The layout used to be a
/// <c>Width</c> x <c>Height</c> lattice with an occupancy array, and rooms knew
/// their cell. That baked a 2D presentation — rooms in rows on a grid — into the
/// simulation, which made three things wrong at once: the same code could not
/// present the base any other way, adjacency became arithmetic rather than data,
/// and a "width" in cells became a field that every save format would inherit.
/// </para>
/// <para>
/// What remains is the part that is genuinely a rule: rooms occupy
/// <em>slot indices</em>, sit in a <em>layer</em>, and are <em>adjacent</em> to an
/// explicit set of other rooms. Placement declares all three. Presentation maps
/// slots and layers onto whatever it likes.
/// </para>
/// <para>
/// <b>Layer survives deliberately.</b> Depth is a rule input, not a rendering one:
/// rooms in deeper layers cost more to build
/// (<see cref="ApplyDepthCost"/>). Only the <em>interpretation</em> of a layer is
/// presentational.
/// </para>
/// <para>
/// Adjacency is symmetric and maintained here, so a room never has to trust a
/// one-sided declaration. Merging therefore does not depend on which of two
/// adjacent rooms the player clicked — which matters because that order-dependence
/// was once a real source of replay divergence.
/// </para>
/// </remarks>
public sealed class BaseLayout
{
    /// <summary>How many depth layers the layout has.</summary>
    public int LayerCount { get; }

    /// <summary>How many slots each layer has. Slot indices are per-layer.</summary>
    public int SlotsPerLayer { get; }

    /// <summary>Room occupying each slot, keyed by layer then slot. Empty is null.</summary>
    private readonly Dictionary<int, Dictionary<int, Room>> _slots = new();

    private readonly Dictionary<RoomId, Room> _rooms = new();
    private readonly List<Room> _ordered = new();
    private int _nextRoomId = 1;

    /// <summary>
    /// Per-layer build-cost multiplier in percent. Layer 0 costs 100%. Deeper
    /// layers cost more, which is what pushes the player to expand shallow before
    /// deep.
    /// </summary>
    public int[] DepthCostModifier { get; private set; }

    /// <summary>Room types currently unlocked by story progress.</summary>
    public HashSet<int> UnlockedRoomTypeIds { get; } = new();

    /// <summary>Minimum layer a given room type may be placed in.</summary>
    public Dictionary<int, int> MinimumDepthByType { get; } = new();

    public BaseLayout(int layerCount = 12, int slotsPerLayer = 24)
    {
        if (layerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(layerCount), layerCount, "Layer count must be positive.");

        if (slotsPerLayer <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotsPerLayer), slotsPerLayer, "Slots per layer must be positive.");

        LayerCount = layerCount;
        SlotsPerLayer = slotsPerLayer;
        DepthCostModifier = DefaultDepthCurve(layerCount);
    }

    /// <summary>All placed rooms, in placement order.</summary>
    public IReadOnlyList<Room> Rooms => _ordered;

    /// <summary>Number of placed rooms.</summary>
    public int RoomCount => _ordered.Count;

    /// <summary>Total slots across every layer.</summary>
    public int SlotCapacity => LayerCount * SlotsPerLayer;

    /// <summary>
    /// Default depth curve: each layer deeper costs 10% more than the one above,
    /// capped at 100% extra.
    /// </summary>
    private static int[] DefaultDepthCurve(int layerCount)
    {
        var curve = new int[layerCount];
        for (int layer = 0; layer < layerCount; layer++)
            curve[layer] = Math.Min(100 + (layer * 10), 200);

        return curve;
    }

    /// <summary>Replaces the depth cost curve. Called by the table loader.</summary>
    public void SetDepthCostModifier(IReadOnlyList<int> curve)
    {
        if (curve is null) throw new ArgumentNullException(nameof(curve));

        if (curve.Count != LayerCount)
            throw new ArgumentException(
                $"Depth curve has {curve.Count} entries but the layout has {LayerCount} layers.",
                nameof(curve));

        for (int i = 0; i < curve.Count; i++)
        {
            if (curve[i] <= 0)
                throw new ArgumentException($"Depth cost modifier at layer {i} must be positive.", nameof(curve));
        }

        DepthCostModifier = curve.ToArray();
    }

    // ---- Placement -----------------------------------------------------------

    /// <summary>
    /// Why the given placement would fail, or <see cref="PlacementError.None"/>.
    /// </summary>
    /// <remarks>
    /// Validates everything before anything is mutated, so a rejected placement
    /// leaves the layout byte-for-byte as it was.
    /// </remarks>
    public PlacementError CheckPlacement(
        int typeId,
        int layer,
        IReadOnlyCollection<int> slotIndices,
        IReadOnlyCollection<RoomId>? adjacentRoomIds = null)
    {
        if (slotIndices is null || slotIndices.Count == 0)
            return PlacementError.NoSlots;

        if (layer < 0 || layer >= LayerCount)
            return PlacementError.LayerOutOfRange;

        foreach (int slot in slotIndices)
        {
            if (slot < 0 || slot >= SlotsPerLayer)
                return PlacementError.SlotOutOfRange;
        }

        // Deduped so a repeated slot in the input reports as occupied-by-itself
        // rather than quietly passing twice.
        foreach (int slot in slotIndices.Distinct())
        {
            if (RoomInSlot(layer, slot) is not null)
                return PlacementError.SlotOccupied;
        }

        if (!UnlockedRoomTypeIds.Contains(typeId) && UnlockedRoomTypeIds.Count > 0)
            return PlacementError.LockedByStory;

        if (MinimumDepthByType.TryGetValue(typeId, out int minLayer) && layer < minLayer)
            return PlacementError.LayerTooShallow;

        if (adjacentRoomIds is not null)
        {
            foreach (RoomId neighbourId in adjacentRoomIds)
            {
                Room? neighbour = GetRoom(neighbourId);

                if (neighbour is null || neighbourId == RoomId.None)
                    return PlacementError.UnknownNeighbour;

                if (neighbour.Layer != layer)
                    return PlacementError.NeighbourInOtherLayer;
            }
        }

        return PlacementError.None;
    }

    /// <summary>True when the placement would be accepted.</summary>
    public bool CanPlace(
        int typeId,
        int layer,
        IReadOnlyCollection<int> slotIndices,
        IReadOnlyCollection<RoomId>? adjacentRoomIds = null)
        => CheckPlacement(typeId, layer, slotIndices, adjacentRoomIds) == PlacementError.None;

    /// <summary>
    /// Places a room. Returns the new room, or <c>null</c> with
    /// <paramref name="error"/> set. The layout is never left partially modified.
    /// </summary>
    public Room? Place(
        int typeId,
        int layer,
        IReadOnlyCollection<int> slotIndices,
        Tick builtOnTick,
        out PlacementError error,
        IReadOnlyCollection<RoomId>? adjacentRoomIds = null)
    {
        error = CheckPlacement(typeId, layer, slotIndices, adjacentRoomIds);
        if (error != PlacementError.None)
            return null;

        var room = new Room
        {
            Id = new RoomId(_nextRoomId++),
            TypeId = typeId,
            Layer = layer,
            Level = 1,
            Condition = Room.MaxCondition,
            BuiltOnTick = builtOnTick,
        };

        foreach (int slot in slotIndices.Distinct())
        {
            room.SlotIndices.Add(slot);
            OccupySlot(layer, slot, room);
        }

        _rooms[room.Id] = room;
        _ordered.Add(room);

        LinkNeighbours(room, adjacentRoomIds);
        return room;
    }

    /// <summary>Places a room, discarding the failure reason.</summary>
    public Room? Place(
        int typeId,
        int layer,
        IReadOnlyCollection<int> slotIndices,
        Tick builtOnTick,
        IReadOnlyCollection<RoomId>? adjacentRoomIds = null)
        => Place(typeId, layer, slotIndices, builtOnTick, out _, adjacentRoomIds);

    /// <summary>Removes a room, frees its slots and unlinks its neighbours.</summary>
    public bool Demolish(RoomId id)
    {
        if (!_rooms.TryGetValue(id, out Room? room))
            return false;

        foreach (int slot in room.SlotIndices)
            VacateSlot(room.Layer, slot, id);

        // Symmetry has to be repaired on both sides or the survivor keeps a
        // dangling adjacency to a room that no longer exists.
        foreach (RoomId neighbourId in room.AdjacentRoomIds.ToArray())
        {
            if (_rooms.TryGetValue(neighbourId, out Room? neighbour))
                neighbour.AdjacentRoomIds.Remove(id);
        }

        room.AdjacentRoomIds.Clear();
        _rooms.Remove(id);
        _ordered.Remove(room);
        return true;
    }

    // ---- adjacency -----------------------------------------------------------

    /// <summary>
    /// Makes <paramref name="adjacency"/> symmetric between the given rooms.
    /// </summary>
    /// <remarks>
    /// Every mutation of adjacency goes through here. Asymmetric adjacency would
    /// make merging depend on which room the player clicked, which is precisely
    /// the order-dependence that makes replays diverge.
    /// </remarks>
    private void LinkNeighbours(Room room, IReadOnlyCollection<RoomId>? adjacency)
    {
        if (adjacency is null)
            return;

        foreach (RoomId neighbourId in adjacency)
        {
            if (!_rooms.TryGetValue(neighbourId, out Room? neighbour))
                continue;

            if (neighbour.Id == room.Id)
                continue;

            room.AdjacentRoomIds.Add(neighbour.Id);
            neighbour.AdjacentRoomIds.Add(room.Id);
        }
    }

    /// <summary>True when two rooms declare each other adjacent.</summary>
    public bool AreAdjacent(RoomId a, RoomId b)
    {
        Room? room = GetRoom(a);
        return room is not null && room.AdjacentRoomIds.Contains(b);
    }

    /// <summary>Every room declared adjacent to <paramref name="id"/>, in placement order.</summary>
    public IReadOnlyList<Room> NeighboursOf(RoomId id)
    {
        Room? room = GetRoom(id);
        if (room is null)
            return Array.Empty<Room>();

        var matches = new List<Room>();
        foreach (Room candidate in _ordered)
        {
            if (candidate.Id != id && room.AdjacentRoomIds.Contains(candidate.Id))
                matches.Add(candidate);
        }

        return matches;
    }

    // ---- Merging -------------------------------------------------------------

    /// <summary>
    /// Folds every adjacent, mergeable neighbour into the canonical room of the
    /// group, repeating until no further merge is possible.
    /// </summary>
    /// <returns>
    /// The id of the surviving room — always the lowest-slot member of the merged
    /// group, so the result does not depend on which room the player clicked — or
    /// <see cref="RoomId.None"/> if the id is unknown.
    /// </returns>
    public RoomId TryMergeAdjacent(RoomId id)
    {
        if (!_rooms.TryGetValue(id, out Room? room))
            return RoomId.None;

        // Re-anchor on the canonical room of the connected group. Absorbing a
        // low-slot neighbour into a high-slot room would otherwise leave the
        // survivor in a different state depending on click order, which is exactly
        // the kind of order-dependence that makes replays diverge.
        foreach (Room candidate in _ordered.ToArray())
        {
            if (ReferenceEquals(candidate, room))
                continue;

            if (room.CanMergeWith(candidate) && candidate.LowestSlotIndex < room.LowestSlotIndex)
                room = candidate;
        }

        bool merged = true;
        while (merged)
        {
            merged = false;

            // Take a snapshot: Absorb/Demolish mutate the backing collections.
            foreach (Room candidate in _ordered.ToArray())
            {
                if (ReferenceEquals(candidate, room))
                    continue;

                if (!room.CanMergeWith(candidate))
                    continue;

                RoomId survivor = room.Id;
                room.Absorb(candidate);
                Demolish(candidate.Id);

                // Demolish stripped the absorbed room's edges out of the survivor,
                // so re-link what is left before merging any further.
                RebindAdjacency(survivor);
                merged = true;
                break;
            }
        }

        return room.Id;
    }

    /// <summary>Rooms that could merge into <paramref name="id"/>'s room.</summary>
    public IReadOnlyList<Room> FindMergeCandidates(RoomId id)
    {
        if (!_rooms.TryGetValue(id, out Room? room))
            return Array.Empty<Room>();

        var candidates = new List<Room>();
        foreach (Room candidate in _ordered)
        {
            if (ReferenceEquals(candidate, room))
                continue;

            if (room.CanMergeWith(candidate))
                candidates.Add(candidate);
        }

        return candidates;
    }

    /// <summary>
    /// Drops edges to rooms that no longer exist and links the survivor to its
    /// absorbed room's remaining neighbours.
    /// </summary>
    private void RebindAdjacency(RoomId id)
    {
        if (!_rooms.TryGetValue(id, out Room? room))
            return;

        // Drop edges to rooms that no longer exist, and any self-edge. Symmetry was
        // maintained by Demolish on the way out, so only the survivor's own set needs
        // repairing here.
        room.AdjacentRoomIds.RemoveWhere(
            neighbourId => neighbourId == room.Id || !_rooms.ContainsKey(neighbourId));

        foreach (Room neighbour in _ordered)
        {
            if (neighbour.Id == room.Id)
                continue;

            if (!room.AdjacentRoomIds.Contains(neighbour.Id))
                continue;

            neighbour.AdjacentRoomIds.Add(room.Id);
        }
    }

    // ---- occupancy queries ---------------------------------------------------

    /// <summary>True when the slot is inside the layout and occupied.</summary>
    public bool IsSlotOccupied(int layer, int slotIndex) => RoomInSlot(layer, slotIndex) is not null;

    /// <summary>The room occupying a slot, or null.</summary>
    public Room? RoomInSlot(int layer, int slotIndex)
    {
        if (!_slots.TryGetValue(layer, out Dictionary<int, Room>? row))
            return null;

        return row.TryGetValue(slotIndex, out Room? room) ? room : null;
    }

    private void OccupySlot(int layer, int slotIndex, Room room)
    {
        if (!_slots.TryGetValue(layer, out Dictionary<int, Room>? row))
        {
            row = new Dictionary<int, Room>();
            _slots[layer] = row;
        }

        row[slotIndex] = room;
    }

    private void VacateSlot(int layer, int slotIndex, RoomId owner)
    {
        if (!_slots.TryGetValue(layer, out Dictionary<int, Room>? row))
            return;

        // Only clear the slot if this room still owns it. Clearing blindly would
        // let a stale reference erase a room that has since taken the slot.
        if (row.TryGetValue(slotIndex, out Room? room) && room.Id == owner)
            row.Remove(slotIndex);
    }

    /// <summary>Looks up a room by id.</summary>
    public Room? GetRoom(RoomId id) => _rooms.TryGetValue(id, out Room? room) ? room : null;

    /// <summary>Rooms in a given depth layer.</summary>
    public IReadOnlyList<Room> RoomsAtLayer(int layer)
    {
        var matches = new List<Room>();
        foreach (Room room in _ordered)
        {
            if (room.Layer == layer)
                matches.Add(room);
        }

        return matches;
    }

    /// <summary>All rooms of a given type.</summary>
    public IReadOnlyList<Room> RoomsOfType(int typeId)
    {
        var matches = new List<Room>();
        foreach (Room room in _ordered)
        {
            if (room.TypeId == typeId)
                matches.Add(room);
        }

        return matches;
    }

    /// <summary>Total occupied slots.</summary>
    public int OccupiedSlotCount()
    {
        int count = 0;
        foreach (Room room in _ordered)
            count += room.SlotCount;

        return count;
    }

    // ---- cost ----------------------------------------------------------------

    /// <summary>
    /// Applies a depth cost modifier to a base cost.
    /// </summary>
    /// <remarks>
    /// Integer arithmetic throughout: a float here would make build costs depend on
    /// rounding, and costs are part of the save contract (knowledge.md rule 6).
    /// </remarks>
    public long ApplyDepthCost(long baseCost, int layer)
    {
        if (baseCost <= 0)
            return 0;

        if (layer < 0 || layer >= LayerCount)
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "Layer out of range.");

        int modifier = DepthCostModifier[layer];
        return baseCost + ((baseCost * (modifier - 100)) / 100);
    }

    /// <summary>The build cost of a room in a given layer, after the modifier.</summary>
    public long ComputeBuildCost(long baseCost, int layer) => ApplyDepthCost(baseCost, layer);
}
