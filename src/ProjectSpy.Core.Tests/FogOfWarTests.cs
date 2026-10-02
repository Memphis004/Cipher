using System.Reflection;
using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// <see cref="FogOfWar"/>: what the player is allowed to know about a mission map, and
/// when a node's interior is allowed to exist at all.
/// </summary>
/// <remarks>
/// <para>
/// The leak-proofing tests live in <see cref="FogOfWarLeakTests"/>, because they are
/// about the shape of Core's public surface rather than about any one behaviour. Here
/// it is the rules: what a team can see, and when.
/// </para>
/// </remarks>
public class FogOfWarTests
{
    private static MissionMap Map(ulong worldSeed = 4242UL, int missionId = 1, int tier = 2)
        => MissionMapGenerator.Generate(
            missionId, MissionMapGenerator.DeriveMapSeed(worldSeed, missionId), tier);

    private static SkillSet Infiltration(int value) => new(value, 0, 0, 0, 0);

    // ---- reveal radius --------------------------------------------------------

    [Fact]
    public void ASpecialistOutrangesATeamOfTheSameSizeAndAverage()
    {
        // One infiltrator at 60 with three rookies, against four competent agents.
        // The specialist team's average is lower (30 vs 40) and its radius is still
        // larger. This is the reason to build a team around one expert instead of
        // four interchangeable bodies.
        var specialist = new[]
        {
            Infiltration(60), Infiltration(20), Infiltration(20), Infiltration(20),
        };

        var uniform = new[]
        {
            Infiltration(40), Infiltration(40), Infiltration(40), Infiltration(40),
        };

        int specialistRadius = FogOfWar.ComputeRevealRadius(specialist);
        int uniformRadius = FogOfWar.ComputeRevealRadius(uniform);

        Assert.True(
            specialistRadius > uniformRadius,
            $"specialist radius {specialistRadius} should exceed uniform radius {uniformRadius}");
    }

    [Fact]
    public void TheRevealRadiusComesFromTheBestInfiltratorNotTheAverage()
    {
        var oneWeak = new[] { Infiltration(5) };
        var oneStrong = new[] { Infiltration(80) };

        Assert.True(FogOfWar.ComputeRevealRadius(oneStrong) > FogOfWar.ComputeRevealRadius(oneWeak));
    }

    [Fact]
    public void AnEmptyTeamStillSeesTheNodeItIsStandingIn()
    {
        // A team-less call must not return a radius of nothing at all, or a mission
        // with no deployable agents reveals nothing and cannot be diagnosed.
        Assert.True(FogOfWar.ComputeRevealRadius(Array.Empty<SkillSet>()) >= 1);
    }

    [Fact]
    public void ASmallerTeamIsNotPenalisedByDividingTheAverage()
    {
        // The average is a mean, not a sum, so a two-agent team is not weaker at
        // scouting than a four-agent team of the same shape.
        var pair = new[] { Infiltration(50), Infiltration(50) };
        var quad = new[] { Infiltration(50), Infiltration(50), Infiltration(50), Infiltration(50) };

        Assert.Equal(FogOfWar.ComputeRevealRadius(pair), FogOfWar.ComputeRevealRadius(quad));
    }

    [Fact]
    public void TheScoutRadiusIsTighterThanTheRevealRadius()
    {
        foreach (int radius in new[] { 2, 5, 11, 40 })
        {
            int scout = FogOfWar.ScoutRadiusFor(radius);
            Assert.InRange(scout, 0, radius);
        }
    }

    [Fact]
    public void GadgetsAndTerminalsGrantAScoutBonus()
    {
        Assert.True(FogOfWar.GadgetScoutBonus() > 0);
        Assert.True(FogOfWar.TerminalScoutBonus() > 0);
    }

    // ---- initial state -------------------------------------------------------

    [Fact]
    public void EveryNodeStartsHiddenExceptTheEntryTheTeamOccupies()
    {
        FogOfWar fog = new(Map());

        foreach (MissionNode node in fog.Map.Nodes)
        {
            if (node.Id == fog.Map.EntryNodeId)
                Assert.Equal(MissionVisibility.Revealed, fog.StateOf(node.Id));
            else
                Assert.Equal(MissionVisibility.Hidden, fog.StateOf(node.Id));
        }

        // Only the entry has an interior, because only the entry has been observed.
        Assert.Equal(1, fog.ObservedNodeCount);
    }

    // ---- propagation ---------------------------------------------------------

    [Fact]
    public void RevealReachesTheGivenNumberOfHopsAndNoFurther()
    {
        FogOfWar fog = new(Map());
        MissionNodeId origin = fog.Map.EntryNodeId;

        const int Radius = 2;
        const int ScoutRadius = 1;

        fog.RevealAround(origin, Radius, ScoutRadius);

        foreach (MissionNode node in fog.Map.Nodes)
        {
            int hops = HopsFrom(fog.Map, origin, node.Id);
            MissionVisibility expected;

            if (hops == 0)
            {
                // The team is standing here, so this node is observed rather than
                // merely within reach of the team.
                expected = MissionVisibility.Revealed;
            }
            else if (hops < 0)
                expected = MissionVisibility.Hidden;
            else if (hops <= ScoutRadius)
                expected = MissionVisibility.Scouted;
            else if (hops <= Radius)
                expected = MissionVisibility.Silhouette;
            else
                expected = MissionVisibility.Hidden;

            Assert.Equal(expected, fog.StateOf(node.Id));
        }
    }

    [Fact]
    public void KnowledgeNeverGoesBackwards()
    {
        FogOfWar fog = new(Map());
        MissionNodeId origin = fog.Map.EntryNodeId;

        fog.RevealAround(origin, 3, 1);

        var before = new Dictionary<MissionNodeId, MissionVisibility>();
        foreach (MissionNode node in fog.Map.Nodes)
            before[node.Id] = fog.StateOf(node.Id);

        Assert.Contains(before.Values, v => v == MissionVisibility.Scouted);

        // Walking away and revealing a smaller radius from the far end of the map
        // must not un-know anything.
        fog.RevealAround(fog.Map.ExtractionNodeId, 1, 0);

        foreach (MissionNode node in fog.Map.Nodes)
        {
            Assert.True(
                fog.StateOf(node.Id) >= before[node.Id],
                $"node {node.Id} went from {before[node.Id]} back to {fog.StateOf(node.Id)}");
        }
    }

    [Fact]
    public void ScoutingANodeDoesNotGenerateItsInterior()
    {
        FogOfWar fog = new(Map());
        MissionNode node = fog.Map.NodesInLayer(2).First();
        int before = fog.ObservedNodeCount;

        Assert.True(fog.Scout(node.Id));
        Assert.Equal(MissionVisibility.Scouted, fog.StateOf(node.Id));
        Assert.Equal(before, fog.ObservedNodeCount);
        Assert.False(fog.TryGetContents(node.Id, out _));

        // Scouting twice changes nothing and is reported as no change, so a command
        // cannot charge a gadget twice for one effect.
        Assert.False(fog.Scout(node.Id));
    }

    [Fact]
    public void ScoutingAnAlreadyRevealedNodeIsANoOp()
    {
        FogOfWar fog = new(Map());
        MissionNodeId entry = fog.Map.EntryNodeId;

        Assert.False(fog.Scout(entry));
        Assert.Equal(MissionVisibility.Revealed, fog.StateOf(entry));
    }

    // ---- what the player may read -------------------------------------------

    [Fact]
    public void AHiddenNodeRevealsNothingButItsLayer()
    {
        FogOfWar fog = new(Map());
        MissionNode node = fog.Map.NodesInLayer(2).First();

        NodeSight sight = fog.Sight(node.Id);

        Assert.Equal(MissionVisibility.Hidden, sight.State);
        Assert.Equal(node.Layer, sight.Layer);
        Assert.Equal(string.Empty, sight.NameKey);
        Assert.Empty(sight.Tags);
        Assert.False(sight.HasLoot);
    }

    [Fact]
    public void ASilhouetteShowsTheNodeExistsButNotWhatIsInIt()
    {
        FogOfWar fog = new(Map());
        MissionNodeId origin = fog.Map.EntryNodeId;

        // A radius of one hop and a scout radius of zero leaves neighbours as plain
        // silhouettes.
        fog.RevealAround(origin, 1, 0);

        foreach (MissionNodeId neighbour in fog.Map.OutboundFrom(origin))
        {
            NodeSight sight = fog.Sight(neighbour);
            Assert.Equal(MissionVisibility.Silhouette, sight.State);
            Assert.Equal(string.Empty, sight.NameKey);
            Assert.Empty(sight.Tags);
            Assert.False(sight.HasLoot);
        }
    }

    [Fact]
    public void AScoutedNodeNamesItselfAndItsLootButStillHasNoInterior()
    {
        FogOfWar fog = new(Map());
        MissionNode node = fog.Map.NodesInLayer(2).First();

        fog.Scout(node.Id);
        NodeSight sight = fog.Sight(node.Id);

        Assert.Equal(MissionVisibility.Scouted, sight.State);
        Assert.Equal(node.NameKey, sight.NameKey);
        Assert.Equal(node.HasLoot, sight.HasLoot);
        Assert.False(sight.HasInterior);
        Assert.False(fog.TryGetContents(node.Id, out _));
    }

    // ---- interior generation -------------------------------------------------

    [Fact]
    public void ObservingANodeGeneratesItsInteriorExactlyOnce()
    {
        FogOfWar fog = new(Map());
        MissionNode node = fog.Map.NodesInLayer(2).First();

        Assert.False(fog.TryGetContents(node.Id, out _));

        RoomContents first = fog.Observe(node.Id);

        Assert.True(fog.TryGetContents(node.Id, out RoomContents readBack));
        Assert.Same(first, readBack);

        // Observing again must not roll a new room: a command that searches a
        // container and then re-enters has to find their own changes.
        Assert.Same(first, fog.Observe(node.Id));
    }

    [Fact]
    public void GeneratedInteriorsAreThemselvesValid()
    {
        for (int index = 0; index < 300; index++)
        {
            MissionMap map = Map((ulong)index + 1, index + 1, 1 + (index % 3));
            FogOfWar fog = new(map);

            foreach (MissionNode node in map.Nodes)
            {
                RoomContents room = fog.Observe(node.Id);
                Assert.True(room.Validate(out string problem),
                    $"node {node.Id} of map {index}: {problem}");
            }
        }
    }

    [Fact]
    public void ANodeThatAdvertisesLootContainsSomethingToFind()
    {
        // Fog may hide a node until the player looks, but once the interior exists it
        // has to keep the promise the node's sight made. Otherwise "has loot" is a
        // lie the simulation tells.
        for (int index = 0; index < 200; index++)
        {
            MissionMap map = Map((ulong)index + 500, index + 1, 3);
            FogOfWar fog = new(map);

            foreach (MissionNode node in map.Nodes)
            {
                if (!node.HasLoot)
                    continue;

                RoomContents room = fog.Observe(node.Id);
                Assert.NotEmpty(room.OfType(InteractableType.Container));
            }
        }
    }

    [Fact]
    public void ASecurityNodeAlwaysHoldsAGuard()
    {
        int checkedNodes = 0;

        for (int index = 0; index < 150; index++)
        {
            MissionMap map = Map((ulong)index + 900, index + 1, 1 + (index % 3));
            FogOfWar fog = new(map);

            foreach (MissionNode node in map.Nodes)
            {
                if (!node.IsSecurity)
                    continue;

                RoomContents room = fog.Observe(node.Id);
                Assert.NotEmpty(room.OfType(InteractableType.Guard));
                checkedNodes++;
            }
        }

        Assert.True(checkedNodes > 0, "no security nodes were generated to check");
    }

    [Fact]
    public void ATerminalNodeAlwaysHoldsATerminal()
    {
        int checkedNodes = 0;

        for (int index = 0; index < 150; index++)
        {
            MissionMap map = Map((ulong)index + 1300, index + 1, 1 + (index % 3));
            FogOfWar fog = new(map);

            foreach (MissionNode node in map.Nodes)
            {
                if (!node.Tags.Contains("terminal"))
                    continue;

                RoomContents room = fog.Observe(node.Id);
                Assert.NotEmpty(room.OfType(InteractableType.Terminal));
                checkedNodes++;
            }
        }

        Assert.True(checkedNodes > 0, "no terminal nodes were generated to check");
    }

    // ---- determinism of lazy generation --------------------------------------

    [Fact]
    public void ANodesInteriorDoesNotDependOnWhichOtherNodesWereObservedFirst()
    {
        // The reason interiors are seeded by MapSeed + nodeId rather than drawn from
        // a stream. If they were, walking into room A then room B would fill room B
        // differently depending on what was in room A, and a save reloaded later
        // would produce a different building.
        MissionMap map = Map(31337UL, 5, 3);
        MissionNode target = map.NodesInLayer(3).First();

        var forward = new FogOfWar(map);
        foreach (MissionNode node in map.Nodes)
            forward.Observe(node.Id);

        // Now generate the same node into a fog that has observed nothing but the
        // entry, on a map object of its own.
        MissionMap mapAgain = Map(31337UL, 5, 3);
        var backward = new FogOfWar(mapAgain);
        RoomContents alone = backward.Observe(target.Id);

        Assert.Equal(
            forward.Observe(target.Id).Interactables.Select(Describe),
            alone.Interactables.Select(Describe));
    }

    [Fact]
    public void TheSameMapSeedProducesTheSameInteriorsEveryTime()
    {
        MissionMap map = Map(555UL, 9, 2);
        MissionNode target = map.NodesInLayer(2).First();

        var first = new FogOfWar(map).Observe(target.Id);
        var second = new FogOfWar(Map(555UL, 9, 2)).Observe(target.Id);

        Assert.Equal(
            first.Interactables.Select(Describe),
            second.Interactables.Select(Describe));
    }

    [Fact]
    public void ADifferentMissionProducesDifferentInteriorsForTheSameRoomType()
    {
        // The counterweight to the two tests above: if interiors were keyed only by
        // room type, every office in the game would be the same office.
        MissionMap first = Map(1UL, 1, 2);
        MissionMap second = Map(1UL, 2, 2);

        var a = new FogOfWar(first).Observe(first.NodesInLayer(2).First().Id);
        var b = new FogOfWar(second).Observe(second.NodesInLayer(2).First().Id);

        Assert.NotEqual(a.Interactables.Count + a.SlotCount,
                        b.Interactables.Count + b.SlotCount);
    }

    // ---- helpers -------------------------------------------------------------

    private static string Describe(Interactable interactable)
        => $"{interactable.Type}:{interactable.SlotIndex}:{interactable.State}";

    /// <summary>Hop distance between two nodes, or -1 when unreachable.</summary>
    private static int HopsFrom(MissionMap map, MissionNodeId from, MissionNodeId to)
    {
        if (from == to) return 0;

        var seen = new HashSet<MissionNodeId> { from };
        var frontier = new Queue<MissionNodeId>();
        frontier.Enqueue(from);
        int distance = 0;

        while (frontier.Count > 0)
        {
            distance++;
            int level = frontier.Count;

            for (int i = 0; i < level; i++)
            {
                MissionNodeId current = frontier.Dequeue();

                foreach (MissionNodeId next in map.OutboundFrom(current))
                {
                    if (next == to) return distance;
                    if (seen.Add(next))
                        frontier.Enqueue(next);
                }

                foreach (MissionNodeId previous in map.InboundTo(current))
                {
                    if (previous == to) return distance;
                    if (seen.Add(previous))
                        frontier.Enqueue(previous);
                }
            }
        }

        return -1;
    }
}
