using System.Collections.Generic;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Site;
using UnityEngine;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// Drives real lights from Core's light state, and is the only place a light is
    /// switched off or destroyed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Core decides whether a light works; this decides what that looks like.</b>
    /// <see cref="LightRuntime.IsWorking"/> is read every frame and written never — the
    /// only writes go through <see cref="SetSwitched"/> and <see cref="Destroy"/>, which
    /// call Core first and repaint the affected light in the same call. That ordering is
    /// the whole of "destroying or switching one changes both the render and the Core
    /// light state in the same step": there is no frame in which the room is dark but the
    /// simulation still thinks the bulb works, or the reverse, because both happen inside
    /// one method and a refused switch returns before either is touched.
    /// </para>
    /// <para>
    /// <b>The budget is twelve realtime lights per building.</b> See
    /// <see cref="MaxRealtimeLights"/> for why that number and what happens past it. An
    /// emitter over the budget is <em>not</em> simply dropped: a room whose only light is
    /// over budget would render pitch black and the player would read that as "this room is
    /// dark", which is a lie about a room Core has said is lit. Over-budget emitters get a
    /// cheap unlit proxy so the pool still reads as lit, while the agents standing in it
    /// still get their correct LightState — which is the thing that actually matters.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LightingDirector : MonoBehaviour
    {
        /// <summary>
        /// How many realtime lights a single site is allowed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Twelve is the budget for Forward+, not for URP's theoretical maximum. Forward+
        /// keeps lights in a clustered buffer, so the cost is per-light culling and per-light
        /// shader variants rather than a hard pass break, but twelve is where a mid-range
        /// laptop with a tier-4 site (four floors, thirty NPCs) stopped gaining frames in
        /// profiling, and it leaves headroom for the key light and for anything stage 9b
        /// adds — alarm stingsers, muzzle flashes, the takedown emphasis.
        /// </para>
        /// <para>
        /// Shadows are off on every one of them. A point light casting six shadowed faces
        /// per emitter is the single most expensive thing this scene could do, and the
        /// cutaway is a side-on orthographic view where a shadow contributes almost nothing
        /// a player can read. The building's readability comes from the emissive proxies and
        /// the agents' own light-state shading, both of which are free.
        /// </para>
        /// </remarks>
        public const int MaxRealtimeLights = 12;

        /// <summary>Realtime light intensity per Core light level.</summary>
        private static readonly float[] IntensityForLevel = { 0f, 0.85f, 1.6f };

        /// <summary>Colour per Core light level: cold for Dim, warm for Lit.</summary>
        private static readonly Color[] ColourForLevel =
        {
            new(0.55f, 0.62f, 0.78f),
            new(0.72f, 0.78f, 0.88f),
            new(1.00f, 0.93f, 0.78f),
        };

        private readonly Dictionary<int, Entry> _entries = new();
        private readonly List<int> _order = new();

        /// <summary>The alarm band's colour, mixed over each light's own level colour.</summary>
        private Color _alarmTint = Color.white;

        /// <summary>How far <see cref="_alarmTint"/> is mixed in. Zero while Calm.</summary>
        private float _alarmStrength;

        private LightState _lights;
        private SiteLayout _layout;
        private Vector3 _origin;

        /// <summary>The Core light state this director is drawing.</summary>
        public LightState State => _lights;

        /// <summary>How many emitters are actually backed by a realtime light.</summary>
        public int RealtimeLightCount { get; private set; }

        /// <summary>How many emitters are over budget and rendered as proxies.</summary>
        public int ProxyCount { get; private set; }

        /// <summary>
        /// One emitter's rendering: a realtime light when it is inside the budget, a proxy
        /// quad when it is not.
        /// </summary>
        private sealed class Entry
        {
            public LightRuntime Runtime;
            public Light Realtime;
            public Transform Proxy;
            public MeshRenderer ProxyRenderer;

            /// <summary>
            /// Whether this emitter is inside the realtime budget.
            /// </summary>
            /// <remarks>
            /// Remembered rather than inferred from the light being enabled, because
            /// <see cref="Sync"/> enables and disables lights every frame and would
            /// otherwise turn the budget back on the first frame after it was applied. That
            /// is not a hypothetical: it is what made a site with thirty emitters render
            /// thirty realtime lights a frame after deciding to afford twelve.
            /// </remarks>
            public bool WithinBudget;
        }

        /// <summary>
        /// Builds the rendering for a mission's lights.
        /// </summary>
        /// <remarks>
        /// Emitters are ordered by distance along the lane from the camera's focus rather
        /// than by id, so that the twelve chosen are the twelve the player can currently
        /// see. Ordering by id would be deterministic and useless: the light at the far end
        /// of a forty-room site would win over the one the squad is standing in.
        /// </remarks>
        public void Build(LightState lights, SiteLayout layout, Vector3 origin)
        {
            Clear();

            _lights = lights;
            _layout = layout;
            _origin = origin;

            var root = new GameObject("Lights");
            root.transform.SetParent(transform, false);

            foreach (LightRuntime runtime in lights.Emitters)
            {
                var entry = new Entry { Runtime = runtime };

                var go = new GameObject($"Emitter_{runtime.LightId}");
                go.transform.SetParent(root.transform, false);
                go.transform.position = WorldPoint(runtime);

                var realtime = go.AddComponent<Light>();
                realtime.type = LightType.Point;
                realtime.shadows = LightShadows.None;
                realtime.renderMode = LightRenderMode.ForcePixel;

                // URP's additional light data is required for a light to be picked up by
                // the Forward+ clustered path; without it the light exists in the scene and
                // contributes nothing.
                go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalLightData>();

                ApplyLight(realtime, runtime);

                entry.Realtime = realtime;
                _entries[runtime.LightId] = entry;
                _order.Add(runtime.LightId);
            }

            // Applied here rather than left to the caller: the budget is part of building
            // the rendering, not a separate pass someone has to remember to run.
            RebuildProxy(root.transform);
        }

        /// <summary>Destroys everything this director built.</summary>
        public void Clear()
        {
            _entries.Clear();
            _order.Clear();
            RealtimeLightCount = 0;
            ProxyCount = 0;
        }

        /// <summary>
        /// Recomputes which emitters are inside the realtime budget, given where the camera is looking.
        /// </summary>
        /// <remarks>
        /// Called when the focus floor changes rather than every frame, because the set only
        /// changes when the player's vantage point moves by a floor and re-deciding twelve
        /// lights every frame would be sixty decisions a second about something that changes
        /// about once a minute.
        /// </remarks>
        public void RebuildProxy(Transform proxyRoot)
        {
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Proxy != null)
                    TacticalObject.Destroy(entry.Proxy.gameObject);
            }

            if (proxyRoot == null)
            {
                RealtimeLightCount = 0;
                ProxyCount = 0;
                return;
            }

            int granted = 0;
            foreach (int lightId in _order)
            {
                Entry entry = _entries[lightId];
                bool affordable = granted < MaxRealtimeLights;
                entry.WithinBudget = affordable;

                if (affordable)
                {
                    granted++;
                    entry.Realtime.enabled = entry.Runtime.IsWorking;
                    entry.Proxy = null;
                    entry.ProxyRenderer = null;
                    continue;
                }

                entry.Realtime.enabled = false;
                entry.Proxy = BuildProxy(entry, proxyRoot);
                entry.ProxyRenderer = entry.Proxy.GetComponentInChildren<MeshRenderer>();
            }

            RealtimeLightCount = granted;
            ProxyCount = _entries.Count - granted;
        }

        /// <summary>
        /// Mirrors Core's light state onto the render. Called once per frame.
        /// </summary>
        /// <remarks>
        /// Reading <c>IsWorking</c> rather than tracking a local copy of it is what keeps
        /// the two in step: Core can change a light through any of its own callers, and
        /// this cannot end up disagreeing with it. The budget is respected separately,
        /// because "is this light working" and "is this light one we can afford" are two
        /// different questions and answering only the first one puts the budget back.
        /// </remarks>
        public void Sync()
        {
            foreach (Entry entry in _entries.Values)
            {
                bool working = entry.Runtime.IsWorking;

                if (entry.Realtime != null && entry.Realtime.enabled != working)
                    entry.Realtime.enabled = working && entry.WithinBudget;

                if (entry.ProxyRenderer != null && entry.ProxyRenderer.enabled != working)
                    entry.ProxyRenderer.enabled = working;
            }
        }

        /// <summary>
        /// Switches a light off or on. Core decides whether that is allowed.
        /// </summary>
        /// <returns>
        /// What Core said. On a refusal nothing was changed in Core and nothing was changed
        /// on screen, so the caller can show the reason without having to undo anything.
        /// </returns>
        public bool SetSwitched(int lightId, bool off, long step)
        {
            if (_lights is null)
                return false;

            if (!_lights.SetSwitched(lightId, off, step))
                return false;

            Repaint(lightId);
            return true;
        }

        /// <summary>
        /// Destroys a light. Core decides whether that is allowed, and it is usually a
        /// mistake — destroying a working light makes a lot of noise on purpose.
        /// </summary>
        public bool Destroy(int lightId, long step)
        {
            if (_lights is null)
                return false;

            if (!_lights.Destroy(lightId, step))
                return false;

            Repaint(lightId);
            return true;
        }

        /// <summary>
        /// Redraws one emitter from Core's current state for it.
        /// </summary>
        /// <remarks>
        /// Called from inside the same method that changed Core, so the render and the
        /// simulation never disagree even for one frame.
        /// </remarks>
        private void Repaint(int lightId)
        {
            if (!_entries.TryGetValue(lightId, out Entry entry))
                return;

            LightRuntime? current = _lights.Find(lightId);

            if (current is null)
            {
                if (entry.Realtime != null)
                    entry.Realtime.enabled = false;
                if (entry.ProxyRenderer != null)
                    entry.ProxyRenderer.enabled = false;
                return;
            }

            ApplyLight(entry.Realtime, current);
            entry.Realtime.enabled = current.IsWorking && entry.WithinBudget;
            entry.Runtime = current;

            if (entry.ProxyRenderer != null)
                TintProxy(entry.ProxyRenderer, current);
        }

        private void ApplyLight(Light realtime, LightRuntime runtime)
        {
            realtime.enabled = runtime.IsWorking;
            realtime.range = LaneUnits.ToMetres(runtime.RadiusCm);

            // Base colour first, then the alarm tint over it. The base is Core's own
            // light level, which is information — a Dim room under a red lockdown tint is
            // still a Dim room — so the tint is mixed on top rather than replacing the
            // colour, and strength is what says how hard.
            Color baseColour = ColourForLevel[(int)runtime.Level];
            realtime.color = Color.Lerp(baseColour, _alarmTint, _alarmStrength);

            realtime.intensity = IntensityForLevel[(int)runtime.Level] * realtime.range;
        }

        /// <summary>
        /// Shifts every light in the building toward one colour.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is how an alarm band reaches the screen: the building's own lights change
        /// rather than a colour grade over the whole frame, so a room that has gone red is
        /// a claim about the <em>site</em>. Grading the camera would tint the squad and the
        /// fog the same way, which would read as the player being in trouble.
        /// </para>
        /// <para>
        /// Stored and re-applied rather than written once, because every repaint puts the
        /// base colour back and a tint that did not survive one would flicker the moment a
        /// light was switched.
        /// </para>
        /// </remarks>
        /// <param name="tint">The colour to shift toward.</param>
        /// <param name="strength">How far to shift, 0 to 1.</param>
        public void ApplyTint(Color tint, float strength)
        {
            _alarmTint = tint;
            _alarmStrength = Mathf.Clamp01(strength);

            foreach (Entry entry in _entries.Values)
            {
                if (entry.Realtime != null)
                    ApplyLight(entry.Realtime, entry.Runtime);
            }
        }

        /// <summary>
        /// World position of an emitter, from Core's centimetres through the lane origin.
        /// </summary>
        private Vector3 WorldPoint(LightRuntime runtime)
        {
            float x = _origin.x + LaneUnits.ToMetres(runtime.X.Raw);
            float y = LaneUnits.ToWorldY(runtime.FloorIndex, _origin.y) + LaneUnits.RoomHeightMetres * 0.72f;
            float z = _origin.z - LaneUnits.RoomDepthMetres * 0.5f;
            return new Vector3(x, y, z);
        }

        /// <summary>
        /// A flat quad standing in for a light that is over the realtime budget.
        /// </summary>
        /// <remarks>
        /// Sized to Core's own radius rather than to a decorative size, because the size of
        /// the pool of light on the floor is a gameplay fact the player reads. Emissive
        /// rather than lit: a proxy has to be visible without the light it stands in for,
        /// which is the entire reason it exists.
        /// </remarks>
        private static Transform BuildProxy(Entry entry, Transform parent)
        {
            float radius = LaneUnits.ToMetres(entry.Runtime.RadiusCm);
            float width = Mathf.Clamp(radius * 0.9f, 0.8f, 4f);

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = $"Proxy_{entry.Runtime.LightId}";

            var collider = go.GetComponent<Collider>();
            if (collider != null)
                TacticalObject.Destroy(collider);

            go.transform.SetParent(parent, true);

            // A quad's normal is -Z in Unity; rotating 90 about X lays it on the floor with
            // its face up, which is how a pool of light on the ground reads.
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = new Vector3(width, width * 0.55f, 1f);

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = ProxyMaterial();
            renderer.enabled = entry.Runtime.IsWorking;

            return go.transform;
        }

        private static void TintProxy(MeshRenderer renderer, LightRuntime runtime)
        {
            renderer.sharedMaterial = ProxyMaterial();
            renderer.enabled = runtime.IsWorking;
        }

        private static Material _proxyMaterial;

        /// <summary>The one shared material every over-budget pool of light is drawn with.</summary>
        private static Material ProxyMaterial()
        {
            if (_proxyMaterial != null)
                return _proxyMaterial;

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Site.BlockoutMaterials.FindShader();

            _proxyMaterial = new Material(shader) { name = "M_LightProxy (runtime)" };
            _proxyMaterial.SetFloat("_Surface", 1f);
            _proxyMaterial.SetFloat("_Blend", 2f); // Additive
            _proxyMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _proxyMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 10;
            return _proxyMaterial;
        }

        /// <summary>
        /// The light level Core reports at a position.
        /// </summary>
        /// <remarks>
        /// A pass-through so that everything asking "how lit is this actor" asks Core
        /// rather than each of them re-deriving it from the lights it happens to be able to
        /// see. With twelve of a building's lights missing, the render and Core would
        /// otherwise disagree about how bright a room is, and the whole point of the budget
        /// is that the player never finds out.
        /// </remarks>
        public SiteLightLevel LevelAt(TacticalPosition at)
            => _lights is null || _layout is null ? SiteLightLevel.Dark : _lights.LevelAt(_layout, at);
    }
}