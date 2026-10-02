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
/// A room occupies <see cref="Width"/> contiguous cells along the grid row it was
/// placed on. Depth is the grid row index, which is what the depth cost modifier
/// keys off — see <see cref="BaseLayout.DepthCostModifier"/>.
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

    /// <summary>Leftmost grid column occupied.</summary>
    public int GridX { get; set; }

    /// <summary>Grid row occupied. Doubles as the room's depth.</summary>
    public int GridY { get; set; }

    /// <summary>Width in cells.</summary>
    public int Width { get; set; } = 1;

    public int Level { get; set; } = 1;

    /// <summary>Structural condition, 0 (ruined) to 100 (as new).</summary>
    public int Condition { get; set; } = MaxCondition;

    /// <summary>Agents currently assigned here.</summary>
    public List<AgentId> AssignedAgentIds { get; } = new();

    public Tick BuiltOnTick { get; set; }

    /// <summary>Ticks of construction still remaining. Zero once finished.</summary>
    public int ConstructionTicksRemaining { get; set; }

    /// <summary>Rightmost grid column occupied (inclusive).</summary>
    public int MaxX => GridX + Width - 1;

    /// <summary>True while the room is still being built.</summary>
    public bool IsUnderConstruction => ConstructionTicksRemaining > 0;

    /// <summary>True when the room is damaged enough to need repair.</summary>
    public bool NeedsRepair => Condition < MaxCondition;

    /// <summary>
    /// True when <paramref name="other"/> can be absorbed by this room: same type,
    /// same row, same level, directly adjacent, and both finished building.
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
        if (GridY != other.GridY) return false;
        if (IsUnderConstruction || other.IsUnderConstruction) return false;

        return Adjacent(other);
    }

    /// <summary>True when the two rooms sit side by side with no gap.</summary>
    public bool Adjacent(Room other)
    {
        if (other is null) return false;

        bool touchesRight = other.GridX == MaxX + 1;
        bool touchesLeft = other.MaxX == GridX - 1;
        return touchesRight || touchesLeft;
    }

    /// <summary>True when this room covers the given cell.</summary>
    public bool Contains(int x, int y) => y == GridY && x >= GridX && x <= MaxX;

    /// <summary>Absorbs <paramref name="other"/>'s width, widening this room.</summary>
    public void Absorb(Room other)
    {
        if (other is null) return;

        int newMinX = Math.Min(GridX, other.GridX);
        int newMaxX = Math.Max(MaxX, other.MaxX);
        GridX = newMinX;
        Width = newMaxX - newMinX + 1;

        // The merged room takes the worse of the two conditions.
        Condition = Math.Min(Condition, other.Condition);
    }

    /// <summary>Clamps level and condition into legal ranges.</summary>
    public void Normalize()
    {
        if (Width < 1) Width = 1;
        if (Level < 1) Level = 1;
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

    /// <summary>True when the cell holds an assigned agent.</summary>
    public bool HasAgent(AgentId id) => AssignedAgentIds.Contains(id);
}
