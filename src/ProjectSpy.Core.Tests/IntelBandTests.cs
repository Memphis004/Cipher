using System.Reflection;
using ProjectSpy.Core.Missions;
using Xunit;
using Xunit.Abstractions;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Stage 4b's first promise: an intel report never contains a fact from a band above
/// the one the player has paid for.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that makes the whole sleeper economy meaningful. If a low band
/// leaked a high band's facts, then intelligence would be free — the player would start
/// an operation, look at the map, and find the objective, the patrols and the lights
/// already drawn — and the cost of being found, which is the entire risk the system
/// offers, would be paid for nothing.
/// </para>
/// <para>
/// So the assertions are written per band rather than as one "the right number of rooms"
/// check. A count would pass if the wrong band leaked one room while another hid one,
/// and would not notice at all if the wrong <em>kind</em> of fact leaked: a snapshot that
/// correctly withholds room types while quietly including every patrol route has not
/// leaked a count.
/// </para>
/// </remarks>
public class IntelBandTests
{
    private readonly ITestOutputHelper _output;

    public IntelBandTests(ITestOutputHelper output) => _output = output;

    /// <summary>One percentage inside each band, plus both edges of every band.</summary>
    private static readonly int[] Percentages =
    {
        0, 1, 24,
        25, 37, 49,
        50, 62, 74,
        75, 88, 99,
        100,
    };

    [Fact]
    public void TheHighestBandNeverLeaksRoomContents()
    {
        // knowledge.md rule 11: intel reveals layout, room types and connections, and
        // never the detailed contents of a room nobody has entered. This is the one
        // thing a band filter must not be extended to cover, because it would hand the
        // player a complete picture of a building the team has never walked into.
        Assembly core = typeof(SiteLayout).Assembly;
        var offenders = new List<string>();

        foreach (Type type in core.GetTypes())
        {
            if (!type.IsPublic && !type.IsNestedPublic)
                continue;

            if (type.Namespace != "ProjectSpy.Core")
                continue;

            if (!typeof(IntelEntry).IsAssignableFrom(type))
                continue;

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (IsInterior(property.PropertyType, 0))
                    offenders.Add($"{type.FullName}.{property.Name} -> {property.PropertyType.Name}");

                foreach (PropertyInfo nested in Nested(property.PropertyType, 0))
                    offenders.Add($"{type.FullName}.{property.Name}.{nested.Name} -> {nested.PropertyType.Name}");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (IsInterior(method.ReturnType, 0))
                    offenders.Add($"{type.FullName}.{method.Name}() -> {method.ReturnType.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "An intel entry can carry room contents, so a pre-mission report would reveal "
            + "interiors of rooms nobody has entered (knowledge.md rule 11): "
            + string.Join(", ", offenders));

        // And the same claim about the whole snapshot: nothing on IntelSnapshot reaches a
        // SiteRoomDetail either.
        foreach (PropertyInfo property in typeof(IntelSnapshot).GetProperties())
            Assert.False(IsInterior(property.PropertyType, 0), $"IntelSnapshot.{property.Name}");
    }

    [Fact]
    public void EachBandRevealsExactlyWhatItPromises()
    {
        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();
        Assert.NotEmpty(templates);

        var counts = new SortedDictionary<string, int>();

        foreach (ProjectSpy.Tables.SiteTemplate template in templates)
        {
            SiteLayout layout = LayoutFor(template.Id, 4242);

            foreach (int percent in Percentages)
            {
                IntelBand band = IntelBands.For(percent);
                IntelSnapshot snapshot = Build(layout, percent, SleeperStatus.Embedded);

                Assert.True(band == snapshot.Band,
                    $"site {template.Id} at {percent}% landed in {snapshot.Band}.");
                Assert.True(percent == snapshot.IntelPercent);

                // A prisoner is placed only at full intel, at every band edge included.
                Assert.Empty(snapshot.Holdings);

                Count(counts, band, "rooms", snapshot.Rooms.Count());
                Count(counts, band, "connections", snapshot.Connections.Count());
                Count(counts, band, "patrols", snapshot.Patrols.Count());

                switch (band)
                {
                    case IntelBand.EntranceOnly:
                        // One room, and only its position. This is the band that has to be
                        // stingiest, because it is where an operation starts and it is the
                        // band a player is most tempted to read as "nearly free".
                        IntelRoomEntry only = Assert.Single(snapshot.Rooms);
                        Assert.Equal(layout.EntranceRoomId, only.RoomId);
                        Assert.Equal(0, only.RoomTemplateId);
                        Assert.Equal(string.Empty, only.NameKey);
                        Assert.Empty(snapshot.Connections);
                        Assert.Empty(snapshot.Patrols);
                        Assert.Null(snapshot.ClaimedObjective);
                        Assert.Empty(snapshot.ClaimedExtraction);
                        Assert.True(only.Roles.HasFlag(IntelKnownRole.Entrance));
                        break;

                    case IntelBand.Layout:
                        Assert.Equal(layout.Rooms.Count, snapshot.Rooms.Count());
                        foreach (IntelRoomEntry room in snapshot.Rooms)
                        {
                            Assert.Equal(0, room.RoomTemplateId);
                            Assert.Equal(string.Empty, room.NameKey);
                            Assert.Equal(-1, room.GuardCount);
                            Assert.Equal(-1, room.CivilianCount);
                            Assert.False(room.LightLevelKnown);
                        }

                        Assert.Empty(snapshot.Connections);
                        Assert.Empty(snapshot.Patrols);
                        Assert.Null(snapshot.ClaimedObjective);
                        break;

                    case IntelBand.TypesAndConnections:
                        Assert.Equal(layout.Rooms.Count, snapshot.Rooms.Count());
                        foreach (IntelRoomEntry room in snapshot.Rooms)
                            Assert.NotEqual(0, room.RoomTemplateId);

                        Assert.Equal(layout.Connections.Count, snapshot.Connections.Count());
                        Assert.Empty(snapshot.Patrols);
                        Assert.Null(snapshot.ClaimedObjective);
                        break;

                    case IntelBand.Details:
                        Assert.Equal(layout.Rooms.Count, snapshot.Rooms.Count());
                        Assert.Equal(layout.Connections.Count, snapshot.Connections.Count());
                        Assert.Equal(layout.Guards.Count, snapshot.Patrols.Count());
                        Assert.Equal(layout.ObjectiveRoomId, snapshot.ClaimedObjective);
                        Assert.NotEmpty(snapshot.ClaimedExtraction);

                        foreach (IntelRoomEntry room in snapshot.Rooms)
                        {
                            Assert.NotEqual(-1, room.GuardCount);
                            Assert.NotEqual(-1, room.CivilianCount);
                            Assert.True(room.LightLevelKnown);
                        }

                        break;

                    case IntelBand.Complete:
                        // The full report is asserted field by field in
                        // IntelSnapshotAccuracyTests; here only that nothing is withheld.
                        Assert.Equal(layout.Rooms.Count, snapshot.Rooms.Count());
                        Assert.Equal(layout.Connections.Count, snapshot.Connections.Count());
                        Assert.Equal(layout.Guards.Count, snapshot.Patrols.Count());
                        break;
                }
            }
        }

        foreach (KeyValuePair<string, int> entry in counts)
            _output.WriteLine($"{entry.Key,-28} {entry.Value}");
    }

    [Fact]
    public void EveryFactInASnapshotDescribesSomethingThatExists()
    {
        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();

        foreach (ProjectSpy.Tables.SiteTemplate template in templates)
        {
            SiteLayout layout = LayoutFor(template.Id, 909);

            foreach (int percent in new[] { 30, 60, 80, 100 })
            {
                IntelSnapshot snapshot = Build(layout, percent, SleeperStatus.Embedded);

                foreach (IntelRoomEntry room in snapshot.Rooms)
                {
                    SiteRoom? real = layout.Find(room.RoomId);
                    Assert.NotNull(real);
                    Assert.Equal(real!.FloorIndex, room.FloorIndex);
                    Assert.Equal(real.StartX.Raw, room.StartX.Raw);
                    Assert.Equal(real.EndX.Raw, room.EndX.Raw);
                }

                foreach (IntelConnectionEntry connection in snapshot.Connections)
                {
                    Assert.NotNull(layout.Find(connection.RoomA));
                    Assert.NotNull(layout.Find(connection.RoomB));
                }

                foreach (IntelPatrolEntry patrol in snapshot.Patrols)
                {
                    foreach (SiteRoomId roomId in patrol.Route)
                        Assert.NotNull(layout.Find(roomId));
                }
            }
        }
    }

    [Fact]
    public void AReportIsIdenticalWhenRebuiltFromTheSameSeed()
    {
        SiteLayout layout = LayoutFor(11010, 77);

        IntelSnapshot first = Build(layout, 80, SleeperStatus.Embedded, Tick.FromDays(9));
        IntelSnapshot second = Build(layout, 80, SleeperStatus.Embedded, Tick.FromDays(9));

        Assert.Equal(first.Entries.Count, second.Entries.Count);

        for (int index = 0; index < first.Entries.Count; index++)
        {
            Assert.Equal(first.Entries[index].FactKind, second.Entries[index].FactKind);
            Assert.Equal(first.Entries[index].GetType(), second.Entries[index].GetType());

            Assert.Equal(
                Describe(first.Entries[index]),
                Describe(second.Entries[index]));
        }

        // ...and a different tick must not change the content, only the stamp. A report
        // whose story shifted between refreshes would read as a broken map rather than as
        // an unreliable source.
        IntelSnapshot later = Build(layout, 80, SleeperStatus.Embedded, Tick.FromDays(40));
        Assert.Equal(first.Entries.Count, later.Entries.Count);
        Assert.Equal(
            Describe(first.Entries[0]),
            Describe(later.Entries[0]));
    }

    // ---- helpers -------------------------------------------------------------

    private static SiteLayout LayoutFor(int siteId, int salt)
    {
        ProjectSpy.Tables.SiteTemplate? template = SimulationRules.SiteTemplateFor(siteId);
        Assert.NotNull(template);

        ulong mapSeed = SiteGenerator.DeriveMapSeed(0x1DEAUL + (ulong)salt, siteId);
        return SiteGenerator.Generate(siteId, template!.Tier, siteId, 0x1DEAUL + (ulong)salt, mapSeed);
    }

    private static IntelSnapshot Build(
        SiteLayout layout,
        int percent,
        SleeperStatus status,
        Tick? tick = null)
    {
        var operation = new SleeperOperation
        {
            AgentId = new AgentId(1),
            SiteId = layout.SiteTemplateId,
            StartedOnTick = Tick.Zero,
            IntelPercent = percent,
            Status = status,
        };

        return IntelSnapshotBuilder.Build(layout, operation, tick ?? Tick.FromDays(1));
    }

    private static void Count(SortedDictionary<string, int> counts, IntelBand band, string what, int value)
    {
        string key = $"{band}.{what}";
        counts.TryGetValue(key, out int seen);
        counts[key] = seen + value;
    }

    /// <summary>A stable string for one entry, for the determinism comparison.</summary>
    private static string Describe(IntelEntry entry) => entry switch
    {
        IntelRoomEntry room =>
            $"room {room.RoomId.Value} f{room.FloorIndex} [{room.StartX.Raw},{room.EndX.Raw}) "
            + $"tpl={room.RoomTemplateId} name={room.NameKey} g={room.GuardCount} c={room.CivilianCount} "
            + $"light={(room.LightLevelKnown ? room.LightLevel.ToString() : "?")} roles={room.Roles}",

        IntelConnectionEntry connection =>
            $"conn {connection.ConnectionId.Value} {connection.RoomA.Value}-{connection.RoomB.Value} "
            + $"{connection.Kind} locked={connection.IsLocked}",

        IntelPatrolEntry patrol =>
            $"patrol {patrol.GuardId.Value} home={patrol.HomeRoomId.Value} "
            + $"route=[{string.Join(",", patrol.Route.Select(r => r.Value))}]",

        IntelHoldingEntry holding =>
            $"holding {holding.AgentId.Value} room={holding.RoomId.Value} ticks={holding.TicksUntilLost}",

        _ => entry.GetType().Name,
    };

    /// <summary>True when a type is (or contains) a room interior.</summary>
    private static bool IsInterior(Type type, int depth)
    {
        if (depth > 3)
            return false;

        string? name = Nullable.GetUnderlyingType(type)?.Name;

        if (name is nameof(SiteRoomDetail) || name == nameof(SiteLootEntry) || name == nameof(SiteProp))
            return true;

        if (type.IsArray || type.IsGenericType)
        {
            foreach (Type inner in type.GetGenericArguments())
            {
                if (IsInterior(inner, depth + 1))
                    return true;
            }

            if (type.IsArray)
                return IsInterior(type.GetElementType()!, depth + 1);

            return false;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsInterior(property.PropertyType, depth + 1))
                return true;
        }

        return false;
    }

    private static IEnumerable<PropertyInfo> Nested(Type type, int depth)
    {
        if (depth > 2)
            yield break;

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType.Assembly != typeof(SiteLayout).Assembly)
                continue;

            foreach (PropertyInfo inner in Nested(property.PropertyType, depth + 1))
                yield return inner;
        }
    }
}