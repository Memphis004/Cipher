using System.Diagnostics;
using ProjectSpy.Core.Missions;
using Xunit;
using Xunit.Abstractions;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The stage-4a sweep: twenty thousand generated sites, every invariant on every one.
/// </summary>
/// <remarks>
/// <para>
/// This is the test that says the generator is correct rather than usually correct. Every
/// other site test asks a question about one layout; this asks "is any of twenty thousand
/// of them broken", and the generator's own <c>Validate</c> is the thing under test — it
/// runs inside <c>Generate</c>, so a failure throws naming the site rather than being
/// reported twenty thousand lines later.
/// </para>
/// <para>
/// The distributions are printed, not asserted. They are the stage-4a deliverable the
/// brief asks for, and a silent distribution is a distribution nobody reads; the numbers
/// also make an unintended balance shift visible at a glance in the test log.
/// </para>
/// </remarks>
public class SiteGeneratorTests
{
    /// <summary>Sites generated in the sweep.</summary>
    private const int SiteCount = 20_000;

    /// <summary>The per-site budget from the stage-4a brief.</summary>
    private const long BudgetMicroseconds = 10_000;

    /// <summary>Sites generated before the timed sweep, to pay for JIT.</summary>
    private const int WarmupCount = 50;

    private readonly ITestOutputHelper _output;

    public SiteGeneratorTests(ITestOutputHelper output) => _output = output;

    /// <summary>Milliseconds a single cold call took, reported but not asserted.</summary>
    private long coldStartMicroseconds;

    private static long ElapsedMicroseconds(Stopwatch watch)
        => watch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

    [Fact]
    public void TwentyThousandSitesAllSatisfyEveryInvariant()
    {
        Assert.True(SimulationRules.AreTablesLoaded,
            "Tables did not load, so site generation is untestable (knowledge.md rule 3).");

        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();
        Assert.NotEmpty(templates);

        var floorCounts = new Dictionary<int, int>();
        var roomCounts = new SortedDictionary<int, int>();
        var routeCounts = new SortedDictionary<long, int>();
        var totalRooms = 0;
        var totalConnections = 0;
        long slowestMicroseconds = 0;
        int slowestMission = 0;
        int slowestSite = 0;
        int guardTotal = 0;
        int civilianTotal = 0;
        int forwardPostCount = 0;

        // Warm up before timing anything. The first call into the generator pays for JIT
        // of the whole path — floor partition, graph queries, validation — and measures
        // the runtime rather than the generator. A cold number is still worth printing,
        // because it is what a game's first mission actually costs, but the brief's
        // budget is about steady-state generation and that is what is asserted.
        var warmup = new Stopwatch();
        coldStartMicroseconds = 0;

        for (int index = 0; index < WarmupCount; index++)
        {
            ProjectSpy.Tables.SiteTemplate template = templates[index % templates.Count];
            ulong seed = SiteGenerator.DeriveMapSeed(0xA11CEUL + (ulong)index, index);

            warmup.Restart();
            SiteLayout _ = SiteGenerator.Generate(template.Id, template.Tier, index, 0xA11CEUL + (ulong)index, seed);
            warmup.Stop();

            long elapsed = ElapsedMicroseconds(warmup);

            if (index == 0)
                coldStartMicroseconds = elapsed;
        }

        var watch = new Stopwatch();

        for (int index = 0; index < SiteCount; index++)
        {
            // Walk every template rather than picking one at random, so that "across all
            // templates and tiers" is literally true. The tier is each template's own:
            // Generate reads it back from the row rather than trusting the argument, and
            // TierComesFromTheTemplateRow pins that, so the site templates at tiers 1
            // through 4 between them cover every tier.
            ProjectSpy.Tables.SiteTemplate template = templates[index % templates.Count];
            int tier = template.Tier;
            int mission = index;

            ulong mapSeed = SiteGenerator.DeriveMapSeed(0xA11CEUL + (ulong)index, mission);

            watch.Restart();
            SiteLayout layout = SiteGenerator.Generate(
                template.Id, tier, mission, 0xA11CEUL + (ulong)index, mapSeed);
            watch.Stop();

            long elapsed = ElapsedMicroseconds(watch);
            if (elapsed > slowestMicroseconds)
            {
                slowestMicroseconds = elapsed;
                slowestMission = mission;
                slowestSite = template.Id;
            }

            // Validate is already called inside Generate; calling it again here is
            // deliberate. It proves the public entry point agrees with generation rather
            // than trusting that the same code ran twice for the same reason.
            Assert.True(
                layout.Validate(out string problem),
                $"site {template.Id} tier {tier} mission {mission} failed validation: {problem}");

            AssertInvariantsHold(layout, template.Id, tier, mission);

            int floors = layout.MainSite.Floors.Count;
            floorCounts.TryGetValue(floors, out int seenFloors);
            floorCounts[floors] = seenFloors + 1;

            int rooms = layout.MainSite.RoomCount;
            roomCounts.TryGetValue(rooms, out int seenRooms);
            roomCounts[rooms] = seenRooms + 1;

            long routes = layout.CountRoutesToObjective(1000);
            routeCounts.TryGetValue(routes, out int seenRoutes);
            routeCounts[routes] = seenRoutes + 1;

            totalRooms += rooms;
            totalConnections += layout.Connections.Count;
            guardTotal += layout.Guards.Count;
            civilianTotal += layout.Civilians.Count;

            if (layout.ForwardPost is not null)
                forwardPostCount++;
        }

        _output.WriteLine($"cold first site    {coldStartMicroseconds / 1000.0:F3} ms (JIT warm-up, not asserted)");
        _output.WriteLine(Describe("floor counts", floorCounts));
        _output.WriteLine(Describe("room counts", roomCounts));
        _output.WriteLine(Describe("routes to objective", routeCounts));
        _output.WriteLine($"rooms per site      avg {totalRooms / (double)SiteCount:F2}");
        _output.WriteLine($"connections per site avg {totalConnections / (double)SiteCount:F2}");
        _output.WriteLine($"guards per site      avg {guardTotal / (double)SiteCount:F2}");
        _output.WriteLine($"civilians per site   avg {civilianTotal / (double)SiteCount:F2}");
        _output.WriteLine($"forward posts        {forwardPostCount} of {SiteCount}");
        _output.WriteLine(
            $"slowest single site  {slowestMicroseconds / 1000.0:F3} ms "
            + $"(site {slowestSite}, mission {slowestMission})");

        Assert.True(
            slowestMicroseconds < BudgetMicroseconds,
            $"Generating one site took {slowestMicroseconds / 1000.0:F3} ms, over the "
            + $"{BudgetMicroseconds / 1000.0:F0} ms budget (site {slowestSite}, mission {slowestMission}).");
    }

    /// <summary>
    /// Asserts the brief's six invariants directly, not through <c>Validate</c>.
    /// </summary>
    /// <remarks>
    /// <c>Validate</c> is the generator's own claim about itself, and asserting it against
    /// itself proves only that it is self-consistent. These checks are written out
    /// independently so that a validator which quietly stopped checking something is
    /// caught here rather than being the only thing standing between the generator and a
    /// broken building.
    /// </remarks>
    private static void AssertInvariantsHold(SiteLayout layout, int siteTemplateId, int tier, int mission)
    {
        string where = $"site {siteTemplateId} tier {tier} mission {mission}: ";

        // 1. Every room is reachable from the entrance.
        Assert.True(layout.RoomsUnreachableFromEntrance().Count == 0,
            where + "a room cannot be reached from the entrance.");

        // 2. At least two routes reach the objective that do not share every connection.
        Assert.True(layout.HasAlternateRouteToObjective,
            where + "there is only one route to the objective.");

        // 3. Every floor with a room has at least one vertical connection (vacuous for a
        //    single-floor building, which has no floor to be joined to).
        Assert.True(layout.FloorsWithoutVerticalConnection().Count == 0,
            where + "a floor has rooms but no vertical connection.");

        // 4. No room is narrower than its template minimum; no overlapping intervals.
        var seenRooms = new HashSet<int>();

        foreach (SiteFloor floor in layout.MainSite.Floors)
        {
            Fixed32 cursor = Fixed32.Zero;

            foreach (SiteRoom room in floor.Rooms)
            {
                Assert.True(room.Span.Raw > 0, where + $"{room.Id} has no width.");
                Assert.True(room.StartX >= cursor,
                    where + $"{room.Id} overlaps the room before it on floor {floor.Index}.");
                Assert.True(room.EndX <= floor.Span,
                    where + $"{room.Id} runs past the end of floor {floor.Index}.");
                Assert.True(seenRooms.Add(room.Id.Value), where + $"{room.Id} appears twice.");

                ProjectSpy.Tables.RoomTemplate? template =
                    SimulationRules.TacticalRoomTemplateFor(room.RoomTemplateId);

                if (template is not null)
                {
                    int minimum = Fixed32.FromMetres(template.WidthMin).Raw;
                    Assert.True(room.Span.Raw >= minimum,
                        where + $"{room.Id} is {room.Span.Raw}cm, narrower than its template minimum {minimum}cm.");
                }

                cursor = room.EndX;
            }
        }

        // 5. Every patrol route is traversable by its guard.
        foreach (SiteGuard guard in layout.Guards)
        {
            Assert.True(layout.IsRouteTraversable(guard),
                where + $"guard {guard.Id} has a route it could not walk: "
                + $"[{string.Join(" ", guard.PatrolRoute)}].");
        }

        // 6. At least one extraction point is reachable from the objective.
        Assert.True(layout.ReachableExtractionPoints().Count >= 1,
            where + "no extraction point can be reached from the objective.");
    }

    private static string Describe<TKey>(string title, IEnumerable<KeyValuePair<TKey, int>> counts)
        where TKey : notnull
    {
        var parts = new List<string>();
        foreach (KeyValuePair<TKey, int> entry in counts)
            parts.Add($"{entry.Key}: {entry.Value}");

        return $"{title,-18} {string.Join(", ", parts)}";
    }
}