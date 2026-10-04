using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// A straight chain of rooms joined by doors, for the noise attenuation matrix.
/// </summary>
/// <remarks>
/// <para>
/// The noise question is always the same question — "how many doors are between this
/// sound and that listener" — and a generated site cannot answer it, because the number
/// of doors is a property of the route rather than of the two rooms. This builds the
/// chain explicitly: <c>rooms</c> rooms in a row, each joined to the next by one door, so
/// a listener in room <c>n</c> is exactly <c>n</c> doors from a noise in room 0.
/// </para>
/// <para>
/// Rooms are wide enough that a listener always stands at the same offset from its door,
/// so the only thing that changes between two rows of the matrix is the number of
/// attenuations applied.
/// </para>
/// </remarks>
internal static class NoiseFixture
{
    /// <summary>Id of the first room in the chain.</summary>
    internal const int FirstRoom = 1;

    /// <summary>Id of the listener.</summary>
    internal const int Listener = 900;

    /// <summary>Id of the actor who makes the noise.</summary>
    public const int Source = 800;

    /// <summary>Width of every room, in centimetres.</summary>
    internal const int RoomWidthCm = 1_000;

    /// <summary>Where a listener stands inside its room, in centimetres from the room's left edge.</summary>
    internal const int ListenerOffsetCm = 200;

    /// <summary>Centre of the first room, in centimetres.</summary>
    internal static int FirstRoomCentre => (FirstRoom - 1) * RoomWidthCm + RoomWidthCm / 2;

    /// <summary>Centre of the last room of a chain of the given length, in centimetres.</summary>
    internal static int LastRoomCentreFor(int rooms) => (rooms - 1) * RoomWidthCm + RoomWidthCm / 2;

    /// <summary>Centre of the last room of the longest chain the tests build.</summary>
    internal const int LongestChain = 8;

    /// <summary>Centre of the last room, for the longest chain.</summary>
    internal static int LastRoomCentre => LastRoomCentreFor(LongestChain);

    /// <summary>
    /// Where a listener stands in the last room of a chain of the given length — the
    /// same offset into its room that <see cref="Actors"/> gives the shared listener, so
    /// a test that builds its own listener is standing exactly where the fixture would.
    /// </summary>
    public static int LastRoomListenerAt(int rooms)
        => ((rooms - 1) * RoomWidthCm) + ListenerOffsetCm;

    /// <summary>Builds a chain of <paramref name="rooms"/> rooms joined by doors.</summary>
    public static SiteLayout Chain(int rooms)
    {
        if (rooms < 1)
            throw new ArgumentOutOfRangeException(nameof(rooms), rooms, "A chain needs at least one room.");

        var floor = new SiteFloor
        {
            Index = 0,
            Kind = SiteFloorKind.Ground,
            Span = new Fixed32(rooms * RoomWidthCm),
        };

        var region = new SiteRegion { Kind = SiteRegionKind.Site };
        region.Floors.Add(floor);

        for (int i = 0; i < rooms; i++)
        {
            floor.Rooms.Add(new SiteRoom
            {
                Id = new SiteRoomId(FirstRoom + i),
                FloorIndex = 0,
                StartX = new Fixed32(i * RoomWidthCm),
                EndX = new Fixed32((i + 1) * RoomWidthCm),
                RoomTemplateId = 12_002,
                NameKey = "chain" + i,
                DefaultLightLevel = SiteLightLevel.Lit,
                NoiseAbsorptionPercent = 0,
            });
        }

        for (int i = 0; i < rooms - 1; i++)
        {
            region.Connections.Add(new SiteConnection
            {
                Id = new SiteConnectionId(i + 1),
                RoomA = new SiteRoomId(FirstRoom + i),
                FloorIndexA = 0,
                RoomB = new SiteRoomId(FirstRoom + i + 1),
                FloorIndexB = 0,
                Kind = SiteConnectionKind.Door,
                ConnectionTypeId = 12_051,
                X = new Fixed32((i + 1) * RoomWidthCm),
                UpperX = new Fixed32((i + 1) * RoomWidthCm),
                TraverseSteps = 10,
                IsLocked = false,
                BlocksVision = true,
                UsableByNpc = true,
            });
        }

        // One emitter per room, each filling it, so lighting is not a variable either.
        for (int i = 0; i < rooms; i++)
        {
            region.Lights.Add(new SiteLight
            {
                Id = i + 1,
                RoomId = new SiteRoomId(FirstRoom + i),
                LightSourceId = 12_251,
                X = new Fixed32((i * RoomWidthCm) + (RoomWidthCm / 2)),
                Level = SiteLightLevel.Lit,
                RadiusCm = RoomWidthCm,
            });
        }

        return new SiteLayout
        {
            SiteTemplateId = 11_002,
            Tier = 1,
            MissionId = 2,
            WorldSeed = 1UL,
            MapSeed = 1UL,
            MainSite = region,
            EntranceRoomId = new SiteRoomId(FirstRoom),
            ObjectiveRoomId = new SiteRoomId(FirstRoom + rooms - 1),
            ExtractionRoomIds = new[] { new SiteRoomId(FirstRoom) },
        };
    }

    /// <summary>
    /// A source making the noise and a listener standing in the last room.
    /// </summary>
    /// <remarks>
    /// Exactly two actors, because the matrix is about the distance between one sound and
    /// one pair of ears. A third listener would add a second question — "which of them
    /// was closer" — that belongs to a different test.
    /// </remarks>
    public static IReadOnlyList<TacticalActor> Actors(SiteLayout layout, int noiseAtCm)
    {
        var source = new TacticalActor
        {
            Id = new TacticalActorId(Source),
            Kind = TacticalActorKind.Agent,
            Position = PerceptionFixture.At(noiseAtCm),
            Facing = Facing.Right,
            Condition = ActorCondition.Active,
            Health = 100,
            MaxHealth = 100,
        };

        SiteRoom? lastRoom = layout.Rooms.Count == 0 ? null : layout.Rooms[^1];

        if (lastRoom is null)
            throw new InvalidOperationException("The chain has no rooms.");

        int listenerCm = lastRoom.StartX.Raw + ListenerOffsetCm;

        var listener = new TacticalActor
        {
            Id = new TacticalActorId(Listener),
            Kind = TacticalActorKind.Guard,
            Position = PerceptionFixture.At(listenerCm),
            Facing = Facing.Left,
            Condition = ActorCondition.Active,
            Health = 100,
            MaxHealth = 100,
        };

        return new[] { source, listener };
    }

    /// <summary>
    /// A noise made by the source, positioned at <paramref name="originCm"/>.
    /// </summary>
    /// <remarks>
    /// Names the source the way every real call site does. That matters rather than
    /// being tidiness: <see cref="NoiseSystem.Propagate"/> drops the source from the
    /// listener set, because an actor does not hear themselves, and an event that left
    /// <c>SourceActorId</c> at zero would put the maker in their own listener list.
    /// </remarks>
    public static NoiseEvent Noise(int originCm, int profileId, long step = 0, string? sourceKey = null, int? baseRadiusCm = null)
        => new(PerceptionFixture.At(originCm), new Fixed32(baseRadiusCm ?? NoiseSystem.BaseRadiusCm(profileId)), profileId, step)
        {
            SourceActorId = Source,
            SourceKey = sourceKey ?? "noise.test",
        };

    /// <summary>
    /// One guard standing <see cref="ListenerOffsetCm"/> into the given room of the
    /// chain, for a test that needs listeners at more than one distance.
    /// </summary>
    public static TacticalActor ListenerIn(int roomIndex, int actorId)
        => new()
        {
            Id = new TacticalActorId(actorId),
            Kind = TacticalActorKind.Guard,
            Position = PerceptionFixture.At((roomIndex * RoomWidthCm) + ListenerOffsetCm),
            Facing = Facing.Left,
            Condition = ActorCondition.Active,
            Health = 100,
            MaxHealth = 100,
        };

    /// <summary>Every door on a chain, open, so nothing blocks the sound.</summary>
    public static IReadOnlyDictionary<SiteConnectionId, ConnectionState> Doors(SiteLayout layout)
    {
        var doors = new Dictionary<SiteConnectionId, ConnectionState>();

        foreach (SiteConnection connection in layout.Connections)
            doors[connection.Id] = ConnectionState.Open;

        return doors;
    }
}
