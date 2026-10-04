using System;
using System.Collections.Generic;
using System.IO;
using ProjectSpy.Core;
using ProjectSpy.Tables;
using UnityEngine;
using GameTables = ProjectSpy.Tables.Tables;

namespace ProjectSpy.Unity.Persistence
{
    /// <summary>
    /// Loads and holds the compiled game tables for the Unity build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thin wrapper over <see cref="ProjectSpy.Tables.TableService"/>, which searches
    /// upwards from a directory for <c>assets/data/tables</c>. That search cannot work in a
    /// shipped player — the repository is not there — so this resolves the binaries from
    /// <c>StreamingAssets</c> instead and feeds the same loader delegate. The bytes are
    /// identical either way, which is the point: the editor, the tests and the headless
    /// simulator all read the same compiled tables rather than three copies that can drift.
    /// </para>
    /// <para>
    /// Tables are loaded once and never reloaded. They are immutable content, and Core
    /// holds direct references into them for the whole session.
    /// </para>
    /// </remarks>
    public sealed class TableService : Services.IProjectSpyService
    {
        /// <summary>StreamingAssets subfolder the table binaries are copied to.</summary>
        public const string TableFolder = "ProjectSpyTables";

        private GameTables _tables;

        /// <summary>The loaded tables. Throws if used before <see cref="Load"/>.</summary>
        public GameTables Tables
        {
            get
            {
                if (_tables is null)
                    throw new InvalidOperationException(
                        "Tables have not been loaded. Call TableService.Load during boot.");
                return _tables;
            }
        }

        /// <summary>True once <see cref="Load"/> has succeeded.</summary>
        public bool IsLoaded => _tables != null;

        /// <summary>
        /// Loads the tables from StreamingAssets.
        /// </summary>
        /// <param name="startDirectory">
        /// Directory to search upward from. Defaults to StreamingAssets.
        /// </param>
        /// <exception cref="DirectoryNotFoundException">
        /// The binaries are missing. Almost always means <c>tools/sync-dlls.ps1</c> or the
        /// table copy step has not been run.
        /// </exception>
        public void Load(string startDirectory = null)
        {
            if (_tables != null)
                return;

            string directory = startDirectory ?? Application.streamingAssetsPath;
            var found = FindTableDirectory(directory);

            if (found == null)
            {
                throw new DirectoryNotFoundException(
                    $"Could not find table binaries under '{directory}'. " +
                    "Run 'pwsh tools/sync-dlls.ps1', which copies them into StreamingAssets.");
            }

            _tables = new GameTables(name =>
            {
                string file = System.IO.Path.Combine(found, name + ".bytes");
                if (!System.IO.File.Exists(file))
                {
                    throw new System.IO.FileNotFoundException(
                        $"Table binary '{name}.bytes' is missing from '{found}'.", file);
                }

                return new Luban.ByteBuf(System.IO.File.ReadAllBytes(file));
            });

            // Hand them to Core. Core's own lazy search walks up from
            // AppContext.BaseDirectory, which inside Unity is the Editor *install*
            // directory rather than the project, so that search always fails here and every
            // rule would quietly fall back to its documented default. Pushing the tables in
            // explicitly is what stops a Unity build from silently running on fallback
            // balance numbers.
            SimulationRules.UseTables(_tables);
        }

        /// <summary>
        /// Walks up from <paramref name="start"/> looking for the table folder.
        /// </summary>
        /// <remarks>
        /// Searching rather than assuming an exact path is what lets the same code find the
        /// tables when run from the Editor, from a test's temp directory, or from a built
        /// player where StreamingAssets sits at a different depth.
        /// </remarks>
        private static string FindTableDirectory(string start)
        {
            var directory = new System.IO.DirectoryInfo(start);
            while (directory != null)
            {
                string candidate = System.IO.Path.Combine(directory.FullName, TableFolder);
                if (System.IO.Directory.Exists(candidate))
                    return candidate;

                candidate = System.IO.Path.Combine(directory.FullName, "assets/data/tables");
                if (System.IO.Directory.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            return null;
        }
    }
}