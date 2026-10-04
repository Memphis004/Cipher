using ProjectSpy.Core.Missions;
using Xunit;
using Xunit.Abstractions;

using SiteTemplateRow = ProjectSpy.Tables.SiteTemplate;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Stale intel: reports the player knows might be out of date, and is told so.
/// </summary>
/// <remarks>
/// knowledge.md rule 17 puts stale information in the middle of the reveal curve, and it
/// is the most delicate thing the snapshot does. Too little and the confidence label is
/// decoration; too much and the player learns to ignore the map, which is the same as
/// having no intel system at all.
/// </remarks>
public class IntelStalenessTests
{
    private readonly ITestOutputHelper _output;

    public IntelStalenessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AFreshReportAtTheStaleBandIsEntirelyCurrent()
    {
        SiteLayout layout = LayoutFor(11005, 1);

        // The staleness window is a day count, so "fresh" has to mean measured against it
        // rather than against the tick the operation started.
        int window = SimulationRules.Intel("intel_stale_after_ticks", 96);

        var operation = Operation(layout, 60, SleeperStatus.Embedded, startedAt: Tick.Zero);
        IntelSnapshot fresh = IntelSnapshotBuilder.Build(layout, operation, new Tick(window));

        Assert.Equal(IntelBand.TypesAndConnections, fresh.Band);
        AssertAllCurrent(fresh);
    }

    [Fact]
    public void AnOldReportAgesSomeOfItsFactsIntoDoubt()
    {
        int window = SimulationRules.Intel("intel_stale_after_ticks", 96);
        int target = SimulationRules.Intel("intel_stale_fact_percent", 35);

        int stale = 0;
        int total = 0;
        int reportsWithSomeDoubt = 0;
        int reports = 0;

        // Swept across every template and both sides of the reveal curve, because a single
        // small site gives a sample of ten or twelve facts and a percentage drawn from
        // that is mostly noise — the first version of this test asserted on one site and
        // failed on exactly that.
        for (int index = 0; index < 200; index++)
        {
            SiteLayout layout = LayoutFor(11001 + (index % 15), index);

            // Inside the band that is allowed to go stale, which is the middle one.
            var operation = Operation(layout, 60, SleeperStatus.Embedded, startedAt: Tick.Zero);
            IntelSnapshot aged = IntelSnapshotBuilder.Build(layout, operation, new Tick(window * 4));

            reports++;

            int here = aged.Entries.Count(e => e.Confidence == IntelConfidence.Stale);

            stale += here;
            total += aged.Entries.Count;

            if (here > 0 && here < aged.Entries.Count)
                reportsWithSomeDoubt++;

            foreach (IntelEntry entry in aged.Entries)
            {
                if (entry.Confidence == IntelConfidence.Stale)
                    Assert.Equal(IntelSource.SleeperPrior, entry.Source);
                else
                    Assert.Equal(IntelSource.Sleeper, entry.Source);
            }

            // Nothing pre-mission is ever Observed. That is what the tactical layer
            // writes, and a snapshot claiming otherwise would be claiming the team has
            // already been inside the building.
            Assert.DoesNotContain(aged.Entries, e => e.Confidence == IntelConfidence.Observed);
        }

        _output.WriteLine($"reports              {reports}");
        _output.WriteLine($"facts                {total}");
        _output.WriteLine($"stale                {stale} ({100.0 * stale / total:F1}%, configured {target}%)");
        _output.WriteLine($"partly stale         {reportsWithSomeDoubt} of {reports}");

        Assert.True(total > 1_000, $"only {total} facts sampled; too few to say anything about a percentage");
        Assert.True(stale > 0, "an operation embedded for a week reported nothing as out of date");

        // Loose, because it is a sample: enough to catch a filter that ages everything or
        // ages nothing, which are the two ways this goes wrong.
        double share = 100.0 * stale / total;
        Assert.True(share > target * 0.6 && share < target * 1.4,
            $"{share:F1}% of facts aged, configured {target}%. Far enough off to mean the "
            + "staleness filter is not drawing at the rate the table asks for.");

        // Scattered, not all-or-nothing: a uniformly stale map and a uniformly current one
        // are indistinguishable to a player deciding what to believe.
        //
        // Not "every single report", because on a small site with eight facts a 35%
        // chance per fact leaves a real chance of drawing none, and demanding that every
        // report be mixed would be asserting the sampler is never unlucky. The property
        // that matters is that reports are overwhelmingly mixed rather than falling into
        // two piles.
        Assert.True(
            reportsWithSomeDoubt > reports * 9 / 10,
            $"{reports - reportsWithSomeDoubt} of {reports} reports were uniformly stale or "
            + "uniformly current, so the confidence label tells the player nothing");
    }

    [Fact]
    public void ACompleteReportIsNeverStale()
    {
        // knowledge.md rule 17: at 100 the report is fully current. A stale fact at full
        // intel would mean the one band where the player has paid for certainty still
        // could not be trusted, which is not a thing worth selling.
        SiteLayout layout = LayoutFor(11013, 3);

        int window = SimulationRules.Intel("intel_stale_after_ticks", 96);

        var operation = Operation(layout, 100, SleeperStatus.Embedded, startedAt: Tick.Zero);
        IntelSnapshot complete = IntelSnapshotBuilder.Build(layout, operation, new Tick(window * 40));

        Assert.Equal(IntelBand.Complete, complete.Band);
        AssertAllCurrent(complete);
    }

    [Fact]
    public void BandsThatForbidStalenessNeverProduceIt()
    {
        SiteLayout layout = LayoutFor(11009, 4);
        int window = SimulationRules.Intel("intel_stale_after_ticks", 96);

        foreach (int percent in new[] { 10, 40, 100 })
        {
            var operation = Operation(layout, percent, SleeperStatus.Embedded, startedAt: Tick.Zero);
            IntelSnapshot snapshot = IntelSnapshotBuilder.Build(layout, operation, new Tick(window * 10));

            AssertAllCurrent(snapshot);
        }
    }

    private static void AssertAllCurrent(IntelSnapshot snapshot)
    {
        foreach (IntelEntry entry in snapshot.Entries)
        {
            Assert.NotEqual(IntelConfidence.Stale, entry.Confidence);
            Assert.NotEqual(IntelSource.SleeperPrior, entry.Source);
        }
    }

    internal static SiteLayout LayoutFor(int siteId, int salt)
    {
        SiteTemplateRow? template = SimulationRules.SiteTemplateFor(siteId);
        Assert.NotNull(template);

        ulong worldSeed = 0x57A1UL + (ulong)salt;
        ulong mapSeed = SiteGenerator.DeriveMapSeed(worldSeed, siteId);

        return SiteGenerator.Generate(siteId, template!.Tier, siteId, worldSeed, mapSeed);
    }

    internal static SleeperOperation Operation(
        SiteLayout layout,
        int percent,
        SleeperStatus status,
        Tick startedAt)
        => new()
        {
            AgentId = new AgentId(1),
            SiteId = layout.SiteTemplateId,
            StartedOnTick = startedAt,
            IntelPercent = percent,
            Status = status,
        };
}

/// <summary>
/// The fog the mission starts with, and what happens when the team finds it wrong.
/// </summary>
public class MissionFogTests
{
    private readonly ITestOutputHelper _output;

    public MissionFogTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AFogSeededFromIntelShowsExactlyWhatTheReportNamed()
    {
        SiteLayout layout = IntelStalenessTests.LayoutFor(11010, 11);
        var operation = IntelStalenessTests.Operation(layout, 60, SleeperStatus.Embedded, Tick.Zero);

        IntelSnapshot snapshot = IntelSnapshotBuilder.Build(layout, operation, Tick.FromDays(1));
        MissionFog fog = MissionFog.FromSnapshot(layout, snapshot);

        Assert.Equal(layout.SiteTemplateId, fog.SiteId);
        Assert.Equal(60, fog.IntelPercent);
        Assert.Equal(0, fog.ContradictionCount);

        foreach (IntelRoomEntry room in snapshot.Rooms)
            Assert.Equal(FogState.Reported, fog.RoomState(room.RoomId));

        foreach (IntelConnectionEntry connection in snapshot.Connections)
            Assert.Equal(FogState.Reported, fog.ConnectionState(connection.ConnectionId));

        // A forward post's rooms are in no report at all, and must stay invisible: the
        // player is told about the building, not about the van parked behind it.
        foreach (SiteRoom room in layout.Rooms)
        {
            if (snapshot.Room(room.Id) is null)
                Assert.Equal(FogState.Unknown, fog.RoomState(room.Id));
        }
    }

    [Fact]
    public void AFogWithNoIntelIsBlankRatherThanBroken()
    {
        SiteLayout layout = IntelStalenessTests.LayoutFor(11010, 12);
        MissionFog fog = MissionFog.FromSnapshot(layout, null);

        Assert.Equal(-1, fog.IntelPercent);

        foreach (SiteRoom room in layout.Rooms)
            Assert.Equal(FogState.Unknown, fog.RoomState(room.Id));

        foreach (SiteConnection connection in layout.Connections)
            Assert.Equal(FogState.Unknown, fog.ConnectionState(connection.Id));
    }

    [Fact]
    public void KnowledgeOnlyEverMovesUp()
    {
        var fog = new MissionFog();
        var room = new SiteRoomId(1);
        var connection = new SiteConnectionId(1);

        Assert.True(fog.Promote(room, FogState.Reported));
        Assert.Equal(FogState.Reported, fog.RoomState(room));

        Assert.True(fog.Scout(room));
        Assert.Equal(FogState.Scouted, fog.RoomState(room));

        Assert.True(fog.Promote(room, FogState.Observed));
        Assert.Equal(FogState.Observed, fog.RoomState(room));

        Assert.True(fog.Clear(room));
        Assert.Equal(FogState.Cleared, fog.RoomState(room));

        // Every attempt to walk it back is refused. A map that un-revealed itself as the
        // team moved would flicker, and a player who cannot trust the map to stay drawn
        // is playing a different game.
        Assert.False(fog.Promote(room, FogState.Observed));
        Assert.False(fog.Promote(room, FogState.Reported));
        Assert.False(fog.Promote(room, FogState.Unknown));
        Assert.False(fog.Scout(room));
        Assert.Equal(FogState.Cleared, fog.RoomState(room));

        // A connection nobody has heard of can be observed in one step: the ladder is
        // monotone, not mandatory, so a gadget or a walk can skip Reported entirely.
        Assert.True(fog.Promote(connection, FogState.Observed));
        Assert.False(fog.Promote(connection, FogState.Unknown));
        Assert.Equal(FogState.Observed, fog.ConnectionState(connection));

        // Promotion from Unknown is always allowed, including straight past Reported.
        var fresh = new SiteRoomId(99);
        Assert.True(fog.Promote(fresh, FogState.Observed));
        Assert.Equal(FogState.Observed, fog.RoomState(fresh));
    }

    [Fact]
    public void WalkingIntoTheClaimedObjectiveRevealsWhetherTheReportWasRight()
    {
        int contradictionsRaised = 0;
        int poisonedReports = 0;

        for (int index = 0; index < 300; index++)
        {
            SiteLayout layout = IntelStalenessTests.LayoutFor(
                11001 + (index % 15), index);

            var operation = IntelStalenessTests.Operation(layout, 100, SleeperStatus.Poisoned, Tick.Zero);
            IntelSnapshot snapshot = IntelSnapshotBuilder.Build(layout, operation, Tick.FromDays(3));

            if (!snapshot.IsPoisoned)
                continue;

            poisonedReports++;

            var sink = new RecordingSink();
            var fog = MissionFog.FromSnapshot(layout, snapshot);

            // Walk into the room the report says holds the objective.
            SiteRoomId claimed = snapshot.ClaimedObjective!.Value;

            IReadOnlyList<IntelContradicted> found =
                fog.ObserveRoom(claimed, layout, snapshot, Tick.FromDays(4), sink);

            if (claimed == layout.ObjectiveRoomId)
            {
                Assert.Empty(found);
            }
            else
            {
                Assert.NotEmpty(found);
                contradictionsRaised += found.Count;
            }

            // Seeing the room always promotes it, whatever the report claimed.
            Assert.Equal(FogState.Observed, fog.RoomState(claimed));

            if (found.Count > 0)
                Assert.True(sink.Saw(GameEventKind.IntelContradicted));
        }

        _output.WriteLine($"poisoned reports     {poisonedReports}");
        _output.WriteLine($"contradictions       {contradictionsRaised}");

        Assert.True(poisonedReports > 100, "not enough poisoned reports to make this meaningful");
        Assert.True(contradictionsRaised > 0, "no poisoned report was ever caught being wrong");
    }

    [Fact]
    public void AnHonestReportIsNeverContradicted()
    {
        for (int index = 0; index < 200; index++)
        {
            SiteLayout layout = IntelStalenessTests.LayoutFor(11001 + (index % 15), index + 900);

            var operation = IntelStalenessTests.Operation(layout, 100, SleeperStatus.Embedded, Tick.Zero);
            IntelSnapshot snapshot = IntelSnapshotBuilder.Build(layout, operation, Tick.FromDays(3));

            var sink = new RecordingSink();
            var fog = MissionFog.FromSnapshot(layout, snapshot);

            foreach (SiteRoom room in layout.Rooms)
                Assert.Empty(fog.ObserveRoom(room.Id, layout, snapshot, Tick.FromDays(4), sink));

            foreach (SiteConnection connection in layout.Connections)
                Assert.Empty(fog.ObserveConnection(connection.Id, layout, snapshot, Tick.FromDays(4), sink));

            foreach (SiteGuard guard in layout.Guards)
                Assert.Empty(fog.ObservePatrol(guard.Id, layout, snapshot, Tick.FromDays(4), sink));

            Assert.Equal(0, fog.ContradictionCount);
            Assert.False(sink.Saw(GameEventKind.IntelContradicted));
        }
    }
}

/// <summary>
/// The rescue: a prisoner somewhere in a building, and a room the player may or may not
/// be able to name.
/// </summary>
public class IntelRescueTests
{
    [Fact]
    public void OnlyACompleteReportNamesTheHoldingRoom()
    {
        SiteLayout layout = IntelStalenessTests.LayoutFor(11010, 21);

        ProjectSpy.Tables.SiteTemplate? site = SimulationRules.SiteTemplateFor(layout.SiteTemplateId);
        Assert.NotNull(site);

        ProjectSpy.Tables.CaptureSite? facility = SimulationRules.CaptureSiteFor(site!.Tier);
        Assert.NotNull(facility);

        var prisoner = World.Hire(new GameSession(0xF00DUL));
        var capture = new CaptureRecord
        {
            AgentId = prisoner.Id,
            CaptureSiteId = facility!.Id,
            HostSiteId = layout.SiteTemplateId,
            CapturedOnTick = Tick.Zero,
            TicksUntilLost = facility.RescueTimeLimitDays * Tick.TicksPerDay,
        };

        var captures = new List<CaptureRecord> { capture };

        // Every band below full intel: the team knows the prisoner is in the building and
        // nothing else, so a rescue is a search rather than a walk to a door.
        foreach (int percent in new[] { 0, 24, 25, 49, 50, 74, 75, 99 })
        {
            var operation = IntelStalenessTests.Operation(layout, percent, SleeperStatus.Embedded, Tick.Zero);
            IntelSnapshot snapshot = IntelSnapshotBuilder.Build(layout, operation, Tick.FromDays(1), captures);

            Assert.Equal(IntelBands.For(percent), snapshot.Band);
            Assert.Empty(snapshot.Holdings);
        }

        // At 100 the room is named, with the clock still running on it.
        var complete = IntelStalenessTests.Operation(layout, 100, SleeperStatus.Embedded, Tick.Zero);
        IntelSnapshot full = IntelSnapshotBuilder.Build(layout, complete, Tick.FromDays(1), captures);

        IntelHoldingEntry holding = Assert.Single(full.Holdings);

        Assert.Equal(prisoner.Id, holding.AgentId);
        Assert.Equal(capture.TicksUntilLost, holding.TicksUntilLost);
        Assert.Equal(IntelSource.CaptureRecord, holding.Source);
        Assert.NotNull(layout.Find(holding.RoomId));

        // And it is deterministic: the same report names the same room.
        IntelHoldingEntry again = Assert.Single(
            IntelSnapshotBuilder.Build(layout, complete, Tick.FromDays(1), captures).Holdings);

        Assert.Equal(holding.RoomId, again.RoomId);
    }

    [Fact]
    public void TheCountdownKeepsRunningWhileTheTeamSearches()
    {
        SiteLayout layout = IntelStalenessTests.LayoutFor(11005, 31);

        ProjectSpy.Tables.SiteTemplate? site = SimulationRules.SiteTemplateFor(layout.SiteTemplateId);
        ProjectSpy.Tables.CaptureSite? facility = SimulationRules.CaptureSiteFor(site!.Tier);

        var session = new GameSession(0xF00DUL);
        var sink = new RecordingSink();

        Agent agent = World.Hire(session);
        agent.Skills = new SkillSet(Infiltration: 100, Combat: 0, Tech: 0, Social: 100, Nerve: 0);

        var capture = new CaptureRecord
        {
            AgentId = agent.Id,
            CaptureSiteId = facility!.Id,
            HostSiteId = layout.SiteTemplateId,
            CapturedOnTick = Tick.Zero,
            TicksUntilLost = 20,
        };

        session.World.Captures.Add(capture);

        // An operation is running, so the tick that would normally renew the report is
        // happening; the countdown must tick down alongside it rather than only when a
        // mission is dispatched.
        SleeperSystem.Start(session.World, agent.Id, layout.SiteTemplateId, Tick.Zero, sink);

        for (int tick = 1; tick <= 10; tick++)
        {
            SleeperSystem.Tick(session.World, new Tick(tick), sink);
            Assert.Equal(20 - tick, capture.TicksUntilLost);
        }

        Assert.Single(session.World.Captures);

        for (int tick = 11; tick <= 25; tick++)
            SleeperSystem.Tick(session.World, new Tick(tick), sink);

        Assert.Empty(session.World.Captures);
        Assert.True(capture.IsLost);
        Assert.True(sink.Saw(GameEventKind.AgentLost));

        // A lost prisoner is not reported as being somewhere.
        var operation = IntelStalenessTests.Operation(layout, 100, SleeperStatus.Embedded, Tick.Zero);
        IntelSnapshot snapshot = IntelSnapshotBuilder.Build(
            layout, operation, Tick.FromDays(2), session.World.Captures);

        Assert.Empty(snapshot.Holdings);
    }

    [Fact]
    public void APrisonerHeldSomewhereElseIsNotReportedOnThisSite()
    {
        SiteLayout layout = IntelStalenessTests.LayoutFor(11010, 41);

        ProjectSpy.Tables.SiteTemplate? site = SimulationRules.SiteTemplateFor(layout.SiteTemplateId);
        ProjectSpy.Tables.CaptureSite? facility = SimulationRules.CaptureSiteFor(site!.Tier);

        var otherSite = 11001 == layout.SiteTemplateId ? 11002 : 11001;
        var agent = World.Hire(new GameSession(0xBEEFUL));

        var captures = new List<CaptureRecord>
        {
            new()
            {
                AgentId = agent.Id,
                CaptureSiteId = facility!.Id,
                HostSiteId = otherSite,
                CapturedOnTick = Tick.Zero,
                TicksUntilLost = 100,
            },
        };

        var operation = IntelStalenessTests.Operation(layout, 100, SleeperStatus.Embedded, Tick.Zero);
        IntelSnapshot snapshot = IntelSnapshotBuilder.Build(layout, operation, Tick.FromDays(1), captures);

        Assert.Empty(snapshot.Holdings);
    }
}