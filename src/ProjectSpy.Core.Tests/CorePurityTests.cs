using System.Reflection;
using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Enforces knowledge.md rule 2 (no UnityEngine, no non-deterministic framework
/// types) and rule 10 (no coordinates, meshes or camera data).
/// </newString>
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

    // ---- rule 10: no coordinates, meshes or camera data -----------------------

    /// <summary>
    /// Substrings that mean Core had started storing a position or a piece of
    /// rendering state.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively as a <em>substring</em> of the member name, so
    /// <c>GridX</c>, <c>NewGridX</c> and <c>worldPosition</c> are all caught. An exact
    /// match was tried first and immediately let <c>RoomMerged.NewGridX</c> through:
    /// a prefixed name is the obvious way to reintroduce the thing being banned.
    /// </remarks>
    private static readonly string[] ForbiddenMemberFragments =
    {
        "grid",
        "position", "viewport", "transform", "quaternion", "euler", "rotation",
        "collider", "hitbox", "bounds", "width", "height", "scale",
        "mesh", "sprite", "renderer", "prefab", "material", "texture",
        "camera", "raycast", "linecast", "spherecast",
        "worldspace", "localposition", "screenposition",
    };

    /// <summary>The subset of <see cref="ForbiddenMemberFragments"/> that names RENDER state.</summary>
    private static readonly HashSet<string> RenderFragments = new(StringComparer.Ordinal)
    {
        "mesh", "sprite", "renderer", "prefab", "material", "texture",
        "camera", "raycast", "linecast", "spherecast",
        "collider", "hitbox", "bounds",
    };

    /// <summary>
    /// Member names that are legitimate despite containing a forbidden word.
    /// </summary>
    /// <remarks>
    /// Listing an exemption is cheap; silently renaming a correct member later, or
    /// loosening the check until it passes, is not. Anything added here has to say
    /// why the word is a false positive.
    /// </remarks>
    private static readonly HashSet<string> ForbiddenMemberExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Integer multiplier on build cost, not a transform scale.
        nameof(BaseLayout.DepthCostModifier),

        // A mission node's security camera. The stage-4 brief lists `camera` as an
        // interactable kind, and the word is on the forbidden list because of what it
        // usually names in Core: a rendering component.
        //
        // It is not one here. This is a rule object — it watches a set of abstract slot
        // indices and raises the alarm meter when the team is seen. It holds no
        // position, no transform, no frustum and no render state, and it exists in the
        // 2D, 2.5D and 3D presentations equally. The ban targets render state, and the
        // guard below checks member names because it cannot check types; this is a
        // documented instance of a name being innocent of the thing it is named after.
        //
        // If a camera ever grows a transform, a view frustum or a render layer, this
        // exemption becomes a bug rather than a loophole — so it is listed by name
        // rather than by pattern, and the leak tests still hold it to the same
        // coordinate-free surface as everything else.
        nameof(InteractableType.Camera),
    };

    [Fact]
    public void Core_ExposesNoCoordinateOrRenderingMembers()
    {
        // knowledge.md rule 10. Scanning member names catches the shape of the
        // violation — a field called GridX — even though the type used to hold it
        // (int) is perfectly innocent on its own.
        Assembly core = typeof(Tick).Assembly;
        var offenders = new List<string>();

        foreach (Type type in core.GetTypes())
        {
            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (IsForbiddenMemberName(property.Name, type.Namespace))
                    offenders.Add($"{type.FullName}.{property.Name}");
            }

            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (IsForbiddenMemberName(field.Name, type.Namespace))
                    offenders.Add($"{type.FullName}.{field.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "ProjectSpy.Core exposes coordinate- or render-shaped members "
            + "(knowledge.md rule 10): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Core_TypesExposeNoCoordinateParameterTypes()
    {
        // Catches the other half of the rule: not storing a position is not enough if
        // a method accepts one and quietly turns it into layout state.
        Assembly core = typeof(Tick).Assembly;
        var offenders = new List<string>();

        foreach (Type type in core.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.DeclaringType is not null && method.DeclaringType.Assembly != core)
                    continue;

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    string? parameterName = parameter.Name;
                    if (parameterName is null || !IsForbiddenMemberName(parameterName, type.Namespace))
                        continue;

                    // A single int called `position` is the shape the grid API had.
                    // `position` as a name is forbidden wherever it appears.
                    offenders.Add($"{type.Name}.{method.Name}({parameter.Name})");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "ProjectSpy.Core takes coordinate-named parameters "
            + "(knowledge.md rule 10): " + string.Join(", ", offenders));
    }

    [Fact]
    public void RoomsAreDescribedBySlotsAndLayersNotByPosition()
    {
        // The rule as it applies to the type that most wanted to keep coordinates.
        foreach (string gone in new[] { "GridX", "GridY", "Width", "MaxX", "Contains", "Adjacent" })
            Assert.Null(typeof(Room).GetProperty(gone, BindingFlags.Public | BindingFlags.Instance));

        Assert.NotNull(typeof(Room).GetProperty(nameof(Room.Layer)));
        Assert.NotNull(typeof(Room).GetProperty(nameof(Room.SlotIndices)));
        Assert.NotNull(typeof(Room).GetProperty(nameof(Room.AdjacentRoomIds)));
    }

    [Fact]
    public void TheLayoutHasNoGridDimensionsOrOccupancyArray()
    {
        foreach (string gone in new[] { "Width", "Height", "OccupiedCellCount", "OccupancyPercent", "RoomAt", "IsOccupied" })
            Assert.Null(typeof(BaseLayout).GetProperty(gone, BindingFlags.Public | BindingFlags.Instance));

        // The flat width-by-height occupancy array is gone, and so is the name that
        // described it. A leftover `_occupancy` here was a dictionary of slots, which
        // is fine, but the name kept implying a lattice that no longer exists.
        Assert.Null(typeof(BaseLayout).GetField(
            "_occupancy", BindingFlags.NonPublic | BindingFlags.Instance));

        Assert.NotNull(typeof(BaseLayout).GetProperty(nameof(BaseLayout.LayerCount)));
        Assert.NotNull(typeof(BaseLayout).GetProperty(nameof(BaseLayout.SlotsPerLayer)));
    }

    [Fact]
    public void RoomContentsCarryNoCoordinates()
    {
        // The mission-interior model is the rule's positive example: an object is a
        // type, a state, and a slot index.
        foreach (PropertyInfo property in typeof(Interactable).GetProperties())
        {
            Assert.False(IsForbiddenMemberName(property.Name, typeof(Interactable).Namespace),
                $"Interactable.{property.Name} is coordinate- or render-shaped");
        }
    }

    /// <summary>
    /// True when a member's name contains a coordinate- or render-shaped word.
    /// </summary>
    /// <remarks>
    /// Matching is on <em>word tokens</em>, not raw substrings. A substring check
    /// flagged <c>Resources.Materials</c> because it contains "material", which is how
    /// the list got quietly useless: a check that cries wolf over the resource counter
    /// is a check someone will delete. Tokenizing the identifier into camel-case words
    /// keeps <c>NewGridX</c> (New, Grid, X) while leaving <c>Materials</c> alone.
    /// </remarks>
    /// <remarks>
    /// One vocabulary gap is worth recording rather than pretending away. Adding the
    /// token "world" to catch <c>WorldPosition</c> was tried and reverted: "world" is
    /// this project's own noun for the global simulation (<c>worldSeed</c>,
    /// <c>WorldState</c>, and the <c>world</c> parameter on every phase method), so
    /// banning the word bans the simulation's own vocabulary and the guard stops being
    /// about render state at all. <c>WorldPosition</c> is therefore forgiven inside the
    /// tactical namespaces along with any other position word. The mitigation is naming
    /// rather than banning — the tactical types say <c>StartX</c> and
    /// <c>TacticalPosition</c> — and the render half, which is what rule 10 is
    /// actually for, stays banned everywhere.
    /// </remarks>
    private static bool IsForbiddenMemberName(string name)
    {
        if (ForbiddenMemberExemptions.Contains(name))
            return false;

        foreach (string word in Tokenize(name))
        {
            foreach (string fragment in ForbiddenMemberFragments)
            {
                if (string.Equals(word, fragment, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Namespaces where a coordinate-shaped member name is legitimate (rule 10's
    /// tactical-space exception, rules 15 and 16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ProjectSpy.Core.Tactical</c> holds <c>TacticalPosition</c>.
    /// <c>ProjectSpy.Core.Missions</c> holds the site-layout types, which describe a
    /// generated building as ordered <c>[StartX, EndX)</c> intervals of centimetres.
    /// Both are simulation space: a room's <c>StartX</c> is a number the simulation
    /// does interval maths on, in exactly the way rule 15 requires.
    /// </para>
    /// <para>
    /// <b>The exemption covers coordinate words only.</b> Render words — mesh,
    /// sprite, renderer, texture, camera, collider — are still rejected inside these
    /// namespaces, which is the half that matters. A building having rooms with
    /// extents is a fact about a building; a building having a material is a fact
    /// about a renderer, and nothing in Core ever needs it.
    /// </para>
    /// <para>
    /// Namespace-scoped rather than type-scoped on purpose: a name-by-name exemption
    /// list is a list that has to be edited every time the model grows a field, and
    /// the next person to add one will not be the person who wrote the justification.
    /// Scoping it to the namespace means the question stays "is this simulation
    /// space or render space", which is answerable without reading a list.
    /// </para>
    /// <para>
    /// Note that the older files that happen to live under <c>Missions/</c> declare
    /// <c>namespace ProjectSpy.Core</c>, so they are <em>not</em> covered by this.
    /// That is the point: the exemption was granted to the new site-layout namespace,
    /// not to a folder.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> TacticalSpaceNamespaces = new(StringComparer.Ordinal)
    {
        "ProjectSpy.Core.Tactical",
        "ProjectSpy.Core.Missions",
    };

    /// <summary>
    /// True when a member name is coordinate- or render-shaped <em>for the type that
    /// declares it</em>.
    /// </summary>
    /// <remarks>
    /// The namespace-aware counterpart of <see cref="IsForbiddenMemberName(string)"/>:
    /// the same token check, except that coordinate words are forgiven inside
    /// <see cref="TacticalSpaceNamespaces"/> and render words are forgiven nowhere.
    /// </remarks>
    private static bool IsForbiddenMemberName(string name, string? declaringNamespace)
    {
        bool tacticalSpace = declaringNamespace is not null
                             && TacticalSpaceNamespaces.Contains(declaringNamespace);

        if (ForbiddenMemberExemptions.Contains(name))
            return false;

        foreach (string word in Tokenize(name))
        {
            foreach (string fragment in ForbiddenMemberFragments)
            {
                if (!string.Equals(word, fragment, StringComparison.Ordinal))
                    continue;

                if (tacticalSpace && !RenderFragments.Contains(fragment))
                    continue;

                return true;
            }
        }

        return false;
    }

    [Theory]
    [InlineData("GridX", true)]
    [InlineData("GridY", true)]
    [InlineData("NewGridX", true)]        // the prefixed form that got through first
    [InlineData("WorldPosition", true)]
    [InlineData("LocalPosition", true)]
    [InlineData("Camera", false)]          // exempted: a rule object, not a render component
    [InlineData("RenderCamera", true)]    // the shapes the ban actually targets
    [InlineData("CameraTransform", true)]
    [InlineData("Bounds", true)]
    [InlineData("Width", true)]
    [InlineData("Height", true)]
    [InlineData("MeshRenderer", true)]
    [InlineData("RaycastHit", true)]
    [InlineData("Viewport", true)]
    [InlineData("SlotIndex", false)]
    [InlineData("Layer", false)]
    [InlineData("Materials", false)]      // contains "material" but is not one
    [InlineData("SlotCount", false)]
    [InlineData("AdjacentRoomIds", false)]
    [InlineData("DepthCostModifier", false)]
    [InlineData("MentalStamina", false)]
    public void TheCoordinateGuardActuallyCatchesCoordinates(string name, bool expected)
    {
        // A guard that cannot fail is worse than no guard: it reads as protection and
        // provides none. These are the exact shapes the rule 10 migration removed.
        Assert.Equal(expected, IsForbiddenMemberName(name));
    }

    [Theory]
    // The tactical-space exception (rules 15 and 16). A room occupies an interval of
    // centimetres and a `TacticalPosition` names a point on a floor; both are facts
    // about the simulation, so these names are legitimate here and nowhere else.
    [InlineData("StartX", false)]
    [InlineData("EndX", false)]
    [InlineData("Span", false)]
    [InlineData("Width", false)]      // a room's span in centimetres, not a render extent
    [InlineData("FloorIndex", false)]

    // The other half of the split: render words are forgiven in NO namespace. A site
    // layout is not a renderer, and a `SiteRoom.Material` would be the exact
    // regression rule 10 exists to prevent.
    [InlineData("MeshRenderer", true)]
    [InlineData("Texture", true)]
    [InlineData("CameraTransform", true)]
    [InlineData("Collider", true)]
    [InlineData("WorldPosition", false)]  // see the note on the token check: 'world' is a
                                          // project noun (worldSeed, WorldState), not render state
    public void TheTacticalSpaceExceptionForgivesCoordinatesButNeverRenderState(
        string name, bool expected)
    {
        Assert.Equal(expected, IsForbiddenMemberName(name, "ProjectSpy.Core.Missions"));
        Assert.Equal(expected, IsForbiddenMemberName(name, "ProjectSpy.Core.Tactical"));
    }

    [Fact]
    public void TheTacticalSpaceExceptionDoesNotLeakOutsideItsNamespaces()
    {
        // The exemption is scoped by namespace, so the same coordinate name is still
        // forbidden on the base layout and on the older mission-map types — which
        // live under Missions/ but declare ProjectSpy.Core. Scoping it to a folder
        // rather than a namespace would have quietly let all of them back in.
        foreach (string name in new[] { "Width", "Position", "Bounds", "Scale", "Transform" })
        {
            Assert.True(IsForbiddenMemberName(name, "ProjectSpy.Core"));
            Assert.True(IsForbiddenMemberName(name, "ProjectSpy.Core.Missions.Something"));
            Assert.True(IsForbiddenMemberName(name, null));
        }
    }

    /// <summary>Splits a PascalCase or camelCase identifier into lowercase words.</summary>
    private static List<string> Tokenize(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (char c in name)
        {
            if (char.IsUpper(c) && current.Length > 0)
            {
                words.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }

            current.Append(c);
        }

        if (current.Length > 0)
            words.Add(current.ToString().ToLowerInvariant());

        return words;
    }
}
