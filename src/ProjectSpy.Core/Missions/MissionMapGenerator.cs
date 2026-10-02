using ProjectSpy.Tables;

namespace ProjectSpy.Core;

/// <summary>
/// Builds a mission map deterministically from <c>map_gen_rule</c> and
/// <c>node_room</c>.
/// </summary>
/// <remarks>
/// <para>
/// The generator is the only place a map's shape is decided, and it decides it from
/// exactly two inputs: the tier's rule row and a seed derived from
/// <see cref="DeriveMapSeed"/>. Nothing else — not the world's Mission RNG stream,
/// not the clock, not the roster — can influence the result.
/// </para>
/// <para>
/// That isolation is the whole point. The world's shared Mission stream advances
/// with every roll any other system makes, so generating a map from it would mean a
/// save reloaded at a different moment in the run produced a different map. A private
/// generator seeded from <c>MapSeed</c> means mission 7 of a given world is the same
/// map every time, in the simulator, in a replay, and in the shipping game
/// (knowledge.md rule 6).
/// </para>
/// <para>
/// Every guarantee the brief asks for is structural rather than probabilistic — the
/// generator makes them true by construction and then asserts them with
/// <see cref="MissionMap.Validate"/>, so a bad roll cannot produce an unplayable map.
/// </para>
/// </remarks>
public static class MissionMapGenerator
{
    /// <summary>
    /// Derives the map seed for one mission of one world.
    /// </summary>
    /// <remarks>
    /// <c>WorldSeed + missionId</c>, avalanched through the same mixing routine that
    /// derives RNG streams. Reusing <see cref="RngStreams.DeriveSeed"/> rather than
    /// writing a second mixer means there is one documented way a seed is perturbed,
    /// so two subsystems cannot drift into subtly different derivations.
    /// </remarks>
    public static ulong DeriveMapSeed(ulong worldSeed, int missionId)
        => RngStreams.DeriveSeed(worldSeed, missionId);

    /// <summary>
    /// Generates a map.
    /// </summary>
    /// <param name="missionId">Mission this map belongs to. Appears in the result only.</param>
    /// <param name="mapSeed">
    /// Seed from <see cref="DeriveMapSeed"/>. Two calls with the same seed and tier
    /// produce identical maps.
    /// </param>
    /// <param name="tier">Tier from <c>mission_type</c>; selects the <c>map_gen_rule</c> row.</param>
    public static MissionMap Generate(int missionId, ulong mapSeed, int tier)
    {
        MapGenRule? rule = SimulationRules.MapGenRuleFor(tier);

        int transitLayers = rule?.LayerCount ?? DefaultTransitLayers;
        int nodesPerLayerMin = rule?.NodesPerLayerMin ?? DefaultNodesPerLayerMin;
        int nodesPerLayerMax = rule?.NodesPerLayerMax ?? DefaultNodesPerLayerMax;
        int branchChance = rule?.BranchChance ?? DefaultBranchChance;
        int deadEndChance = rule?.DeadEndChance ?? DefaultDeadEndChance;
        int securityRatio = rule?.SecurityRoomRatio ?? DefaultSecurityRoomRatio;
        int lootRatio = rule?.LootNodeRatio ?? DefaultLootNodeRatio;
        int requiredRoutes = rule?.GuaranteedAltRoutes ?? DefaultGuaranteedAltRoutes;

        // A private generator, never the world's shared Mission stream.
        var rng = new XorShift128Rng(mapSeed);

        IReadOnlyList<NodeRoom> rooms = SimulationRules.NodeRoomsForTier(tier);
        IReadOnlyList<NodeRoom> securityRooms = SimulationRules.SecurityNodeRoomsForTier(tier);

        if (rooms.Count == 0)
        {
            throw new InvalidOperationException(
                $"No node_room rows are usable at tier {tier}. The map cannot be generated; "
                + "this is a table data error, not a runtime condition to recover from.");
        }

        var map = new MissionMap
        {
            MissionId = missionId,
            Tier = tier,
            MapSeed = mapSeed,
            TransitLayerCount = transitLayers,
        };

        // Layer 0 is Entry, layers 1..transitLayers are transit, transitLayers+1 is the
        // Objective and transitLayers+2 is Extraction.
        int checkpointLayer = transitLayers;
        int nextId = 1;

        MissionNodeId entryId = new(nextId++);
        map.EntryNodeId = entryId;
        map.AddNode(CreateNode(
            rng, rooms, securityRooms, entryId, MissionNodeKind.Entry, 0, checkpointLayer, securityRatio));

        // The spine: one node list per transit layer, built before any edge exists.
        var transitLayerNodes = new List<List<MissionNodeId>>(transitLayers);

        for (int layer = 1; layer <= transitLayers; layer++)
        {
            int count = rng.NextInt(nodesPerLayerMin, nodesPerLayerMax + 1);
            var layerNodes = new List<MissionNodeId>(count);

            for (int i = 0; i < count; i++)
            {
                var id = new MissionNodeId(nextId++);
                layerNodes.Add(id);
                map.AddNode(CreateNode(
                    rng, rooms, securityRooms, id, MissionNodeKind.Transit, layer, checkpointLayer, securityRatio));
            }

            transitLayerNodes.Add(layerNodes);
        }

        MissionNodeId objectiveId = new(nextId++);
        map.ObjectiveNodeId = objectiveId;
        map.AddNode(CreateNode(
            rng, rooms, securityRooms, objectiveId, MissionNodeKind.Objective,
            transitLayers + 1, checkpointLayer, securityRatio));

        MissionNodeId extractionId = new(nextId++);
        map.ExtractionNodeId = extractionId;
        map.AddNode(CreateNode(
            rng, rooms, securityRooms, extractionId, MissionNodeKind.Extraction,
            transitLayers + 2, checkpointLayer, securityRatio));

        // The ordered spine, which the connection passes work from.
        var layers = new List<List<MissionNodeId>>(transitLayers + 3)
        {
            new List<MissionNodeId> { entryId },
        };
        layers.AddRange(transitLayerNodes);
        layers.Add(new List<MissionNodeId> { objectiveId });
        layers.Add(new List<MissionNodeId> { extractionId });

        ConnectLayers(map, rng, layers, branchChance);
        AddDeadEnds(map, rng, transitLayerNodes, rooms, deadEndChance, securityRooms, securityRatio);
        AssignLoot(map, rng, rooms, lootRatio);
        GuaranteeRouteCount(map, rng, layers, requiredRoutes);

        if (!map.Validate(out string problem))
        {
            // Unreachable by construction; if it ever fires, the generator has a bug
            // and a map that violates its own guarantees must not reach a caller.
            throw new InvalidOperationException(
                $"Generated map for mission {missionId} tier {tier} failed validation: {problem}");
        }

        return map;
    }

    /// <summary>
    /// Links consecutive layers so that every node has an outbound edge and every
    /// node has an inbound one.
    /// </summary>
    /// <remarks>
    /// Two passes rather than one: giving every child a parent first guarantees no
    /// node is unreachable, and then giving any parent left with no child guarantees
    /// nothing is stranded. Either pass alone leaves orphans or stranded nodes behind.
    /// </remarks>
    private static void ConnectLayers(
        MissionMap map,
        IRng rng,
        List<List<MissionNodeId>> layers,
        int branchChance)
    {
        for (int layer = 0; layer < layers.Count - 1; layer++)
        {
            List<MissionNodeId> from = layers[layer];
            List<MissionNodeId> to = layers[layer + 1];

            // Every child gets a parent.
            foreach (MissionNodeId child in to)
                map.AddEdge(rng.Pick(from), child);

            // Every parent gets a child.
            foreach (MissionNodeId parent in from)
            {
                if (map.OutboundFrom(parent).Count == 0)
                    map.AddEdge(parent, rng.Pick(to));
            }

            // Branching: an extra edge here and there turns a corridor into a site
            // with a choice in it, and is the main source of alternate routes.
            foreach (MissionNodeId parent in from)
            {
                if (to.Count <= 1 || rng.NextInt(1, 101) > branchChance)
                    continue;

                MissionNodeId extra = rng.Pick(to);
                if (!map.HasEdge(parent, extra))
                    map.AddEdge(parent, extra);
            }
        }
    }

    /// <summary>
    /// Hangs reachable dead ends off transit nodes.
    /// </summary>
    /// <remarks>
    /// Dead ends are added after the spine is complete and attach to a node that
    /// already has an inbound edge, so they are reachable by construction and can
    /// never become orphans. At least one is forced: the map is meant to offer
    /// somewhere worth going that does not advance the mission, and "probably" is not
    /// a guarantee a designer can rely on.
    /// </remarks>
    /// <remarks>
    /// A dead end sits one layer <em>deeper</em> than the node it hangs off, because
    /// every edge spans exactly one layer. Putting it alongside its parent would make
    /// a same-layer edge, which is not a graph this generator is allowed to build.
    /// </remarks>
    private static void AddDeadEnds(
        MissionMap map,
        IRng rng,
        List<List<MissionNodeId>> transitLayerNodes,
        IReadOnlyList<NodeRoom> rooms,
        int deadEndChance,
        IReadOnlyList<NodeRoom> securityRooms,
        int securityRatio)
    {
        int nextId = map.Nodes.Count + 1;
        int placed = 0;

        for (int layer = 0; layer < transitLayerNodes.Count; layer++)
        {
            // The last transit layer is the security checkpoint, and the layer past
            // it is the objective's own. Hanging a dead end off either would put a
            // node in the objective's layer — harmless to the route graph, but a room
            // beside the objective that goes nowhere is not a thing a player should
            // have to interpret.
            if (layer == transitLayerNodes.Count - 1)
                continue;

            foreach (MissionNodeId parent in transitLayerNodes[layer])
            {
                if (rng.NextInt(1, 101) > deadEndChance)
                    continue;

                var id = new MissionNodeId(nextId++);
                map.AddNode(CreateNode(
                    rng, rooms, securityRooms, id, MissionNodeKind.DeadEnd,
                    layer + 2, DeadEndCheckpointSentinel, securityRatio));

                // Inward edge only: a dead end leads nowhere, which is what makes it
                // one.
                map.AddEdge(parent, id);
                placed++;
            }
        }

        if (placed > 0 || transitLayerNodes.Count == 0)
            return;

        // Forced minimum, so the map always has somewhere to divert to.
        var forcedId = new MissionNodeId(nextId);
        map.AddNode(CreateNode(
            rng, rooms, securityRooms, forcedId, MissionNodeKind.DeadEnd, 2, DeadEndCheckpointSentinel, securityRatio));
        map.AddEdge(transitLayerNodes[0][0], forcedId);
    }

    /// <summary>Rolls each node's loot from the tier's <c>loot_node_ratio</c>.</summary>
    private static void AssignLoot(
        MissionMap map,
        IRng rng,
        IReadOnlyList<NodeRoom> rooms,
        int lootRatio)
    {
        foreach (MissionNode node in map.Nodes)
        {
            if (rng.NextInt(1, 101) > lootRatio)
                continue;

            // The room carries the loot table; the ratio decides whether this
            // instance of the room actually exposes it.
            foreach (NodeRoom room in rooms)
            {
                if (room.Id == node.RoomTypeId)
                {
                    node.AssignLoot(room.LootTableId);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Adds edges until the map has at least as many Entry-to-Extraction routes as the
    /// tier promises.
    /// </summary>
    /// <remarks>
    /// Every new edge strictly increases the route count — each layer already has a
    /// path in and a path out, so one more connection contributes at least one new
    /// complete route. Topping up after the fact makes the guarantee a guarantee
    /// rather than a consequence of how the dice fell.
    /// </remarks>
    private static void GuaranteeRouteCount(
        MissionMap map,
        IRng rng,
        List<List<MissionNodeId>> layers,
        int requiredRoutes)
    {
        if (requiredRoutes <= 1 || map.CountRoutesToExtraction(requiredRoutes) >= requiredRoutes)
            return;

        int attempts = 0;
        int maxAttempts = layers.Count * MaxRouteTopUpAttemptsPerLayer;

        while (attempts++ < maxAttempts &&
               map.CountRoutesToExtraction(requiredRoutes) < requiredRoutes)
        {
            int layer = rng.NextInt(0, layers.Count - 1);
            List<MissionNodeId> from = layers[layer];
            List<MissionNodeId> to = layers[layer + 1];

            MissionNodeId parent = rng.Pick(from);
            MissionNodeId child = rng.Pick(to);

            if (!map.HasEdge(parent, child))
                map.AddEdge(parent, child);
        }
    }

    /// <summary>
    /// Assigns a node's room type, honouring the checkpoint rule and the security
    /// ratio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layer immediately before the objective is a security cut. Because edges
    /// only ever span one layer, every Entry-to-Objective route has to cross it; and
    /// because every spine node in it is a security room, every such route meets a
    /// guard. That is the GDD's "a Security node on every route to the Objective"
    /// made structural rather than hoped-for.
    /// </para>
    /// <para>
    /// <c>security_room_ratio</c> then applies to the transit layers either side of
    /// it, so a map has guards the player can route around as well as the checkpoint
    /// they cannot.
    /// </para>
    /// <para>
    /// The three endpoints stay non-security: an entry or extraction point that was
    /// sometimes a guard post would make the map's start and finish unpredictable,
    /// and the player has to be able to rely on both.
    /// </para>
    /// </remarks>
    private static MissionNode CreateNode(
        IRng rng,
        IReadOnlyList<NodeRoom> rooms,
        IReadOnlyList<NodeRoom> securityRooms,
        MissionNodeId id,
        MissionNodeKind kind,
        int layer,
        int checkpointLayer,
        int securityRatio)
    {
        bool canBeSecurity = kind is MissionNodeKind.Transit or MissionNodeKind.DeadEnd;
        bool isCheckpoint = layer == checkpointLayer && canBeSecurity;

        bool useSecurity = securityRooms.Count > 0
                           && (isCheckpoint
                               || (canBeSecurity && rng.NextInt(1, 101) <= securityRatio));

        NodeRoom room = useSecurity ? WeightedRoom(rng, securityRooms) : WeightedRoom(rng, rooms);

        return new MissionNode
        {
            Id = id,
            Kind = kind,
            Layer = layer,
            RoomTypeId = room.Id,
            NameKey = room.NameKey,
            Tags = SimulationRules.Tags(room.Tags),
            BaseSecurity = room.BaseSecurity,
            NoiseModifier = room.NoiseModifier,
            CandidateEventIds = SimulationRules.IntList(room.PossibleEventIds),
        };
    }

    private static NodeRoom WeightedRoom(IRng rng, IReadOnlyList<NodeRoom> rooms)
    {
        var weights = new int[rooms.Count];
        for (int i = 0; i < rooms.Count; i++)
            weights[i] = Math.Max(0, rooms[i].Weight);

        return rng.WeightedPick(rooms, weights);
    }

    /// <summary>
    /// Last-resort fallbacks used only when a <c>map_gen_rule</c> row is missing.
    /// </summary>
    /// <remarks>
    /// These are not balance numbers — they are what generation does when the data
    /// that would tell it otherwise is absent, which the table validator is meant to
    /// prevent. They match the tier-1 row, so a missing table degrades to a small but
    /// valid map rather than throwing mid-mission.
    /// </remarks>
    private const int DefaultTransitLayers = 3;
    private const int DefaultNodesPerLayerMin = 2;
    private const int DefaultNodesPerLayerMax = 3;
    private const int DefaultBranchChance = 35;
    private const int DefaultDeadEndChance = 20;
    private const int DefaultSecurityRoomRatio = 25;
    private const int DefaultLootNodeRatio = 15;
    private const int DefaultGuaranteedAltRoutes = 1;

    /// <summary>
    /// How many edges a single layer may gain while topping up the route count.
    /// </summary>
    /// <remarks>
    /// A bound rather than a while-true: if a tier were ever too small to hold the
    /// number of routes it promises, generation must still terminate and hand back a
    /// valid map with fewer routes rather than spin. The invariant test then reports
    /// the shortfall instead of the build hanging.
    /// </remarks>
    private const int MaxRouteTopUpAttemptsPerLayer = 64;

    /// <summary>
    /// Passed as the checkpoint layer when creating dead ends, so they are never
    /// forced to be security rooms.
    /// </summary>
    /// <remarks>
    /// A dead end in the checkpoint layer has no outbound edge, so it can never be
    /// part of a route to the objective and forcing it to security would achieve
    /// nothing. It goes through the ordinary ratio roll instead, which is what makes
    /// a guarded dead end possible.
    /// </remarks>
    private const int DeadEndCheckpointSentinel = -1;
}
