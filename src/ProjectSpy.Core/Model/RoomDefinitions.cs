using ProjectSpy.Tables;

// The generated manager class is named `Tables`, which collides with the
// ProjectSpy.Tables namespace. Because this file lives under ProjectSpy.*, the bare
// name binds to the namespace, so an alias is required.
using GameTables = ProjectSpy.Tables.Tables;

namespace ProjectSpy.Core;

/// <summary>
/// Core-side lookups into the room_type table.
/// </summary>
/// <remarks>
/// <para>
/// This is where Core stops hard-coding balance numbers and starts reading them
/// from the compiled tables (knowledge.md rule 3). Keeping the lookups behind a
/// small static surface means the stage-3 tick phases never touch Luban's generated
/// types directly — they ask for the one number they need.
/// </para>
/// <para>
/// Every accessor takes a tolerant path: an id that is absent from the table yields
/// a documented default rather than throwing. A missing room id should not crash
/// the simulation mid-tick, and the save-migration path in stage 5 deliberately
/// tolerates ids that no longer exist.
/// </para>
/// </remarks>
public static class RoomDefinitions
{
    /// <summary>Build time used when a room type is unknown.</summary>
    public const int DefaultBuildTicks = 1;

    /// <summary>Worker capacity used when a room type is unknown.</summary>
    public const int DefaultWorkerSlots = 1;

    /// <summary>The loaded tables. Cached by <see cref="TableService"/>.</summary>
    private static GameTables Tables => TableService.Load();

    /// <summary>Looks up a room type row, or null when the id is not in the table.</summary>
    public static RoomType? Find(int roomTypeId)
    {
        try
        {
            return Tables.TbRoomType.GetOrDefault(roomTypeId);
        }
        catch (DirectoryNotFoundException)
        {
            // Table binaries absent (fresh clone, gen.ps1 not yet run). Fall back to
            // the defaults so Core degrades instead of crashing; the TableValidator
            // test is what actually enforces that generation has happened.
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Construction ticks for a room type, or <see cref="DefaultBuildTicks"/>.</summary>
    public static int BuildTicksFor(WorldState world, int roomTypeId)
        => Find(roomTypeId)?.BuildTicks ?? DefaultBuildTicks;

    /// <summary>Worker capacity for a room type, or <see cref="DefaultWorkerSlots"/>.</summary>
    public static int WorkerSlotsFor(WorldState world, int roomTypeId)
        => Find(roomTypeId)?.WorkerSlots ?? DefaultWorkerSlots;

    /// <summary>Weekly upkeep for a room type, or zero when unknown.</summary>
    public static long UpkeepFor(WorldState world, int roomTypeId)
        => Find(roomTypeId)?.UpkeepPerWeek ?? 0;

    /// <summary>Base build cost for a room type, before the depth modifier.</summary>
    public static long BaseBuildCostFor(WorldState world, int roomTypeId)
        => Find(roomTypeId)?.BuildCost ?? 0;

    /// <summary>
    /// Cost of upgrading a room to <paramref name="targetLevel"/>, or zero when no
    /// such upgrade row exists (e.g. already at max level).
    /// </summary>
    /// <remarks>
    /// room_upgrade is keyed by its own surrogate <c>Id</c>, so the composite
    /// (<c>room_type_id</c>, <c>level</c>) pair is matched by scanning. The table is
    /// a few dozen rows, so a linear scan is cheaper than any index and keeps the
    /// lookup honest: a duplicate row is caught by the validator, not silently
    /// resolved to whichever the map happened to return.
    /// </remarks>
    public static long UpgradeCostFor(int roomTypeId, int targetLevel)
        => FindUpgrade(roomTypeId, targetLevel)?.Cost ?? 0;

    /// <summary>The upgrade row for a (room type, level) pair, or null.</summary>
    public static RoomUpgrade? FindUpgrade(int roomTypeId, int targetLevel)
    {
        foreach (RoomUpgrade upgrade in Tables.TbRoomUpgrade.DataList)
        {
            if (upgrade.RoomTypeId == roomTypeId && upgrade.Level == targetLevel)
                return upgrade;
        }

        return null;
    }

    /// <summary>Upkeep delta for an upgrade level, or zero when none exists.</summary>
    public static long UpgradeUpkeepDeltaFor(int roomTypeId, int targetLevel)
        => FindUpgrade(roomTypeId, targetLevel)?.UpkeepDelta ?? 0;

    /// <summary>Effect multiplier for an upgrade level, as a percentage (150 = 1.5x).</summary>
    public static int UpgradeEffectMultiplierFor(int roomTypeId, int targetLevel)
        => FindUpgrade(roomTypeId, targetLevel)?.EffectMultiplier ?? 100;

    /// <summary>Highest level a room type may reach.</summary>
    public static int MaxLevelFor(int roomTypeId) => Find(roomTypeId)?.MaxLevel ?? 1;

    /// <summary>
    /// Copies the table's minimum depth requirements into the layout, so
    /// <see cref="BaseLayout.CheckPlacement"/> can enforce them.
    /// </summary>
    public static void ApplyDepthRequirements(BaseLayout layout)
    {
        foreach (RoomType room in Tables.TbRoomType.DataList)
            layout.MinimumDepthByType[room.Id] = room.MinDepth;
    }

    /// <summary>
    /// Unlocks every room type. Used at game start and by the stage-10 story tests;
    /// real progression gates these by <c>required_act</c>.
    /// </summary>
    public static void UnlockAll(BaseLayout layout)
    {
        foreach (RoomType room in Tables.TbRoomType.DataList)
            layout.UnlockedRoomTypeIds.Add(room.Id);
    }
}
