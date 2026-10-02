using ProjectSpy.Core;
using ProjectSpy.Tables;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// A sink that keeps every event, for tests that drive a system directly.
/// </summary>
/// <remarks>
/// Preferred over subscribing to a session when testing a system in isolation: a
/// session buffers events until <c>FlushEvents</c>, so a test that publishes into a
/// session and then asserts on its subscriber sees nothing unless it remembers to
/// flush. A plain sink has no such step.
/// </remarks>
internal sealed class RecordingSink : IEventSink
{
    internal List<GameEvent> Events { get; } = new();

    public void Publish(GameEvent gameEvent) => Events.Add(gameEvent);

    /// <summary>True when an event of the given kind was published.</summary>
    internal bool Saw(GameEventKind kind) => Events.Any(e => e.Kind == kind);
}

/// <summary>
/// Shared fixtures for the stage-3 systems tests.
/// </summary>
/// <remarks>
/// <para>
/// These tests need a base that is actually playable — rooms built, agents hired and
/// assigned — because the systems under test are about interactions (stamina spent then
/// restored, salary paid then not affordable, a mole leaking a mission). A bare
/// <see cref="WorldState"/> with one idle agent would pass almost any assertion while
/// proving nothing.
/// </para>
/// <para>
/// Room construction is bypassed (<c>ConstructionTicksRemaining = 0</c>) so a test can
/// place an agent and start exercising the room on the next tick. Waiting out real build
/// times would add nothing but wall-clock to the test.
/// </para>
/// </remarks>
internal static class World
{
    /// <summary>Room type ids, named so the tests read as descriptions of a base.</summary>
    internal static class Rooms
    {
        internal const int Gym = 4001;
        internal const int Range = 4002;
        internal const int ServerRoom = 4003;
        internal const int SocialLab = 4004;
        internal const int Isolation = 4005;
        internal const int Dorm = 4006;
        internal const int Infirmary = 4007;
        internal const int Lounge = 4008;
        internal const int Therapy = 4009;
        internal const int Hr = 4021;
        internal const int CounterIntel = 4022;
    }

    /// <summary>Agent class ids from agent_class.csv.</summary>
    internal static class Classes
    {
        internal const int Infiltrator = 1001;
        internal const int Operative = 1002;
        internal const int Technician = 1003;
        internal const int Facilitator = 1004;
    }

    /// <summary>Trait ids from trait.csv, named by what they do.</summary>
    internal static class Traits
    {
        internal const int CalmUnderFire = 3002;
        internal const int Gossip = 3020;
        internal const int Drinker = 3019;
        internal const int SlowLearner = 3022;
        internal const int Mole = 3101;
        internal const int Patriot = 3106;
    }

    /// <summary>
    /// A session with every room type unlocked and no funds ceiling, so a test can fail
    /// on the system it is about rather than on affordability.
    /// </summary>
    internal static GameSession Session(ulong seed = 4242UL, long funds = 500_000)
    {
        var world = new WorldState(seed);
        world.Resources = world.Resources with { Funds = funds };
        world.BaseLayout.UnlockedRoomTypeIds.Clear();

        foreach (RoomType room in SimulationRules.AllRoomTypes())
            world.BaseLayout.UnlockedRoomTypeIds.Add(room.Id);

        return new GameSession(world);
    }

    /// <summary>
    /// Places a finished room immediately, returning it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>(x, y, width)</c> triple is a <em>test-side</em> convenience that stands
    /// in for Presentation's floorplan: it picks slots, a layer, and which rooms the
    /// new room touches. Core never sees a coordinate (knowledge.md rule 10) — it
    /// receives the abstract slot indices and the adjacency this helper derives.
    /// </para>
    /// <para>
    /// Keeping the helper grid-shaped is what lets ~115 call sites keep working
    /// unchanged while Core stays coordinate-free. Adjacency is derived the way a
    /// floorplan would show it: a room touches the nearest room to its left and right
    /// on the same layer.
    /// </para>
    /// </remarks>
    internal static Room Place(GameSession session, int typeId, int x, int y, int width = 2)
    {
        var slots = new List<int>();
        for (int slot = x; slot < x + width; slot++)
            slots.Add(slot);

        IReadOnlyList<RoomId> neighbours = NeighboursFor(session, y, x, x + width - 1);

        PlacementError error = session.World.BaseLayout.CheckPlacement(typeId, y, slots, neighbours);

        Room? room = session.World.BaseLayout.Place(
            typeId, y, slots, session.World.Clock.Current, out PlacementError placeError, neighbours);

        if (room is null)
        {
            throw new InvalidOperationException(
                $"Could not place room {typeId} at layer {y} slots {string.Join(",", slots)}: "
                + (placeError != PlacementError.None ? placeError.ToString() : error.ToString()));
        }

        // Skip construction so the room is usable on the very next tick.
        room.ConstructionTicksRemaining = 0;
        return room;
    }

    /// <summary>
    /// Which rooms a slot span on a layer would sit beside — the floorplan's idea of
    /// adjacency, standing in for whatever Presentation would compute from real
    /// positions.
    /// </summary>
    private static IReadOnlyList<RoomId> NeighboursFor(GameSession session, int layer, int firstSlot, int lastSlot)
    {
        var neighbours = new List<RoomId>();

        foreach (Room room in session.World.BaseLayout.RoomsAtLayer(layer))
        {
            if (room.Id == RoomId.None)
                continue;

            bool touchesLeft = room.LowestSlotIndex == lastSlot + 1;
            bool touchesRight = room.SlotIndices.Max() == firstSlot - 1;

            if (touchesLeft || touchesRight)
                neighbours.Add(room.Id);
        }

        return neighbours;
    }

    /// <summary>Hires an agent straight onto the roster with the given traits.</summary>
    internal static Agent Hire(
        GameSession session,
        int classId = Classes.Operative,
        long salary = 200,
        IEnumerable<int>? traits = null,
        IEnumerable<int>? hiddenTraits = null)
    {
        var agent = new Agent
        {
            Name = "Agent",
            Codename = "Code",
            ClassId = classId,
            SalaryPerWeek = salary,
            PhysicalStamina = Agent.MaxStamina,
            MentalStamina = Agent.MaxStamina,
            Loyalty = Agent.StartingLoyalty,
            HiredOnTick = session.World.Clock.Current,
        };

        foreach (int traitId in traits ?? Array.Empty<int>())
            agent.TraitIds.Add(traitId);

        foreach (int traitId in hiddenTraits ?? Array.Empty<int>())
            agent.UndiscoveredTraitIds.Add(traitId);

        return session.World.AddAgent(agent);
    }

    /// <summary>Assigns an agent to a room and sets the status a phase expects.</summary>
    internal static void Assign(GameSession session, Agent agent, Room room, AgentStatus status)
    {
        room.AssignedAgentIds.Add(agent.Id);
        agent.AssignedRoomId = room.Id.Value;
        agent.Status = status;
        agent.Normalize();
    }

    /// <summary>
    /// A minimal but complete base: a dorm, an infirmary, two training rooms and an HR
    /// office, plus a small roster. Enough for the weekly systems to have something
    /// real to charge for.
    /// </summary>
    internal static GameSession Agency(ulong seed = 4242UL, long funds = 500_000, int agentCount = 4)
    {
        GameSession session = Session(seed, funds);

        Place(session, Rooms.Dorm, 0, 0, 3);
        Place(session, Rooms.Infirmary, 4, 0, 3);
        Place(session, Rooms.Range, 8, 0, 3);
        Place(session, Rooms.ServerRoom, 12, 0, 3);
        Place(session, Rooms.Lounge, 16, 0, 3);
        Place(session, Rooms.Hr, 0, 1, 3);

        for (int i = 0; i < agentCount; i++)
            Hire(session, Classes.Operative, 200);

        return session;
    }

    /// <summary>Advances a number of days in whole-day batches.</summary>
    internal static void AdvanceDays(GameSession session, int days)
        => session.AdvanceTicks(days * Tick.TicksPerDay);
}