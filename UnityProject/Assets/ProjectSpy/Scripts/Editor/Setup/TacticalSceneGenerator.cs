using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectSpy.Unity.Editor.Setup
{
    /// <summary>
    /// Builds the Tactical scene: a root carrying the simulation runner and the tactical
    /// scene director, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The scene is empty on purpose.</b> The building, the lights, the fog and the HUD
    /// are all created at runtime by <c>TacticalSceneDirector</c> from a site the generator
    /// makes from a seed, because a scene containing a baked building would be a scene
    /// that can only ever show that building. The scene's job is to say <em>run a
    /// mission</em>; everything visible is a consequence of the seed it is given.
    /// </para>
    /// <para>
    /// It carries its own <c>SimulationRunner</c> rather than borrowing Boot's, so that
    /// opening the scene on its own — which is how it is inspected and tested — works. In
    /// play the two are never both live: Boot is the launch scene and Tactical loads
    /// additively over it, and the director prefers a runner on its own root.
    /// </para>
    /// </remarks>
    public static class TacticalSceneGenerator
    {
        /// <summary>Name of the object the tactical view hangs off.</summary>
        public const string RootName = "TacticalView";

        /// <summary>
        /// Rebuilds the Tactical scene from scratch.
        /// </summary>
        /// <returns>The asset path written.</returns>
        [MenuItem("ProjectSpy/Setup/Rebuild Tactical Scene", priority = 120)]
        public static string Generate()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject(RootName);
            root.AddComponent<Simulation.SimulationRunner>();
            root.AddComponent<Tactical.TacticalSceneDirector>();

            // A single key light. Without one, URP's "Main Light" slot is empty and every
            // material renders with no directional term at all, which at blockout makes the
            // whole building read as flat and unlit regardless of the emissive and realtime
            // lights the tactical view is in the middle of setting up. Kept dim so that the
            // per-room lights are still what the eye reads as lighting the building.
            var lightGo = new GameObject("AmbientKey");
            var key = lightGo.AddComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 0.28f;
            key.color = new Color(0.72f, 0.78f, 0.92f);
            key.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.Euler(52f, 28f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.13f, 0.15f, 0.20f);
            RenderSettings.ambientEquatorColor = new Color(0.07f, 0.08f, 0.11f);
            RenderSettings.ambientGroundColor = new Color(0.03f, 0.035f, 0.045f);

            BlockoutArtGenerator.EnsureFolder(SceneGenerator.SceneFolder);
            string path = SceneGenerator.PathFor(Scenes.SceneNames.Tactical);

            EditorSceneManager.SaveScene(scene, path);
            AssetDatabase.Refresh();

            Debug.Log($"[ProjectSpy] Wrote tactical scene to {path}");
            return path;
        }
    }
}