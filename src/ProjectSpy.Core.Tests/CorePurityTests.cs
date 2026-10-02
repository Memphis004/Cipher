using System.Reflection;
using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Enforces knowledge.md rule 2: <c>ProjectSpy.Core</c> must never reference UnityEngine
/// or the other non-deterministic framework types.
/// </summary>
/// <remarks>
/// This is the cheap, always-on half of the stage-5 determinism audit. Scanning the
/// compiled assembly's *reference* table catches the whole class of mistakes —
/// a single accidental <c>using UnityEngine;</c> — without needing a Roslyn analyzer
/// in the build.
/// </remarks>
public class CorePurityTests
{
    /// <summary>
    /// Namespaces Core is forbidden to touch. Matched as a prefix on the full type
    /// namespace so that <c>UnityEngine.UI</c> and <c>Unity</c> are both caught.
    /// </summary>
    private static readonly string[] ForbiddenNamespacePrefixes =
    {
        "UnityEngine",
        "Unity",
        "Unity.Mathematics",
    };

    /// <summary>Individual types that would break determinism if reached for in a rule path.</summary>
    private static readonly string[] ForbiddenTypeNames =
    {
        "System.Random",
        "System.DateTime",
        "System.DateTimeOffset",
        "System.Guid",
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileStream",
        "System.Environment",
        "System.Threading.Thread",
        "System.Diagnostics.Stopwatch",
    };

    [Fact]
    public void Core_ReferencesNoUnityTypes()
    {
        Assembly core = typeof(Tick).Assembly;
        var offenders = new List<string>();

        foreach (AssemblyName reference in core.GetReferencedAssemblies())
        {
            if (reference.Name is not null &&
                reference.Name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(reference.Name!);
            }
        }

        Assert.True(offenders.Count == 0,
            "ProjectSpy.Core must not reference Unity assemblies (knowledge.md rule 2). Found: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Core_DeclaresNoTypesInForbiddenNamespaces()
    {
        Assembly core = typeof(Tick).Assembly;
        var offenders = new List<string>();

        foreach (Type type in core.GetTypes())
        {
            string? ns = type.Namespace;
            if (ns is null)
                continue;

            foreach (string prefix in ForbiddenNamespacePrefixes)
            {
                if (ns.StartsWith(prefix, StringComparison.Ordinal))
                    offenders.Add($"{type.FullName} (namespace {ns})");
            }
        }

        Assert.True(offenders.Count == 0,
            "ProjectSpy.Core declares types in forbidden namespaces: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Core_MembersDoNotExposeForbiddenTypes()
    {
        Assembly core = typeof(Tick).Assembly;
        var offenders = new List<string>();

        foreach (Type type in core.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                // Skip methods inherited from framework interfaces. Every enum carries
                // IConvertible.ToDateTime via the compiler, which says nothing about
                // whether Core chose to depend on wall-clock time.
                if (method.DeclaringType is not null &&
                    method.DeclaringType.Assembly != core)
                {
                    continue;
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (IsForbidden(parameter.ParameterType))
                        offenders.Add($"{type.Name}.{method.Name}({parameter.ParameterType.Name})");
                }

                if (method.ReturnType != typeof(void) && IsForbidden(method.ReturnType))
                    offenders.Add($"{type.Name}.{method.Name} -> {method.ReturnType.Name}");
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (IsForbidden(property.PropertyType))
                    offenders.Add($"{type.Name}.{property.Name} ({property.PropertyType.Name})");
            }
        }

        Assert.True(offenders.Count == 0,
            "ProjectSpy.Core exposes non-deterministic types on its public surface: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Core_ExposesNoUnityAssemblyDependency()
    {
        // A belt-and-braces check that Core is loadable in a plain netstandard host,
        // which is exactly how the console simulator and the tests run it.
        Assembly core = typeof(Tick).Assembly;

        Assert.Equal("ProjectSpy.Core", core.GetName().Name);
        Assert.DoesNotContain(
            core.GetReferencedAssemblies(),
            a => a.Name is not null && a.Name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsForbidden(Type type)
    {
        string fullName = type.FullName ?? type.Name;

        foreach (string forbidden in ForbiddenTypeNames)
        {
            if (string.Equals(fullName, forbidden, StringComparison.Ordinal))
                return true;
        }

        foreach (string prefix in ForbiddenNamespacePrefixes)
        {
            if ((type.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
