using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves the grid rejects out-of-bounds and overlapping placements, merges adjacent
/// rooms, and applies the depth cost modifier.
/// </summary>
public class BaseLayoutTests
{
    private static BaseLayout NewLayout(int width = 12, int height = 6)
        => new(width, height);

    // ---- bounds --------------------------------------------------------------

    [Fact]
    public void CanPlace_AcceptsAPositionFullyInsideTheGrid()
    {
        BaseLayout layout = NewLayout();

        Assert.True(layout.CanPlace(1, 0, 0, 1));
        Assert.True(layout.CanPlace(1, 11, 5, 1));   // last cell
        Assert.True(layout.CanPlace(1, 0, 0, 12));  // full width on row 0
        Assert.True(layout.CanPlace(1, 10, 2, 2));  // ends exactly at the edge
    }

    [Theory]
    [InlineData(1, 12, 0, 1)]   // starts one past the right edge
    [InlineData(1, 11, 0, 2)]   // runs off the right edge
    [InlineData(1, -1, 0, 1)]   // negative column
    [InlineData(1, 0, -1, 1)]   // negative row
    [InlineData(1, 0, 6, 1)]    // row below the grid
    [InlineData(1, 0, 99, 1)]   // far out of range
    public void CanPlace_RejectsOutOfBounds(int typeId, int x, int y, int width)
    {
        BaseLayout layout = NewLayout();

        Assert.False(layout.CanPlace(typeId, x, y, width));
        Assert.Equal(PlacementError.OutOfBounds, layout.CheckPlacement(typeId, x, y, width));
    }

    [Fact]
    public void CanPlace_RejectsNonPositiveWidth()
    {
        BaseLayout layout = NewLayout();

        Assert.False(layout.CanPlace(1, 0, 0, 0));
        Assert.False(layout.CanPlace(1, 0, 0, -3));
        Assert.Equal(PlacementError.NonPositiveWidth, layout.CheckPlacement(1, 0, 0, 0));
    }

    [Fact]
    public void Place_OnInvalidPosition_ReturnsNull_AndPlacesNothing()
    {
        BaseLayout layout = NewLayout();

        // Width 3 at x=0 fits, but row 6 is below a 6-row grid.
        Room? room = layout.Place(1, 0, 6, 3, Tick.Zero, out PlacementError error);

        Assert.Null(room);
        Assert.Equal(PlacementError.OutOfBounds, error);
        Assert.Equal(0, layout.RoomCount);
    }

    // ---- overlap -------------------------------------------------------------

    [Fact]
    public void Place_RejectsAnExactOverlap()
    {
        BaseLayout layout = NewLayout();
        Room first = layout.Place(1, 4, 2, 3, Tick.Zero)!;

        Room? clash = layout.Place(2, 4, 2, 3, Tick.Zero, out PlacementError error);

        Assert.Null(clash);
        Assert.Equal(PlacementError.OverlapsExisting, error);
        Assert.Equal(1, layout.RoomCount);
        Assert.Equal(first.Id, layout.RoomAt(4, 2)!.Id);
    }

    [Fact]
    public void Place_RejectsPartialOverlapOnEitherSide()
    {
        BaseLayout layout = NewLayout();
        layout.Place(1, 4, 2, 4, Tick.Zero); // occupies x = 4,5,6,7

        // Overlaps the left edge.
        Assert.Equal(PlacementError.OverlapsExisting, layout.CheckPlacement(1, 3, 2, 2));
        // Overlaps the right edge.
        Assert.Equal(PlacementError.OverlapsExisting, layout.CheckPlacement(1, 7, 2, 2));
        // Fully contained.
        Assert.Equal(PlacementError.OverlapsExisting, layout.CheckPlacement(1, 5, 2, 1));
        // Fully containing.
        Assert.Equal(PlacementError.OverlapsExisting, layout.CheckPlacement(1, 2, 2, 8));
    }

    [Fact]
    public void RowsAreIndependent_SameColumnDifferentRowIsAllowed()
    {
        BaseLayout layout = NewLayout();

        Assert.True(layout.Place(1, 0, 0, 5, Tick.Zero) is not null);
        Assert.True(layout.Place(1, 0, 1, 5, Tick.Zero) is not null);
        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void AdjacentPlacements_AreNotOverlaps()
    {
        BaseLayout layout = NewLayout();
        layout.Place(1, 0, 0, 4, Tick.Zero); // x = 0..3

        // Starts exactly where the first ends: legal, and a merge candidate.
        Assert.True(layout.CanPlace(1, 4, 0, 4));
    }

    // ---- occupancy -----------------------------------------------------------

    [Fact]
    public void IsOccupied_And_RoomAt_TrackPlacement()
    {
        BaseLayout layout = NewLayout();
        Room room = layout.Place(1, 2, 1, 3, Tick.Zero)!; // x = 2,3,4 on row 1

        for (int x = 2; x <= 4; x++)
        {
            Assert.True(layout.IsOccupied(x, 1));
            Assert.Equal(room.Id, layout.RoomAt(x, 1)!.Id);
        }

        Assert.False(layout.IsOccupied(1, 1));
        Assert.False(layout.IsOccupied(5, 1));
        Assert.False(layout.IsOccupied(2, 0)); // same column, different row
    }

    [Fact]
    public void RoomAt_OutOfGrid_ReturnsNullRatherThanThrowing()
    {
        BaseLayout layout = NewLayout();

        Assert.Null(layout.RoomAt(-1, 0));
        Assert.Null(layout.RoomAt(0, -1));
        Assert.Null(layout.RoomAt(999, 0));
        Assert.Null(layout.RoomAt(0, 999));
    }

    [Fact]
    public void OccupancyCounters_MatchPlacedRooms()
    {
        BaseLayout layout = NewLayout();
        layout.Place(1, 0, 0, 4, Tick.Zero);
        layout.Place(1, 6, 0, 2, Tick.Zero);

        Assert.Equal(6, layout.OccupiedCellCount());
        Assert.Equal(2, layout.Rooms.Count);
    }

    // ---- demolish ------------------------------------------------------------

    [Fact]
    public void Demolish_FreesCells_SoTheSpaceCanBeRebuilt()
    {
        BaseLayout layout = NewLayout();
        Room room = layout.Place(1, 3, 2, 3, Tick.Zero)!;

        Assert.True(layout.Demolish(room.Id));
        Assert.Equal(0, layout.RoomCount);
        Assert.False(layout.IsOccupied(3, 2));
        Assert.True(layout.CanPlace(1, 3, 2, 3));
    }

    [Fact]
    public void Demolish_UnknownRoom_ReturnsFalse()
    {
        BaseLayout layout = NewLayout();
        Assert.False(layout.Demolish(new RoomId(999)));
    }

    // ---- merging -------------------------------------------------------------

    [Fact]
    public void TryMergeAdjacent_FoldsAdjacentRoomsOfTheSameType()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, 0, 2, Tick.Zero)!;
        Room b = layout.Place(1, 2, 0, 2, Tick.Zero)!;
        Room c = layout.Place(1, 4, 0, 2, Tick.Zero)!;

        RoomId merged = layout.TryMergeAdjacent(a.Id);

        Assert.Equal(a.Id, merged);
        Assert.Equal(1, layout.RoomCount);

        Room result = layout.GetRoom(a.Id)!;
        Assert.Equal(0, result.GridX);
        Assert.Equal(6, result.Width); // 2 + 2 + 2
        Assert.Null(layout.GetRoom(b.Id));
        Assert.Null(layout.GetRoom(c.Id));
    }

    [Fact]
    public void TryMergeAdjacent_MergesLeftwardsToo()
    {
        BaseLayout layout = NewLayout();
        Room left = layout.Place(1, 0, 0, 2, Tick.Zero)!;
        Room right = layout.Place(1, 2, 0, 3, Tick.Zero)!;

        // Merging from the right-hand room must still produce the correct left edge.
        RoomId merged = layout.TryMergeAdjacent(right.Id);

        Assert.Equal(left.Id, merged);
        Assert.Equal(5, layout.GetRoom(left.Id)!.Width);
    }

    [Fact]
    public void TryMergeAdjacent_LeavesNonAdjacentRoomsAlone()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, 0, 2, Tick.Zero)!;
        layout.Place(1, 3, 0, 2, Tick.Zero); // one-cell gap at x=2

        layout.TryMergeAdjacent(a.Id);

        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void TryMergeAdjacent_DoesNotMergeDifferentTypes()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, 0, 2, Tick.Zero)!;
        Room b = layout.Place(2, 2, 0, 2, Tick.Zero)!;

        Assert.Empty(layout.FindMergeCandidates(a.Id));

        // Merging is a no-op: the rooms stay separate.
        Assert.Equal(a.Id, layout.TryMergeAdjacent(a.Id));
        Assert.Equal(2, layout.RoomCount);
        Assert.NotNull(layout.GetRoom(b.Id));
    }

    [Fact]
    public void TryMergeAdjacent_DoesNotMergeDifferentRows()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, 0, 2, Tick.Zero)!;
        layout.Place(1, 2, 1, 2, Tick.Zero); // same column range, next row

        Assert.Empty(layout.FindMergeCandidates(a.Id));
        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void TryMergeAdjacent_DoesNotMergeRoomsOfDifferentLevels()
    {
        // Merging would silently discard the upgrade, so it is forbidden.
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, 0, 2, Tick.Zero)!;
        Room b = layout.Place(1, 2, 0, 2, Tick.Zero)!;
        b.Level = 2;

        Assert.False(a.CanMergeWith(b));
        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void TryMergeAdjacent_TakesTheWorseCondition()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, 0, 2, Tick.Zero)!;
        Room b = layout.Place(1, 2, 0, 2, Tick.Zero)!;
        a.Condition = 90;
        b.Condition = 40;

        layout.TryMergeAdjacent(a.Id);

        Assert.Equal(40, layout.GetRoom(a.Id)!.Condition);
    }

    [Fact]
    public void FindMergeCandidates_ListsOnlyValidPartners()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, 0, 2, Tick.Zero)!;
        layout.Place(1, 2, 0, 2, Tick.Zero);   // mergeable
        layout.Place(2, 4, 0, 2, Tick.Zero);   // wrong type
        layout.Place(1, 9, 0, 2, Tick.Zero);   // not adjacent

        IReadOnlyList<Room> candidates = layout.FindMergeCandidates(a.Id);

        Assert.Single(candidates);
        Assert.Equal(2, candidates[0].GridX);
    }

    // ---- depth cost ----------------------------------------------------------

    [Fact]
    public void DepthCostModifier_GrowsWithDepth()
    {
        BaseLayout layout = NewLayout(12, 6);

        long shallow = layout.ComputeBuildCost(1000, 0);
        long deeper = layout.ComputeBuildCost(1000, 3);
        long deepest = layout.ComputeBuildCost(1000, 5);

        Assert.Equal(1000, shallow);
        Assert.True(deeper > shallow);
        Assert.True(deepest > deeper);
    }

    [Fact]
    public void ApplyDepthCost_UsesIntegerArithmetic()
    {
        BaseLayout layout = NewLayout(12, 6);

        // Row 3 is 100 + (3 * 10) = 130%.
        Assert.Equal(1300, layout.ApplyDepthCost(1000, 3));
        Assert.Equal(0, layout.ApplyDepthCost(0, 3));
        Assert.Equal(0, layout.ApplyDepthCost(-50, 3));
    }

    [Fact]
    public void SetDepthCostModifier_RejectsWrongLengthAndNonPositive()
    {
        BaseLayout layout = NewLayout(12, 6);

        Assert.Throws<ArgumentException>(() => layout.SetDepthCostModifier(new[] { 100, 110 }));
        Assert.Throws<ArgumentException>(() => layout.SetDepthCostModifier(new[] { 100, 0, 120, 130, 140, 150 }));
    }

    [Fact]
    public void ApplyDepthCost_RejectsDepthOutsideTheGrid()
    {
        BaseLayout layout = NewLayout(12, 6);

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ApplyDepthCost(100, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ApplyDepthCost(100, -1));
    }

    // ---- story gating --------------------------------------------------------

    [Fact]
    public void Placement_IsRejectedWhenTheRoomTypeIsLocked()
    {
        BaseLayout layout = NewLayout();
        layout.UnlockedRoomTypeIds.Add(1);

        Assert.True(layout.CanPlace(1, 0, 0, 2));
        Assert.Equal(PlacementError.LockedByStory, layout.CheckPlacement(2, 0, 0, 2));
    }

    [Fact]
    public void Placement_IsRejectedAboveAMinimumDepth()
    {
        BaseLayout layout = NewLayout();
        layout.UnlockedRoomTypeIds.Add(7);
        layout.MinimumDepthByType[7] = 2;

        Assert.Equal(PlacementError.DepthTooShallow, layout.CheckPlacement(7, 0, 0, 2));
        Assert.Equal(PlacementError.DepthTooShallow, layout.CheckPlacement(7, 0, 1, 2));
        Assert.Equal(PlacementError.None, layout.CheckPlacement(7, 0, 2, 2));
    }
}
