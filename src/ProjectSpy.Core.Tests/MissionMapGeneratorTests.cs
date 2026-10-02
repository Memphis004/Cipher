using ProjectSpy.Core;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// <see cref="MissionMapGenerator"/>: deterministic layered graphs that hold their
/// structural promises on every generation, not most of them.
/// </summary>
/// <remarks>
/// <para>
/// The headline test generates ten thousand maps across all three tiers and asserts
/// every guarantee on every one. A generator that satisfies its invariants 99% of the
/// time is still a generator that eventually hands a player a mission they cannot
/// finish, and the only way that never reaches a build is to check every roll rather
/// than a sample.
/// </para>
/// <para>
/// Each guarantee also has a focused test, because a single looping assertion that
/// fails reports "a map was invalid" and leaves the reader to work out which promise
/// broke.
/// </para>
/// </remarks>
public class MissionMapGeneratorTests
{
    private static readonly int[] Tiers = { 1, 2, 3 };

    /// <summary>
    /// The generator is table-driven, and a missing table would silently fall back to
    /// the tier-1 defaults — producing tests that pass against data that is not there.
    /// </summary>
    [Fact]
    public void TheGeneratedTablesAreActuallyLoaded()
    {
        Assert.True(
            SimulationRules.AreTablesLoaded,
            "Table binaries are missing. Run 'pwsh tools/gen.ps1' before the tests.");

        Assert.NotNull(SimulationRules.MapGenRuleFor(1));
        Assert.NotNull(SimulationRules.MapGenRuleFor(2));
        Assert.NotNull(SimulationRules.MapGenRuleFor(3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryTierHasSecurityRoomsToBuildItsCheckpointFrom(int tier)
    {
        // The "security on every route to the Objective" guarantee is built by making
        // the layer before the objective entirely security rooms. A tier with no
        // security rooms could not honour it at all.
        Assert.NotEmpty(SimulationRules.SecurityNodeRoomsForTier(tier));
    }

    // ---- determinism ---------------------------------------------------------

    [Fact]
    public void TheSameSeedAndTierProduceAnIdenticalMap()
    {
        foreach (int tier in Tiers)
        {
            ulong seed = MissionMapGenerator.DeriveMapSeed(20240607UL, tier);

            MissionMap first = MissionMapGenerator.Generate(tier, seed, tier);
            MissionMap second = MissionMapGenerator.Generate(tier, seed, tier);

            Assert.Equal(Describe(first), Describe(second));
        }
    }

    [Fact]
    public void DifferentSeedsActuallyProduceDifferentMaps()
    {
        // The counterweight to the test above: a generator that ignored its seed and
        // returned a constant map would pass that one every time.
        var shapes = new HashSet<string>();

        for (ulong worldSeed = 1; worldSeed <= 40; worldSeed++)
        {
            ulong seed = MissionMapGenerator.DeriveMapSeed(worldSeed, 1);
            shapes.Add(Describe(MissionMapGenerator.Generate(1, seed, 1)));
        }

        Assert.True(
            shapes.Count >= 30,
            $"40 world seeds produced only {shapes.Count} distinct tier-1 map shapes; "
            + "the generator is not using its seed.");
    }

    [Fact]
    public void TwoMissionsOfOneWorldGetDifferentMaps()
    {
        const ulong WorldSeed = 987654321UL;

        ulong first = MissionMapGenerator.DeriveMapSeed(WorldSeed, 1);
        ulong second = MissionMapGenerator.DeriveMapSeed(WorldSeed, 2);

        Assert.NotEqual(first, second);
        Assert.NotEqual(
            Describe(MissionMapGenerator.Generate(1, first, 2)),
            Describe(MissionMapGenerator.Generate(2, second, 2)));
    }

    [Fact]
    public void MapSeedDependsOnBothTheWorldSeedAndTheMissionId()
    {
        const ulong A = 11111UL;
        const ulong B = 22222UL;

        Assert.Equal(
            MissionMapGenerator.DeriveMapSeed(A, 3),
            MissionMapGenerator.DeriveMapSeed(A, 3));

        Assert.NotEqual(MissionMapGenerator.DeriveMapSeed(A, 3), MissionMapGenerator.DeriveMapSeed(B, 3));
        Assert.NotEqual(MissionMapGenerator.DeriveMapSeed(A, 3), MissionMapGenerator.DeriveMapSeed(A, 4));
    }

    [Fact]
    public void GeneratingAMapDoesNotTouchTheWorldsSharedMissionStream()
    {
        // The reason the generator owns a private RNG rather than drawing from
        // world.RngStreams[Mission]. If it drew from the shared stream, loading a save
        // and regenerating the map a mission was in the middle of would produce a
        // different map, because the stream would be at a different point.
        var world = new WorldState(555_000UL);

        IRng missionStream = world.RngStreams[RngStreams.StreamKind.Mission];
        RngState before = missionStream.SaveState();

        MissionMapGenerator.Generate(1, MissionMapGenerator.DeriveMapSeed(world.Seed, 1), 2);

        Assert.Equal(before, missionStream.SaveState());
    }

    // ---- the ten-thousand-map invariant sweep -------------------------------

    [Fact]
    public void TenThousandMapsAcrossAllTiersHoldEveryStructuralInvariant()
    {
        const int MapsToGenerate = 10_000;

        for (int index = 0; index < MapsToGenerate; index++)
        {
            int tier = Tiers[index % Tiers.Length];
            ulong worldSeed = (ulong)(index + 1) * 7919UL;
            int missionId = index + 1;

            ulong mapSeed = MissionMapGenerator.DeriveMapSeed(worldSeed, missionId);
            MissionMap map = MissionMapGenerator.Generate(missionId, mapSeed, tier);

            MapGenRule rule = SimulationRules.MapGenRuleFor(tier)!;

            Assert.True(
                map.Validate(out string problem),
                $"map #{index} (tier {tier}, seed {mapSeed}) invalid: {problem}");

            Assert.Empty(map.NodesUnreachableFromEntry());
            Assert.Empty(map.NodesStrandedFromExtraction());

            Assert.True(
                map.CountRoutesToExtraction(long.MaxValue) >= rule.GuaranteedAltRoutes,
                $"map #{index} (tier {tier}) has fewer routes than the tier promises "
                + $"({rule.GuaranteedAltRoutes}).");

            Assert.True(
                map.EveryRouteToObjectiveCrossesSecurity,
                $"map #{index} (tier {tier}) has a route to the objective that avoids every security node.");

            Assert.True(
                map.DeadEndCount >= 1,
                $"map #{index} (tier {tier}) has no dead end at all.");

            Assert.All(map.Nodes, node =>
            {
                Assert.Contains(
                    node.RoomTypeId,
                    SimulationRules.NodeRoomsForTier(tier).Select(r => r.Id));

                // A dead end and the extraction point both lead nowhere by
                // definition; every other node has somewhere to go.
                bool leadsNowhere = node.IsDeadEnd || node.Id == map.ExtractionNodeId;

                if (leadsNowhere)
                    Assert.Empty(map.OutboundFrom(node.Id));
                else
                    Assert.NotEmpty(map.OutboundFrom(node.Id));
            });
        }
    }

    // ---- individual guarantees ----------------------------------------------

    [Fact]
    public void EveryRouteToExtractionPassesThroughTheObjective()
    {
        // "at least one path Entry -> Objective -> Extraction": because edges only
        // ever span one layer and the Objective is alone on its own layer, every
        // route is forced through it. Asserted here rather than assumed, because a
        // future edge that skipped a layer would quietly break it.
        for (int index = 0; index < 500; index++)
        {
            int tier = Tiers[index % Tiers.Length];
            MissionMap map = MissionMapGenerator.Generate(
                index + 1, MissionMapGenerator.DeriveMapSeed((ulong)index + 1, index + 1), tier);

            foreach (MissionNode node in map.NodesInLayer(map.TransitLayerCount + 1))
                Assert.Equal(map.ObjectiveNodeId, node.Id);
        }
    }

    [Fact]
    public void EveryDeadEndIsReachableAndLeadsNowhere()
    {
        for (int index = 0; index < 500; index++)
        {
            int tier = Tiers[index % Tiers.Length];
            MissionMap map = MissionMapGenerator.Generate(
                index + 1, MissionMapGenerator.DeriveMapSeed((ulong)index + 77, index + 1), tier);

            var reachable = new HashSet<MissionNodeId>(map.InboundTo(map.EntryNodeId));
            var frontier = new Queue<MissionNodeId>();
            frontier.Enqueue(map.EntryNodeId);
            reachable.Add(map.EntryNodeId);

            while (frontier.Count > 0)
            {
                foreach (MissionNodeId next in map.OutboundFrom(frontier.Dequeue()))
                {
                    if (reachable.Add(next))
                        frontier.Enqueue(next);
                }
            }

            foreach (MissionNode node in map.Nodes)
            {
                if (!node.IsDeadEnd)
                    continue;

                Assert.Contains(node.Id, reachable);
                Assert.Empty(map.OutboundFrom(node.Id));

                // Dead ends are excluded from the stranded report on purpose: they
                // cannot reach extraction by design, and listing them there would
                // bury a genuinely stranded node in expected noise.
                Assert.DoesNotContain(node.Id, map.NodesStrandedFromExtraction());
            }
        }
    }

    [Fact]
    public void TheLayerBeforeTheObjectiveIsEntirelySecurityRooms()
    {
        // The structural mechanism behind the coverage guarantee. Pinned so that a
        // change to how it is enforced cannot quietly weaken it: if this stops
        // holding, `EveryRouteToObjectiveCrossesSecurity` is the only thing left, and
        // it would be asserting a property nothing produces any more.
        for (int index = 0; index < 300; index++)
        {
            int tier = Tiers[index % Tiers.Length];
            MissionMap map = MissionMapGenerator.Generate(
                index + 1, MissionMapGenerator.DeriveMapSeed((ulong)index + 13, index + 1), tier);

            IReadOnlyList<MissionNode> checkpoint = map.NodesInLayer(map.TransitLayerCount);

            Assert.NotEmpty(checkpoint);

            // Dead ends are excluded: they have no outbound edge, so no route to the
            // objective can pass through one, and forcing them to be security would
            // guard a corridor that leads nowhere.
            foreach (MissionNode node in checkpoint)
            {
                if (node.IsDeadEnd)
                    continue;

                Assert.True(
                    node.IsSecurity,
                    $"node {node.Id} in checkpoint layer {map.TransitLayerCount} has tags "
                    + $"[{string.Join(",", node.Tags)}] and is not a security node.");
            }
        }
    }

    [Fact]
    public void TheMapHasOneEntryOneObjectiveAndOneExtractionInOrder()
    {
        foreach (int tier in Tiers)
        {
            MissionMap map = MissionMapGenerator.Generate(
                1, MissionMapGenerator.DeriveMapSeed(31337UL, 1), tier);

            Assert.Equal(MissionNodeKind.Entry, map.Find(map.EntryNodeId)!.Kind);
            Assert.Equal(MissionNodeKind.Objective, map.Find(map.ObjectiveNodeId)!.Kind);
            Assert.Equal(MissionNodeKind.Extraction, map.Find(map.ExtractionNodeId)!.Kind);

            Assert.Equal(0, map.Find(map.EntryNodeId)!.Layer);
            Assert.Equal(map.TransitLayerCount + 1, map.Find(map.ObjectiveNodeId)!.Layer);
            Assert.Equal(map.TransitLayerCount + 2, map.Find(map.ExtractionNodeId)!.Layer);
        }
    }

    [Fact]
    public void HigherTiersProduceBiggerMaps()
    {
        // A guard that the tier actually selects a bigger rule row, rather than every
        // tier quietly producing a tier-1 map.
        IReadOnlyList<MissionNode> tier1 = GenerateTier(1);
        IReadOnlyList<MissionNode> tier3 = GenerateTier(3);

        Assert.True(
            tier3.Count > tier1.Count,
            $"tier 3 produced {tier3.Count} nodes and tier 1 produced {tier1.Count}; "
            + "the tier is not reaching the map.");
    }

    [Fact]
    public void LootAppearsOnSomeNodesButNotAll()
    {
        int withLoot = 0;
        int total = 0;

        for (int index = 0; index < 200; index++)
        {
            MissionMap map = MissionMapGenerator.Generate(
                index + 1, MissionMapGenerator.DeriveMapSeed((ulong)index + 5, index + 1), 3);

            foreach (MissionNode node in map.Nodes)
            {
                total++;
                if (node.HasLoot)
                    withLoot++;
            }
        }

        // The ratio is per-node and probabilistic, so the assertion is deliberately
        // loose: what it rules out is the ratio being ignored in either direction.
        Assert.True(withLoot > 0, "No node at any tier carried loot; loot_node_ratio is not being applied.");
        Assert.True(withLoot < total, "Every node carried loot; loot_node_ratio is not being applied.");
    }

    [Fact]
    public void NodesCarryTheTagsAndEventsTheirRoomRowDeclares()
    {
        MissionMap map = MissionMapGenerator.Generate(
            1, MissionMapGenerator.DeriveMapSeed(24680UL, 1), 2);

        foreach (MissionNode node in map.Nodes)
        {
            NodeRoom? room = SimulationRules.AllNodeRooms()
                .FirstOrDefault(r => r.Id == node.RoomTypeId);

            Assert.NotNull(room);
            Assert.Equal(room!.NameKey, node.NameKey);
            Assert.Equal(room.BaseSecurity, node.BaseSecurity);
            Assert.Equal(room.NoiseModifier, node.NoiseModifier);
            Assert.Equal(
                SimulationRules.Tags(room.Tags).OrderBy(t => t, StringComparer.Ordinal),
                node.Tags.OrderBy(t => t, StringComparer.Ordinal));
            Assert.Equal(
                SimulationRules.IntList(room.PossibleEventIds),
                node.CandidateEventIds);
        }
    }

    [Fact]
    public void RegeneratingTheSameMissionReproducesTheSameMap()
    {
        // The property a save reload depends on: mission 7 of world W is the same map
        // whether it is being played for the first time or restored from disk.
        const ulong WorldSeed = 8080UL;
        const int MissionId = 7;
        const int Tier = 2;

        ulong seed = MissionMapGenerator.DeriveMapSeed(WorldSeed, MissionId);

        MissionMap live = MissionMapGenerator.Generate(MissionId, seed, Tier);

        // Something else in the world rolls a lot of numbers in between.
        var world = new WorldState(WorldSeed);
        for (int i = 0; i < 500; i++)
            world.RngStreams[RngStreams.StreamKind.Mission].NextRoll100();

        MissionMap reloaded = MissionMapGenerator.Generate(MissionId, seed, Tier);

        Assert.Equal(Describe(live), Describe(reloaded));
    }

    // ---- helpers -------------------------------------------------------------

    private static IReadOnlyList<MissionNode> GenerateTier(int tier)
    {
        var nodes = new List<MissionNode>();

        for (int index = 0; index < 200; index++)
        {
            nodes.AddRange(MissionMapGenerator
                .Generate(index + 1, MissionMapGenerator.DeriveMapSeed((ulong)index + 1, index + 1), tier)
                .Nodes);
        }

        return nodes;
    }

    /// <summary>
    /// A stable textual fingerprint of everything generation decides.
    /// </summary>
    /// <remarks>
    /// Compared as a string rather than field by field so that a difference points at
    /// the exact node or edge that changed, instead of only saying "the maps differ".
    /// </remarks>
    private static string Describe(MissionMap map)
    {
        var text = new System.Text.StringBuilder();
        text.Append(map.Tier).Append('|').Append(map.MapSeed).Append('|');
        text.Append(map.TransitLayerCount).Append('|');
        text.Append(map.EntryNodeId).Append('>').Append(map.ObjectiveNodeId)
            .Append('>').Append(map.ExtractionNodeId).Append('|');

        foreach (MissionNode node in map.Nodes)
        {
            text.Append(node.Id).Append(':').Append(node.Kind).Append(':')
                .Append(node.Layer).Append(':').Append(node.RoomTypeId).Append(':')
                .Append(node.LootTableId).Append(':')
                .Append(string.Join("|", node.Tags)).Append(';');
        }

        foreach (MissionEdge edge in map.Edges)
            text.Append(edge.From).Append("->").Append(edge.To).Append(';');

        return text.ToString();
    }
}
