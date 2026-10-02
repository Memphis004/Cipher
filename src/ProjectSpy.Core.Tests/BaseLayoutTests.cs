using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The base layout: rooms occupy abstract slots in abstract layers, adjacency is
/// explicit data, and depth cost is keyed by layer.
/// </summary>
/// <remarks>
/// These tests were written against a grid (<c>x</c>, <c>y</c>, <c>width</c>, an
/// occupancy array, adjacency derived from coordinates). Core no longer stores
/// coordinates (knowledge.md rule 10), so the layout is now tested in the vocabulary
/// it actually uses. The rules being checked are unchanged — the same placements are
/// legal and illegal, the same merges happen, the same costs apply — but they are
/// expressed in slots and layers.
/// </remarks>
public class BaseLayoutTests
{
    private static BaseLayout NewLayout(int slotsPerLayer = 12, int layerCount = 6)
        => new(layerCount, slotsPerLayer);

    /// <summary>A contiguous slot span, standing in for a room that occupies several slots.</summary>
    private static int[] Span(int first, int count)
    {
        var slots = new int[count];
        for (int i = 0; i < count; i++)
            slots[i] = first + i;

        return slots;
    }

    // ---- bounds --------------------------------------------------------------

    [Fact]
    public void CanPlace_AcceptsSlotsFullyInsideTheLayout()
    {
        BaseLayout layout = NewLayout();

        Assert.True(layout.CanPlace(1, 0, new[] { 0 }));
        Assert.True(layout.CanPlace(1, 5, new[] { 11 }));   // last slot
        Assert.True(layout.CanPlace(1, 0, Span(0, 12)));   // whole layer
        Assert.True(layout.CanPlace(1, 2, Span(10, 2)));   // ends exactly at the edge
    }

    [Fact]
    public void CanPlace_RejectsASlotPastTheEndOfTheLayer()
    {
        BaseLayout layout = NewLayout();

        Assert.False(layout.CanPlace(1, 0, new[] { 12 }));
        Assert.False(layout.CanPlace(1, 0, Span(11, 2)));
        Assert.Equal(PlacementError.SlotOutOfRange, layout.CheckPlacement(1, 0, new[] { 99 }));
    }

    [Fact]
    public void CanPlace_RejectsANegativeSlot()
    {
        BaseLayout layout = NewLayout();

        Assert.Equal(PlacementError.SlotOutOfRange, layout.CheckPlacement(1, 0, new[] { -1 }));
    }

    [Fact]
    public void CanPlace_RejectsALayerOutsideTheLayout()
    {
        BaseLayout layout = NewLayout();

        Assert.Equal(PlacementError.LayerOutOfRange, layout.CheckPlacement(1, 6, new[] { 0 }));
        Assert.Equal(PlacementError.LayerOutOfRange, layout.CheckPlacement(1, 99, new[] { 0 }));
        Assert.Equal(PlacementError.LayerOutOfRange, layout.CheckPlacement(1, -1, new[] { 0 }));
    }

    [Fact]
    public void CanPlace_RejectsARoomWithNoSlots()
    {
        BaseLayout layout = NewLayout();

        Assert.Equal(PlacementError.NoSlots, layout.CheckPlacement(1, 0, Array.Empty<int>()));
    }

    [Fact]
    public void Place_OnAnInvalidLayer_ReturnsNull_AndPlacesNothing()
    {
        BaseLayout layout = NewLayout();

        // Three slots fit, but layer 6 does not exist in a six-layer layout.
        Room? room = layout.Place(1, 6, Span(0, 3), Tick.Zero, out PlacementError error);

        Assert.Null(room);
        Assert.Equal(PlacementError.LayerOutOfRange, error);
        Assert.Equal(0, layout.RoomCount);
    }

    // ---- slot conflicts ------------------------------------------------------

    [Fact]
    public void Place_RejectsAnExactSlotClash()
    {
        BaseLayout layout = NewLayout();
        Room first = layout.Place(1, 2, Span(4, 3), Tick.Zero)!;

        Room? clash = layout.Place(2, 2, Span(4, 3), Tick.Zero, out PlacementError error);

        Assert.Null(clash);
        Assert.Equal(PlacementError.SlotOccupied, error);
        Assert.Equal(1, layout.RoomCount);
        Assert.Equal(first.Id, layout.RoomInSlot(2, 5)!.Id);
    }

    [Fact]
    public void Place_RejectsPartialOverlapOnEitherSide()
    {
        BaseLayout layout = NewLayout();
        layout.Place(1, 2, Span(4, 4), Tick.Zero); // slots 4,5,6,7 on layer 2

        // Overlaps the low edge.
        Assert.Equal(PlacementError.SlotOccupied, layout.CheckPlacement(1, 2, Span(3, 2)));
        // Overlaps the high edge.
        Assert.Equal(PlacementError.SlotOccupied, layout.CheckPlacement(1, 2, Span(7, 2)));
        // Fully contained.
        Assert.Equal(PlacementError.SlotOccupied, layout.CheckPlacement(1, 2, Span(5, 1)));
        // Fully containing.
        Assert.Equal(PlacementError.SlotOccupied, layout.CheckPlacement(1, 2, Span(2, 8)));
    }

    [Fact]
    public void ARepeatedSlotInOneRequestIsNotASilentPass()
    {
        // Deduplicated internally, so it must not be mistaken for a free slot.
        BaseLayout layout = NewLayout();
        layout.Place(1, 0, new[] { 4 }, Tick.Zero);

        Assert.Equal(PlacementError.SlotOccupied, layout.CheckPlacement(1, 0, new[] { 4, 4, 4 }));
    }

    [Fact]
    public void LayersAreIndependent_SameSlotDifferentLayerIsAllowed()
    {
        BaseLayout layout = NewLayout();

        Assert.True(layout.Place(1, 0, Span(0, 5), Tick.Zero) is not null);
        Assert.True(layout.Place(1, 1, Span(0, 5), Tick.Zero) is not null);
        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void PlacedSlotsAreNotClashes()
    {
        BaseLayout layout = NewLayout();
        layout.Place(1, 0, Span(0, 4), Tick.Zero); // slots 0..3

        // Starts exactly where the first ends: legal.
        Assert.True(layout.CanPlace(1, 0, Span(4, 4)));
    }

    // ---- occupancy -----------------------------------------------------------

    [Fact]
    public void IsSlotOccupied_And_RoomInSlot_TrackPlacement()
    {
        BaseLayout layout = NewLayout();
        Room room = layout.Place(1, 1, Span(2, 3), Tick.Zero)!; // slots 2,3,4 on layer 1

        for (int slot = 2; slot <= 4; slot++)
        {
            Assert.True(layout.IsSlotOccupied(1, slot));
            Assert.Equal(room.Id, layout.RoomInSlot(1, slot)!.Id);
        }

        Assert.False(layout.IsSlotOccupied(1, 1));
        Assert.False(layout.IsSlotOccupied(1, 5));
        Assert.False(layout.IsSlotOccupied(0, 2)); // same slot, different layer
    }

    [Fact]
    public void RoomInSlot_OutOfRange_ReturnsNullRatherThanThrowing()
    {
        BaseLayout layout = NewLayout();

        Assert.Null(layout.RoomInSlot(0, -1));
        Assert.Null(layout.RoomInSlot(-1, 0));
        Assert.Null(layout.RoomInSlot(0, 999));
        Assert.Null(layout.RoomInSlot(99, 0));
    }

    [Fact]
    public void SlotCounters_MatchPlacedRooms()
    {
        BaseLayout layout = NewLayout();
        layout.Place(1, 0, Span(0, 4), Tick.Zero);
        layout.Place(1, 0, Span(6, 2), Tick.Zero);

        Assert.Equal(6, layout.OccupiedSlotCount());
        Assert.Equal(6 * 12, layout.SlotCapacity);
        Assert.Equal(2, layout.Rooms.Count);
    }

    // ---- adjacency -----------------------------------------------------------

    [Fact]
    public void DeclaredAdjacencyIsSymmetric()
    {
        // A one-sided edge would make merging depend on which room the player
        // clicked, which is exactly the order-dependence that breaks replays.
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;

        Assert.True(layout.AreAdjacent(a.Id, b.Id));
        Assert.True(layout.AreAdjacent(b.Id, a.Id));
        Assert.Contains(b.Id, a.AdjacentRoomIds);
        Assert.Contains(a.Id, b.AdjacentRoomIds);
    }

    [Fact]
    public void APlacementWithNoDeclaredNeighboursTouchesNothing()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero)!;

        Assert.False(layout.AreAdjacent(a.Id, b.Id));
        Assert.Empty(a.AdjacentRoomIds);
    }

    [Fact]
    public void NeighboursOf_ListsOnlyDeclaredNeighbours()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;
        layout.Place(1, 0, Span(8, 2), Tick.Zero); // far away, not declared

        IReadOnlyList<Room> neighbours = layout.NeighboursOf(a.Id);

        Assert.Single(neighbours);
        Assert.Equal(b.Id, neighbours[0].Id);
    }

    [Fact]
    public void Placement_RejectsAnUnknownNeighbour()
    {
        BaseLayout layout = NewLayout();

        Assert.Equal(
            PlacementError.UnknownNeighbour,
            layout.CheckPlacement(1, 0, new[] { 4 }, new[] { new RoomId(999) }));
    }

    [Fact]
    public void Placement_RejectsANeighbourInAnotherLayer()
    {
        BaseLayout layout = NewLayout();
        Room other = layout.Place(1, 3, Span(0, 2), Tick.Zero)!;

        Assert.Equal(
            PlacementError.NeighbourInOtherLayer,
            layout.CheckPlacement(1, 0, new[] { 4 }, new[] { other.Id }));
    }

    // ---- demolish ------------------------------------------------------------

    [Fact]
    public void Demolish_FreesSlots_SoTheSpaceCanBeRebuilt()
    {
        BaseLayout layout = NewLayout();
        Room room = layout.Place(1, 2, Span(3, 3), Tick.Zero)!;

        Assert.True(layout.Demolish(room.Id));
        Assert.Equal(0, layout.RoomCount);
        Assert.False(layout.IsSlotOccupied(2, 3));
        Assert.True(layout.CanPlace(1, 2, Span(3, 3)));
    }

    [Fact]
    public void Demolish_UnlinksNeighboursOnBothSides()
    {
        // A survivor left pointing at a demolished room would carry a dangling
        // adjacency that no rule can resolve.
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;

        layout.Demolish(b.Id);

        Assert.DoesNotContain(b.Id, a.AdjacentRoomIds);
        Assert.Empty(b.AdjacentRoomIds);
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
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;
        Room c = layout.Place(1, 0, Span(4, 2), Tick.Zero, new[] { b.Id })!;

        RoomId merged = layout.TryMergeAdjacent(a.Id);

        Assert.Equal(a.Id, merged);
        Assert.Equal(1, layout.RoomCount);

        Room result = layout.GetRoom(a.Id)!;
        Assert.Equal(6, result.SlotCount); // 2 + 2 + 2
        Assert.Null(layout.GetRoom(b.Id));
        Assert.Null(layout.GetRoom(c.Id));
    }

    [Fact]
    public void TryMergeAdjacent_MergesBackwardsToo()
    {
        BaseLayout layout = NewLayout();
        Room left = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room right = layout.Place(1, 0, Span(2, 3), Tick.Zero, new[] { left.Id })!;

        // Merging from the higher-slot room must still produce the same survivor.
        RoomId merged = layout.TryMergeAdjacent(right.Id);

        Assert.Equal(left.Id, merged);
        Assert.Equal(5, layout.GetRoom(left.Id)!.SlotCount);
    }

    [Fact]
    public void TryMergeAdjacent_LeavesUndeclaredNeighboursAlone()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        layout.Place(1, 0, Span(2, 2), Tick.Zero); // sits beside a, but not declared

        layout.TryMergeAdjacent(a.Id);

        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void TryMergeAdjacent_DoesNotMergeDifferentTypes()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        layout.Place(2, 0, Span(2, 2), Tick.Zero, new[] { a.Id });

        Assert.Empty(layout.FindMergeCandidates(a.Id));
        Assert.Equal(a.Id, layout.TryMergeAdjacent(a.Id));
        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void TryMergeAdjacent_DoesNotMergeAcrossLayers()
    {
        // Adjacency is explicit, so this is only true because the merge rule still
        // requires the same layer. Dropping it would let a room absorb something the
        // player placed directly above it.
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 1, Span(2, 2), Tick.Zero)!;

        // Force the adjacency the placement API would have rejected, to prove the
        // layer check is what stops the merge.
        b.AdjacentRoomIds.Add(a.Id);
        a.AdjacentRoomIds.Add(b.Id);

        Assert.False(a.CanMergeWith(b));
        Assert.Empty(layout.FindMergeCandidates(a.Id));
        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void TryMergeAdjacent_DoesNotMergeRoomsOfDifferentLevels()
    {
        // Merging would silently discard the upgrade, so it is forbidden.
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;
        b.Level = 2;

        Assert.False(a.CanMergeWith(b));
        Assert.Equal(2, layout.RoomCount);
    }

    [Fact]
    public void TryMergeAdjacent_TakesTheWorseCondition()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;
        a.Condition = 90;
        b.Condition = 40;

        layout.TryMergeAdjacent(a.Id);

        Assert.Equal(40, layout.GetRoom(a.Id)!.Condition);
    }

    [Fact]
    public void TryMergeAdjacent_LeavesTheSurvivorAdjacentToTheOuterNeighbours()
    {
        // Absorbing a room must not take the outer edges down with it. a-b-c are a
        // mergeable chain; d is a different type so it survives the merge, and the
        // survivor still has to be touching it afterwards.
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;
        Room c = layout.Place(1, 0, Span(4, 2), Tick.Zero, new[] { b.Id })!;
        Room d = layout.Place(2, 0, Span(6, 2), Tick.Zero, new[] { c.Id })!;

        RoomId mergedId = layout.TryMergeAdjacent(a.Id);

        Room survivor = layout.GetRoom(mergedId)!;

        // b and c were absorbed and are gone, so the survivor must not still point at
        // them — but d is untouched and has to remain a neighbour.
        Assert.DoesNotContain(b.Id, survivor.AdjacentRoomIds);
        Assert.DoesNotContain(c.Id, survivor.AdjacentRoomIds);
        Assert.Contains(d.Id, survivor.AdjacentRoomIds);
        Assert.Contains(survivor.Id, d.AdjacentRoomIds);
        Assert.Equal(6, survivor.SlotCount); // a + b + c
    }

    [Fact]
    public void TryMergeAdjacent_NeverLeavesARoomAdjacentToItself()
    {
        // A self-edge passes every membership check while meaning nothing, and it
        // would otherwise survive into the save and the replay fingerprint.
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room b = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;

        RoomId mergedId = layout.TryMergeAdjacent(a.Id);

        Room survivor = layout.GetRoom(mergedId)!;
        Assert.DoesNotContain(survivor.Id, survivor.AdjacentRoomIds);
        Assert.NotNull(b);
    }

    [Fact]
    public void APlacementNamingAnUnknownNeighbourIsRejected()
    {
        BaseLayout layout = NewLayout();

        Assert.Equal(
            PlacementError.UnknownNeighbour,
            layout.CheckPlacement(1, 0, Span(2, 2), new[] { new RoomId(999) }));

        // Nothing was placed as a side effect of the rejected check.
        Assert.Equal(0, layout.RoomCount);
    }

    [Fact]
    public void FindMergeCandidates_ListsOnlyValidPartners()
    {
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room mergeable = layout.Place(1, 0, Span(2, 2), Tick.Zero, new[] { a.Id })!;
        layout.Place(2, 0, Span(4, 2), Tick.Zero, new[] { a.Id });   // wrong type
        layout.Place(1, 0, Span(6, 2), Tick.Zero);                     // not declared adjacent

        IReadOnlyList<Room> candidates = layout.FindMergeCandidates(a.Id);

        Assert.Single(candidates);
        Assert.Equal(mergeable.Id, candidates[0].Id);
    }

    [Fact]
    public void SlotDistanceAloneDoesNotMakeRoomsAdjacent()
    {
        // The sharpest behavioural consequence of storing adjacency as data: two rooms
        // can be numerically side by side and still not touch, because Core no longer
        // infers adjacency from geometry. Presentation decides who touches whom.
        BaseLayout layout = NewLayout();
        Room a = layout.Place(1, 0, Span(0, 2), Tick.Zero)!;
        Room beside = layout.Place(1, 0, Span(2, 2), Tick.Zero)!;

        Assert.False(layout.AreAdjacent(a.Id, beside.Id));
        Assert.Empty(layout.FindMergeCandidates(a.Id));
    }

    // ---- depth cost ----------------------------------------------------------

    [Fact]
    public void DepthCostModifier_GrowsWithLayer()
    {
        BaseLayout layout = NewLayout();

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
        BaseLayout layout = NewLayout();

        // Layer 3 is 100 + (3 * 10) = 130%.
        Assert.Equal(1300, layout.ApplyDepthCost(1000, 3));
        Assert.Equal(0, layout.ApplyDepthCost(0, 3));
        Assert.Equal(0, layout.ApplyDepthCost(-50, 3));
    }

    [Fact]
    public void SetDepthCostModifier_RejectsWrongLengthAndNonPositive()
    {
        BaseLayout layout = NewLayout();

        Assert.Throws<ArgumentException>(() => layout.SetDepthCostModifier(new[] { 100, 110 }));
        Assert.Throws<ArgumentException>(() => layout.SetDepthCostModifier(new[] { 100, 0, 120, 130, 140, 150 }));
    }

    [Fact]
    public void ApplyDepthCost_RejectsALayerOutsideTheLayout()
    {
        BaseLayout layout = NewLayout();

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ApplyDepthCost(100, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ApplyDepthCost(100, -1));
    }

    // ---- story gating --------------------------------------------------------

    [Fact]
    public void Placement_IsRejectedWhenTheRoomTypeIsLocked()
    {
        BaseLayout layout = NewLayout();
        layout.UnlockedRoomTypeIds.Add(1);

        Assert.True(layout.CanPlace(1, 0, Span(0, 2)));
        Assert.Equal(PlacementError.LockedByStory, layout.CheckPlacement(2, 0, Span(0, 2)));
    }

    [Fact]
    public void Placement_IsRejectedAboveAMinimumLayer()
    {
        BaseLayout layout = NewLayout();
        layout.UnlockedRoomTypeIds.Add(7);
        layout.MinimumDepthByType[7] = 2;

        Assert.Equal(PlacementError.LayerTooShallow, layout.CheckPlacement(7, 0, Span(0, 2)));
        Assert.Equal(PlacementError.LayerTooShallow, layout.CheckPlacement(7, 1, Span(0, 2)));
        Assert.Equal(PlacementError.None, layout.CheckPlacement(7, 2, Span(0, 2)));
    }
}
