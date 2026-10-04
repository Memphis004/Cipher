using System.Reflection;
using ProjectSpy.Core.Missions;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Stage 4a's lazy-content rule (knowledge.md rule 11): a room's contents do not exist
/// until somebody observes it, and nothing on Core's public surface can produce them any
/// earlier.
/// </summary>
/// <remarks>
/// <para>
/// The rule is only worth anything if it is enforced against the <em>compiled</em>
/// assembly rather than against the intention of the code. A method added next year that
/// returned every room's contents would compile, would look reasonable, and would hand the
/// UI a complete picture of a building nobody has entered — so the leak tests here walk
/// the public surface by reflection and fail on any member that could do it.
/// </para>
/// </remarks>
public class SiteLazyContentTests
{
    private static SiteLayout GenerateSample()
    {
        const int siteTemplateId = 11010;
        ProjectSpy.Tables.SiteTemplate? template = SimulationRules.SiteTemplateFor(siteTemplateId);
        Assert.NotNull(template);

        ulong mapSeed = SiteGenerator.DeriveMapSeed(0xF06UL, 11);
        return SiteGenerator.Generate(siteTemplateId, template!.Tier, 11, 0xF06UL, mapSeed);
    }

    [Fact]
    public void NoRoomHasContentsUntilItIsObserved()
    {
        SiteLayout layout = GenerateSample();

        Assert.True(layout.Rooms.Count > 1, "sample site should have several rooms");
        Assert.Equal(0, layout.ObservedRoomCount);

        foreach (SiteRoom room in layout.Rooms)
        {
            Assert.False(layout.IsRoomObserved(room.Id));
            Assert.Null(layout.ObservedRoom(room.Id));
        }
    }

    [Fact]
    public void ObservingARoomGeneratesItsContentsAndIsIdempotent()
    {
        SiteLayout layout = GenerateSample();
        SiteRoom room = layout.Rooms[0];

        SiteRoomDetail first = layout.ObserveRoom(room.Id);
        SiteRoomDetail second = layout.ObserveRoom(room.Id);

        Assert.Same(first, second);
        Assert.Equal(1, layout.ObservedRoomCount);
        Assert.True(layout.IsRoomObserved(room.Id));
        Assert.Equal(room.Id, first.RoomId);

        // A room nobody has looked at is still untouched.
        Assert.False(layout.IsRoomObserved(layout.Rooms[1].Id));
        Assert.Null(layout.ObservedRoom(layout.Rooms[1].Id));
    }

    [Fact]
    public void ContentsAreIdenticalNoMatterWhenTheRoomIsObserved()
    {
        // The whole reason the contents stream is derived from (MapSeed, roomId) rather
        // than drawn from a shared sequence. Observing room 5 first and room 1 first must
        // produce the same furniture in both, or a save reloaded later describes a
        // different building.
        SiteLayout forwards = GenerateSample();
        SiteLayout backwards = GenerateSample();

        SiteRoomId first = forwards.Rooms[0].Id;
        SiteRoomId second = forwards.Rooms[1].Id;

        // `forwards` sees room 0 first, `backwards` sees room 1 first. Each variable below
        // is named for the room it holds, not for the order it was observed in, so the
        // pairs being compared are always the same room in both layouts.
        SiteRoomDetail forwardsRoom0 = forwards.ObserveRoom(first);
        SiteRoomDetail forwardsRoom1 = forwards.ObserveRoom(second);

        SiteRoomDetail backwardsRoom1 = backwards.ObserveRoom(second);
        SiteRoomDetail backwardsRoom0 = backwards.ObserveRoom(first);

        Assert.Equal(Describe(forwardsRoom0), Describe(backwardsRoom0));
        Assert.Equal(Describe(forwardsRoom1), Describe(backwardsRoom1));

        // Re-observing an already-held room must not re-roll it either.
        Assert.Same(forwardsRoom1, forwards.ObserveRoom(second));
        Assert.Equal(Describe(forwardsRoom1), Describe(backwards.ObserveRoom(second)));
    }

    [Fact]
    public void ContentsAreTheSameOnAFreshlyRegeneratedSite()
    {
        SiteLayout first = GenerateSample();
        SiteLayout second = GenerateSample();

        foreach (SiteRoom room in first.Rooms)
        {
            Assert.Equal(
                Describe(first.ObserveRoom(room.Id)),
                Describe(second.ObserveRoom(room.Id)));
        }
    }

    [Fact]
    public void ObservingARoomFromAnotherSiteThrowsRatherThanInventingOne()
    {
        SiteLayout layout = GenerateSample();
        SiteLayout other = GenerateSample(0xF07UL, 12);

        int strangerId = other.Rooms[^1].Id.Value + 10_000;

        Assert.Throws<InvalidOperationException>(() => layout.ObserveRoom(new SiteRoomId(strangerId)));
    }

    /// <summary>
    /// The leak test proper: nothing on Core's public surface can hand back the contents
    /// of a room nobody has observed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately paranoid about shape rather than about names. It rejects any public
    /// member that returns <see cref="SiteRoomDetail"/> other than the two named entry
    /// points, and any that returns a collection of them — because a collection is the
    /// dangerous shape, since "give me the contents of every room" is exactly the call
    /// the rule forbids and is easy to write by accident.
    /// </para>
    /// <para>
    /// It also checks the generator itself, which is the other way to leak: if
    /// <c>Generate</c> produced a layout whose rooms already had contents, every other
    /// guarantee here would be true and useless.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPublicApiCanEnumerateUnobservedRoomContents()
    {
        Assembly core = typeof(SiteLayout).Assembly;
        var offenders = new List<string>();

        foreach (Type type in core.GetTypes())
        {
            if (!type.IsPublic && !type.IsNestedPublic)
                continue;

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Type? returned = Unwrap(method.ReturnType);

                bool returnsDetail = returned == typeof(SiteRoomDetail)
                                     || (returned is not null && typeof(IEnumerable<SiteRoomDetail>).IsAssignableFrom(returned));

                if (!returnsDetail)
                    continue;

                // The two sanctioned doors. ObserveRoom requires the room; ObservedRoom
                // only reports one already held.
                bool sanctioned =
                    (type == typeof(SiteLayout) && method.Name == nameof(SiteLayout.ObserveRoom))
                    || (type == typeof(SiteLayout) && method.Name == nameof(SiteLayout.ObservedRoom));

                if (!sanctioned)
                    offenders.Add($"{type.FullName}.{method.Name} -> {method.ReturnType.Name}");
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Type? returned = Unwrap(property.PropertyType);
                bool returnsDetail = returned == typeof(SiteRoomDetail)
                                     || (returned is not null && typeof(IEnumerable<SiteRoomDetail>).IsAssignableFrom(returned));

                if (returnsDetail)
                    offenders.Add($"{type.FullName}.{property.Name} -> {property.PropertyType.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Core exposes a public member that can hand back room contents without the room "
            + "being observed (knowledge.md rule 11): " + string.Join(", ", offenders));

        // The other half: generation itself must not leave anything lying around.
        SiteLayout layout = GenerateSample();
        Assert.Equal(0, layout.ObservedRoomCount);
        Assert.Empty(layout.ObservedRoomIds);
    }

    /// <summary>Strips nullable and array wrapping so a return type can be classified.</summary>
    private static Type? Unwrap(Type type)
    {
        Type current = type;

        while (true)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                current = current.GetGenericArguments()[0];
                continue;
            }

            if (current.IsArray)
            {
                current = current.GetElementType() ?? current;
                continue;
            }

            return current;
        }
    }

    private static SiteLayout GenerateSample(ulong worldSeed, int mission)
    {
        const int siteTemplateId = 11010;
        ProjectSpy.Tables.SiteTemplate? template = SimulationRules.SiteTemplateFor(siteTemplateId);
        Assert.NotNull(template);

        ulong mapSeed = SiteGenerator.DeriveMapSeed(worldSeed, mission);
        return SiteGenerator.Generate(siteTemplateId, template!.Tier, mission, worldSeed, mapSeed);
    }

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