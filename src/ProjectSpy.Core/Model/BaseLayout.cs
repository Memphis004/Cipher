namespace ProjectSpy.Core;

/// <summary>
/// Why a placement attempt failed. The UI maps these to localized text; Core never
/// produces an English sentence (knowledge.md rule 4).
/// </summary>
public enum PlacementError
{
    None = 0,
    OutOfBounds = 1,
    OverlapsExisting = 2,
    NonPositiveWidth = 3,
    UnknownRoomType = 4,
    LockedByStory = 5,
    DepthTooShallow = 6,
}

/// <summary>
/// The base cutaway grid: a 2D lattice where depth is the vertical axis.
/// </summary>
/// <remarks>
/// <para>
/// The grid is <c>Width</c> columns by <c>Height</c> rows. A room occupies
/// <c>Width</c> contiguous cells in a single row; its depth is that row index.
/// Deeper rooms are more expensive to build — that is what
/// <see cref="DepthCostModifier"/> encodes, and it is the pressure that makes the
/// cutaway layout a decision rather than a container.
/// </para>
/// <para>
/// Occupancy is tracked in a flat array rather than by scanning rooms, so
/// <see cref="CanPlace"/> stays O(width) regardless of how many rooms exist.
/// </para>
/// </remarks>
public sealed class BaseLayout
{
    /// <summary>Total grid columns.</summary>
    public int Width { get; }

    /// <summary>Total grid rows, i.e. the number of depth layers.</summary>
    public int Height { get; }

    /// <summary>Occupied-cell index per grid cell; 0 means empty.</summary>
    private readonly int[] _occupancy;

    private readonly Dictionary<RoomId, Room> _rooms = new();
    private readonly List<Room> _ordered = new();
    private int _nextRoomId = 1;

    /// <summary>
    /// Per-depth build-cost multiplier in percent. Depth 0 costs 100%. Deeper rows
    /// cost more, which is what pushes the player to expand shallow before deep.
    /// </summary>
    /// <remarks>
    /// TODO(stage-2): source the per-depth curve from room_type.csv rather than this
    /// default, once tables exist. Kept settable so the table loader can replace it
    /// without touching call sites.
    /// </remarks>
    public int[] DepthCostModifier { get; private set; }

    /// <summary>Room types currently unlocked by story progress.</summary>
    public HashSet<int> UnlockedRoomTypeIds { get; } = new();

    /// <summary>Minimum depth row a given room type may be placed on.</summary>
    public Dictionary<int, int> MinimumDepthByType { get; } = new();

    public BaseLayout(int width = 24, int height = 12)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        Width = width;
        Height = height;
        _occupancy = new int[width * height];
        DepthCostModifier = DefaultDepthCurve(height);
    }

    /// <summary>All placed rooms, in placement order.</summary>
    public IReadOnlyList<Room> Rooms => _ordered;

    /// <summary>Number of placed rooms.</summary>
    public int RoomCount => _ordered.Count;

    /// <summary>
    /// Default depth curve: each row deeper costs 10% more than the row above,
    /// capped at 100% extra.
    /// </summary>
    private static int[] DefaultDepthCurve(int height)
    {
        var curve = new int[height];
        for (int y = 0; y < height; y++)
            curve[y] = Math.Min(100 + (y * 10), 200);
        return curve;
    }

    /// <summary>Replaces the depth cost curve. Called by the stage-2 table loader.</summary>
    public void SetDepthCostModifier(IReadOnlyList<int> curve)
    {
        if (curve is null) throw new ArgumentNullException(nameof(curve));
        if (curve.Count != Height)
            throw new ArgumentException(
                $"Depth curve has {curve.Count} entries but the grid has {Height} rows.",
                nameof(curve));

        for (int i = 0; i < curve.Count; i++)
        {
            if (curve[i] <= 0)
                throw new ArgumentException($"Depth cost modifier at row {i} must be positive.", nameof(curve));
        }

        DepthCostModifier = curve.ToArray();
    }

    // ---- Placement -----------------------------------------------------------

    /// <summary>Why the given placement would fail, or <see cref="PlacementError.None"/>.</summary>
    public PlacementError CheckPlacement(int typeId, int x, int y, int width)
    {
        if (width <= 0)
            return PlacementError.NonPositiveWidth;

        if (x < 0 || y < 0 || y >= Height || x + width > Width)
            return PlacementError.OutOfBounds;

        if (!UnlockedRoomTypeIds.Contains(typeId) && UnlockedRoomTypeIds.Count > 0)
            return PlacementError.LockedByStory;

        if (MinimumDepthByType.TryGetValue(typeId, out int minDepth) && y < minDepth)
            return PlacementError.DepthTooShallow;

        for (int cx = x; cx < x + width; cx++)
        {
            if (_occupancy[(y * Width) + cx] != 0)
                return PlacementError.OverlapsExisting;
        }

        return PlacementError.None;
    }

    /// <summary>True when the placement would be accepted.</summary>
    public bool CanPlace(int typeId, int x, int y, int width)
        => CheckPlacement(typeId, x, y, width) == PlacementError.None;

    /// <summary>
    /// Places a room. Returns the new room, or <c>null</c> with
    /// <paramref name="error"/> set. The grid is never left partially modified.
    /// </summary>
    public Room? Place(int typeId, int x, int y, int width, Tick builtOnTick, out PlacementError error)
    {
        error = CheckPlacement(typeId, x, y, width);
        if (error != PlacementError.None)
            return null;

        var room = new Room
        {
            Id = new RoomId(_nextRoomId++),
            TypeId = typeId,
            GridX = x,
            GridY = y,
            Width = width,
            Level = 1,
            Condition = Room.MaxCondition,
            BuiltOnTick = builtOnTick,
        };

        for (int cx = x; cx < x + width; cx++)
            _occupancy[(y * Width) + cx] = room.Id.Value;

        _rooms[room.Id] = room;
        _ordered.Add(room);
        return room;
    }

    /// <summary>Places a room, discarding the failure reason.</summary>
    public Room? Place(int typeId, int x, int y, int width, Tick builtOnTick)
        => Place(typeId, x, y, width, builtOnTick, out _);

    /// <summary>Removes a room and frees its cells. Returns false if it was not placed.</summary>
    public bool Demolish(RoomId id)
    {
        if (!_rooms.TryGetValue(id, out Room? room))
            return false;

        for (int cx = room.GridX; cx <= room.MaxX; cx++)
        {
            int index = (room.GridY * Width) + cx;
            if (index >= 0 && index < _occupancy.Length)
                _occupancy[index] = 0;
        }

        _rooms.Remove(id);
        _ordered.Remove(room);
        return true;
    }

    // ---- Merging -------------------------------------------------------------

    /// <summary>
    /// Folds every adjacent, mergeable neighbour into the leftmost room of the group,
    /// repeating until no further merge is possible.
    /// </summary>
    /// <returns>
    /// The id of the surviving room — always the leftmost member of the merged span,
    /// so the result does not depend on which room the player happened to click — or
    /// <see cref="RoomId.None"/> if the id is unknown.
    /// </returns>
    public RoomId TryMergeAdjacent(RoomId id)
    {
        if (!_rooms.TryGetValue(id, out Room? room))
            return RoomId.None;

        // Re-anchor on the leftmost room of the connected group. Absorbing a
        // left-hand neighbour into a right-hand room would otherwise leave the
        // surviving room at a different grid position depending on click order,
        // which is exactly the kind of order-dependence that makes replays diverge.
        foreach (Room candidate in _ordered.ToArray())
        {
            if (ReferenceEquals(candidate, room))
                continue;

            if (room.CanMergeWith(candidate) && candidate.GridX < room.GridX)
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

                room.Absorb(candidate);
                Demolish(candidate.Id);
                merged = true;
                break;
            }
        }

        return room.Id;
    }

    /// <summary>Rooms on a given row that could merge into <paramref name="id"/>'s room.</summary>
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

    // ---- Occupancy queries ---------------------------------------------------

    /// <summary>True when the cell is inside the grid and occupied.</summary>
    public bool IsOccupied(int x, int y) => RoomAt(x, y) is not null;

    /// <summary>The room covering the cell, or null. Out-of-grid cells return null.</summary>
    public Room? RoomAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return null;

        int id = _occupancy[(y * Width) + x];
        return id == 0 ? null : _rooms[new RoomId(id)];
    }

    /// <summary>Looks up a room by id.</summary>
    public Room? GetRoom(RoomId id) => _rooms.TryGetValue(id, out Room? room) ? room : null;

    /// <summary>Rooms on a given depth row.</summary>
    public IReadOnlyList<Room> RoomsAtDepth(int y)
    {
        var matches = new List<Room>();
        foreach (Room room in _ordered)
        {
            if (room.GridY == y)
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

    /// <summary>Total occupied cells.</summary>
    public int OccupiedCellCount()
    {
        int count = 0;
        foreach (int cell in _occupancy)
        {
            if (cell != 0)
                count++;
        }

        return count;
    }

    /// <summary>Fraction of the grid that is built on, in whole percent.</summary>
    public int OccupancyPercent() => (OccupiedCellCount() * 100) / (Width * Height);

    /// <summary>
    /// Applies a depth cost modifier to a base cost.
    /// </summary>
    /// <remarks>
    /// Integer arithmetic throughout: a float here would make build costs depend on
    /// rounding, and costs are part of the save contract (knowledge.md rule 6).
    /// </remarks>
    public long ApplyDepthCost(long baseCost, int y)
    {
        if (baseCost <= 0)
            return 0;

        if (y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(y), y, "Depth row out of range.");

        int modifier = DepthCostModifier[y];
        return baseCost + ((baseCost * (modifier - 100)) / 100);
    }

    /// <summary>The build cost of a room at a given depth, after the modifier.</summary>
    public long ComputeBuildCost(long baseCost, int y) => ApplyDepthCost(baseCost, y);
}
