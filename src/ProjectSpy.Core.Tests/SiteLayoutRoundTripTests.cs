using ProjectSpy.Core.Missions;
using Xunit;
using Xunit.Abstractions;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Stage 4a's save requirement: a site is fully serializable, and rebuilding it from the
/// save reproduces the same building exactly.
/// </summary>
/// <remarks>
/// <para>
/// This is the cheapest of the stage's guarantees to state and the most expensive to get
/// wrong, because "the save is a seed" is only a legitimate design if the generator really
/// is a pure function of that seed. If any step of generation quietly consulted a counter,
/// a hash iteration order, the clock or the name of a collection, then saving and loading
/// would produce a building that looked right and differed where it matters — a door in
/// the wrong wall, the objective behind a locked route, a guard patrolling rooms the team
/// has already looted.
/// </para>
/// <para>
/// The canonical text is the instrument here, not just an assertion. Comparing two
/// serialized sites names the field that drifted; comparing two digests only says
/// "something". Both are asserted, deliberately, because they fail in different ways.
/// </para>
/// </remarks>
public class SiteLayoutRoundTripTests
{
    /// <summary>Sites round-tripped in the sweep. Enough to cover every template twice.</summary>
    private const int SiteCount = 600;

    private readonly ITestOutputHelper _output;

    public SiteLayoutRoundTripTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SaveAndLoadRebuildsAByteIdenticalSite()
    {
        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();
        Assert.NotEmpty(templates);

        int forwardPosts = 0;
        int withObserved = 0;

        for (int index = 0; index < SiteCount; index++)
        {
            ProjectSpy.Tables.SiteTemplate template = templates[index % templates.Count];
            ulong worldSeed = 0x5A17UL + (ulong)index;

            SiteLayout original = Generate(template, index, worldSeed);

            // Observe a different number of rooms on different iterations, so the sweep
            // covers an untouched site, a partly explored one, and a thoroughly looted one.
            if (index % 3 == 0)
            {
                int take = index % (original.Rooms.Count + 1);
                for (int room = 0; room < take; room++)
                    original.ObserveRoom(original.Rooms[room].Id);

                if (take > 0)
                    withObserved++;
            }

            SiteLayout restored = SiteLayout.Load(original.Save());

            string before = SiteLayoutSerializer.ToCanonicalText(original);
            string after = SiteLayoutSerializer.ToCanonicalText(restored);

            Assert.True(
                before == after,
                $"site {template.Id} mission {index} did not round-trip.\n"
                + "--- original ---\n" + before + "\n--- restored ---\n" + after);

            Assert.Equal(
                SiteLayoutSerializer.ComputeCanonicalDigest(original),
                SiteLayoutSerializer.ComputeCanonicalDigest(restored));

            // The bytes are the UTF-8 of the text, with no byte-order mark; a serializer
            // that emitted a BOM or a different newline would still compare equal as text
            // on a developer machine and differ in a shipped save.
            Assert.Equal(
                System.Text.Encoding.UTF8.GetBytes(before),
                SiteLayoutSerializer.ToCanonicalBytes(restored));

            if (restored.ForwardPost is not null)
                forwardPosts++;
        }

        _output.WriteLine($"round-tripped sites       {SiteCount}");
        _output.WriteLine($"  with observed rooms     {withObserved}");
        _output.WriteLine($"  with a forward post     {forwardPosts}");
    }

    /// <summary>
    /// A second save/load cycle must land on the same place, or the first one only
    /// happened to look right.
    /// </summary>
    [Fact]
    public void SaveLoadIsAFixedPoint()
    {
        ProjectSpy.Tables.SiteTemplate? template = FirstTemplateWithForwardPost();
        Assert.NotNull(template);

        SiteLayout first = Generate(template!, 7, 0xB0BUL);
        first.ObserveRoom(first.EntranceRoomId);
        first.ObserveRoom(first.ObjectiveRoomId);

        SiteLayoutSaveData once = SiteLayoutSerializer.ToSaveData(first);
        SiteLayout second = SiteLayout.Load(once);
        SiteLayoutSaveData twice = SiteLayoutSerializer.ToSaveData(second);

        Assert.Equal(once, twice);
        Assert.Equal(
            SiteLayoutSerializer.ComputeCanonicalDigest(first),
            SiteLayoutSerializer.ComputeCanonicalDigest(second));

        SiteLayout third = SiteLayout.Load(twice);
        Assert.Equal(twice, SiteLayoutSerializer.ToSaveData(third));
        Assert.Equal(
            SiteLayoutSerializer.ComputeCanonicalDigest(second),
            SiteLayoutSerializer.ComputeCanonicalDigest(third));
    }

    /// <summary>
    /// The save must record which rooms had been seen and nothing about what was in them.
    /// </summary>
    /// <remarks>
    /// Room contents are not in the save because they are reproducible from
    /// <c>MapSeed + roomId</c>; storing them would be redundant state that could disagree
    /// with the generator. The observed set, on the other hand, is genuine state — the
    /// difference between an empty room and an unvisited one — and has to survive.
    /// </remarks>
    [Fact]
    public void ObservedRoomsSurviveTheRoundTripWithIdenticalContents()
    {
        ProjectSpy.Tables.SiteTemplate template = FirstTemplateWithForwardPost()!;
        SiteLayout original = Generate(template, 11, 0xFEEDUL);

        var observed = new List<SiteRoomId>();
        for (int room = 0; room < original.Rooms.Count; room += 2)
            observed.Add(original.Rooms[room].Id);

        var before = new Dictionary<SiteRoomId, string>();
        foreach (SiteRoomId id in observed)
            before[id] = Describe(original.ObserveRoom(id));

        SiteLayoutSaveData save = original.Save();

        Assert.Equal(observed.Count, save.ObservedRoomIds.Count);
        Assert.Equal(
            template.Id, save.SiteTemplateId);
        Assert.Equal(template.Tier, save.Tier);
        Assert.Equal(11, save.MissionId);
        Assert.Equal(0xFEEDUL, save.WorldSeed);
        Assert.Equal(original.MapSeed, save.MapSeed);

        // The save names the seed, so it must not carry a description of the building.
        string saveText = System.Text.Json.JsonSerializer.Serialize(save);
        Assert.DoesNotContain("prop", saveText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("loot", saveText, StringComparison.OrdinalIgnoreCase);

        SiteLayout restored = SiteLayout.Load(save);

        Assert.Equal(observed.Count, restored.ObservedRoomCount);

        foreach (SiteRoomId id in observed)
        {
            Assert.True(restored.IsRoomObserved(id), $"room {id.Value} lost its observed flag.");
            SiteRoomDetail detail = restored.ObserveRoom(id);
            Assert.Equal(before[id], Describe(detail));
        }

        // ...and the rooms nobody entered are still nobody's business.
        foreach (SiteRoom room in restored.Rooms)
        {
            if (observed.Contains(room.Id))
                continue;

            Assert.False(restored.IsRoomObserved(room.Id));
            Assert.Null(restored.ObservedRoom(room.Id));
        }
    }

    /// <summary>
    /// Two runs that walked the building in opposite orders are the same run.
    /// </summary>
    [Fact]
    public void ObservationOrderDoesNotChangeTheSaveOrTheBuilding()
    {
        ProjectSpy.Tables.SiteTemplate template = FirstTemplateWithForwardPost()!;

        SiteLayout forwards = Generate(template, 3, 0x5151UL);
        SiteLayout backwards = Generate(template, 3, 0x5151UL);

        Assert.Equal(
            SiteLayoutSerializer.ComputeCanonicalDigest(forwards),
            SiteLayoutSerializer.ComputeCanonicalDigest(backwards));

        var ids = new List<SiteRoomId>();
        foreach (SiteRoom room in forwards.Rooms)
            ids.Add(room.Id);

        foreach (SiteRoomId id in ids)
            forwards.ObserveRoom(id);

        for (int index = ids.Count - 1; index >= 0; index--)
            backwards.ObserveRoom(ids[index]);

        Assert.Equal(
            SiteLayoutSerializer.ToSaveData(forwards),
            SiteLayoutSerializer.ToSaveData(backwards));

        // The observed set is part of the canonical dump, so two sites that differ only
        // in what has been seen must not share a digest. Without this, the equality above
        // would also be satisfied by a serializer that wrote no rooms at all.
        SiteLayout untouched = Generate(template, 3, 0x5151UL);
        Assert.NotEqual(
            SiteLayoutSerializer.ComputeCanonicalDigest(forwards),
            SiteLayoutSerializer.ComputeCanonicalDigest(untouched));
    }

    /// <summary>
    /// Different seeds must produce different buildings. A serializer that silently
    /// omitted guards, or lights, or any other region, would otherwise satisfy every
    /// equality test above while describing the same building forever.
    /// </summary>
    [Fact]
    public void DifferentSeedsProduceDifferentCanonicalDumps()
    {
        ProjectSpy.Tables.SiteTemplate template = SimulationRules.AllSiteTemplates()[0];

        var digests = new HashSet<ulong>();

        for (int index = 0; index < 64; index++)
        {
            SiteLayout layout = Generate(template, index, 0x1BADUL + (ulong)index);
            digests.Add(SiteLayoutSerializer.ComputeCanonicalDigest(layout));
        }

        Assert.Equal(64, digests.Count);
    }

    /// <summary>
    /// A save naming a room the rebuilt site does not contain is refused, rather than
    /// leaving an id in the observed set that nothing can ever resolve.
    /// </summary>
    [Fact]
    public void LoadRefusesASaveNamingARoomThatDoesNotExist()
    {
        ProjectSpy.Tables.SiteTemplate template = SimulationRules.AllSiteTemplates()[0];
        SiteLayout layout = Generate(template, 1, 0x3131UL);

        SiteLayoutSaveData save = layout.Save();
        int stranger = layout.Rooms[^1].Id.Value + 100_000;

        var tampered = save with
        {
            ObservedRoomIds = new[] { new SiteRoomId(stranger) }
        };

        Assert.Throws<InvalidOperationException>(() => SiteLayout.Load(tampered));
    }

    /// <summary>The save is a seed and a room list, not the building.</summary>
    [Fact]
    public void SaveDataCarriesTheSeedRatherThanTheBuilding()
    {
        ProjectSpy.Tables.SiteTemplate template = FirstTemplateWithForwardPost()!;
        SiteLayout layout = Generate(template, 5, 0xC0FFEEUL);

        Assert.True(layout.Rooms.Count > 2, "sample site should have several rooms");

        SiteLayoutSaveData save = SiteLayout.Load(layout.Save()).Save();

        Assert.Equal(6, save.GetType().GetProperties().Length);
        Assert.Empty(save.ObservedRoomIds);
    }

    // ---- helpers -------------------------------------------------------------

    private static SiteLayout Generate(ProjectSpy.Tables.SiteTemplate template, int mission, ulong worldSeed)
    {
        ulong mapSeed = SiteGenerator.DeriveMapSeed(worldSeed, mission);
        return SiteGenerator.Generate(template.Id, template.Tier, mission, worldSeed, mapSeed);
    }

    /// <summary>
    /// The first template that grows a forward command post, so the round-trip covers the
    /// second region and its travel edge rather than only single-region sites.
    /// </summary>
    private static ProjectSpy.Tables.SiteTemplate? FirstTemplateWithForwardPost()
    {
        foreach (ProjectSpy.Tables.SiteTemplate template in SimulationRules.AllSiteTemplates())
        {
            SiteLayout layout = Generate(template, 2, 0x4E4FUL);
            if (layout.ForwardPost is not null)
                return template;
        }

        return null;
    }

    /// <summary>Renders a room's contents as a stable, culture-free string.</summary>
    private static string Describe(SiteRoomDetail detail)
    {
        var loot = new List<string>();
        foreach (SiteLootEntry entry in detail.Loot)
            loot.Add($"{entry.LootTableId}:{entry.ItemId}x{entry.Count}");

        var props = new List<string>();
        foreach (SiteProp prop in detail.Props)
            props.Add($"{prop.PropId}@{prop.X.Raw}");

        loot.Sort(StringComparer.Ordinal);
        props.Sort(StringComparer.Ordinal);

        return $"loot[{string.Join(",", loot)}] props[{string.Join(",", props)}]";
    }
}