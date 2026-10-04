using ProjectSpy.Core;
using ProjectSpy.Core.Tactical;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves the three tactical-era slots on <see cref="WorldState"/> exist, hold what
/// they claim to, and are covered by the two equality checks that keep a
/// determinism test honest.
/// </summary>
/// <remarks>
/// The hash and canonical-dump assertions are the load-bearing part. A field that is
/// not in either is a field the determinism test cannot see, which means a save
/// could differ from its replay in exactly that field and the test would pass.
/// </remarks>
public class TacticalWorldStateTests
{
    private static WorldState NewWorld() => new(20240101UL);

    // ---- ActiveMission -------------------------------------------------------

    [Fact]
    public void ANewWorldHasNoActiveMission()
    {
        Assert.Null(NewWorld().ActiveMission);
    }

    [Fact]
    public void AnActiveMissionIsSerializableState()
    {
        // Stage 4c gave TacticalState a building to live in, so a mission with no layout
        // can no longer be hashed or played — which is right, because such a thing is not
        // a mission. The identity assertions below therefore check that the slot carries
        // the factory's own values, not hand-written ones.
        WorldState world = NewWorld();
        world.ActiveMission = TacticalHarness.MissionAt(7_777);

        Assert.NotNull(world.ActiveMission);
        Assert.Equal(TacticalHarness.MissionId, world.ActiveMission!.MissionId);
        Assert.Equal(TacticalHarness.WarehouseTemplate, world.ActiveMission.SiteId);
        Assert.Equal(Tick.Zero.Value, world.ActiveMission.StartedOnTick.Value);
        Assert.Equal(7_777, world.ActiveMission.Step);
        Assert.False(world.ActiveMission.TimeConverted);
    }

    [Fact]
    public void TheActiveMissionIsDistinguishedFromTheDeploymentList()
    {
        // Two different questions: ActiveMissions tracks a mission across travel and
        // resolution, ActiveMission is only non-null while it is being played.
        WorldState world = NewWorld();
        world.ActiveMissions[3] = new MissionState { Id = 3, TypeId = 1 };

        Assert.Single(world.ActiveMissions);
        Assert.Null(world.ActiveMission);

        world.ActiveMission = TacticalHarness.MissionAt(0);

        Assert.Single(world.ActiveMissions);
        Assert.NotNull(world.ActiveMission);
    }

    // ---- SleeperOperations --------------------------------------------------

    [Fact]
    public void SleeperOperationsStartEmptyAndHoldTheirFields()
    {
        WorldState world = NewWorld();
        Assert.Empty(world.SleeperOperations);

        world.SleeperOperations.Add(new SleeperOperation
        {
            AgentId = new AgentId(5),
            SiteId = 2,
            StartedOnTick = new Tick(96),
            IntelPercent = 40,
        });

        SleeperOperation sleeper = Assert.Single(world.SleeperOperations);
        Assert.Equal(5, sleeper.AgentId.Value);
        Assert.Equal(2, sleeper.SiteId);
        Assert.Equal(96, sleeper.StartedOnTick.Value);
        Assert.Equal(40, sleeper.IntelPercent);
        Assert.Equal(SleeperStatus.Inserting, sleeper.Status);
        Assert.Null(sleeper.DiscoveredOnTick);
        Assert.True(sleeper.IsActive);
    }

    [Fact]
    public void TwoOperationsAgainstOneSiteAreBothKept()
    {
        // A list, not a per-site dictionary: running two agents at one site is legal.
        WorldState world = NewWorld();
        world.SleeperOperations.Add(new SleeperOperation { AgentId = new AgentId(1), SiteId = 2 });
        world.SleeperOperations.Add(new SleeperOperation { AgentId = new AgentId(2), SiteId = 2 });

        Assert.Equal(2, world.SleeperOperations.Count);
        Assert.All(world.SleeperOperations, s => Assert.Equal(2, s.SiteId));
    }

    [Fact]
    public void ABurnedOrDiscoveredOperationIsNoLongerActive()
    {
        var burned = new SleeperOperation { Status = SleeperStatus.Burned };
        var discovered = new SleeperOperation { Status = SleeperStatus.Discovered };
        var extracted = new SleeperOperation { Status = SleeperStatus.Extracted };

        Assert.False(burned.IsActive);
        Assert.False(discovered.IsActive);
        Assert.False(extracted.IsActive);

        // Poisoned still reports, which is the entire risk of the system.
        Assert.True(new SleeperOperation { Status = SleeperStatus.Poisoned }.IsActive);
    }

    // ---- IntelSnapshots -----------------------------------------------------

    [Fact]
    public void IntelSnapshotsAreKeyedBySiteAndHoldATimestamp()
    {
        WorldState world = NewWorld();
        Assert.Empty(world.IntelSnapshots);

        world.IntelSnapshots[4] = new IntelSnapshot
        {
            SiteId = 4,
            TakenOnTick = new Tick(1_200),
            IntelPercent = 80,
        };

        Assert.Single(world.IntelSnapshots);
        IntelSnapshot snapshot = world.IntelSnapshots[4];
        Assert.Equal(4, snapshot.SiteId);
        Assert.Equal(1_200, snapshot.TakenOnTick.Value);
        Assert.Equal(80, snapshot.IntelPercent);
        Assert.False(snapshot.IsPoisoned);
    }

    [Fact]
    public void ANewSnapshotForTheSameSiteReplacesTheOldOne()
    {
        // Keyed by site so the collection stays bounded: a hundred spyings on one site
        // hold one snapshot, not a hundred.
        WorldState world = NewWorld();
        world.IntelSnapshots[4] = new IntelSnapshot { SiteId = 4, IntelPercent = 20 };
        world.IntelSnapshots[4] = new IntelSnapshot { SiteId = 4, IntelPercent = 90 };

        Assert.Single(world.IntelSnapshots);
        Assert.Equal(90, world.IntelSnapshots[4].IntelPercent);
    }

    [Fact]
    public void APoisonedSnapshotIsMarkedButNotHiddenFromCore()
    {
        // The flag exists so tests and the debrief can assert poisoned intel stays
        // solvable. It is not a presentation flag: Core must be able to tell the
        // difference to write that assertion.
        var snapshot = new IntelSnapshot { SiteId = 1, IntelPercent = 50, IsPoisoned = true };

        Assert.True(snapshot.IsPoisoned);
    }

    // ---- they participate in the state fingerprint --------------------------

    [Fact]
    public void TheStateHashDistinguishesAnActiveMissionFromNone()
    {
        WorldState without = NewWorld();
        WorldState with = NewWorld();
        with.ActiveMission = TacticalHarness.MissionAt(5);

        Assert.NotEqual(without.ComputeStateHash(), with.ComputeStateHash());
    }

    [Fact]
    public void TheStateHashDistinguishesSleeperProgress()
    {
        WorldState quiet = NewWorld();
        WorldState busy = NewWorld();
        busy.SleeperOperations.Add(new SleeperOperation { AgentId = new AgentId(1), SiteId = 2, IntelPercent = 60 });

        Assert.NotEqual(quiet.ComputeStateHash(), busy.ComputeStateHash());
    }

    [Fact]
    public void TheStateHashDistinguishesIntelSnapshots()
    {
        WorldState blind = NewWorld();
        WorldState informed = NewWorld();
        informed.IntelSnapshots[2] = new IntelSnapshot { SiteId = 2, IntelPercent = 75 };

        Assert.NotEqual(blind.ComputeStateHash(), informed.ComputeStateHash());
    }

    [Fact]
    public void TheStateHashIgnoresCollectionInsertionOrder()
    {
        // Sorted iteration: a hash that depended on insertion order would make two
        // identical worlds compare unequal purely because of how they were built.
        WorldState a = NewWorld();
        a.SleeperOperations.Add(new SleeperOperation { AgentId = new AgentId(1), SiteId = 2 });
        a.SleeperOperations.Add(new SleeperOperation { AgentId = new AgentId(2), SiteId = 3 });

        WorldState b = NewWorld();
        b.SleeperOperations.Add(new SleeperOperation { AgentId = new AgentId(2), SiteId = 3 });
        b.SleeperOperations.Add(new SleeperOperation { AgentId = new AgentId(1), SiteId = 2 });

        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    // ---- and in the canonical dump ------------------------------------------

    [Fact]
    public void TheCanonicalDumpCarriesTheTacticalState()
    {
        WorldState world = NewWorld();
        world.ActiveMission = TacticalHarness.MissionAt(900);
        world.SleeperOperations.Add(new SleeperOperation { AgentId = new AgentId(1), SiteId = 3, IntelPercent = 55 });
        world.IntelSnapshots[3] = new IntelSnapshot { SiteId = 3, TakenOnTick = new Tick(24), IntelPercent = 55 };

        string text = WorldStateSerializer.ToCanonicalText(world);

        Assert.Contains("tactical.active=1", text, StringComparison.Ordinal);
        Assert.Contains(
            $"tactical.mission={TacticalHarness.MissionId}", text, StringComparison.Ordinal);
        Assert.Contains("tactical.step=900", text, StringComparison.Ordinal);
        Assert.Contains("sleeper.agent=1", text, StringComparison.Ordinal);
        Assert.Contains("sleeper.intelPercent=55", text, StringComparison.Ordinal);
        Assert.Contains("intelSnapshot.site=3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCanonicalDumpSaysWhenThereIsNoMissionRatherThanOmittingIt()
    {
        // A world with no mission and a dump that simply skips the field look the
        // same, and the omission is invisible in a diff of a failing test.
        WorldState world = NewWorld();

        string text = WorldStateSerializer.ToCanonicalText(world);

        Assert.Contains("tactical.active=0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoWorldsDifferingOnlyInTheTacticalStateHaveDifferentDigests()
    {
        WorldState a = NewWorld();
        WorldState b = NewWorld();
        b.ActiveMission = TacticalHarness.MissionAt(1);

        Assert.NotEqual(
            WorldStateSerializer.ComputeCanonicalDigest(a),
            WorldStateSerializer.ComputeCanonicalDigest(b));
    }
}