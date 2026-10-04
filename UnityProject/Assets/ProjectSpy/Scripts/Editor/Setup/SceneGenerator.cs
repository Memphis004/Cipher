using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectSpy.Unity.Editor.Setup
{
    /// <summary>
    /// Creates the three scenes the game runs on and puts them in the build settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Boot is build index 0 and the only Single-mode scene.</b> Everything else loads
    /// additively on top of it. The order is set in code rather than left to a hand-sorted
    /// build settings window, because a reorder there is invisible and would make a fresh
    /// launch start inside a mission with no session behind it.
    /// </para>
    /// <para>
    /// Base and Tactical both load additively so that returning from a mission does not
    /// destroy the base view, and entering one does not destroy the services the
    /// <c>RootLifetimeScope</c> in Boot holds. See <c>SceneRouter</c>.
    /// </para>
    /// </remarks>
    public static class SceneGenerator
    {
        /// <summary>Folder generated scenes are written to.</summary>
        public const string SceneFolder = "Assets/ProjectSpy/Scenes";

        /// <summary>
        /// Creates all three scenes and writes the build settings.
        /// </summary>
        /// <returns>Paths written, in build order.</returns>
        public static IReadOnlyList<string> GenerateAll()
        {
            var written = new List<string>
            {
                CreateBootScene(),
                CreateEmptyScene("Base"),
                CreateEmptyScene("Tactical"),
            };

            WriteBuildSettings(written);
            return written;
        }

        /// <summary>
        /// Creates Boot: a camera, a light, the root scope and the simulation runner.
        /// </summary>
        /// <remarks>
        /// The only scene that carries objects. Everything else is a content scene layered
        /// over it, which is what makes "unload the mission, keep the campaign" a single
        /// call.
        /// </remarks>
        private static string CreateBootScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("PersistentRoot");

            // The lifetime scope builds every service. It must exist before anything
            // resolves them, so it is added first.
            root.AddComponent<Boot.RootLifetimeScope>();

            var simulation = root.AddComponent<Simulation.SimulationRunner>();

            var cameraGo = new GameObject("MainCamera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 30f;
            camera.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
            camera.transform.position = new Vector3(0f, 8f, 40f);
            cameraGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();

            var lightGo = new GameObject("KeyLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

            string path = PathFor(Scenes.SceneNames.Boot);
            BlockoutArtGenerator.EnsureFolder(SceneFolder);
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        /// <summary>
        /// Creates a scene holding only a root transform.
        /// </summary>
        /// <remarks>
        /// Empty apart from a named root, because the brief requires no textures and no
        /// detail: the geometry comes from <c>SiteAssembler</c> at runtime, and the camera
        /// comes from Cinemachine in stage 9a.
        /// </remarks>
        private static string CreateEmptyScene(string name)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject($"{name}Root");

            BlockoutArtGenerator.EnsureFolder(SceneFolder);
            string path = PathFor(name);
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        /// <summary>
        /// Writes the build settings with Boot at index 0.
        /// </summary>
        /// <remarks>
        /// Replaces the whole list rather than appending, because the template project ships
        /// a SampleScene that would otherwise sit at index 0 and become the launch scene.
        /// </remarks>
        private static void WriteBuildSettings(IReadOnlyList<string> scenePaths)
        {
            var scenes = new List<EditorBuildSettingsScene>(scenePaths.Count);
            foreach (string path in scenePaths)
            {
                scenes.Add(new EditorBuildSettingsScene(path, true));
            }

            EditorBuildSettings.scenes = scenes.ToArray();
        }

        /// <summary>Asset path for a scene name.</summary>
        public static string PathFor(string sceneName) => $"{SceneFolder}/{sceneName}.unity";

        /// <summary>
        /// Verifies the build settings are in the required order.
        /// </summary>
        /// <returns>Null when correct, otherwise a description of what is wrong.</returns>
        public static string ValidateBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes;
            if (scenes.Length == 0)
                return "No scenes in build settings.";

            if (!scenes[0].enabled)
                return "Build index 0 is disabled; the game would launch with no scene.";

            if (Path.GetFileNameWithoutExtension(scenes[0].path) != Scenes.SceneNames.Boot)
            {
                return $"Build index 0 is '{scenes[0].path}', expected " +
                       $"'{Scenes.SceneNames.Boot}' at index 0.";
            }

            for (int i = 1; i < scenes.Length; i++)
            {
                if (!scenes[i].enabled)
                    return $"Build index {i} ('{scenes[i].path}') is disabled.";
            }

            return null;
        }
    }
}