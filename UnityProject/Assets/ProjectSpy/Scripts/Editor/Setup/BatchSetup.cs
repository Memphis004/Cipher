using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ProjectSpy.Unity.Editor.Setup
{
    /// <summary>
    /// Checks the project is in a state where the game can actually run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every check here corresponds to a failure that produces no error message at all.
    /// That is the criterion: a check is worth writing only if its absence is silent. A
    /// missing forward renderer draws an empty screen; a missing scene at build index 0
    /// launches into nothing; Core running on fallback tables produces a game that looks
    /// fine and ignores every balance number.
    /// </para>
    /// </remarks>
    public static class ProjectValidator
    {
        /// <summary>One problem found.</summary>
        public readonly struct Problem
        {
            public Problem(string severity, string message)
            {
                Severity = severity;
                Message = message;
            }

            /// <summary>"Error" or "Warning".</summary>
            public string Severity { get; }

            /// <summary>What is wrong and what to do about it.</summary>
            public string Message { get; }
        }

        /// <summary>Menu entry.</summary>
        [MenuItem("ProjectSpy/Validate Project", priority = 201)]
        public static void ValidateAndReport()
        {
            var problems = Validate();
            foreach (Problem problem in problems)
            {
                if (problem.Severity == "Error")
                    Debug.LogError("[ProjectSpy] " + problem.Message);
                else
                    Debug.LogWarning("[ProjectSpy] " + problem.Message);
            }

            int errors = 0;
            foreach (Problem problem in problems)
            {
                if (problem.Severity == "Error") errors++;
            }

            if (errors == 0)
                Debug.Log($"[ProjectSpy] Project validation passed ({problems.Count} warning(s)).");
            else
                Debug.LogError($"[ProjectSpy] Project validation found {errors} error(s).");
        }

        /// <summary>Runs every check.</summary>
        public static IReadOnlyList<Problem> Validate()
        {
            var problems = new List<Problem>();

            problems.AddRange(CheckRenderPipeline());
            problems.AddRange(CheckScenes());
            problems.AddRange(CheckCorePlugins());
            problems.AddRange(CheckBlockoutArt());

            return problems;
        }

        private static IEnumerable<Problem> CheckRenderPipeline()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null)
            {
                yield return new Problem("Error",
                    "No render pipeline asset is active. Assign the URP asset in Graphics Settings.");
                yield break;
            }

            var so = new SerializedObject(asset);
            var list = so.FindProperty("m_RendererDataList");
            if (list == null || list.arraySize == 0)
            {
                yield return new Problem("Error", "The URP asset has no renderers assigned.");
                yield break;
            }

            var renderer = list.GetArrayElementAtIndex(0).objectReferenceValue as ScriptableRendererData;
            if (renderer == null)
            {
                yield return new Problem("Error", "Renderer 0 on the URP asset is not set.");
                yield break;
            }

            // A 2D renderer draws sprites and nothing else, so a 3D blockout renders as an
            // empty background that looks exactly like an assembler bug.
            if (renderer.GetType().Name.Contains("2D"))
            {
                yield return new Problem("Error",
                    $"Renderer 0 is a '{renderer.GetType().Name}', which cannot draw 3D geometry. " +
                    "Run ProjectSpy/Setup/Run Full Setup to create a Forward+ renderer.");
            }

            var rendererSo = new SerializedObject(renderer);
            var mode = rendererSo.FindProperty("m_RenderingMode");
            if (mode != null && mode.intValue != 0)
            {
                yield return new Problem("Warning",
                    "The active renderer is not set to Forward+ (value " + mode.intValue + ").");
            }
        }

        private static IEnumerable<Problem> CheckScenes()
        {
            string sceneProblem = SceneGenerator.ValidateBuildSettings();
            if (sceneProblem != null)
                yield return new Problem("Error", sceneProblem);

            foreach (string name in new[] { Scenes.SceneNames.Boot, Scenes.SceneNames.Base, Scenes.SceneNames.Tactical })
            {
                if (!System.IO.File.Exists(SceneGenerator.PathFor(name)))
                {
                    yield return new Problem("Error",
                        $"Scene '{name}' does not exist at {SceneGenerator.PathFor(name)}.");
                }
            }
        }

        private static IEnumerable<Problem> CheckCorePlugins()
        {
            foreach (string dll in new[]
                     {
                         "ProjectSpy.Core", "ProjectSpy.Tables",
                     })
            {
                if (System.IO.File.Exists($"Assets/Plugins/ProjectSpy/{dll}.dll"))
                    continue;

                yield return new Problem("Error",
                    $"{dll}.dll is missing from Assets/Plugins/ProjectSpy. " +
                    "Run 'pwsh tools/sync-dlls.ps1'.");
            }

            // A duplicate assembly is the conflict that produces a TypeLoadException far from
            // its cause, and Unity resolves it by silently discarding one of the two — so the
            // Editor log line is an informational warning, not a failure the project reports.
            //
            // This is checked by name across the whole project rather than naming MessagePack
            // specifically, because the version of this check that did name it missed the two
            // duplicates that were actually present: NuGetForUnity resolves a package's whole
            // transitive closure, so MessagePack also brought Microsoft.Bcl.AsyncInterfaces and
            // System.Collections.Immutable into Assets/Plugins/NuGet, and sync-dlls.ps1 was
            // publishing Core's older builds of both over the top. Unity discarded ours every
            // time and reported nothing but a warning nobody was reading.
            const string pluginDir = "Assets/Plugins/ProjectSpy";
            if (!System.IO.Directory.Exists(pluginDir))
                yield break;

            foreach (string dll in System.IO.Directory.GetFiles(pluginDir, "*.dll"))
            {
                string name = System.IO.Path.GetFileName(dll);
                var others = AssetDatabase.FindAssets(name)
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p != $"{pluginDir}/{name}" &&
                                System.IO.Path.GetFileName(p) == name)
                    .Distinct()
                    .ToList();

                if (others.Count == 0)
                    continue;

                yield return new Problem("Error",
                    $"{name} exists both in {pluginDir} and at {string.Join(", ", others)}. " +
                    "Unity resolves duplicates by discarding one copy and logging only a " +
                    "warning. Delete the copy in Plugins; sync-dlls.ps1 should be skipping it " +
                    "as NuGet-supplied.");
            }
        }

        private static IEnumerable<Problem> CheckBlockoutArt()
        {
            foreach (Site.KitPiece piece in System.Enum.GetValues(typeof(Site.KitPiece)))
            {
                string path = Site.BlockoutKit.PathFor(piece);
                if (!System.IO.File.Exists(path))
                {
                    yield return new Problem("Warning",
                        $"Kit piece '{piece}' has no prefab at {path}. The assembler will fall " +
                        "back to runtime boxes, which is fine but slower.");
                }
            }

            if (!System.IO.File.Exists(BlockoutArtGenerator.AgentPath))
            {
                yield return new Problem("Warning",
                    "The agent blockout prefab is missing. Run the Blockout Art Generator.");
            }
        }
    }

    /// <summary>
    /// Generates every prefab the game needs.
    /// </summary>
    /// <remarks>
    /// A named entry point rather than folding everything into
    /// <see cref="BlockoutArtGenerator"/>, because the brief lists prefabs and blockout art
    /// as separate deliverables and a scene or a menu item that wants "the prefabs" should
    /// not have to know that blockout art is what produces them.
    /// </remarks>
    public static class PrefabGenerator
    {
        /// <summary>Generates every prefab: the kit, the markers and the agent.</summary>
        public static void GenerateAll() => BlockoutArtGenerator.GenerateAll();
    }

    /// <summary>
    /// Rebuilds the whole Unity side from a clean clone, in dependency order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered by what depends on what, because the steps are not independent: art must
    /// exist before scenes reference it, and the pipeline asset must exist before a scene
    /// can be rendered correctly in the first place. Running this twice is safe — each step
    /// is idempotent — which is what makes it safe to run after pulling a change to the
    /// Core sources.
    /// </para>
    /// </remarks>
    public static class BatchSetup
    {
        /// <summary>Menu entry.</summary>
        [MenuItem("ProjectSpy/Setup/Run Full Setup", priority = 100)]
        public static void RunFullSetup()
        {
            Debug.Log("[ProjectSpy] Setup 1/4: render pipeline");
            var pipeline = UrpSetup.EnsureForwardRenderer();
            UrpSetup.ActivatePipeline(pipeline);

            Debug.Log("[ProjectSpy] Setup 2/4: blockout art and prefabs");
            PrefabGenerator.GenerateAll();

            Debug.Log("[ProjectSpy] Setup 3/4: scenes");
            SceneGenerator.GenerateAll();

            Debug.Log("[ProjectSpy] Setup 4/4: validation");
            ProjectValidator.ValidateAndReport();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ProjectSpy] Full setup complete.");
        }
    }
}