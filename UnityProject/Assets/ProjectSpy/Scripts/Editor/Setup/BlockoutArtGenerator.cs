using System.Collections.Generic;
using System.IO;
using ProjectSpy.Unity.Site;
using UnityEditor;
using UnityEngine;

namespace ProjectSpy.Unity.Editor.Setup
{
    /// <summary>
    /// Builds every placeholder asset the site needs: the modular room kit, the coloured
    /// markers, and the agent capsule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No textures, no detail.</b> Everything is an untextured primitive. That is the
    /// whole method: at blockout the only two questions worth asking are "is this the right
    /// size" and "can I read the silhouette". A texture makes a six-metre room look
    /// identical to a twenty-metre one, which is exactly the mistake blockout exists to
    /// catch, and it would also make every generated room cost draw setup for nothing.
    /// </para>
    /// <para>
    /// <b>Scale is the payload.</b> Prefabs are authored at unit scale and stretched per
    /// instance by <see cref="SiteAssembler"/>, because a room's width varies per generated
    /// layout while the kit must stay one asset. The kit pieces themselves that must keep a
    /// fixed real-world size — a door opening, a ladder, an agent — are authored at final
    /// size and marked so.
    /// </para>
    /// <para>
    /// Writes into a <c>Resources</c> folder so the runtime assembler can load the kit
    /// without an addressable or direct-reference scheme. Fine at this stage; the path is
    /// centralised in <see cref="BlockoutKit"/> so a later switch is one edit.
    /// </para>
    /// </remarks>
    public static class BlockoutArtGenerator
    {
        /// <summary>Generates every blockout asset.</summary>
        public static void GenerateAll()
        {
            GenerateMaterials();
            GenerateKit();
            GenerateMarkers();
            GenerateAgent();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ProjectSpy] Blockout art generated.");
        }

        /// <summary>Creates the shared grey kit material.</summary>
        private static void GenerateMaterials()
        {
            EnsureFolder(PathForMaterials());
            var material = AssetDatabase.LoadAssetAtPath<Material>(BlockoutKit.KitMaterialPath);
            if (material == null)
            {
                material = new Material(BlockoutMaterials.FindShader())
                {
                    name = "M_BlockoutGrey",
                    color = new Color(0.55f, 0.55f, 0.58f),
                };
                material.SetFloat("_Metallic", 0f);
                material.SetFloat("_Smoothness", 0.05f);
                AssetDatabase.CreateAsset(material, BlockoutKit.KitMaterialPath);
            }
        }

        /// <summary>
        /// Creates one prefab per kit piece, each a single unit cube on the local origin.
        /// </summary>
        /// <remarks>
        /// Unit scale, because <see cref="SiteAssembler"/> sets the real dimensions per room.
        /// The only thing that differs per piece is the naming, which is deliberate: seeing
        /// <c>SM_DoorFrame</c> in the hierarchy is how a wall with a door in it is told apart
        /// from a wall with a hole in it.
        /// </remarks>
        private static void GenerateKit()
        {
            EnsureFolder(BlockoutKit.KitFolder);

            foreach (KitPiece piece in System.Enum.GetValues(typeof(KitPiece)))
            {
                string path = BlockoutKit.PathFor(piece);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                    continue;

                var root = new GameObject($"SM_{piece}");
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.name = "Body";
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = Vector3.zero;
                body.transform.localScale = Vector3.one;

                // Colliders are stripped: a generated building can hold hundreds of boxes,
                // and none of them need to be hit by physics at blockout. The agent capsule
                // is the only thing that will.
                var collider = body.GetComponent<Collider>();
                if (collider != null)
                    Object.DestroyImmediate(collider);

                var renderer = body.GetComponent<Renderer>();
                renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(BlockoutKit.KitMaterialPath);

                // A floor is oriented differently from a wall only in name; keeping the cube
                // axis-aligned means the assembler's per-instance scale is readable.
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Creates one coloured box prefab per marker kind.</summary>
        private static void GenerateMarkers()
        {
            EnsureFolder(BlockoutKit.MarkerFolder);

            foreach (MarkerKind kind in System.Enum.GetValues(typeof(MarkerKind)))
            {
                string path = PathForMarker(kind);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                    continue;

                float height = MarkerStyle.HeightFor(kind);
                float depth = MarkerStyle.DepthFor(kind);

                var root = new GameObject(MarkerStyle.NameFor(kind));
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.name = "Body";
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
                body.transform.localScale = new Vector3(0.5f, height, depth);

                var collider = body.GetComponent<Collider>();
                if (collider != null)
                    Object.DestroyImmediate(collider);

                var material = AssetDatabase.LoadAssetAtPath<Material>(PathForMarkerMaterial(kind));
                if (material == null)
                {
                    material = new Material(BlockoutMaterials.FindShader())
                    {
                        name = MarkerStyle.NameFor(kind) + "_Mat",
                        color = MarkerStyle.ColourFor(kind),
                    };
                    material.SetFloat("_Metallic", 0f);
                    material.SetFloat("_Smoothness", 0.1f);
                    AssetDatabase.CreateAsset(material, PathForMarkerMaterial(kind));
                }

                body.GetComponent<Renderer>().sharedMaterial = material;

                PrefabUtility.SaveAsPrefabAsset(root, path);
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Creates the agent: a capsule with a nose, so facing is readable from the
        /// cutaway camera.
        /// </summary>
        /// <remarks>
        /// The facing indicator is the one piece of "detail" that earns its keep. A capsule
        /// is rotationally symmetric, so without a directional marker an agent sliding along
        /// a lane gives the player no information at all about which way it is looking — and
        /// facing is one of the inputs to Core's perception, so the player has to be able to
        /// see it. A small box protruding from the front of the capsule is the cheapest thing
        /// that makes the rotation legible from any of the cutaway angles.
        /// </remarks>
        private static void GenerateAgent()
        {
            EnsureFolder(BlockoutKit.AgentFolder);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(AgentPath) != null)
                return;

            var root = new GameObject("Agent_Blockout");

            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Capsule";
            capsule.transform.SetParent(root.transform, false);
            capsule.transform.localPosition = new Vector3(0f, LaneUnits.AgentHeightMetres * 0.5f, 0f);
            capsule.transform.localScale = new Vector3(
                LaneUnits.AgentRadiusMetres * 2f,
                LaneUnits.AgentHeightMetres * 0.5f,
                LaneUnits.AgentRadiusMetres * 2f);

            var agentMaterial = AssetDatabase.LoadAssetAtPath<Material>(AgentMaterialPath);
            if (agentMaterial == null)
            {
                agentMaterial = new Material(BlockoutMaterials.FindShader())
                {
                    name = "M_Agent",
                    color = new Color(0.92f, 0.92f, 0.95f),
                };
                agentMaterial.SetFloat("_Metallic", 0f);
                agentMaterial.SetFloat("_Smoothness", 0.1f);
                AssetDatabase.CreateAsset(agentMaterial, AgentMaterialPath);
            }

            capsule.GetComponent<Renderer>().sharedMaterial = agentMaterial;

            // The nose: a small box on +Z, so rotation reads as direction at a glance.
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "FacingIndicator";
            nose.transform.SetParent(root.transform, false);
            nose.transform.localPosition = new Vector3(
                0f, LaneUnits.AgentHeightMetres * 0.5f,
                LaneUnits.AgentRadiusMetres + 0.12f);
            nose.transform.localScale = new Vector3(0.12f, 0.12f, 0.3f);

            var noseMaterial = new Material(BlockoutMaterials.FindShader())
            {
                name = "M_AgentFacing",
                color = new Color(0.15f, 0.35f, 0.75f),
            };
            noseMaterial.SetFloat("_Metallic", 0f);
            AssetDatabase.CreateAsset(noseMaterial, AgentFacingMaterialPath);
            nose.GetComponent<Renderer>().sharedMaterial = noseMaterial;

            // Only the capsule keeps a collider: it is the one thing that will be
            // raycast or clicked against.
            var noseCollider = nose.GetComponent<Collider>();
            if (noseCollider != null)
                Object.DestroyImmediate(noseCollider);

            // A marker component so a renderer can find "the thing to move".
            root.AddComponent<AgentMarker>();

            PrefabUtility.SaveAsPrefabAsset(root, AgentPath);
            Object.DestroyImmediate(root);
        }

        /// <summary>Asset path of the agent prefab.</summary>
        public const string AgentPath = BlockoutKit.AgentFolder + "/Agent_Blockout.prefab";

        /// <summary>Asset path of the agent material.</summary>
        public const string AgentMaterialPath = BlockoutKit.AgentFolder + "/M_Agent.mat";

        /// <summary>Asset path of the facing-indicator material.</summary>
        public const string AgentFacingMaterialPath = BlockoutKit.AgentFolder + "/M_AgentFacing.mat";

        private static string PathForMaterials() => "Assets/ProjectSpy/Art/Blockout/Materials";

        private static string PathForMarker(MarkerKind kind)
            => $"{BlockoutKit.MarkerFolder}/{MarkerStyle.NameFor(kind)}.prefab";

        private static string PathForMarkerMaterial(MarkerKind kind)
            => $"{BlockoutKit.MarkerFolder}/{MarkerStyle.NameFor(kind)}_Mat.mat";

        /// <summary>Creates every missing folder in an <c>Assets/</c> path.</summary>
        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
                return;

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}

namespace ProjectSpy.Unity.Site
{
    /// <summary>
    /// Marks an object as the transform a renderer should drive for one agent.
    /// </summary>
    /// <remarks>
    /// A component rather than a tag or a name lookup, so that attaching a second agent
    /// model later does not require renaming anything, and so a typo in a name lookup
    /// fails loudly at the point of use instead of silently not finding anyone.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class AgentMarker : MonoBehaviour
    {
        /// <summary>Which squad member this is, when the squad bar knows. Zero when unassigned.</summary>
        public int AgentIndex;
    }
}