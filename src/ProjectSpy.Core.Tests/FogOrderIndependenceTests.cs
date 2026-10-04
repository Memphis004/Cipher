using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The two properties that make fog of war safe to build on: exploring a map in a
/// different order must not change what any room contains, and a better infiltrator
/// must never see less.
/// </summary>
/// <remarks>
/// <para>
/// Both are easy to state and easy to break silently. Interiors are generated lazily,
/// which means the obvious implementation — draw from the world's mission stream — would
/// make a room's contents depend on how many rooms the player happened to look at first.
/// Nothing would throw; the same save would just produce a different building on a
/// different playthrough.
/// </para>
/// <para>
/// The guard-count assertions live here too because they are the same kind of claim: a
/// value the player was shown before entering has to still be true afterwards.
/// </para>
/// </remarks>
public class FogOrderIndependenceTests
{
    /// <summary>Maps exercised by the order-independence sweep.</summary>
    private const int OrderIndependenceMaps = 500;

    /// <summary>Distinct visit orders each map is explored in.</summary>
    private const int OrdersPerMap = 5;

    /// <summary>The Infiltration bands the coverage table reports.</summary>
    private static readonly int[] InfiltrationBands = { 20, 40, 60, 80, 100 };

    /// <summary>Maps averaged into each band's coverage figure.</summary>
    private const int CoverageMaps = 120;

    private static MissionMap Map(ulong worldSeed, int missionId, int tier)
        => MissionMapGenerator.Generate(
            missionId, MissionMapGenerator.DeriveMapSeed(worldSeed, missionId), tier);

    /// <summary>
    /// A room's contents must not depend on the order the team entered other rooms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Five different orders per map, and every node visited in each. The orders are
    /// generated from a fixed seed rather than <see cref="Random"/>: the test has to be
    /// reproducible when it fails, or the failure is not something anyone can act on
    /// (knowledge.md rule 6 applies to the harness too, not only to the simulation).
    /// </para>
    /// <para>
    /// Each order is run against a <em>freshly generated map</em>, not a reused one,
    /// because the node objects differ between calls and comparing across them would be
    /// comparing different rooms.
    /// </para>
    /// </remarks>
    [Fact]
    public void InteriorContentsDoNotDependOnTheOrderRoomsAreEntered()
    {
        int nodesChecked = 0;

        for (int index = 0; index < OrderIndependenceMaps; index++)
        {
            int tier = 1 + (index % 3);
            ulong worldSeed = (ulong)index + 1;

            // The reference contents: every node, generated in map order on one fog.
            MissionMap referenceMap = Map(worldSeed, index + 1, tier);
            var reference = new Dictionary<int, string>();

            foreach (MissionNode node in referenceMap.Nodes)
            {
                reference[node.Id.Value] = Describe(new FogOfWar(referenceMap).Observe(node.Id));
            }

            // Five shuffled visit orders, each on its own map and its own fog.
            var orderRng = new XorShift128Rng((ulong)(index * 7919) + 13);

            for (int attempt = 0; attempt < OrdersPerMap; attempt++)
            {
                MissionMap shuffledMap = Map(worldSeed, index + 1, tier);
                var fog = new FogOfWar(shuffledMap);

                foreach (MissionNodeId visitOrder in Shuffled(shuffledMap.Nodes, orderRng))
                {
                    string observed = Describe(fog.Observe(visitOrder));

                    Assert.True(
                        reference.TryGetValue(visitOrder.Value, out string? expected),
                        $"node {visitOrder} of map {index} was generated but not in the reference");

                    Assert.Equal(expected, observed);
                    nodesChecked++;
                }
            }
        }

        Assert.True(
            nodesChecked >= OrderIndependenceMaps * OrdersPerMap,
            $"only checked {nodesChecked} node visits; the sweep did less work than intended");
    }

    /// <summary>
    /// A node's guard count, reported by fog before the team enters, must be the count
    /// the interior actually holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the promise that forced <see cref="MissionNode.GuardCount"/> onto the
    /// map skeleton rather than being read back out of the contents: a Scouted sight
    /// shows a guard band, and deriving that from an interior would mean generating one
    /// for a room nobody entered.
    /// </para>
    /// <para>
    /// The comparison is <c>&lt;=</c>, not <c>==</c>. A room can be promised two guards
    /// and have room for one, and the honest reading of that is "as guarded as this room
    /// can be", not "the simulation lied". The reverse direction is asserted exactly:
    /// an interior may never hold <em>more</em> guards than the skeleton promised,
    /// because that is the direction in which the player is under-informed.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGuardBandFogReportsMatchesTheInteriorItPromised()
    {
        int checkedNodes = 0;
        int exact = 0;

        for (int index = 0; index < 200; index++)
        {
            MissionMap map = Map((ulong)index + 9000, index + 1, 1 + (index % 3));
            var fog = new FogOfWar(map);

            foreach (MissionNode node in map.Nodes)
            {
                RoomContents room = fog.Observe(node.Id);
                int actual = room.OfType(InteractableType.Guard).Count;

                Assert.True(
                    actual <= node.GuardCount,
                    $"node {node.Id} of map {index} holds {actual} guards but its skeleton promised "
                    + $"{node.GuardCount}; the sight would understate the threat");

                if (actual == node.GuardCount)
                    exact++;

                checkedNodes++;
            }
        }

        Assert.True(checkedNodes > 0, "no nodes were generated to check");

        // Nearly every node should honour the count exactly. A low rate would mean the
        // rooms are systematically too small for their templates, which is a content bug
        // rather than a rounding artefact.
        Assert.True(
            exact >= checkedNodes * 0.9,
            $"only {exact} of {checkedNodes} nodes matched their promised guard count exactly; "
            + "room templates are too tight for their guard_max");
    }

    /// <summary>
    /// The fraction of a map a team can see rises with its best infiltrator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Printed as a table because the numbers are the deliverable. A fog-of-war model
    /// that technically increases coverage but only at absurd skill values is not
    /// playable, and "is the curve actually rising at the values players reach" is a
    /// balance question that only a table answers.
    /// </para>
    /// <para>
    /// Asserted as non-decreasing rather than strictly increasing: integer hop radii
    /// mean adjacent bands can legitimately resolve to the same radius, and a strict
    /// assertion would fail on correct arithmetic. The strict part is covered by
    /// asserting the top band beats the bottom one by a real margin.
    /// </para>
    /// </remarks>
    [Fact]
    public void VisibleFractionIncreasesMonotonicallyWithInfiltration()
    {
        var coverage = new Dictionary<int, double>();

        foreach (int band in InfiltrationBands)
        {
            long revealed = 0;
            long total = 0;

            for (int index = 0; index < CoverageMaps; index++)
            {
                MissionMap map = Map((ulong)index + 5000, index + 1, 1 + (index % 3));
                var fog = new FogOfWar(map);

                // A four-agent team at the band's infiltration. Standing at the entry,
                // which is where a mission begins.
                int radius = FogOfWar.ComputeRevealRadius(TeamAt(band));
                fog.RevealAround(
                    map.EntryNodeId,
                    FogOfWar.SilhouetteRadiusFor(radius),
                    FogOfWar.ScoutRadiusFor(radius));

                foreach (MissionNode node in map.Nodes)
                {
                    total++;
                    if (fog.StateOf(node.Id) != MissionVisibility.Hidden)
                        revealed++;
                }
            }

            coverage[band] = (double)revealed / total;
        }

        foreach (int band in InfiltrationBands)
        {
            Console.WriteLine(
                $"  Infiltration {band,3}: visible {coverage[band] * 100.0,5:F1}%  "
                + $"radius {FogOfWar.ComputeRevealRadius(TeamAt(band)),3}");
        }

        for (int i = 1; i < InfiltrationBands.Length; i++)
        {
            int previous = InfiltrationBands[i - 1];
            int current = InfiltrationBands[i];

            Assert.True(
                coverage[current] >= coverage[previous] - 0.0001,
                $"coverage fell from {coverage[previous]:P1} at Infiltration {previous} to "
                + $"{coverage[current]:P1} at {current}; more skill must never see less");
        }

        Assert.True(
            coverage[InfiltrationBands[^1]] > coverage[InfiltrationBands[0]] + 0.05,
            $"the curve is too flat to matter: {coverage[InfiltrationBands[0]]:P1} at Infiltration "
            + $"{InfiltrationBands[0]} against {coverage[InfiltrationBands[^1]]:P1} at "
            + $"{InfiltrationBands[^1]}. Bringing a specialist would barely change what the team sees");
    }

    /// <summary>A four-agent team whose members all have the given Infiltration.</summary>
    private static SkillSet[] TeamAt(int infiltration)
    {
        return new[]
        {
            new SkillSet(infiltration, 0, 0, 0, 0),
            new SkillSet(infiltration, 0, 0, 0, 0),
            new SkillSet(infiltration, 0, 0, 0, 0),
            new SkillSet(infiltration, 0, 0, 0, 0),
        };
    }

    /// <summary>
    /// A copy of <paramref name="nodes"/> in a seeded random order.
    /// </summary>
    /// <remarks>
    /// A partial Fisher-Yates rather than <c>OrderBy(_ =&gt; rng)</c>: the latter evaluates
    /// the key once per comparison, so the permutation depends on how many comparisons
    /// the sort happens to make and is not the shuffle it looks like.
    /// </remarks>
    private static List<MissionNodeId> Shuffled(IReadOnlyList<MissionNode> nodes, IRng rng)
    {
        var ids = new List<MissionNodeId>(nodes.Count);
        foreach (MissionNode node in nodes)
            ids.Add(node.Id);

        for (int i = ids.Count - 1; i > 0; i--)
        {
            int swap = rng.NextInt(0, i + 1);
            (ids[i], ids[swap]) = (ids[swap], ids[i]);
        }

        return ids;
    }

    /// <summary>A canonical description of a room, for order-independence comparison.</summary>
    private static string Describe(RoomContents room)
    {
        var parts = new List<string>(room.Count + 2);

        parts.Add($"slots:{room.SlotCount}");

        // Sorted by id, because the generation order inside a room is itself part of
        // what is being asserted and must not be re-sorted into agreement.
        foreach (Interactable interactable in room.Interactables.OrderBy(i => i.Id.Value))
        {
            parts.Add(
                $"{interactable.Id.Value}:{interactable.Type}:{interactable.SlotIndex}:{interactable.State}:"
                + $"[{string.Join(",", interactable.BlockedSlotIndices.OrderBy(s => s))}]");
        }

        return string.Join("|", parts);
    }
}