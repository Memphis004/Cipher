using System.Collections.Generic;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Unity.Site;
using UnityEditor;
using UnityEngine;

namespace ProjectSpy.Unity.Editor.Setup
{
    /// <summary>
    /// Lays out many generated buildings at once so a whole catalogue can be eyeballed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why fifty.</b> A single generated site almost always looks fine. The defect this
    /// catches is the one that only shows up across a spread: a template whose rooms are all
    /// the same width, a tier whose buildings are always one floor, a room template whose
    /// minimum width produces a corridor too narrow to read. One building hides all of
    /// those; fifty shows them immediately.
    /// </para>
    /// <para>
    /// The buildings are generated with different seeds from the same tables the game uses,
    /// so this is a genuine sample of what missions will look like rather than a curated set.
    /// </para>
    /// </remarks>
    public sealed class SitePreviewWindow : EditorWindow
    {
        private const string RootName = "SitePreview";

        private int _count = 50;
        private ulong _seed = 20251004UL;
        private int _spacingMetres = 14;
        private Vector3 _scroll;
        private string _lastSummary = string.Empty;
        private readonly List<GameObject> _built = new();

        /// <summary>Opens the window.</summary>
        [MenuItem("ProjectSpy/Setup/Site Preview (50 buildings)", priority = 120)]
        public static void Open()
        {
            var window = GetWindow<SitePreviewWindow>("Site Preview");
            window.minSize = new Vector2(460f, 320f);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Generated site preview", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Assembles many independently generated buildings side by side. Look for " +
                "templates that always produce the same shape, rooms too narrow to read, and " +
                "floors that fail to line up.",
                MessageType.Info);

            _count = EditorGUILayout.IntSlider("Buildings", _count, 1, 200);
            _seed = (ulong)EditorGUILayout.LongField("World seed", (long)_seed);
            _spacingMetres = EditorGUILayout.IntSlider("Spacing (m)", _spacingMetres, 6, 60);

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Generate", GUILayout.Height(28f)))
                    Generate();

                if (GUILayout.Button("Clear", GUILayout.Height(28f)))
                    Clear();
            }

            if (!string.IsNullOrEmpty(_lastSummary))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_lastSummary, MessageType.None);
            }
        }

        /// <summary>
        /// Builds the preview, replacing anything already there.
        /// </summary>
        /// <remarks>
        /// Each building is generated with its own mission id and its own map seed derived
        /// from the world seed, so the spread is reproducible: the same seed and count
        /// produce the same fifty buildings, which is what makes a visual regression
        /// report from a playtester actionable.
        /// </remarks>
        public void Generate()
        {
            Clear();

            if (!SimulationRules.AreTablesLoaded)
            {
                _lastSummary =
                    "Core has no tables loaded, so no site can be generated. Run " +
                    "'pwsh tools/gen.ps1' if the binaries are missing, then reopen the window.";
                Debug.LogError("[ProjectSpy] " + _lastSummary);
                return;
            }

            var root = new GameObject(RootName);
            _built.Add(root);

            int cursor = 0;
            int failures = 0;
            var problems = new List<string>();

            for (int i = 0; i < _count; i++)
            {
                int templateId = PickTemplate(i);
                int tier = PickTier(i);

                SiteLayout layout;
                try
                {
                    ulong mapSeed = SiteGenerator.DeriveMapSeed(_seed, i);
                    layout = SiteGenerator.Generate(templateId, tier, i + 1, _seed, mapSeed);
                }
                catch (System.Exception ex)
                {
                    failures++;
                    if (problems.Count < 5)
                        problems.Add($"#{i} generate failed: {ex.Message}");
                    continue;
                }

                var result = SiteAssembler.Assemble(
                    layout, new Vector3(cursor, 0f, 0f), root.transform);

                cursor += Mathf.CeilToInt(result.WidthMetres) + _spacingMetres;

                foreach (string warning in result.Warnings)
                {
                    if (problems.Count < 5)
                        problems.Add($"#{i} {warning}");
                }
            }

            Selection.activeGameObject = root;
            _lastSummary =
                $"Built {_count} building(s) across {cursor}m." +
                (failures > 0 ? $"\n{failures} failed to generate." : string.Empty) +
                (problems.Count > 0 ? "\n" + string.Join("\n", problems) : string.Empty);

            Debug.Log($"[ProjectSpy] Site preview: {_lastSummary}");
        }

        /// <summary>Removes everything the window built.</summary>
        public void Clear()
        {
            foreach (GameObject go in _built)
            {
                if (go != null)
                    DestroyImmediate(go);
            }

            _built.Clear();
            _lastSummary = string.Empty;
        }

        /// <summary>
        /// Cycles through the available site templates so the spread covers the catalogue
        /// rather than hammering whichever one happens to be first.
        /// </summary>
        private int PickTemplate(int index)
        {
            var ids = new List<int>();
            foreach (var row in ProjectSpy.Tables.TableService.Load().TbSiteTemplate.DataList)
                ids.Add(row.Id);

            return ids.Count == 0 ? 11001 : ids[index % ids.Count];
        }

        /// <summary>Cycles tiers so both small and large buildings appear.</summary>
        private static int PickTier(int index) => 1 + (index % 4);

        private void OnDisable() => Clear();
    }
}