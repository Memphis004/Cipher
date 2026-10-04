using ProjectSpy.Core;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The pieces stage 4a added on top of the existing fog rules: the divisor-based
/// radii, the reveal-source extension point, the guard band, and the two tables that
/// decide what a room contains.
/// </summary>
/// <remarks>
/// Separate from <see cref="FogOfWarTests"/> because these are assertions about the
/// shape of the model rather than about what a specific team can see.
/// </remarks>
public class FogVisibilityModelTests
{
    private static MissionMap Map(ulong worldSeed = 4242UL, int missionId = 1, int tier = 2)
        => MissionMapGenerator.Generate(
            missionId, MissionMapGenerator.DeriveMapSeed(worldSeed, missionId), tier);

    // ---- divisor-based radii -------------------------------------------------

    [Fact]
    public void ScoutedAlwaysReachesNoFurtherThanSilhouette()
    {
        // The divisor model only means anything if the two grades stay ordered: a
        // larger divisor is a tighter circle, so scouted_divisor must exceed
        // silhouette_divisor or the stricter state would be the wider one.
        foreach (int reveal in new[] { 1, 10, 25, 40, 100, 400, 5000 })
        {
            int silhouette = FogOfWar.SilhouetteRadiusFor(reveal);
            int scouted = FogOfWar.ScoutRadiusFor(reveal);

            Assert.True(
                scouted <= silhouette,
                $"reveal {reveal}: scouted radius {scouted} exceeds silhouette {silhouette}, "
                + "so the stricter grade reaches further than the looser one");
        }
    }

    [Fact]
    public void EveryTeamSeesAtLeastTheRoomTheDoorLeadsInto()
    {
        // Integer division would give a weak team a silhouette radius of zero, and a
        // team that cannot see the next room cannot route anywhere. The floor is what
        // stops that.
        foreach (int reveal in new[] { 0, 1, 5, 24 })
        {
            Assert.True(
                FogOfWar.SilhouetteRadiusFor(reveal) >= 1,
                $"a team with a reach of {reveal} hops could see nothing at all");
        }
    }

    [Fact]
    public void BothRadiiGrowWithTheTeamsReach()
    {
        int previousSilhouette = -1;
        int previousScouted = -1;

        foreach (int reveal in new[] { 40, 80, 120, 160, 200 })
        {
            int silhouette = FogOfWar.SilhouetteRadiusFor(reveal);
            int scouted = FogOfWar.ScoutRadiusFor(reveal);

            Assert.True(
                silhouette >= previousSilhouette,
                $"silhouette radius shrank when the team's reach grew to {reveal}");

            Assert.True(
                scouted >= previousScouted,
                $"scouted radius shrank when the team's reach grew to {reveal}");

            previousSilhouette = silhouette;
            previousScouted = scouted;
        }
    }

    [Fact]
    public void ARadiusBonusWidensEveryGradeAtOnce()
    {
        // A gadget that made you see further without teaching you more would be a
        // strange thing to spend a limited-use item on, so the bonus is applied to the
        // budget before both divisors are taken.
        const int Reveal = 200;

        int silhouettePlain = FogOfWar.SilhouetteRadiusFor(Reveal);
        int scoutedPlain = FogOfWar.ScoutRadiusFor(Reveal);

        int silhouetteBoosted = FogOfWar.SilhouetteRadiusFor(Reveal, 4);
        int scoutedBoosted = FogOfWar.ScoutRadiusFor(Reveal, 4);

        Assert.True(silhouetteBoosted > silhouettePlain);
        Assert.True(scoutedBoosted > scoutedPlain);
    }

    // ---- the IFogReveal extension point -------------------------------------

    [Fact]
    public void ARevealSourceWidensWhatTheTeamCanSee()
    {
        MissionMap map = Map();
        var fog = new FogOfWar(map);

        int before = fog.VisibleNodeCount();

        // Stand well away from anything already known, so a widening is visible.
        MissionNodeId origin = map.ExtractionNodeId;
        int gained = fog.RevealFrom(origin, FogRevealSource.Gadget);

        Assert.True(gained > 0, "a gadget revealed nothing at all");
        Assert.True(fog.VisibleNodeCount() > before);
    }

    [Fact]
    public void ARevealSourceNeverReachesRevealedForANodeNobodyEntered()
    {
        // The gadget has to scout, not observe. Generating interiors for rooms the team
        // has not walked into is the exact leak the lazy content rule prevents, and a
        // reveal source that reached Revealed would defeat it while looking harmless.
        for (int index = 0; index < 40; index++)
        {
            MissionMap map = Map((ulong)index + 100, index + 1, 1 + (index % 3));
            var fog = new FogOfWar(map);

            fog.RevealFrom(map.ExtractionNodeId, FogRevealSource.Gadget);

            foreach (MissionNode node in map.Nodes)
            {
                bool entered = node.Id == map.EntryNodeId;

                if (entered)
                    continue;

                Assert.True(
                    fog.StateOf(node.Id) < MissionVisibility.Revealed,
                    $"node {node.Id} reached Revealed from a gadget; a reveal source must not "
                    + "generate an interior");

                Assert.False(
                    fog.TryGetContents(node.Id, out _),
                    $"node {node.Id} acquired contents without being entered");
            }
        }
    }

    [Fact]
    public void ARevealSourceNamesItselfForTheUi()
    {
        // Core returns keys, never prose (knowledge.md rule 4). The key is what
        // Presentation maps to a localized "revealed by gadget" line.
        foreach (FogRevealSource source in FogRevealSource.All)
        {
            Assert.StartsWith("fog.source.", source.SourceKey);
        }
    }

    // ---- guard band ----------------------------------------------------------

    [Theory]
    [InlineData(0, GuardBand.None)]
    [InlineData(1, GuardBand.Few)]
    [InlineData(2, GuardBand.Many)]
    [InlineData(5, GuardBand.Many)]
    public void TheGuardBandBucketsByTheTablesThreshold(int guardCount, GuardBand expected)
    {
        Assert.Equal(expected, MissionNode.GuardBandFor(guardCount));
    }

    [Fact]
    public void EverySecurityNodeHoldsAtLeastOneGuard()
    {
        // The map promises a guard post on every route to the objective. A security
        // room that rolled zero would be a room the player walks through unopposed,
        // and a sight promising a guarded room that holds none is a lie.
        int securityNodes = 0;

        for (int index = 0; index < 300; index++)
        {
            MissionMap map = Map((ulong)index + 3000, index + 1, 1 + (index % 3));

            foreach (MissionNode node in map.Nodes)
            {
                if (!node.IsSecurity)
                    continue;

                securityNodes++;

                Assert.True(
                    node.GuardCount >= 1,
                    $"node {node.Id} is tagged security but its skeleton promised {node.GuardCount} guards");
            }
        }

        Assert.True(securityNodes > 0, "no security nodes were generated to check");
    }

    // ---- the tables that decide what a room contains ------------------------

    [Fact]
    public void EveryNodeRoomHasExactlyOneTemplate()
    {
        var templatedRooms = SimulationRules.AllRoomTemplates()
            .Select(t => t.NodeRoomId)
            .ToHashSet();

        foreach (NodeRoom room in SimulationRules.AllNodeRooms())
        {
            Assert.True(
                templatedRooms.Contains(room.Id),
                $"node_room {room.Id} has no room_template, so its interiors would fall back "
                + "to defaults rather than to designed content");

            Assert.NotNull(SimulationRules.RoomTemplateFor(room.Id));
        }
    }

    [Fact]
    public void EveryTemplateCanFitWhatItPromisesInItsOwnSlots()
    {
        // A template asking for more objects than it has abstract slots cannot be
        // honoured, and would produce rooms permanently emptier than designed.
        foreach (NodeInteriorTemplate template in SimulationRules.AllRoomTemplates())
        {
            Assert.True(
                template.MaxInteractables <= template.SlotCount,
                $"room_template {template.Id} allows {template.MaxInteractables} objects in "
                + $"{template.SlotCount} slots");

            Assert.True(template.SlotCount >= 1, $"room_template {template.Id} has no slots");
        }
    }

    [Fact]
    public void EveryInteractableKindIsAllowedSomewhere()
    {
        // A kind barred from every room is a kind that does not exist, and the map would
        // quietly be simpler than the tables describe.
        foreach (InteractableType kind in Enum.GetValues<InteractableType>())
        {
            bool reachable = SimulationRules.AllInteractableTypes()
                .Any(row => SimulationRules.ToCoreKind(row.Kind) == kind);

            Assert.True(reachable, $"no interactable_type row produces {kind}");
        }
    }

    [Fact]
    public void ARoomOnlyContainsKindsItsTagsAllow()
    {
        // The point of allowed_room_tags: a terminal in a corridor is noise, and a
        // designer who tags a room "storage" means it.
        for (int index = 0; index < 150; index++)
        {
            MissionMap map = Map((ulong)index + 6000, index + 1, 1 + (index % 3));
            var fog = new FogOfWar(map);

            foreach (MissionNode node in map.Nodes)
            {
                RoomContents room = fog.Observe(node.Id);
                var roomTags = new HashSet<string>(node.Tags, StringComparer.Ordinal);

                foreach (Interactable interactable in room.Interactables)
                {
                    bool permitted = SimulationRules.AllInteractableTypes()
                        .Where(row => SimulationRules.ToCoreKind(row.Kind) == interactable.Type)
                        .Any(row => SimulationRules.IsKindAllowedIn(row, roomTags.ToList()));

                    Assert.True(
                        permitted,
                        $"node {node.Id} (room {node.RoomTypeId}, tags [{string.Join(",", node.Tags)}]) "
                        + $"holds a {interactable.Type}, which no interactable_type row allows there");
                }
            }
        }
    }

    [Fact]
    public void NoRoomEverHoldsTwoExitsOrTwoObjectives()
    {
        // Both are checked by RoomContents.Validate, so this is a coverage assertion
        // that the validator is actually reached on real generated data rather than a
        // second rule about the same thing.
        for (int index = 0; index < 150; index++)
        {
            MissionMap map = Map((ulong)index + 7000, index + 1, 1 + (index % 3));
            var fog = new FogOfWar(map);

            foreach (MissionNode node in map.Nodes)
            {
                RoomContents room = fog.Observe(node.Id);

                Assert.True(
                    room.OfType(InteractableType.Exit).Count <= 1,
                    $"node {node.Id} has more than one exit");

                Assert.True(
                    room.OfType(InteractableType.Objective).Count <= 1,
                    $"node {node.Id} has more than one objective");

                Assert.True(room.Validate(out string problem), $"node {node.Id}: {problem}");
            }
        }
    }

    [Fact]
    public void EveryGeneratedRoomHoldsAtLeastOneThing()
    {
        // A room with nothing in it is indistinguishable from an ungenerated one, which
        // would make entering a room feel like it did not happen.
        int empty = 0;
        int total = 0;

        for (int index = 0; index < 150; index++)
        {
            MissionMap map = Map((ulong)index + 8000, index + 1, 1 + (index % 3));
            var fog = new FogOfWar(map);

            foreach (MissionNode node in map.Nodes)
            {
                total++;

                if (fog.Observe(node.Id).Count == 0)
                    empty++;
            }
        }

        Assert.Equal(0, empty);
        Assert.True(total > 0, "no rooms were generated to check");
    }
}