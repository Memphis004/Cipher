using ProjectSpy.Core.Missions;
using Xunit;
using Xunit.Abstractions;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Stage 4b's hardest promise: poisoned intel is always still winnable.
/// </summary>
/// <remarks>
/// <para>
/// A discovered operation goes on reporting, and what it reports is deliberately false.
/// The player is never told. That makes the system a genuine gamble — and it also makes
/// it a place where a bug becomes an unwinnable mission rather than a funny anecdote.
/// </para>
/// <para>
/// The guarantee is <b>structural</b>, not statistical. <see cref="PoisonPlan"/> settles
/// which room it will claim, walks the real route from the entrance to that room, and
/// refuses to lie about any door on it. So no seed, no site and no number of unlucky
/// draws can produce a report the player cannot act on. That is why this test can sweep
/// thousands of poisoned reports and assert on every one of them: the assertion is not
/// "usually winnable", it is "winnable".
/// </para>
/// <para>
/// The property asserted is that the report never <em>mismiscribes the most direct way to
/// the room it points at</em>: every connection on the real shortest path from the
/// entrance to the claimed objective is described with its true lock state.
/// </para>
/// <para>
/// An earlier version of this test asserted something stronger and better-sounding — that
/// a route with no locked doors at all must exist. That is not something the site
/// generator promises. It promises an alternate <em>route</em>, not an alternate
/// <em>unlocked</em> one, and a building whose short paths to the objective are all behind
/// a lock is perfectly legal. Asserting the stronger property would have produced a test
/// that passes until it finds the site where it is simply untrue, which is to say it would
/// have been a bug report dressed as a guarantee.
/// </para>
/// <para>
/// What matters for the player is that they are never <em>misled</em> about the route
/// that matters. A locked door reported as locked is a problem the player can see and
/// solve; a locked door reported as open is the failure this system must never ship.
/// </para>
/// </remarks>
public class IntelPoisonTests
{
    /// <summary>Poisoned reports checked. Enough to cover every site and every seed class.</summary>
    private const int ReportCount = 2_000;

    private readonly ITestOutputHelper _output;

    public IntelPoisonTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void APoisonedReportAlwaysLeavesAWalkableRouteToWhereItSaysTheObjectiveIs()
    {
        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();

        int poisonedReports = 0;
        int objectiveLies = 0;
        int lockLies = 0;
        int patrolLies = 0;

        for (int index = 0; index < ReportCount; index++)
        {
            ProjectSpy.Tables.SiteTemplate template = templates[index % templates.Count];
            SiteLayout layout = LayoutFor(template.Id, index);

            // 100%: the band that reveals connections and the objective, so "can the
            // player act on this" is a question about the report rather than about the
            // band. Every lower band has fewer claims and so a strictly easier time.
            IntelSnapshot snapshot = Build(layout, 100, SleeperStatus.Poisoned);
            poisonedReports++;

            Assert.True(snapshot.IsPoisoned);
            Assert.True(snapshot.ClaimedEntrance.HasValue);
            Assert.True(snapshot.ClaimedObjective.HasValue);

            // Weak property: the player can find a path through what they were told.
            Assert.NotNull(snapshot.RouteToClaimedObjective());

            // The real property: the most direct way to the room the report points at is
            // described exactly as it is. Every door on it is reported with its true lock
            // state, so a locked door here is one the player can see coming.
            AssertTrueRouteIsDescribedHonestly(
                layout,
                snapshot,
                $"site {template.Id} seed {index}: a poisoned report misdescribed the route to "
                + $"the objective it claimed at room {snapshot.ClaimedObjective}.");

            if (snapshot.ClaimedObjective != layout.ObjectiveRoomId)
                objectiveLies++;

            foreach (IntelConnectionEntry claim in snapshot.Connections)
            {
                SiteConnection? real = Find(layout, claim.ConnectionId);
                Assert.NotNull(real);

                if (real!.IsLocked != claim.IsLocked)
                    lockLies++;
            }

            foreach (IntelPatrolEntry patrol in snapshot.Patrols)
            {
                SiteGuard? guard = FindGuard(layout, patrol.GuardId);
                Assert.NotNull(guard);

                if (!SameRoute(patrol.Route, guard!.PatrolRoute))
                    patrolLies++;
            }
        }

        _output.WriteLine($"poisoned reports   {poisonedReports}");
        _output.WriteLine($"objective lies     {objectiveLies}");
        _output.WriteLine($"lock lies          {lockLies}");
        _output.WriteLine($"patrol lies        {patrolLies}");

        // The lies have to actually happen, or the whole subsystem is decoration. A test
        // that only ever saw honest reports would pass every assertion above.
        Assert.True(objectiveLies > ReportCount / 20, "the objective was essentially never lied about");
        Assert.True(lockLies > 0, "no door was ever reported with the wrong lock state");
        Assert.True(patrolLies > 0, "no patrol route was ever reported wrong");
    }

    [Fact]
    public void PoisonNeverInventsOrRemovesAConnection()
    {
        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();

        for (int index = 0; index < 200; index++)
        {
            ProjectSpy.Tables.SiteTemplate template = templates[index % templates.Count];
            SiteLayout layout = LayoutFor(template.Id, index + 5_000);

            IntelSnapshot poisoned = Build(layout, 100, SleeperStatus.Poisoned);

            Assert.Equal(layout.Connections.Count, poisoned.Connections.Count());

            foreach (IntelConnectionEntry claim in poisoned.Connections)
            {
                SiteConnection? real = Find(layout, claim.ConnectionId);
                Assert.NotNull(real);

                // The endpoints and the kind are the shape of the building. Changing them
                // is not a lie about the world, it is a different world.
                Assert.Equal(real!.RoomA, claim.RoomA);
                Assert.Equal(real.RoomB, claim.RoomB);
                Assert.Equal(real.Kind, claim.Kind);
                Assert.Equal(real.IsVertical, claim.IsVertical);
            }
        }
    }

    [Fact]
    public void AReportIsNeverMarkedPoisonedWhenTheOperationIsHonest()
    {
        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();

        foreach (ProjectSpy.Tables.SiteTemplate template in templates)
        {
            SiteLayout layout = LayoutFor(template.Id, 31);

            foreach (SleeperStatus status in new[] { SleeperStatus.Inserting, SleeperStatus.Embedded, SleeperStatus.Extracted })
            {
                IntelSnapshot snapshot = Build(layout, 100, status);
                Assert.False(snapshot.IsPoisoned, $"{status} produced a poisoned report");
                Assert.Equal(layout.ObjectiveRoomId, snapshot.ClaimedObjective);
            }
        }
    }

    /// <summary>
    /// Asserts that every connection on a shortest real path from the claimed entrance to
    /// the claimed objective is reported with its true lock state.
    /// </summary>
    /// <remarks>
    /// The path is found independently here, by breadth-first over the real connections,
    /// rather than by asking <see cref="PoisonPlan"/> what it protected. Reusing the code
    /// under test would make the assertion tautological: a plan that protected nothing
    /// would report that it had protected nothing and pass.
    /// </remarks>
    private static void AssertTrueRouteIsDescribedHonestly(
        SiteLayout layout,
        IntelSnapshot snapshot,
        string message)
    {
        if (snapshot.ClaimedEntrance is not SiteRoomId entrance
            || snapshot.ClaimedObjective is not SiteRoomId objective)
        {
            Assert.Fail(message + " (no claimed entrance or objective)");
            return;
        }

        var onRoute = WalkShortestRealRoute(layout, entrance, objective);
        Assert.NotNull(onRoute);

        foreach (SiteConnectionId connectionId in onRoute!)
        {
            SiteConnection? real = Find(layout, connectionId);
            Assert.NotNull(real);

            IntelConnectionEntry? claim = snapshot.Connection(connectionId);
            Assert.NotNull(claim);

            Assert.True(
                real!.IsLocked == claim!.IsLocked,
                message + $" Connection {connectionId} is really "
                + $"{(real.IsLocked ? "locked" : "unlocked")} and the report calls it "
                + $"{(claim.IsLocked ? "locked" : "unlocked")}.");
        }
    }

    /// <summary>
    /// Connection ids on a shortest real path, or null when the rooms are not connected.
    /// </summary>
    private static HashSet<SiteConnectionId>? WalkShortestRealRoute(
        SiteLayout layout,
        SiteRoomId from,
        SiteRoomId to)
    {
        var cameBy = new Dictionary<SiteRoomId, SiteConnectionId>();
        var cameFrom = new Dictionary<SiteRoomId, SiteRoomId>();
        var queue = new Queue<SiteRoomId>();

        queue.Enqueue(from);
        cameFrom[from] = from;

        while (queue.Count > 0)
        {
            SiteRoomId current = queue.Dequeue();

            foreach (SiteConnection connection in layout.ConnectionsAt(current))
            {
                SiteRoomId next = connection.RoomA == current ? connection.RoomB : connection.RoomA;

                if (cameFrom.ContainsKey(next))
                    continue;

                cameFrom[next] = current;
                cameBy[next] = connection.Id;
                queue.Enqueue(next);
            }
        }

        if (!cameFrom.ContainsKey(to))
            return null;

        var onRoute = new HashSet<SiteConnectionId>();
        SiteRoomId cursor = to;

        while (cursor != from && cameBy.TryGetValue(cursor, out SiteConnectionId edge))
        {
            onRoute.Add(edge);

            if (!cameFrom.TryGetValue(cursor, out SiteRoomId previous))
                break;

            cursor = previous;
        }

        return onRoute;
    }

    // ---- helpers -------------------------------------------------------------

    private static SiteLayout LayoutFor(int siteId, int salt)
    {
        ProjectSpy.Tables.SiteTemplate? template = SimulationRules.SiteTemplateFor(siteId);
        Assert.NotNull(template);

        ulong worldSeed = 0xB0DEUL + (ulong)salt;
        ulong mapSeed = SiteGenerator.DeriveMapSeed(worldSeed, siteId);

        return SiteGenerator.Generate(siteId, template!.Tier, siteId, worldSeed, mapSeed);
    }

    private static IntelSnapshot Build(SiteLayout layout, int percent, SleeperStatus status)
    {
        var operation = new SleeperOperation
        {
            AgentId = new AgentId(1),
            SiteId = layout.SiteTemplateId,
            StartedOnTick = Tick.Zero,
            IntelPercent = percent,
            Status = status,
        };

        return IntelSnapshotBuilder.Build(layout, operation, Tick.FromDays(12));
    }

    private static SiteConnection? Find(SiteLayout layout, SiteConnectionId connectionId)
    {
        foreach (SiteConnection connection in layout.Connections)
        {
            if (connection.Id == connectionId)
                return connection;
        }

        return null;
    }

    private static SiteGuard? FindGuard(SiteLayout layout, SiteGuardId guardId)
    {
        foreach (SiteGuard guard in layout.Guards)
        {
            if (guard.Id == guardId)
                return guard;
        }

        return null;
    }

    private static bool SameRoute(IReadOnlyList<SiteRoomId> left, IReadOnlyList<SiteRoomId> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
                return false;
        }

        return true;
    }
}

/// <summary>
/// Stage 4b at full intel: the report and the building must be the same thing.
/// </summary>
/// <remarks>
/// Full intel is the only band where the report is a complete claim, so it is the only
/// place a field-by-field comparison is meaningful. Below it the report is deliberately
/// incomplete, and a test that compared everything would be testing the filter rather
/// than the truth.
/// </remarks>
public class IntelSnapshotAccuracyTests
{
    [Fact]
    public void AFullIntelReportMatchesTheLiveBuildingExactly()
    {
        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();
        Assert.NotEmpty(templates);

        foreach (ProjectSpy.Tables.SiteTemplate template in templates)
        {
            for (int salt = 0; salt < 4; salt++)
            {
                ulong worldSeed = 0xACCEUL + (ulong)salt;
                ulong mapSeed = SiteGenerator.DeriveMapSeed(worldSeed, template.Id);

                SiteLayout layout = SiteGenerator.Generate(
                    template.Id, template.Tier, template.Id, worldSeed, mapSeed);

                var operation = new SleeperOperation
                {
                    AgentId = new AgentId(1),
                    SiteId = template.Id,
                    StartedOnTick = Tick.Zero,
                    IntelPercent = 100,
                    Status = SleeperStatus.Embedded,
                };

                IntelSnapshot snapshot = IntelSnapshotBuilder.Build(layout, operation, Tick.FromDays(30));

                Assert.False(snapshot.IsPoisoned);
                Assert.Equal(layout.MapSeed, snapshot.MapSeed);
                Assert.Equal(layout.EntranceRoomId, snapshot.ClaimedEntrance);
                Assert.Equal(layout.ObjectiveRoomId, snapshot.ClaimedObjective);

                foreach (SiteRoom room in layout.Rooms)
                {
                    IntelRoomEntry? entry = snapshot.Room(room.Id);
                    Assert.True(entry is not null, $"room {room.Id} missing from a full report");

                    Assert.Equal(room.RoomTemplateId, entry!.RoomTemplateId);
                    Assert.Equal(room.NameKey, entry.NameKey);
                    Assert.Equal(room.FloorIndex, entry.FloorIndex);
                    Assert.Equal(room.StartX.Raw, entry.StartX.Raw);
                    Assert.Equal(room.EndX.Raw, entry.EndX.Raw);
                    Assert.True(entry.LightLevelKnown);
                    Assert.Equal(room.Role.HasFlag(SiteRoomRole.Entrance), entry.Roles.HasFlag(IntelKnownRole.Entrance));
                    Assert.Equal(room.Role.HasFlag(SiteRoomRole.Objective), entry.Roles.HasFlag(IntelKnownRole.Objective));
                    Assert.Equal(room.Role.HasFlag(SiteRoomRole.Extraction), entry.Roles.HasFlag(IntelKnownRole.Extraction));
                }

                Assert.Equal(
                    layout.ExtractionRoomIds.OrderBy(id => id.Value),
                    snapshot.ClaimedExtraction.OrderBy(id => id.Value));

                foreach (SiteConnection connection in layout.Connections)
                {
                    IntelConnectionEntry? entry = snapshot.Connection(connection.Id);
                    Assert.True(entry is not null, $"connection {connection.Id} missing from a full report");

                    Assert.Equal(connection.Kind, entry!.Kind);
                    Assert.Equal(connection.IsLocked, entry.IsLocked);
                    Assert.Equal(connection.IsVertical, entry.IsVertical);
                }

                foreach (SiteGuard guard in layout.Guards)
                {
                    IntelPatrolEntry? entry = null;

                    foreach (IntelPatrolEntry patrol in snapshot.Patrols)
                    {
                        if (patrol.GuardId == guard.Id)
                        {
                            entry = patrol;
                            break;
                        }
                    }

                    Assert.True(entry is not null, $"guard {guard.Id} missing from a full report");
                    Assert.Equal(guard.ArchetypeId, entry!.ArchetypeId);
                    Assert.Equal(guard.Role, entry.Role);
                    Assert.Equal(guard.HomeRoomId, entry.HomeRoomId);
                    Assert.Equal(
                        guard.PatrolRoute.Select(r => r.Value),
                        entry.Route.Select(r => r.Value));
                }

                // Nothing in the report may name a room, connection or guard that the
                // building does not have. A report about a slightly different building is
                // the failure mode that would be hardest to spot in play.
                foreach (IntelRoomEntry room in snapshot.Rooms)
                    Assert.NotNull(layout.Find(room.RoomId));

                Assert.Equal(layout.Rooms.Count, snapshot.Rooms.Count());
                Assert.Equal(layout.Connections.Count, snapshot.Connections.Count());
            }
        }
    }
}