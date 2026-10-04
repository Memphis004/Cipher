using UnityEngine;

namespace ProjectSpy.Unity.Site
{
    /// <summary>
    /// The one place Core's integer centimetres become Unity's float metres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists as a type rather than a constant.</b> A lane unit is one metre
    /// and Core works in centimetres, so every number crossing this boundary is
    /// <c>cm / 100</c>. Written inline that is a <c>/ 100</c> in twenty places, and one
    /// of them will eventually be a <c>* 100</c>. Worse, <c>Fixed32</c> exposes
    /// <c>ToDisplayMetres()</c> explicitly labelled presentation-only, and having a
    /// second, differently-rounded conversion next to it invites the two to disagree.
    /// There is therefore exactly one implementation and no other place allowed to
    /// divide by 100.
    /// </para>
    /// <para>
    /// <b>Why the depth values are here too.</b> Core is one-dimensional: a room is an
    /// interval on a line, with no depth. Presentation has to invent a depth, and if
    /// each prefab invents its own then a two-metre room and a two-metre corridor end up
    /// different widths and the cutaway reads as a mistake. The room depth is therefore a
    /// decision recorded in one file.
    /// </para>
    /// <para>
    /// <b>Floats here are correct.</b> knowledge.md rule 15 permits floating point in
    /// Presentation only. Nothing in this file may be called from a rule path, and no
    /// value produced here may be fed back into Core: the return trip is
    /// <see cref="ToCentimetres"/>, which is integral.
    /// </para>
    /// </remarks>
    public static class LaneUnits
    {
        /// <summary>Centimetres in one lane unit. Core stores centimetres; a lane unit is a metre.</summary>
        public const float CentimetresPerMetre = 100f;

        /// <summary>Lane units (metres) in one storey. Floor-to-floor height, not room height.</summary>
        public const float StoreyHeightMetres = 3.2f;

        /// <summary>
        /// Clear height inside a room: the storey height minus the slab that sits above it.
        /// </summary>
        public const float RoomHeightMetres = StoreyHeightMetres - FloorThicknessMetres;

        /// <summary>Thickness of a floor slab, used when stacking storeys.</summary>
        public const float FloorThicknessMetres = 0.25f;

        /// <summary>
        /// How deep a room is from front to back, measured to the back wall's inner face.
        /// </summary>
        /// <remarks>
        /// The cutaway camera looks at the building from the side with the front wall
        /// removed, so this is the depth of the visible interior. A shallow depth keeps
        /// every floor's contents legible from one angle, which is the whole point of a
        /// 2.5D view: if it were deep enough that the back wall fell outside the frame at
        /// shallow camera angles, the player's read of the floor plan would depend on
        /// camera angle.
        /// </remarks>
        public const float RoomDepthMetres = 6f;

        /// <summary>Width of a doorway opening. Comfortably wider than an agent capsule.</summary>
        public const float DoorWidthMetres = 1.1f;

        /// <summary>Height of a doorway opening.</summary>
        public const float DoorHeightMetres = 2.1f;

        /// <summary>Thickness of an interior wall.</summary>
        public const float WallThicknessMetres = 0.15f;

        /// <summary>Thickness of an exterior wall, thicker so the building silhouette reads.</summary>
        public const float ExteriorWallThicknessMetres = 0.3f;

        /// <summary>Radius of the agent capsule. Unity's default capsule is 0.5m radius, 2m tall.</summary>
        public const float AgentRadiusMetres = 0.35f;

        /// <summary>Total height of the agent capsule marker.</summary>
        public const float AgentHeightMetres = 1.8f;

        /// <summary>Converts Core centimetres to Unity metres.</summary>
        public static float ToMetres(int centimetres) => centimetres / CentimetresPerMetre;

        /// <summary>Converts a Unity metre position on the lane to Core centimetres.</summary>
        /// <remarks>
        /// The inverse of <see cref="ToMetres(int)"/>, and the only sanctioned way to turn
        /// a rendered position back into a simulated one. Rounds half away from zero, which
        /// matches <see cref="Fixed32"/>'s rounding so a round trip through both lands on
        /// the same centimetre rather than one off.
        /// </remarks>
        public static int ToCentimetres(float metres)
            => Mathf.RoundToInt(metres * CentimetresPerMetre);

        /// <summary>
        /// World X for a Core X value, relative to an origin offset.
        /// </summary>
        /// <remarks>
        /// Taking the origin as a parameter rather than assuming zero is what lets a
        /// generated building be placed anywhere in a scene — and, for the preview tool, what
        /// lets fifty of them be laid out in a grid without each one hard-coding its own
        /// offset.
        /// </remarks>
        public static float ToWorldX(int centimetres, float originX = 0f)
            => originX + ToMetres(centimetres);

        /// <summary>World Y for a Core floor index, relative to an origin offset.</summary>
        /// <remarks>
        /// Floor 0 sits at the origin rather than one storey up, so a single-storey site has
        /// its ground floor on y=0 and reads as sitting on the ground plane.
        /// </remarks>
        public static float ToWorldY(int floorIndex, float originY = 0f)
            => originY + floorIndex * StoreyHeightMetres;

        /// <summary>
        /// The centre of a room in world space, given its Core interval.
        /// </summary>
        /// <remarks>
        /// The centre rather than the left edge, because a box primitive is authored around
        /// its own pivot and positioning by centre avoids every prefab needing a different
        /// compensating offset. Width is returned separately by <see cref="RoomWidth"/>,
        /// because folding two meanings into one Vector3 would let a caller pass a centre
        /// where a width was expected.
        /// </remarks>
        public static Vector3 RoomCentre(int startXcm, int endXcm, int floorIndex, float originX, float originY)
            => new Vector3(
                originX + ToMetres((startXcm + endXcm) / 2),
                ToWorldY(floorIndex, originY) + FloorThicknessMetres * 0.5f,
                0f);

        /// <summary>Width in metres of a room interval.</summary>
        public static float RoomWidth(int startXcm, int endXcm)
            => ToMetres(endXcm - startXcm);

        /// <summary>
        /// Guards against a generated layout that would place geometry inside-out.
        /// </summary>
        /// <remarks>
        /// Called by the assembler on every room rather than trusted, because a generator
        /// bug that produced a zero-width room would otherwise manifest as an invisible
        /// box that looks like a hole in the building — a symptom with no obvious cause.
        /// </remarks>
        public static bool IsRenderableInterval(int startXcm, int endXcm)
            => endXcm > startXcm;
    }
}