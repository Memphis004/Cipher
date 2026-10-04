using System;
using UnityEngine;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// How much of a floor the cutaway is drawing.
    /// </summary>
    /// <remarks>
    /// Three states rather than a bool because the interesting case is the middle one: a
    /// ceiling that is neither drawn nor absent. A boolean forces "hide the slab or draw
    /// the slab", and drawing an opaque slab over the room below is exactly the failure
    /// this whole subsystem exists to prevent.
    /// </remarks>
    public enum CutawayRole
    {
        /// <summary>Off the screen. The floor root is deactivated, so it costs nothing.</summary>
        Hidden = 0,

        /// <summary>
        /// Drawn translucently: enough to keep a storey's line on screen, not enough to
        /// see through. Used for the ceiling of a room the camera looks down into.
        /// </summary>
        Faded = 1,

        /// <summary>Drawn normally.</summary>
        Solid = 2,
    }

    /// <summary>
    /// The band of floors the camera currently covers.
    /// </summary>
    /// <remarks>
    /// A band rather than a single floor because an orthographic cutaway camera almost
    /// never frames exactly one storey — at the default zoom it frames two or three, and a
    /// rule that culled everything but the floor the controlled agent was standing on would
    /// remove the stairs the player is about to walk up.
    ///
    /// A band is never empty: constructed with its ends the wrong way round it collapses to
    /// the higher floor rather than inverting. An empty band would hide every floor and
    /// render nothing, which is the same silent failure as a camera pointed away from the
    /// site — nothing in a screenshot says "the band was inverted".
    /// </remarks>
    /// <remarks>
    /// A band rather than a single floor because an orthographic cutaway camera almost
    /// never frames exactly one storey — at the default zoom it frames two or three, and a
    /// rule that culled everything but the floor the controlled agent was standing on
    /// would remove the stairs the player is about to walk up.
    /// </remarks>
    public readonly struct FloorBand : IEquatable<FloorBand>
    {
        public FloorBand(int lowestFloor, int highestFloor)
        {
            LowestFloor = lowestFloor;
            HighestFloor = highestFloor < lowestFloor ? lowestFloor : highestFloor;
        }

        /// <summary>Lowest floor index covered, inclusive.</summary>
        public int LowestFloor { get; }

        /// <summary>Highest floor index covered, inclusive.</summary>
        public int HighestFloor { get; }

        public bool Contains(int floorIndex)
            => floorIndex >= LowestFloor && floorIndex <= HighestFloor;

        /// <summary>
        /// The band an orthographic camera sees, given where it stands and how big it is.
        /// </summary>
        /// <param name="cameraY">The camera's world Y.</param>
        /// <param name="halfHeightMetres">The camera's orthographic half-height.</param>
        /// <param name="storeyHeightMetres">Floor-to-floor height.</param>
        /// <param name="floorCount">How many floors the building has.</param>
        /// <remarks>
        /// <para>
        /// The camera is pitched down, so its world-space vertical extent is larger than
        /// <c>orthographicSize</c> suggests. A 10% margin on the storey height absorbs
        /// that without letting a whole extra floor in at the default zoom.
        /// </para>
        /// <para>
        /// This is the one place in the tactical view that measures the camera rather than
        /// being told where to look, and it is worth being explicit that it is measuring
        /// and not deciding: it produces a band from a geometric fact about the view, and
        /// every gameplay consequence of that band is applied by the caller.
        /// </para>
        /// </remarks>
        public static FloorBand ForOrthographic(
            float cameraY, float halfHeightMetres, float storeyHeightMetres, int floorCount)
        {
            if (storeyHeightMetres <= 0f)
                throw new ArgumentOutOfRangeException(nameof(storeyHeightMetres), storeyHeightMetres,
                    "Storey height must be positive.");

            float margin = storeyHeightMetres * 0.1f;
            float bottom = cameraY - halfHeightMetres - margin;
            float top = cameraY + halfHeightMetres + margin;

            int lowest = Mathf.Clamp(Mathf.FloorToInt(bottom / storeyHeightMetres), 0, Mathf.Max(0, floorCount - 1));
            int highest = Mathf.Clamp(Mathf.FloorToInt(top / storeyHeightMetres), 0, Mathf.Max(0, floorCount - 1));

            return new FloorBand(lowest, highest);
        }

        public bool Equals(FloorBand other)
            => LowestFloor == other.LowestFloor && HighestFloor == other.HighestFloor;

        public override bool Equals(object obj) => obj is FloorBand other && Equals(other);

        public override int GetHashCode() => (LowestFloor * 397) ^ HighestFloor;

        public override string ToString() => $"floors {LowestFloor}..{HighestFloor}";
    }

    /// <summary>
    /// The cutaway's rules, as pure functions over floor indices.
    /// </summary>
    /// <remarks>
    /// Deliberately not a MonoBehaviour. "Which floors may the camera see" is a question
    /// with a right answer that has nothing to do with Unity, and a question like that is
    /// worth being able to assert in an EditMode test on fifty generated layouts without
    /// standing up a camera or a building.
    /// </remarks>
    public static class CutawayVisibility
    {
        /// <summary>
        /// How much of a floor is drawn.
        /// </summary>
        /// <remarks>
        /// Inside the band: solid. Outside it: hidden outright rather than faded, because
        /// the brief asks for "only the floors the camera covers" and a translucent floor
        /// outside the frame is a cost with no reader.
        /// </remarks>
        public static CutawayRole FloorRole(int floorIndex, FloorBand band)
            => band.Contains(floorIndex) ? CutawayRole.Solid : CutawayRole.Hidden;

        /// <summary>
        /// How much of a floor's ceiling slab is drawn.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the "intervening geometry" the cutaway has to fade out. A room's own
        /// ceiling sits between the camera and that room's floor whenever the camera is
        /// above the room — and a camera looking down into a lane is above almost every
        /// storey it covers, so an opaque slab there hides the floor the player is playing.
        /// </para>
        /// <para>
        /// The test is against the floor's <em>slab</em>, not against its ceiling. A
        /// ceiling under the camera is being seen from below, occludes nothing, and fading
        /// it anyway would make the building lose its horizontal structure as the camera
        /// dropped — which reads as missing geometry rather than as a cutaway.
        /// </para>
        /// </remarks>
        public static CutawayRole CeilingRole(
            int floorIndex, FloorBand band, float cameraY, float storeyHeightMetres)
        {
            if (!band.Contains(floorIndex))
                return CutawayRole.Hidden;

            float slabY = floorIndex * storeyHeightMetres;
            return cameraY > slabY ? CutawayRole.Faded : CutawayRole.Solid;
        }
    }
}