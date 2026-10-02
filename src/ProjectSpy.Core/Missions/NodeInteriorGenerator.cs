namespace ProjectSpy.Core;

/// <summary>
/// Builds a mission node's interior — its <see cref="RoomContents"/> — on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is internal on purpose.</b> It is the one place that can conjure a
/// node's contents, and making it public would hand the UI a fog-free path to every
/// interior on the map: <c>NodeInteriorGenerator.Generate(node, mapSeed)</c> takes no
/// visibility into account, so a caller could build the room the player has not
/// entered yet. Keeping it internal means <see cref="FogOfWar"/> is the only way out
/// of Core that can produce contents, and that path is gated by what the player has
/// observed. <c>FogOfWarLeakTests</c> asserts that over the compiled public surface.
/// </para>
/// <para>
/// <b>Determinism is the contract.</b> The generator is seeded from
/// <c>MapSeed + nodeId</c> rather than from a shared stream, so a node's interior is
/// identical whether it is generated on tick 1 or tick 200, and whether it is
/// generated before or after its neighbours. Generation order cannot change the
/// result, which is what makes lazy generation safe to use at all: if interiors came
/// from the world's Mission stream, the contents of the room you walk into first
/// would depend on how many rooms you happened to look at, and a save reloaded later
/// would produce a different building.
/// </para>
/// <para>
/// Every count and chance lives in <c>node_interior_rule</c> rather than in code
/// (knowledge.md rule 3).
/// </para>
/// <para>
/// <b>No coordinates.</b> Slots are abstract indices; Presentation decides what slot 4
/// looks like (knowledge.md rule 10).
/// </para>
/// </remarks>
internal static class NodeInteriorGenerator
{
    /// <summary>Tag that promises a terminal is worth looking for.</summary>
    private const string TagTerminal = "terminal";

    /// <summary>Tag that promises a guard is present.</summary>
    private const string TagGuard = "guard";

    /// <summary>
    /// Generates the interior for one node. Internal: see the type remarks.
    /// </summary>
    /// <param name="node">The node being observed.</param>
    /// <param name="mapSeed">The map's seed.</param>
    public static RoomContents Generate(MissionNode node, ulong mapSeed)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));

        // Keyed by the node, not drawn from a shared sequence: see the remarks.
        var rng = new XorShift128Rng(RngStreams.DeriveSeed(mapSeed, node.Id.Value));

        int slotCount = Math.Max(1, SimulationRules.NodeInterior(
            "interior_slot_count", InteriorFallbacks.SlotCount));

        var room = new RoomContents { SlotCount = slotCount };

        bool wantsTerminal = node.HasAllTags(new[] { TagTerminal });
        bool wantsGuard = node.HasAllTags(new[] { TagGuard }) || node.IsSecurity;

        AddContainers(room, rng, node.HasLoot);
        if (wantsTerminal)
            AddTerminals(room, rng);
        if (wantsGuard)
            AddGuards(room, rng);
        AddDoors(room, rng);
        AddTraps(room, rng);

        // A generated interior that failed its own validator would mean a rule bug
        // that surfaces as a player standing in a room with two exits. Every
        // generation is checked rather than assumed.
        if (!room.Validate(out string problem))
        {
            throw new InvalidOperationException(
                $"Generated an invalid interior for node {node.Id} (room {node.RoomTypeId}): {problem}");
        }

        return room;
    }

    /// <summary>
    /// Places searchable containers, guaranteeing at least one when the node holds
    /// loot.
    /// </summary>
    private static void AddContainers(RoomContents room, IRng rng, bool nodeHasLoot)
    {
        int max = Math.Max(0, SimulationRules.NodeInterior(
            "interior_container_count_max", InteriorFallbacks.ContainerCountMax));

        int count = rng.NextInt(0, max + 1);

        // Loot on the node is a promise the player can see from a Scouted sight, so
        // the interior has to keep it: a room flagged as holding loot with nothing
        // in it is the kind of lie fog of war must not tell.
        if (nodeHasLoot && count == 0)
            count = 1;

        for (int i = 0; i < count; i++)
        {
            Interactable? container = Place(room, rng, InteractableType.Container);
            if (container is null)
                return;

            int lockedPercent = SimulationRules.NodeInterior(
                "interior_locked_container_percent", InteriorFallbacks.LockedContainerPercent);

            container.State = rng.NextInt(1, 101) <= lockedPercent
                ? InteractableState.Closed
                : InteractableState.Idle;
        }
    }

    private static void AddTerminals(RoomContents room, IRng rng)
    {
        int max = Math.Max(1, SimulationRules.NodeInterior(
            "interior_terminal_count_max", InteriorFallbacks.TerminalCountMax));

        int count = rng.NextInt(1, max + 1);

        for (int i = 0; i < count; i++)
        {
            Place(room, rng, InteractableType.Terminal);
        }
    }

    private static void AddGuards(RoomContents room, IRng rng)
    {
        int max = Math.Max(1, SimulationRules.NodeInterior(
            "interior_guard_count_max", InteriorFallbacks.GuardCountMax));

        int count = rng.NextInt(1, max + 1);

        for (int i = 0; i < count; i++)
        {
            if (Place(room, rng, InteractableType.Guard) is null)
                return;
        }
    }

    /// <summary>
    /// Places a door, sometimes locked, blocking a slot behind it.
    /// </summary>
    private static void AddDoors(RoomContents room, IRng rng)
    {
        int chance = SimulationRules.NodeInterior(
            "interior_door_chance_percent", InteriorFallbacks.DoorChancePercent);

        if (rng.NextInt(1, 101) > chance)
            return;

        Interactable? door = Place(room, rng, InteractableType.Door);
        if (door is null)
            return;

        int lockedPercent = SimulationRules.NodeInterior(
            "interior_door_locked_percent", InteriorFallbacks.DoorLockedPercent);

        door.State = rng.NextInt(1, 101) <= lockedPercent
            ? InteractableState.Locked
            : InteractableState.Closed;

        // A door that blocks nothing is scenery. It blocks the next free slot, which
        // is the abstract form of "the doorway behind it is shut" — Core names slots,
        // Presentation draws the doorway (knowledge.md rule 10).
        foreach (int slot in FreeSlots(room))
        {
            door.BlockedSlotIndices.Add(slot);
            break;
        }
    }

    private static void AddTraps(RoomContents room, IRng rng)
    {
        int chance = SimulationRules.NodeInterior(
            "interior_trap_chance_percent", InteriorFallbacks.TrapChancePercent);

        if (rng.NextInt(1, 101) > chance)
            return;

        int max = Math.Max(1, SimulationRules.NodeInterior(
            "interior_trap_count_max", InteriorFallbacks.TrapCountMax));

        int count = rng.NextInt(1, max + 1);

        for (int i = 0; i < count; i++)
        {
            if (Place(room, rng, InteractableType.Trap) is null)
                return;
        }
    }

    /// <summary>
    /// Adds an interactable of a type in a free slot, or returns null when the room
    /// is full.
    /// </summary>
    private static Interactable? Place(RoomContents room, IRng rng, InteractableType type)
    {
        var free = FreeSlots(room);
        if (free.Count == 0)
            return null;

        int slot = free[rng.NextInt(0, free.Count)];
        return room.Add(type, slot);
    }

    /// <summary>
    /// Slot indices in <c>[0, SlotCount)</c> that nothing occupies yet.
    /// </summary>
    private static List<int> FreeSlots(RoomContents room)
    {
        var taken = new HashSet<int>();
        foreach (Interactable interactable in room.Interactables)
            taken.Add(interactable.SlotIndex);

        var free = new List<int>(room.SlotCount);
        for (int slot = 0; slot < room.SlotCount; slot++)
        {
            if (!taken.Contains(slot))
                free.Add(slot);
        }

        return free;
    }

    /// <summary>
    /// Documented fallbacks used only when <c>node_interior_rule</c> is unavailable.
    /// </summary>
    /// <remarks>
    /// Mirrors the shipped table. A missing table must still produce a valid room,
    /// not an exception in the middle of a mission.
    /// </remarks>
    internal static class InteriorFallbacks
    {
        internal const int SlotCount = 6;
        internal const int ContainerCountMax = 3;
        internal const int GuardCountMax = 2;
        internal const int TerminalCountMax = 2;
        internal const int DoorChancePercent = 60;
        internal const int DoorLockedPercent = 40;
        internal const int TrapChancePercent = 25;
        internal const int TrapCountMax = 1;
        internal const int LockedContainerPercent = 20;
    }
}
