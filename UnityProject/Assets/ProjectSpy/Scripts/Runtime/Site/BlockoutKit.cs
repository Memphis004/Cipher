using UnityEngine;

namespace ProjectSpy.Unity.Site
{
    /// <summary>
    /// The modular pieces a building is assembled from, and where they live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A kit rather than a pile of bespoke meshes, for the reason the brief gives: the
    /// building is generated, so it cannot have bespoke geometry. Every room is the same
    /// floor, the same walls and the same openings, recombined — which is what makes it
    /// possible to render fifty generated buildings without a single manual fix.
    /// </para>
    /// <para>
    /// Pure grey, untextured boxes. That is not laziness, it is the point: at blockout the
    /// only information that matters is scale and silhouette. A texture would make two
    /// rooms look identical when they are different sizes, which is exactly the mistake
    /// blockout is meant to catch.
    /// </para>
    /// </remarks>
    public enum KitPiece
    {
        /// <summary>The walkable slab a room stands on.</summary>
        Floor,

        /// <summary>The wall behind the cutaway camera, the one the player looks at.</summary>
        BackWall,

        /// <summary>A side wall, oriented for left or right.</summary>
        SideWall,

        /// <summary>A ceiling slab. Opaque by default so floors occlude one another correctly.</summary>
        Ceiling,

        /// <summary>A doorway opening: two jambs and a lintel, so the hole is real geometry.</summary>
        DoorFrame,

        /// <summary>A stair run between storeys.</summary>
        Stair,

        /// <summary>A vertical ladder.</summary>
        Ladder,

        /// <summary>A wall vent.</summary>
        Vent,
    }

    /// <summary>
    /// Where the generated kit assets live, and how to build each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The asset paths are a single source of truth shared by the editor generator that
    /// writes the prefabs and the runtime assembler that loads them. When they were two
    /// independent string literals per piece, a rename in one place produced an assembler
    /// that silently instantiated nothing — and a building with no walls, which reads as
    /// "the generator is broken" rather than "a path is stale".
    /// </para>
    /// <para>
    /// Every accessor falls back to a procedurally built box when the prefab is missing, so
    /// a fresh clone without generated art still renders a building instead of an empty
    /// scene. The fallback is not a toy: it is what makes the assembler's geometry
    /// assertions meaningful before any art exists.
    /// </para>
    /// </remarks>
    public static class BlockoutKit
    {
        /// <summary>Folder the generated prefabs and materials live in.</summary>
        public const string KitFolder = "Assets/ProjectSpy/Art/Blockout/Kit";

        /// <summary>Folder the generated marker prefabs live in.</summary>
        public const string MarkerFolder = "Assets/ProjectSpy/Art/Blockout/Markers";

        /// <summary>Folder the generated agent prefab lives in.</summary>
        public const string AgentFolder = "Assets/ProjectSpy/Art/Blockout/Agents";

        /// <summary>Shared untextured material for all kit geometry.</summary>
        public const string KitMaterialPath = "Assets/ProjectSpy/Art/Blockout/Materials/M_BlockoutGrey.mat";

        /// <summary>Asset path of one kit prefab.</summary>
        public static string PathFor(KitPiece piece) => $"{KitFolder}/SM_{piece}.prefab";

        /// <summary>
        /// Loads a kit prefab, or builds an equivalent box if it is not there yet.
        /// </summary>
        /// <remarks>
        /// <paramref name="sizeMetres"/> is ignored when the prefab exists, because the
        /// prefab is authored at real-world scale and stretching it per room would make
        /// every wall a different thickness.
        /// </remarks>
        public static GameObject Create(KitPiece piece, Transform parent, Vector3 sizeMetres)
        {
            var prefab = Resources.Load<GameObject>(ResourcePathFor(piece));
            if (prefab is null)
                return BuildFallback(piece, parent, sizeMetres);

            var instance = Object.Instantiate(prefab, parent);
            instance.name = $"SM_{piece}_{instance.GetInstanceID()}";
            return instance;
        }

        /// <summary>
        /// Resources-relative path for a kit piece.
        /// </summary>
        /// <remarks>
        /// The prefabs live under <c>Assets/ProjectSpy/Art/Blockout</c>, which Unity
        /// requires a <c>Resources</c> folder to address at runtime. Rather than move the
        /// art under an existing Resources folder — which would tie generated content to the
        /// asset pipeline's layout — the kit is generated into a Resources folder of its
        /// own. Stage 7 keeps this in one method so a later switch to Addressables or
        /// direct-asset references is a single edit.
        /// </remarks>
        public static string ResourcePathFor(KitPiece piece) => $"Blockout/Kit/SM_{piece}";

        /// <summary>Resources-relative path for a marker.</summary>
        public static string ResourcePathForMarker(MarkerKind marker) => $"Blockout/Markers/M_{marker}";

        /// <summary>
        /// Builds a piece directly out of primitives, for when no prefab has been generated.
        /// </summary>
        /// <remarks>
        /// Grey and untextured for the same reason the prefabs are: nothing in blockout is
        /// allowed to imply a material difference that a real building would not have.
        /// </remarks>
        public static GameObject BuildFallback(KitPiece piece, Transform parent, Vector3 sizeMetres)
        {
            var go = new GameObject($"SM_{piece}");
            go.transform.SetParent(parent, false);

            // A primitive cube is authored 1x1x1 centred on its pivot, so the requested
            // size is applied as scale. Depth is meaningless on the X axis pieces, so a
            // zero or negative requested depth collapses to a sliver rather than an
            // inverted box.
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Body";
            cube.transform.SetParent(go.transform, false);
            cube.transform.localScale = new Vector3(
                Mathf.Max(sizeMetres.x, 0.001f),
                Mathf.Max(sizeMetres.y, 0.001f),
                Mathf.Max(sizeMetres.z, 0.001f));
            cube.transform.localPosition = Vector3.zero;

            var collider = cube.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);

            ApplyGrey(cube);
            return go;
        }

        /// <summary>Puts a shared untextured grey material on a renderer.</summary>
        public static void ApplyGrey(GameObject target)
        {
            var renderer = target.GetComponent<Renderer>();
            if (renderer == null)
                renderer = target.GetComponentInChildren<Renderer>();
            if (renderer == null)
                return;

            var material = BlockoutMaterials.Grey;
            if (material != null)
                renderer.sharedMaterial = material;
        }
    }

    /// <summary>
    /// The coloured markers that stand in for mission contents.
    /// </summary>
    /// <remarks>
    /// Colour is the whole payload. A guard, a civilian and a loot container are three
    /// boxes of identical size that differ only in hue, because at blockout the question
    /// is "can I see the patrol routes from this camera" and nothing finer.
    /// </remarks>
    public enum MarkerKind
    {
        /// <summary>A site guard. Red, so a patrol is the first thing the eye finds.</summary>
        Guard,

        /// <summary>A non-combatant. Green.</summary>
        Civilian,

        /// <summary>A loot container. Amber.</summary>
        Loot,

        /// <summary>A hackable terminal. Cyan.</summary>
        Terminal,

        /// <summary>A light emitter. Yellow.</summary>
        Light,
    }

    /// <summary>Colour and size for each marker kind.</summary>
    public static class MarkerStyle
    {
        /// <summary>Linear-space colour for a marker kind.</summary>
        public static Color ColourFor(MarkerKind kind) => kind switch
        {
            MarkerKind.Guard => new Color(0.85f, 0.15f, 0.12f),
            MarkerKind.Civilian => new Color(0.15f, 0.75f, 0.25f),
            MarkerKind.Loot => new Color(0.90f, 0.65f, 0.10f),
            MarkerKind.Terminal => new Color(0.10f, 0.75f, 0.85f),
            MarkerKind.Light => new Color(1.00f, 0.95f, 0.35f),
            _ => Color.magenta,
        };

        /// <summary>Height in metres, chosen so a marker is visible but never reads as a wall.</summary>
        public static float HeightFor(MarkerKind kind) => kind switch
        {
            MarkerKind.Guard => 1.8f,
            MarkerKind.Civilian => 1.8f,
            MarkerKind.Loot => 0.6f,
            MarkerKind.Terminal => 1.2f,
            MarkerKind.Light => 0.3f,
            _ => 1f,
        };

        /// <summary>Depth in metres. Flat against the back wall for everything but actors.</summary>
        public static float DepthFor(MarkerKind kind) => kind switch
        {
            MarkerKind.Guard => 0.6f,
            MarkerKind.Civilian => 0.6f,
            _ => 0.3f,
        };

        /// <summary>Asset name for a marker prefab.</summary>
        public static string NameFor(MarkerKind kind) => $"M_{kind}";
    }

    /// <summary>
    /// Shared materials, created on demand so blockout never renders magenta.
    /// </summary>
    /// <remarks>
    /// Unity's "default material" is the magenta error material, which is precisely the
    /// thing that makes a broken blockout scene hard to read: everything is wrong and
    /// nothing stands out. These are created in memory and, when running in the Editor,
    /// written into the project by the blockout generator so they are real assets.
    /// </remarks>
    public static class BlockoutMaterials
    {
        private static Material _grey;
        private static readonly System.Collections.Generic.Dictionary<MarkerKind, Material> MarkerCache = new();

        /// <summary>The shared untextured grey used by all kit geometry.</summary>
        public static Material Grey
        {
            get
            {
                if (_grey == null)
                {
                    _grey = new Material(FindShader())
                    {
                        name = "M_BlockoutGrey (runtime)",
                        color = new Color(0.55f, 0.55f, 0.58f),
                    };
                    _grey.SetFloat("_Smoothness", 0.05f);
                    _grey.SetFloat("_Metallic", 0f);
                }

                return _grey;
            }
        }

        /// <summary>The material for a marker kind.</summary>
        public static Material ForMarker(MarkerKind kind)
        {
            if (MarkerCache.TryGetValue(kind, out Material cached) && cached != null)
                return cached;

            var material = new Material(FindShader())
            {
                name = $"M_{kind} (runtime)",
                color = MarkerStyle.ColourFor(kind),
            };
            material.SetFloat("_Smoothness", 0.1f);
            material.SetFloat("_Metallic", 0f);
            MarkerCache[kind] = material;
            return material;
        }

        /// <summary>
        /// URP's Lit shader, resolved by name, falling back to the built-in Standard shader.
        /// </summary>
        /// <remarks>
        /// Needed because the runtime fallback cannot assume the pipeline asset is a URP
        /// one: a material compiled for the wrong pipeline renders as magenta, which again
        /// destroys the legibility of a blockout scene.
        /// </remarks>
        public static Shader FindShader()
        {
            var urp = Shader.Find("Universal Render Pipeline/Lit");
            if (urp != null)
                return urp;

            var standard = Shader.Find("Standard");
            return standard != null ? standard : Shader.Find("Unlit/Color");
        }
    }
}