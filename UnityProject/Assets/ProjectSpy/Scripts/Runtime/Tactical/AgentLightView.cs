using System.Collections.Generic;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using UnityEngine;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// Draws one actor according to how lit Core says it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole reason the tactical view reads as stealth rather than as a
    /// diorama. An agent in a dark room is a silhouette; the same agent under a light is
    /// fully visible, and the difference between those two is exactly the difference
    /// between a guard noticing them and not.
    /// </para>
    /// <para>
    /// <b>The level is Core's, never this component's.</b> It comes from
    /// <c>LightState.LevelAt</c> — the same call that decides whether a perception
    /// succeeds — so the shading cannot disagree with the simulation. Re-deriving "is this
    /// room bright" from the lights that happen to be rendering would produce exactly that
    /// disagreement the moment the twelve-light budget bit, and the player would be shown a
    /// silhouette in a room a guard can see them in.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class AgentLightView : MonoBehaviour
    {
        private static readonly Dictionary<SiteLightLevel, Material> Materials = new();

        private Renderer[] _renderers;
        private LightingDirector _lights;
        private SiteLightLevel _applied = (SiteLightLevel)(-1);

        /// <summary>Core's current verdict for this actor.</summary>
        public SiteLightLevel Level { get; private set; }

        /// <summary>The actor this view is drawing, if any.</summary>
        public TacticalActor Actor { get; private set; }

        /// <summary>
        /// Binds the view to a Core actor and the director that answers light questions.
        /// </summary>
        public void Bind(TacticalActor actor, LightingDirector lights)
        {
            Actor = actor;
            _lights = lights;
            _renderers = GetComponentsInChildren<Renderer>();
            _applied = (SiteLightLevel)(-1);
        }

        /// <summary>
        /// Re-reads Core's light state for this actor and repaints if it changed.
        /// </summary>
        /// <remarks>
        /// Called every frame by the director's owner. The comparison against the last
        /// applied level is what keeps this off the hot path in the common case: an actor
        /// standing still in a lit room costs one dictionary-free enum comparison.
        /// </remarks>
        public void Sync()
        {
            if (Actor is null || _lights is null || _renderers is null || _renderers.Length == 0)
                return;

            SiteLightLevel level = _lights.LevelAt(Actor.Position);
            Level = level;

            if (level == _applied)
                return;

            _applied = level;

            Material material = MaterialFor(level);
            foreach (Renderer renderer in _renderers)
            {
                if (renderer != null)
                    renderer.sharedMaterial = material;
            }
        }

        /// <summary>
        /// The shared material an actor is drawn with at a light level.
        /// </summary>
        /// <remarks>
        /// One per level, shared by every actor at that level. Thirty NPCs times three
        /// cloned materials would be ninety materials for three distinct appearances, and
        /// would break SRP batching for every actor in the mission.
        /// </remarks>
        public static Material MaterialFor(SiteLightLevel level)
        {
            if (Materials.TryGetValue(level, out Material cached) && cached != null)
                return cached;

            Shader shader = level == SiteLightLevel.Lit
                ? Site.BlockoutMaterials.FindShader()
                : Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color")
                  ?? Site.BlockoutMaterials.FindShader();

            var material = new Material(shader)
            {
                name = $"M_Agent_{level} (runtime)",
                color = ColourFor(level),
            };

            Materials[level] = material;
            return material;
        }

        /// <summary>
        /// The colour an actor reads as at a light level.
        /// </summary>
        /// <remarks>
        /// Dark is not black. A pure-black figure on a near-black room is invisible, and
        /// "invisible" would read as "not there" rather than as "there, and unlit" — which
        /// is the opposite of the truth and the opposite of what the player needs. A
        /// silhouette has to be visible <em>as a shape</em> to communicate that something is
        /// standing in the dark.
        /// </remarks>
        public static Color ColourFor(SiteLightLevel level) => level switch
        {
            SiteLightLevel.Lit => new Color(0.86f, 0.87f, 0.88f),
            SiteLightLevel.Dim => new Color(0.34f, 0.37f, 0.42f),
            _ => new Color(0.055f, 0.060f, 0.075f),
        };
    }
}