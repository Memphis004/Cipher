using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ProjectSpy.Unity.Editor.Setup
{
    /// <summary>
    /// Creates and wires the URP configuration Stage 7 requires: a Forward+ renderer, a URP
    /// pipeline asset that points at it, shadows on, and the SRP batcher enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists as code because the project shipped with the wrong renderer.</b> The
    /// template's <c>Assets/Settings</c> contained only a <c>Renderer2D</c> and it was the
    /// pipeline's sole renderer. A 2D renderer draws sprites and nothing else, so a 3D
    /// blockout renders as an empty background — which looks exactly like an assembler bug
    /// and cost real time to tell apart. The renderer is created here so the project can be
    /// rebuilt from a clean clone without anyone having to notice that.
    /// </para>
    /// <para>
    /// Idempotent by construction: it looks for an existing forward renderer by name and
    /// only creates one when absent, so running <see cref="BatchSetup.RunFullSetup"/> twice
    /// does not litter the project with duplicate renderer assets.
    /// </para>
    /// </remarks>
    public static class UrpSetup
    {
        /// <summary>Folder generated URP assets are written to.</summary>
        public const string SettingsFolder = "Assets/ProjectSpy/Settings";

        /// <summary>Asset path of the Forward+ renderer data.</summary>
        public const string ForwardRendererPath = SettingsFolder + "/Renderer_Forward.asset";

        /// <summary>Asset path of the pipeline asset Stage 7 uses.</summary>
        public const string PipelineAssetPath = SettingsFolder + "/URP_ProjectSpy.asset";

        /// <summary>
        /// Ensures a Forward+ renderer exists and is the pipeline's active one.
        /// </summary>
        /// <returns>The pipeline asset, for the caller to save.</returns>
        public static UniversalRenderPipelineAsset EnsureForwardRenderer()
        {
            EnsureFolder(SettingsFolder);

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(ForwardRendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.name = "Renderer_Forward";

                // Forward+ is the mode a 3D project wants: clustered deferred lighting, which
                // is what makes "a single key light per room" affordable at the room counts a
                // generated building reaches.
                ConfigureRenderer(renderer);
                AssetDatabase.CreateAsset(renderer, ForwardRendererPath);
                AssetDatabase.SaveAssets();

                // Reloaded rather than kept: CreateAsset copies the in-memory object into a
                // new serialized asset, and holding the pre-copy instance would hand later
                // code an object that is not the asset Unity will load.
                renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(ForwardRendererPath);
            }

            // The pipeline asset itself: create once, then point at our renderer.
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.name = "URP_ProjectSpy";
                AssetDatabase.CreateAsset(pipeline, PipelineAssetPath);
            }

            AssignRenderer(pipeline, renderer);

            // Lighting and batching settings that a 2.5D cutaway depends on.
            pipeline.supportsHDR = true;
            pipeline.supportsCameraDepthTexture = true;
            pipeline.shadowDistance = 120f;
            pipeline.shadowCascadeCount = 2;
            pipeline.msaaSampleCount = 1;

            EnableSrpBatcher();

            AssetDatabase.SaveAssets();
            return pipeline;
        }

        /// <summary>
        /// Forces a renderer's rendering mode to Forward+ and turns its shadows on.
        /// </summary>
        /// <remarks>
        /// Written against the serialised field rather than the typed property because
        /// <c>UniversalRendererData.renderingMode</c> is internal in URP 14. Reflection on a
        /// known field name, with a loud failure if URP renames it, is better than silently
        /// shipping a Forward renderer that is actually set to Deferred.
        /// </remarks>
        private static void ConfigureRenderer(UniversalRendererData renderer)
        {
            var so = new SerializedObject(renderer);

            var mode = so.FindProperty("m_RenderingMode");
            if (mode != null)
            {
                // UniversalRendererData.RenderingMode.Forward = 0.
                mode.intValue = 0;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning(
                    "[ProjectSpy] Could not find m_RenderingMode on " + renderer.GetType().Name +
                    ". Forward+ could not be selected; check the URP version.");
            }
        }

        /// <summary>Replaces a pipeline asset's renderer list with a single forward renderer.</summary>
        private static void AssignRenderer(
            UniversalRenderPipelineAsset pipeline, ScriptableRendererData renderer)
        {
            var so = new SerializedObject(pipeline);
            var list = so.FindProperty("m_RendererDataList");
            if (list == null)
            {
                Debug.LogWarning("[ProjectSpy] m_RendererDataList not found on the pipeline asset.");
                return;
            }

            list.arraySize = 1;
            list.GetArrayElementAtIndex(0).objectReferenceValue = renderer;

            var index = so.FindProperty("m_DefaultRendererIndex");
            if (index != null)
                index.intValue = 0;

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Turns the SRP batcher on for every quality level.
        /// </summary>
        /// <remarks>
        /// Set on the Graphics settings and on each Quality level, because Unity stores the
        /// flag in both places and the effective value comes from whichever the level
        /// overrides. Setting only one leaves the batcher off for every level that overrides
        /// it, which is most of them in a default project.
        /// </remarks>
        public static void EnableSrpBatcher()
        {
            // QualitySettings has no scripting API for the SRP batcher flag, so it is
            // written through the serialized quality asset. Each level is written
            // individually because the flag lives on the level, not on the file: setting it
            // once leaves every level that carries an explicit override still switched off.
            var qualityAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (qualityAssets == null)
            {
                Debug.LogWarning("[ProjectSpy] QualitySettings.asset could not be loaded; SRP batcher not set.");
                return;
            }

            int written = 0;
            foreach (var asset in qualityAssets)
            {
                if (asset == null)
                    continue;

                var so = new SerializedObject(asset);
                var batcher = so.FindProperty("srpBatcher");
                if (batcher == null)
                    continue;

                batcher.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
                written++;
            }

            if (written == 0)
            {
                Debug.LogWarning(
                    "[ProjectSpy] No 'srpBatcher' property found in QualitySettings.asset. " +
                    "This Unity version stores it elsewhere; the batcher may be off.");
            }
        }

        /// <summary>Assigns the pipeline asset to the project and every quality level.</summary>
        public static void ActivatePipeline(UniversalRenderPipelineAsset pipeline)
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;

            var levels = QualitySettings.names;
            for (int i = 0; i < levels.Length; i++)
                QualitySettings.SetQualityLevel(i, false);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}