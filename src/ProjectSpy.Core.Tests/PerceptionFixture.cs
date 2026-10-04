using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// A purpose-built two-room building for the perception and noise matrices.
/// </summary>
/// <remarks>
/// <para>
/// The generated sites are right for the fuzz sweep and wrong for a matrix. A matrix
/// test's whole job is to vary one thing and hold the rest constant, and a generated
/// site changes its spans, its lights, its occluders and its archetype between seeds —
/// so a test sweeping light levels would be sweeping four other variables at the same
/// time and a failure would name none of them.
/// </para>
/// <para>
/// So this builds the smallest building that can exhibit every factor: two rooms side by
/// side on one floor, one door between them, one emitter per room whose level and reach
/// are known exactly, and any number of occluders the test asks for. Nothing here is
/// random, so a failure is always the formula and never the fixture.
/// </para>
/// <para>
/// The geometry is deliberately plain. Room R001 occupies <c>[0, 900)</c> and room R002
/// <c>[900, 2000)</c>, joined by a door on the shared wall at 900 — the half-open
/// intervals the layout validator insists on, so the fixture is a building the rest of
/// the codebase would also accept.
/// </para>
/// </remarks>
internal static class PerceptionFixture
{
    /// <summary>The left-hand room.</summary>
    internal static readonly SiteRoomId Left = new(1);

    /// <summary>The right-hand room.</summary>
    internal static readonly SiteRoomId Right = new(2);

    /// <summary>The door between them.</summary>
    internal static readonly SiteConnectionId Door = new(1);

    /// <summary>The emitter in the left-hand room.</summary>
    internal const int LeftLight = 1;

    /// <summary>The emitter in the right-hand room.</summary>
    internal const int RightLight = 2;

    /// <summary>Left edge of the left room, in centimetres.</summary>
    internal const int LeftStartCm = 0;

    /// <summary>Right edge of the left room, in centimetres.</summary>
    internal const int LeftEndCm = 900;

    /// <summary>Left edge of the right room, in centimetres.</summary>
    internal const int RightStartCm = 900;

    /// <summary>Right edge of the right room, in centimetres.</summary>
    internal const int RightEndCm = 2000;

    /// <summary>Where the door sits, in centimetres.</summary>
    internal const int DoorXcm = 900;

    /// <summary>Centre of the left room, in centimetres.</summary>
    internal const int LeftCentreCm = 450;

    /// <summary>Centre of the right room, in centimetres.</summary>
    internal const int RightCentreCm = 1450;

    /// <summary>The site template id the fixture reports. Arbitrary and constant.</summary>
    internal const int SiteTemplateId = 11_001;

    /// <summary>
    /// Builds the fixture with every factor under the test's own control.
    /// </summary>
    /// <param name="leftLight">Light level in the left room.</param>
    /// <param name="rightLight">Light level in the right room.</param>
    /// <param name="leftOccluders">
    /// Occluders in the left room, placed evenly between the room's edges.
    /// </param>
    /// <param name="blocksVision">Whether the door stops the sightline going through it.</param>
    public static SiteLayout Build(
        SiteLightLevel leftLight = SiteLightLevel.Lit,
        SiteLightLevel rightLight = SiteLightLevel.Lit,
        int leftOccluders = 0,
        bool blocksVision = true)
    {
        var floor = new SiteFloor
        {
            Index = 0,
            Kind = SiteFloorKind.Ground,
            Span = new Fixed32(RightEndCm),
        };

        floor.Rooms.Add(Room(Left, leftLight, "left"));
        floor.Rooms.Add(Room(Right, rightLight, "right"));

        var region = new SiteRegion { Kind = SiteRegionKind.Site };
        region.Floors.Add(floor);

        region.Connections.Add(new SiteConnection
        {
            Id = Door,
            RoomA = Left,
            FloorIndexA = 0,
            RoomB = Right,
            FloorIndexB = 0,
            Kind = SiteConnectionKind.Door,
            ConnectionTypeId = 12_051,
            X = new Fixed32(DoorXcm),
            UpperX = new Fixed32(DoorXcm),
            TraverseSteps = 10,
            IsLocked = false,
            BlocksVision = blocksVision,
            UsableByNpc = true,
        });

        region.Lights.Add(Emitter(LeftLight, Left, LeftCentreCm, leftLight));
        region.Lights.Add(Emitter(RightLight, Right, RightCentreCm, rightLight));

        for (int i = 0; i < leftOccluders; i++)
        {
            // Spread evenly across the room's interior, never at an edge: an occluder at
            // the exact spot an actor stands on is a different test, and one that would
            // make the fixture's own placement ambiguous.
            int span = (LeftEndCm - LeftStartCm) - 40;
            int x = LeftStartCm + 20 + ((span / Math.Max(1, leftOccluders)) * (i + 1));

            region.Occluders.Add(new SiteOccluder { Id = i + 1, RoomId = Left, X = new Fixed32(x) });
        }

        return new SiteLayout
        {
            SiteTemplateId = SiteTemplateId,
            Tier = 1,
            MissionId = 1,
            WorldSeed = 1UL,
            MapSeed = 1UL,
            MainSite = region,
            EntranceRoomId = Left,
            ObjectiveRoomId = Right,
            ExtractionRoomIds = new[] { Left },
        };
    }

    /// <summary>
    /// A live lighting state for a fixture layout.
    /// </summary>
    /// <remarks>
    /// Built separately from the layout because a light's level is fixed when the emitter
    /// is placed, and the matrix needs to sweep it. Building a fresh fixture per level is
    /// the honest way to do that: it changes exactly the thing under test and nothing
    /// else, which is what a matrix is for.
    /// </remarks>
    public static LightState Lighting(SiteLayout layout) => new(layout);

    /// <summary>
    /// An observer with exactly the sight and hearing a test asks for.
    /// </summary>
    /// <remarks>
    /// Not a guard from a generated site, whose archetype would vary the very numbers the
    /// test is sweeping. The cone and range are parameters so that the cone axis can be
    /// tested without inventing a new archetype row for every value.
    /// </remarks>
    public static TacticalActor Observer(
        TacticalPosition at,
        int rangeCm,
        int coneDegrees,
        Facing facing = Facing.Right)
        => new()
        {
            Id = new TacticalActorId(1),
            Kind = TacticalActorKind.Guard,
            Position = at,
            Facing = facing,
            Posture = Posture.Walk,
            Condition = ActorCondition.Active,
            Health = 100,
            MaxHealth = 100,
            Stamina = 100,
            Vision = new VisionStats
            {
                VisionRangeCm = rangeCm,
                VisionConeDegrees = coneDegrees,
                HearingRangeCm = 0,
                RangeBonusPercent = 0,
            },
        };

    /// <summary>A target with exactly the posture a test asks for.</summary>
    public static TacticalActor Target(TacticalPosition at, Posture posture = Posture.Walk, int disguiseId = 0)
        => new()
        {
            Id = new TacticalActorId(2),
            Kind = TacticalActorKind.Agent,
            Position = at,
            Facing = Facing.Left,
            Posture = posture,
            DisguiseId = disguiseId,
            Condition = ActorCondition.Active,
            Health = 100,
            MaxHealth = 100,
            Stamina = 100,
        };

    /// <summary>A position on the single floor at a centimetre offset.</summary>
    public static TacticalPosition At(int cm) => new(0, new Fixed32(cm));

    /// <summary>A position in the left room at an offset from its left edge.</summary>
    public static TacticalPosition InLeft(int offsetCm) => At(LeftStartCm + offsetCm);

    /// <summary>A position in the right room at an offset from its left edge.</summary>
    public static TacticalPosition InRight(int offsetCm) => At(RightStartCm + offsetCm);

    // ---- private builders ----------------------------------------------------

    private static SiteRoom Room(SiteRoomId id, SiteLightLevel level, string name)
        => new()
        {
            Id = id,
            FloorIndex = 0,
            StartX = new Fixed32(id == Left ? LeftStartCm : RightStartCm),
            EndX = new Fixed32(id == Left ? LeftEndCm : RightEndCm),
            RoomTemplateId = 12_002,
            NameKey = name,
            // Dark, so the emitter is the only thing lighting the room and switching
            // it off genuinely darkens it rather than falling back to a lit default.
            DefaultLightLevel = SiteLightLevel.Dark,
            NoiseAbsorptionPercent = 0,
        };

    private static SiteLight Emitter(int id, SiteRoomId roomId, int x, SiteLightLevel level)
        => new()
        {
            Id = id,
            RoomId = roomId,
            LightSourceId = 12_251,   // light.ceiling_strip: dim, switchable, restorable
            X = new Fixed32(x),
            Level = level,
            RadiusCm = 5_000,          // wide enough that the fixture never runs out of it
        };
}
