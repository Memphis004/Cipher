using System;
using System.IO;
using Luban;

namespace ProjectSpy.Tables;

/// <summary>
/// Loads the Luban-generated tables from the binary output of <c>tools/gen.ps1</c>.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="Tables"/> instance is built once at startup and shared. It is
/// immutable content, so there is nothing to invalidate and nothing to reload.
/// </para>
/// <para>
/// The binary directory is resolved by searching upwards from a supplied start
/// point for <c>assets/data/tables</c>. That keeps the console projects, the tests
/// and (from stage 7) Unity reading the exact same bytes rather than three copies
/// that can drift.
/// </para>
/// </remarks>
public static class TableService
{
    /// <summary>Path fragment searched for, relative to the repository root.</summary>
    public const string TableDataRelativePath = "assets/data/tables";

    private static readonly object LoadGate = new();
    private static Tables? _cached;

    /// <summary>
    /// Loads the tables, caching the result. Subsequent calls return the same instance.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// The binary directory could not be found. Usually this means
    /// <c>pwsh tools/gen.ps1</c> has not been run.
    /// </exception>
    public static Tables Load(string? startDirectory = null)
    {
        lock (LoadGate)
        {
            if (_cached is not null)
                return _cached;

            string directory = ResolveTableDirectory(startDirectory ?? AppContext.BaseDirectory);

            var loaded = new Tables(name =>
            {
                string file = Path.Combine(directory, name + ".bytes");
                if (!File.Exists(file))
                {
                    throw new FileNotFoundException(
                        $"Table binary '{name}.bytes' is missing from '{directory}'. " +
                        "Run 'pwsh tools/gen.ps1' to regenerate the tables.",
                        file);
                }

                byte[] bytes = File.ReadAllBytes(file);
                return new ByteBuf(bytes);
            });

            _cached = loaded;
            return loaded;
        }
    }

    /// <summary>
    /// Walks up from <paramref name="start"/> looking for the shared table directory.
    /// </summary>
    /// <remarks>
    /// The search stops at the filesystem root. Walking up rather than hard-coding
    /// a relative path is what lets the same code work from a test's bin directory,
    /// the console simulator's output directory, and Unity's StreamingAssets without
    /// per-host configuration.
    /// </remarks>
    public static string ResolveTableDirectory(string start)
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, TableDataRelativePath);
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find '{TableDataRelativePath}' in any parent of '{start}'. " +
            "Run 'pwsh tools/gen.ps1' to generate the table binaries.");
    }

    /// <summary>True when the table directory exists and has been generated.</summary>
    public static bool IsAvailable(string? startDirectory = null)
    {
        try
        {
            ResolveTableDirectory(startDirectory ?? AppContext.BaseDirectory);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Drops the cached instance. For tests that reload from disk.</summary>
    public static void ResetCache()
    {
        lock (LoadGate)
        {
            _cached = null;
        }
    }
}
