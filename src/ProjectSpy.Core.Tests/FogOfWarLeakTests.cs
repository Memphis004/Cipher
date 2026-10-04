using System.Collections;
using System.Reflection;
using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves that a mission node the player has never observed has no interior that any
/// part of Core will hand out.
/// </summary>
/// <remarks>
/// <para>
/// The brief asks for this explicitly, and it is the requirement most likely to rot.
/// Every other part of stage 4 can be broken by a designer editing a CSV; this one
/// breaks by someone adding a convenience property — an "all rooms" getter on the
/// mission state, a debug dump that stringifies contents, a serializer that walks
/// every field. Each of those is a small, reasonable-looking change that quietly
/// hands the UI the building the player has not walked into.
/// </para>
/// <para>
/// So the guarantee is tested over the compiled public surface rather than over the
/// handful of calls the author happened to think of. Two independent angles:
/// reflection, which fails the build when a new leak is *declared*; and behaviour,
/// which fails when an existing one starts *returning* data.
/// </para>
/// </remarks>
public class FogOfWarLeakTests
{
    private static Assembly Core => typeof(FogOfWar).Assembly;

    private static MissionMap Map(ulong worldSeed = 2024UL, int missionId = 1, int tier = 2)
        => MissionMapGenerator.Generate(
            missionId, MissionMapGenerator.DeriveMapSeed(worldSeed, missionId), tier);

    // ---- reflection: nothing exposes interiors wholesale --------------------

    [Fact]
    public void NoPublicMemberExposesRoomContentsAsAValue()
    {
        // A property or field of type RoomContents — or of any collection of them —
        // would be a handle the UI could hold without going through a node. Contents
        // have to be asked for one node at a time, so that the ask can be refused.
        var offenders = new List<string>();

        foreach (Type type in Core.GetTypes())
        {
            // Only publicly reachable types are part of the public surface. A public
            // method on an internal class is invisible to the UI, so flagging it
            // would make this check cry wolf and get deleted.
            if (!type.IsPublic && !type.IsNestedPublic)
                continue;

            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (ExposesContents(property.PropertyType))
                    offenders.Add($"property {type.FullName}.{property.Name}");
            }

            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (ExposesContents(field.FieldType))
                    offenders.Add($"field {type.FullName}.{field.Name}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A public member hands out node interiors directly. Contents must be "
            + "reachable only through a method that takes a MissionNodeId and can "
            + "refuse, so that an unobserved node has nothing to return:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheOnlyWayToGetContentsIsAFogOfWarMethodKeyedByNode()
    {
        // The complement to the test above. Method-returning a bare value cannot be
        // banned outright — Observe has to hand the room back — so the requirement is
        // narrowed to: it must belong to FogOfWar and must be keyed by a node.
        var offenders = new List<string>();

        foreach (Type type in Core.GetTypes())
        {
            if (!type.IsPublic && !type.IsNestedPublic)
                continue;

            foreach (MethodInfo method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.DeclaringType is not null && method.DeclaringType.Assembly != Core)
                    continue;

                bool returnsContents = ExposesContents(method.ReturnType);
                bool passesContents = method.GetParameters()
                    .Any(p => p.IsOut && ExposesContents(p.ParameterType));

                if (!returnsContents && !passesContents)
                    continue;

                bool keyedByNode = method.GetParameters()
                    .Any(p => p.ParameterType == typeof(MissionNodeId));

                if (method.DeclaringType != typeof(FogOfWar) || !keyedByNode)
                {
                    offenders.Add(
                        $"{type.FullName}.{method.Name} hands out contents without being "
                        + "a FogOfWar method keyed by MissionNodeId");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Node interiors escaped FogOfWar, which is the only component that knows "
            + "what the player has observed:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheInteriorGeneratorItselfIsNotPublic()
    {
        // NodeInteriorGenerator can build any node's interior and has no fog state to
        // consult, so exposing it would hand out exactly what FogOfWar refuses to.
        // Kept as its own assertion because it is the leak most likely to be
        // reintroduced: someone tidying up sees an internal class that only Core uses
        // and makes it public "for the tools".
        Type? generator = Core.GetType("ProjectSpy.Core.NodeInteriorGenerator");

        Assert.True(generator is null || !generator.IsPublic,
            "NodeInteriorGenerator must stay internal: it can generate a hidden node's "
            + "interior without consulting fog state.");
    }

    // ---- behaviour: an unobserved node has nothing to give --------------------

    [Fact]
    public void AnUnobservedNodeHasNoContentsAndSaysSo()
    {
        for (int index = 0; index < 200; index++)
        {
            MissionMap map = Map((ulong)index + 1, index + 1, 1 + (index % 3));
            FogOfWar fog = new(map);

            foreach (MissionNode node in map.Nodes)
            {
                if (fog.StateOf(node.Id) != MissionVisibility.Hidden)
                    continue;

                Assert.False(
                    fog.TryGetContents(node.Id, out RoomContents contents),
                    $"node {node.Id} was Hidden but TryGetContents returned a room.");
                Assert.Null(contents);
            }
        }
    }

    [Fact]
    public void ExactlyTheObservedNodesHaveInteriors()
    {
        // The internal store is the claim under test: a node that is not observed has
        // no entry, so there is nothing cached, nothing pending and nothing to leak.
        for (int index = 0; index < 100; index++)
        {
            MissionMap map = Map((ulong)index + 700, index + 1, 1 + (index % 3));
            FogOfWar fog = new(map);

            int revealed = map.Nodes.Count(n => fog.StateOf(n.Id) == MissionVisibility.Revealed);
            Assert.Equal(revealed, fog.ObservedNodeCount);

            // Scouting must not create one either.
            foreach (MissionNode node in map.NodesInLayer(2))
                fog.Scout(node.Id);

            Assert.Equal(revealed, fog.ObservedNodeCount);
        }
    }

    [Fact]
    public void InteriorsAreNotBuiltWhenAMapIsGenerated()
    {
        // Lazy means lazy: generating ten thousand maps must not have quietly built
        // ten thousand buildings. If someone "optimises" generation to precompute
        // interiors and filters them at read time, this fails — which is the moment
        // the alternative was supposed to become visible.
        for (int index = 0; index < 10_000; index++)
        {
            MissionMap map = Map((ulong)index + 1, index + 1, 1 + (index % 3));
            var fog = new FogOfWar(map);

            foreach (MissionNode node in map.Nodes)
            {
                // The entry node is occupied the moment the mission starts, so one
                // interior existing is the point, not a failure.
                if (node.Id == map.EntryNodeId)
                    continue;

                Assert.Equal(MissionVisibility.Hidden, fog.StateOf(node.Id));
            }
        }
    }

    [Fact]
    public void SightForAnUnobservedNodeCarriesNoNodeSpecificData()
    {
        // Even the fog-free map skeleton must not reach the player through the view.
        // The one thing that legitimately survives is the layer, because a site plan
        // without knowing there are rooms to go through is not playable.
        MissionMap map = Map();
        FogOfWar fog = new(map);

        foreach (MissionNode node in map.Nodes)
        {
            if (fog.StateOf(node.Id) != MissionVisibility.Hidden)
                continue;

            NodeSight sight = fog.Sight(node.Id);

            Assert.Equal(string.Empty, sight.NameKey);
            Assert.Empty(sight.Tags);
            Assert.False(sight.HasLoot);
            Assert.False(sight.HasInterior);
            Assert.Equal(node.Layer, sight.Layer);
        }
    }

    [Fact]
    public void ObservingEveryNodeIsTheOnlyWayToGetEveryInterior()
    {
        // The positive control: the leak tests above would pass just as well against a
        // FogOfWar that never produced contents at all. Confirming that walking the
        // map does yield every interior keeps them honest.
        MissionMap map = Map();
        FogOfWar fog = new(map);

        foreach (MissionNode node in map.Nodes)
        {
            // The entry node is occupied from the first tick, so it is the one node
            // that legitimately already has an interior.
            if (node.Id == map.EntryNodeId)
            {
                Assert.True(fog.TryGetContents(node.Id, out _));
                continue;
            }

            Assert.False(fog.TryGetContents(node.Id, out _));
            fog.Observe(node.Id);
            Assert.True(fog.TryGetContents(node.Id, out _));
        }

        Assert.Equal(map.Nodes.Count, fog.ObservedNodeCount);
    }

    // ---- the read model Presentation actually reads ------------------------

    /// <summary>
    /// A <see cref="FogView"/> must not be a way back to the simulation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stage-4 brief makes <c>FogView</c> the only thing Presentation may read, so
    /// the property that matters is not what it shows but what it can reach. If a view
    /// held a <see cref="MissionMap"/> or a <see cref="MissionNode"/>, every grade of
    /// fog in it would be decorative: the UI could simply go round it.
    /// </para>
    /// <para>
    /// Checked by shape rather than by reading each member, so adding a field that
    /// happens to leak fails here without anyone having to notice it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFogViewCannotReachTheMapOrANode()
    {
        var offenders = new List<string>();

        foreach (PropertyInfo property in typeof(FogView).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            Type type = property.PropertyType;

            bool handsOutNode = type == typeof(MissionNode)
                               || type == typeof(MissionMap)
                               || type == typeof(FogOfWar)
                               || typeof(RoomContents).IsAssignableFrom(type);

            bool collectionOfNodes = (type.IsArray && type.GetElementType() == typeof(MissionNode))
                                     || (type.IsGenericType
                                         && type.GetGenericArguments().Contains(typeof(MissionNode)));

            if (handsOutNode || collectionOfNodes)
                offenders.Add($"{nameof(FogView)}.{property.Name} : {type.Name}");
        }

        Assert.True(
            offenders.Count == 0,
            "FogView is Presentation's only sanctioned read surface, so it must not be a "
            + "handle back into the simulation. Everything it exposes has to be a copy "
            + "the fog rules already approved:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// No member of the read model carries an interior, in any grade.
    /// </summary>
    /// <remarks>
    /// The same guarantee as <see cref="NoPublicMemberExposesRoomContentsAsAValue"/>,
    /// applied specifically to the two types the UI is expected to hold. Restated
    /// because this is the surface that will actually be used, and a future edit that
    /// adds a convenience <c>Contents</c> property to <see cref="FogNodeView"/> would
    /// be invisible to a test that only looked at the whole assembly.
    /// </remarks>
    [Fact]
    public void TheReadModelItselfNeverCarriesAnInterior()
    {
        foreach (Type type in new[] { typeof(FogView), typeof(FogNodeView), typeof(NodeIntel) })
        {
            foreach (PropertyInfo property in type.GetProperties())
            {
                Assert.False(
                    ExposesContents(property.PropertyType),
                    $"{type.Name}.{property.Name} hands out an interior; contents come from "
                    + "FogOfWar.TryGetContents, keyed by a node the player has entered");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.False(
                    ExposesContents(method.ReturnType),
                    $"{type.Name}.{method.Name}() hands out an interior");
            }
        }
    }

    /// <summary>
    /// A Hidden node in the whole-map view shows no more than the per-node sight does.
    /// </summary>
    /// <remarks>
    /// <see cref="Sight"/> existed before <see cref="FogView"/>, so the risk is not that
    /// either leaks but that the newer one is more generous — a field added to
    /// <see cref="BuildSight"/> for the map view and forgotten in the per-node path.
    /// Comparing the two keeps them from drifting apart.
    /// </remarks>
    [Fact]
    public void TheWholeMapViewHidesExactlyWhatThePerNodeSightHides()
    {
        MissionMap map = Map();
        FogOfWar fog = new(map);

        FogView view = fog.View();

        foreach (MissionNode node in map.Nodes)
        {
            if (fog.StateOf(node.Id) != MissionVisibility.Hidden)
                continue;

            FogNodeView whole = view.Node(node.Id);
            NodeSight single = fog.Sight(node.Id);

            Assert.Equal(MissionVisibility.Hidden, whole.State);
            Assert.True(whole.State == single.State);
            Assert.Equal(single.Layer, whole.Layer);
            Assert.Equal(string.Empty, whole.NameKey);
            Assert.Empty(whole.Tags);
            Assert.False(whole.HasLoot);
            Assert.Equal(0, whole.BaseSecurity);
            Assert.Equal(GuardBand.None, whole.Guards);
            Assert.False(whole.HasIntel);
            Assert.False(whole.HasInterior);
        }
    }

    /// <summary>
    /// Edges out of a hidden node reveal nothing about the far end.
    /// </summary>
    /// <remarks>
    /// A view that listed edges but resolved them into the far node's data would leak
    /// the entire map's shape from a single node. Ids are fine — knowing a door leads
    /// somewhere is what makes a map a map — and resolving one must land on that node's
    /// own view, at whatever grade it is entitled to.
    /// </remarks>
    [Fact]
    public void FollowingAnEdgeFromAHiddenNodeReachesOnlyHiddenNodes()
    {
        MissionMap map = Map();
        FogOfWar fog = new(map);
        FogView view = fog.View();

        foreach (FogNodeView node in view.Nodes)
        {
            if (node.State != MissionVisibility.Hidden)
                continue;

            foreach (MissionNodeId neighbour in node.EdgesTo)
            {
                Assert.True(
                    view.Node(neighbour).State == MissionVisibility.Hidden,
                    $"following {node.NodeId} -> {neighbour} revealed the neighbour");
            }
        }
    }

    /// <summary>
    /// A Scouted node reports a guard band but still exposes no interior.
    /// </summary>
    /// <remarks>
    /// The band is drawn from the map's committed skeleton rather than the contents, so
    /// this is the assertion that the two stay separate: the band exists for rooms the
    /// player has not entered, and the interior still does not.
    /// </remarks>
    [Fact]
    public void AScoutedNodeReportsAGuardBandWithoutExposingAnythingInside()
    {
        MissionMap map = Map();
        FogOfWar fog = new(map);

        int scouted = 0;

        foreach (MissionNode node in map.NodesInLayer(2))
        {
            if (!fog.Scout(node.Id))
                continue;

            scouted++;

            FogNodeView view = fog.View().Node(node.Id);

            Assert.Equal(MissionVisibility.Scouted, view.State);
            Assert.Equal(node.GuardBand, view.Guards);
            Assert.True(view.HasIntel);
            Assert.False(view.HasInterior);

            // The interior genuinely does not exist yet, which is what makes the band
            // above a promise rather than a summary.
            Assert.False(fog.TryGetContents(node.Id, out _));
        }

        Assert.True(scouted > 0, "no nodes were scouted");
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// True when a type hands out <see cref="RoomContents"/>, either directly or as a
    /// collection element.
    /// </summary>
    private static bool ExposesContents(Type type)
    {
        if (type == typeof(RoomContents))
            return true;

        // A collection of interiors is exactly as much of a leak as one interior.
        if (type.IsArray)
            return type.GetElementType() == typeof(RoomContents);

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                if (argument == typeof(RoomContents))
                    return true;
            }
        }

        return false;
    }
}
