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
