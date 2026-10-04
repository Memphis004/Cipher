using System.Collections.Generic;
using ProjectSpy.Core;
using UnityEngine;

// Core declares its own `Resources` (the strategic resource pool), which collides with
// UnityEngine.Resources in any file that does a Resources.Load. `using ProjectSpy.Core;`
// is therefore absent here, and the two Core types actually needed are aliased instead.
// Reintroducing the namespace using is a compile error, which is the point.
using CoreFogState = ProjectSpy.Core.FogState;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// How a room is drawn, given what the player knows about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are <em>presentation</em> states, not a second copy of Core's fog. Core's
    /// <see cref="CoreFogState"/> is monotone — Unknown, Reported, Scouted, Observed,
    /// Cleared — and says nothing about how any of it should look. This enum says how,
    /// and exists so that the mapping from one to the other is written down once, in one
    /// place, and cannot drift between the renderer, the HUD legend and the tests.
    /// </para>
    /// <para>
    /// The values are deliberately ordered from least to most known. Nothing branches on
    /// the ordering — <see cref="FogLooks.ShowsContents"/> is an explicit switch — because an
    /// ordering that reads as meaningful is an ordering that gets compared with >= the
    /// moment someone reorders this enum.
    /// </para>
    /// </remarks>
    public enum FogLook
    {
        /// <summary>
        /// Never seen and never reported. A near-black void.
        /// </summary>
        /// <remarks>
        /// A dark grey box would be worse than nothing: it reads as "a room with nothing in
        /// it", and the player would plan around a space they have no information about.
        /// A void reads as "no information", which is the truth.
        /// </remarks>
        UnknownVoid = 0,

        /// <summary>A sleeper said so. Desaturated, unlit, and visibly not real.</summary>
        Reported = 1,

        /// <summary>Read from outside. Lit, but still the team's picture of it.</summary>
        Scouted = 2,

        /// <summary>Seen by the team. Fully rendered.</summary>
        Observed = 3,

        /// <summary>Searched. Fully rendered, and cooler than Observed.</summary>
        Cleared = 4,
    }

    /// <summary>
    /// The mapping from Core's fog state to a presentation look, plus the material each
    /// look draws with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One shared material per look.</b> A fog renderer assigns <c>sharedMaterial</c>
    /// and never instantiates. Fifty rooms drawn as fifty clones would defeat SRP batching
    /// for the entire building, and the only thing that varies between two rooms in the
    /// same fog state is the fog state.
    /// </para>
    /// <para>
    /// <b>Reported is unlit on purpose.</b> It is the strongest single cue that a room is a
    /// claim rather than a fact: a real room in this building is lit by the building's own
    /// lights, so a room that does not respond to them is visibly not the same kind of
    /// object. A desaturated tint alone was not enough in practice — it reads as a
    /// different paint scheme rather than as a different kind of knowledge.
    /// </para>
    /// </remarks>
    public static class FogLooks
    {
        private static readonly Dictionary<FogLook, Material> Materials = new();
        private static readonly Dictionary<FogLook, Color> Tints = new();

        /// <summary>Near-black. Not pure black: a void must still read as a solid.</summary>
        public static readonly Color UnknownColour = new(0.010f, 0.012f, 0.018f);

        /// <summary>Cold, flat, and slightly green, so it never sits near the lit grey.</summary>
        public static readonly Color ReportedColour = new(0.19f, 0.24f, 0.23f);

        /// <summary>Lit but drained, for a room read from outside.</summary>
        public static readonly Color ScoutedColour = new(0.44f, 0.46f, 0.46f);

        /// <summary>The building's own blockout grey, unchanged.</summary>
        public static readonly Color ClearedColour = new(0.50f, 0.53f, 0.56f);

        /// <summary>
        /// Core's fog state as a look. Total, so an unrecognised value cannot fall through.
        /// </summary>
        public static FogLook LookFor(CoreFogState state) => state switch
        {
            CoreFogState.Reported => FogLook.Reported,
            CoreFogState.Scouted => FogLook.Scouted,
            CoreFogState.Observed => FogLook.Observed,
            CoreFogState.Cleared => FogLook.Cleared,
            _ => FogLook.UnknownVoid,
        };

        /// <summary>
        /// Whether a room's contents are drawn at all.
        /// </summary>
        /// <remarks>
        /// False for everything the team has not walked into, and that is a simulation fact
        /// rather than a visual one: Core generates a room's loot and props on first
        /// observation (rule 11) and not before, so a guard or a loot marker in an
        /// unobserved room is drawing a thing that does not exist. Showing it would hand the
        /// player information the game has not given them.
        /// </remarks>
        public static bool ShowsContents(FogLook look) => look switch
        {
            FogLook.Observed => true,
            FogLook.Cleared => true,
            _ => false,
        };

        /// <summary>Whether a look is drawn without responding to the building's lights.</summary>
        public static bool IsUnlit(FogLook look) => look switch
        {
            FogLook.UnknownVoid => true,
            FogLook.Reported => true,
            _ => false,
        };

        /// <summary>The material a look is drawn with. Shared; never instantiate it.</summary>
        public static Material MaterialFor(FogLook look)
        {
            if (Materials.TryGetValue(look, out Material cached) && cached != null)
                return cached;

            Material material;

            if (look == FogLook.Observed)
            {
                // The building's own grey, so an observed room is indistinguishable from
                // one rendered before fog existed.
                material = Site.BlockoutMaterials.Grey;
            }
            else
            {
                material = new Material(IsUnlit(look) ? FindUnlitShader() : Site.BlockoutMaterials.FindShader())
                {
                    name = $"M_Fog_{look} (runtime)",
                    color = TintFor(look),
                };
            }

            Materials[look] = material;
            return material;
        }

        /// <summary>
        /// The colour a look's HUD chip and legend swatch use.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These are legend glyphs, not pixel samples of the rooms. The material colours are
        /// deliberately close together — Scouted is only "lit but drained", and Observed is
        /// the building's own grey — because in the world the difference between them should
        /// be something you notice rather than something you read off a colour chart. That
        /// makes them useless in a legend: at one glance four of the five swatches have to
        /// be told apart, and a swatch you cannot tell apart is worse than no swatch.
        /// </para>
        /// <para>
        /// So the swatches are spread deliberately along one axis, darkness to brightness,
        /// with Reported the only one carrying a hue. Unknown stays near-black because a void
        /// that reads as a mid grey is not a void; Reported is cold and green because it is a
        /// claim about the building rather than part of it; and Observed and Cleared are the
        /// two bright neutrals at the top, ordered so that the brighter one is the stronger
        /// state. The real rooms run cool (blue at or above green) and only Reported is
        /// green-dominant, which is what makes "coloured" mean "claimed" rather than
        /// "bright". A swatch therefore always matches its room's family — the lit greys
        /// are cool grey, the claim is teal, the void is black — while staying separable
        /// at 20 pixels.
        /// </para>
        /// </remarks>
        public static Color TintFor(FogLook look)
        {
            if (Tints.TryGetValue(look, out Color cached))
                return cached;

            Color tint = look switch
            {
                FogLook.UnknownVoid => new Color(0.09f, 0.10f, 0.13f),
                FogLook.Reported => new Color(0.26f, 0.45f, 0.43f),
                FogLook.Scouted => new Color(0.42f, 0.47f, 0.52f),
                FogLook.Observed => new Color(0.68f, 0.72f, 0.76f),
                FogLook.Cleared => new Color(0.86f, 0.90f, 0.94f),
                _ => new Color(0.68f, 0.72f, 0.76f),
            };

            tint.a = 1f;

            Tints[look] = tint;
            return tint;
        }

        /// <summary>
        /// A translucent material for geometry the cutaway fades out.
        /// </summary>
        /// <remarks>
        /// One shared instance for every faded ceiling in the building. The alternative —
        /// cloning a material per ceiling to give each its own alpha — would allocate once
        /// per room and, worse, would make each ceiling its own draw batch for the rest of
        /// the mission.
        /// </remarks>
        public static Material Ghost
        {
            get
            {
                if (_ghost != null)
                    return _ghost;

                var shader = FindUnlitShader();
                _ghost = new Material(shader)
                {
                    name = "M_CutawayGhost (runtime)",
                    color = new Color(0.62f, 0.68f, 0.78f, GhostAlpha),
                };

                // URP reads transparency off these rather than off the alpha alone; without
                // them the material renders fully opaque and the cutaway does nothing.
                _ghost.SetFloat("_Surface", 1f);
                _ghost.SetFloat("_Blend", 0f);
                _ghost.SetFloat("_ZWrite", 0f);
                _ghost.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _ghost.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                _ghost.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

                return _ghost;
            }
        }

        /// <summary>How faint an intervening ceiling is. Low enough to see through, high enough to draw.</summary>
        public const float GhostAlpha = 0.11f;

        private static Material _ghost;

        private static Shader FindUnlitShader()
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp != null)
                return urp;

            return Shader.Find("Unlit/Color") ?? Site.BlockoutMaterials.FindShader();
        }
    }
}