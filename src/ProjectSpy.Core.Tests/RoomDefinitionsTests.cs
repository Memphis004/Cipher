using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves Core reads room balance data from the compiled tables rather than
/// hard-coding it, and that an unknown id degrades instead of crashing.
/// </summary>
public class RoomDefinitionsTests
{
    private static WorldState NewWorld()
    {
        var world = new WorldState(12345);
        RoomDefinitions.ApplyDepthRequirements(world.BaseLayout);
        return world;
    }

    [Fact]
    public void KnownRoomType_LooksUpItsBuildTicksFromTheTable()
    {
        WorldState world = NewWorld();

        // Gym (4001) is 48 build ticks in room_type.csv.
        Assert.Equal(48, RoomDefinitions.BuildTicksFor(world, 4001));
    }

    [Fact]
    public void KnownRoomType_LooksUpItsWorkerSlotsFromTheTable()
    {
        WorldState world = NewWorld();

        // Ops Center (4010) has 6 worker slots in room_type.csv.
        Assert.Equal(6, RoomDefinitions.WorkerSlotsFor(world, 4010));
    }

    [Fact]
    public void UnknownRoomType_FallsBackInsteadOfThrowing()
    {
        WorldState world = NewWorld();

        // A save can legitimately reference an id the tables no longer contain;
        // stage 5 quarantines those rather than crashing.
        Assert.Equal(RoomDefinitions.DefaultBuildTicks, RoomDefinitions.BuildTicksFor(world, 999999));
        Assert.Equal(RoomDefinitions.DefaultWorkerSlots, RoomDefinitions.WorkerSlotsFor(world, 999999));
        Assert.Equal(0, RoomDefinitions.UpkeepFor(world, 999999));
        Assert.Null(RoomDefinitions.Find(999999));
    }

    [Fact]
    public void ApplyDepthRequirements_MirrorsTheTableIntoTheLayout()
    {
        WorldState world = NewWorld();

        // Vault (4017) declares min_depth 3 in room_type.csv, so shallower layers are
        // refused and layer 3 is accepted.
        Assert.Equal(PlacementError.LayerTooShallow, world.BaseLayout.CheckPlacement(4017, 0, new[] { 0, 1 }));
        Assert.Equal(PlacementError.LayerTooShallow, world.BaseLayout.CheckPlacement(4017, 2, new[] { 0, 1 }));
        Assert.Equal(PlacementError.None, world.BaseLayout.CheckPlacement(4017, 3, new[] { 0, 1 }));
    }

    [Fact]
    public void UpgradeLookup_MatchesOnTheCompositeKey()
    {
        // room_upgrade rows are (4001, level 2) and (4001, level 3).
        Assert.Equal(900, RoomDefinitions.UpgradeCostFor(4001, 2));
        Assert.Equal(1800, RoomDefinitions.UpgradeCostFor(4001, 3));

        // A level with no upgrade row (already max) costs nothing.
        Assert.Equal(0, RoomDefinitions.UpgradeCostFor(4001, 99));
    }

    [Fact]
    public void UpgradeLookup_ReturnsUpkeepAndEffectMultiplier()
    {
        Assert.Equal(60, RoomDefinitions.UpgradeUpkeepDeltaFor(4001, 2));
        Assert.Equal(150, RoomDefinitions.UpgradeEffectMultiplierFor(4001, 2));
        Assert.Equal(210, RoomDefinitions.UpgradeEffectMultiplierFor(4001, 3));
    }

    [Fact]
    public void EveryRoomType_HasUpgradeRowsUpToItsMaxLevel()
    {
        // If a designer raises max_level without adding the matching upgrade rows,
        // the player hits a silent dead end, so it is asserted here.
        foreach (ProjectSpy.Tables.RoomType room in TableData.TableValidator.Load().Tables.TbRoomType.DataList)
        {
            for (int level = 2; level <= room.MaxLevel; level++)
            {
                Assert.True(
                    RoomDefinitions.FindUpgrade(room.Id, level) is not null,
                    $"room_type {room.Id} claims max_level {room.MaxLevel} but has no upgrade row for level {level}.");
            }
        }
    }

    [Fact]
    public void BuildRoomCommand_UsesTheTablesBuildTime()
    {
        WorldState world = NewWorld();
        world.Resources = world.Resources with { Funds = 100_000 };
        RoomDefinitions.UnlockAll(world.BaseLayout);

        var session = new GameSession(world);
        session.Execute(GridCommands.Build(4001, 0, 0, 2, "room.gym", 1200, session.World));

        Room room = Assert.Single(session.World.BaseLayout.Rooms);

        Assert.Equal(48, room.ConstructionTicksRemaining);
        Assert.True(room.IsUnderConstruction);
    }

    [Fact]
    public void AssignAgentToRoom_UsesTheTablesWorkerSlots()
    {
        WorldState world = NewWorld();
        world.Resources = world.Resources with { Funds = 100_000 };
        RoomDefinitions.UnlockAll(world.BaseLayout);

        var session = new GameSession(world);

        // Ops Center (4010) has 6 slots, so six agents fit and a seventh is refused.
        // It also declares min_depth 1, so it must be placed on row 1 or below.
        CommandResult built = session.Execute(GridCommands.Build(4010, 0, 1, 4, "room.ops_center", 2600, session.World));
        Assert.True(built.IsOk, built.MessageKey);

        Room room = session.World.BaseLayout.Rooms[0];

        for (int i = 0; i < 6; i++)
        {
            Agent agent = session.World.AddAgent(new Agent { Name = $"A{i}" });
            Assert.True(session.Execute(new AssignAgentToRoomCommand(agent.Id, room.Id)).IsOk);
        }

        Agent seventh = session.World.AddAgent(new Agent { Name = "A6" });
        CommandResult overflow = session.Execute(new AssignAgentToRoomCommand(seventh.Id, room.Id));

        Assert.Equal(CommandReason.RoomAtCapacity, overflow.Reason);
        Assert.Equal(6, room.AssignedAgentIds.Count);
    }
}
