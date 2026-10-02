namespace ProjectSpy.Core.Tests;

/// <summary>
/// Builds commands from a grid-shaped description, standing in for what Presentation
/// would compute from real positions.
/// </summary>
/// <remarks>
/// <para>
/// Core has no coordinates (knowledge.md rule 10): a <see cref="BuildRoomCommand"/>
/// names a layer, a set of slot indices, and the rooms the new one touches. Tests are
/// far more readable written against "x=4, y=0, width=3", and rewriting ~17 call
/// sites by hand buys nothing — so this translates.
/// </para>
/// <para>
/// The translation is the same one a floorplan UI would perform: <c>x</c> picks the
/// slots, <c>y</c> picks the layer, and adjacency is derived from what is already in
/// that layer. Nothing here reaches into Core's internals; it only speaks the
/// vocabulary Core accepts.
/// </para>
/// </remarks>
internal static class GridCommands
{
    /// <summary>
    /// A <see cref="BuildRoomCommand"/> from a grid position.
    /// </summary>
    /// <param name="typeId">Room type to build.</param>
    /// <param name="x">First slot on the layer.</param>
    /// <param name="y">Depth layer.</param>
    /// <param name="width">How many contiguous slots the room occupies.</param>
    /// <param name="nameKey">Localization key for the room name.</param>
    /// <param name="cost">Total cost charged.</param>
    /// <param name="world">World whose layout supplies the current adjacency.</param>
    internal static BuildRoomCommand Build(
        int typeId,
        int x,
        int y,
        int width,
        string nameKey,
        long cost,
        WorldState world)
    {
        var slots = new List<int>();
        for (int slot = x; slot < x + width; slot++)
            slots.Add(slot);

        return new BuildRoomCommand(typeId, y, slots, NeighboursFor(world, y, x, x + width - 1), nameKey, cost);
    }

    /// <summary>As above, with no adjacency derived from a world.</summary>
    internal static BuildRoomCommand Build(int typeId, int x, int y, int width, string nameKey, long cost)
        => Build(typeId, x, y, width, nameKey, cost, new WorldState(1UL));

    /// <summary>
    /// Which rooms a slot span on a layer would sit beside.
    /// </summary>
    /// <remarks>
    /// Lateral neighbours only, mirroring what the grid version computed from
    /// coordinates. A room stacked directly above is adjacent on screen but was never
    /// a legal merge, and the merge rule still requires the same layer.
    /// </remarks>
    private static IReadOnlyList<RoomId> NeighboursFor(WorldState world, int layer, int firstSlot, int lastSlot)
    {
        var neighbours = new List<RoomId>();

        foreach (Room room in world.BaseLayout.RoomsAtLayer(layer))
        {
            if (room.SlotCount == 0)
                continue;

            bool touchesLow = room.LowestSlotIndex == lastSlot + 1;
            bool touchesHigh = room.SlotIndices.Max() == firstSlot - 1;

            if (touchesLow || touchesHigh)
                neighbours.Add(room.Id);
        }

        return neighbours;
    }
}
