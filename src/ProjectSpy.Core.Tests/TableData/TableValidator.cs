using ProjectSpy.Tables;

// Core already defines SkillKind (the five skills an agent has). The generated table
// enum adds a None member meaning "this room trains no skill". They are genuinely
// different types and stage 3 converts between them explicitly, so the table side
// is aliased here to keep that conversion visible rather than implicit.
using TableSkillKind = ProjectSpy.Tables.SkillKind;

// The generated manager class is named `Tables`, which collides with the
// ProjectSpy.Tables namespace. Because this file lives under ProjectSpy.*, the bare
// name binds to the namespace, so an alias is required rather than qualification.
using GameTables = ProjectSpy.Tables.Tables;

namespace ProjectSpy.Core.Tests.TableData;

/// <summary>
/// Validates the generated tables against the CSV sources.
/// </summary>
/// <remarks>
/// <para>
/// This is the guard that makes it safe for a designer to edit a CSV. It loads the
/// Luban-generated binary (what the game actually runs) and cross-checks it against
/// the localization sources, asserting:
/// </para>
/// <list type="bullet">
///   <item>no dangling foreign key,</item>
///   <item>no weight of zero or less,</item>
///   <item>every name_key and desc_key exists in <em>both</em> languages,</item>
///   <item>every skill a room trains is a real skill,</item>
///   <item>every mission tier has at least three usable node rooms.</item>
/// </list>
/// <para>
/// Foreign keys are read from the generated binary rather than re-parsed from CSV so
/// that the check reflects shipped content, not the editor's intent.
/// </para>
/// </remarks>
internal sealed class TableValidator
{
    private readonly GameTables _tables;
    private readonly LocalizationCatalog _th;
    private readonly LocalizationCatalog _en;

    private TableValidator(GameTables tables, LocalizationCatalog th, LocalizationCatalog en)
    {
        _tables = tables;
        _th = th;
        _en = en;
    }

    /// <summary>The loaded tables.</summary>
    public GameTables Tables => _tables;

    /// <summary>Thai localization.</summary>
    public LocalizationCatalog Thai => _th;

    /// <summary>English localization.</summary>
    public LocalizationCatalog English => _en;

    /// <summary>
    /// Loads the tables and both localization catalogs.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// The table binaries have not been generated.
    /// </exception>
    public static TableValidator Load()
    {
        // Reset first so a developer re-running gen.ps1 mid-session sees new data.
        TableService.ResetCache();
        GameTables tables = TableService.Load();
        string root = LocalizationCatalog.FindRepositoryRoot();

        return new TableValidator(
            tables,
            LocalizationCatalog.Load("th", root),
            LocalizationCatalog.Load("en", root));
    }

    /// <summary>
    /// Collects every validation failure rather than throwing on the first.
    /// </summary>
    /// <remarks>
    /// Reporting all problems at once matters: a designer who has introduced six
    /// broken references should see all six, not fix them one build at a time.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        ValidateNoDanglingForeignKeys(errors);
        ValidateWeightsArePositive(errors);
        ValidateLocalizationCoverage(errors);
        ValidateSkillReferences(errors);
        ValidateNodeRoomCoveragePerTier(errors);
        ValidateIdSpacesAreNonOverlapping(errors);
        ValidateNumericRanges(errors);

        return errors;
    }

    // ---- foreign keys --------------------------------------------------------

    private void ValidateNoDanglingForeignKeys(List<string> errors)
    {
        var roomIds = new HashSet<int>();
        foreach (RoomType room in _tables.TbRoomType.DataList)
            roomIds.Add(room.Id);

        var itemIds = new HashSet<int>();
        foreach (Item item in _tables.TbItem.DataList)
            itemIds.Add(item.Id);

        var eventIds = new HashSet<int>();
        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
            eventIds.Add(e.Id);

        var lootTableIds = new HashSet<int>();
        foreach (LootTable loot in _tables.TbLootTable.DataList)
            lootTableIds.Add(loot.Id);

        // room_type.preferred_room_ids on agent_class
        foreach (AgentClass cls in _tables.TbAgentClass.DataList)
        {
            foreach (int roomId in LocalizationCatalog.ParseIdList(cls.PreferredRoomIds))
            {
                if (!roomIds.Contains(roomId))
                    errors.Add($"agent_class {cls.Id}: preferred_room_ids references unknown room {roomId}");
            }
        }

        // node_room.loot_table_id
        foreach (NodeRoom node in _tables.TbNodeRoom.DataList)
        {
            if (node.LootTableId != 0 && !lootTableIds.Contains(node.LootTableId))
                errors.Add($"node_room {node.Id}: loot_table_id {node.LootTableId} does not exist");
        }

        // node_room.possible_event_ids
        foreach (NodeRoom node in _tables.TbNodeRoom.DataList)
        {
            foreach (int eventId in LocalizationCatalog.ParseIdList(node.PossibleEventIds))
            {
                if (!eventIds.Contains(eventId))
                    errors.Add($"node_room {node.Id}: possible_event_ids references unknown mission_event {eventId}");
            }
        }

        // mission_event.loot_table_id
        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
        {
            if (e.LootTableId != 0 && !lootTableIds.Contains(e.LootTableId))
                errors.Add($"mission_event {e.Id}: loot_table_id {e.LootTableId} does not exist");
        }

        // loot_table.item_id
        foreach (LootTable loot in _tables.TbLootTable.DataList)
        {
            if (!itemIds.Contains(loot.ItemId))
                errors.Add($"loot_table {loot.Id}: entry {loot.EntryId} references unknown item {loot.ItemId}");
        }

        // room_upgrade.room_type_id
        var upgradeLevels = new Dictionary<int, HashSet<int>>();
        foreach (RoomUpgrade upgrade in _tables.TbRoomUpgrade.DataList)
        {
            if (!roomIds.Contains(upgrade.RoomTypeId))
            {
                errors.Add($"room_upgrade {upgrade.Id}: references unknown room_type {upgrade.RoomTypeId}");
                continue;
            }

            if (!upgradeLevels.TryGetValue(upgrade.RoomTypeId, out HashSet<int>? levels))
            {
                levels = new HashSet<int>();
                upgradeLevels[upgrade.RoomTypeId] = levels;
            }

            if (!levels.Add(upgrade.Level))
            {
                errors.Add(
                    $"room_upgrade: duplicate level {upgrade.Level} for room_type {upgrade.RoomTypeId}");
            }
        }

        // trait.conflicts_with must point at real traits and never at itself
        var traitIds = new HashSet<int>();
        foreach (Trait trait in _tables.TbTrait.DataList)
            traitIds.Add(trait.Id);

        foreach (Trait trait in _tables.TbTrait.DataList)
        {
            foreach (int other in LocalizationCatalog.ParseIdList(trait.ConflictsWith))
            {
                if (!traitIds.Contains(other))
                    errors.Add($"trait {trait.Id}: conflicts_with references unknown trait {other}");

                if (other == trait.Id)
                    errors.Add($"trait {trait.Id}: conflicts_with references itself");
            }
        }
    }

    // ---- weights -------------------------------------------------------------

    private void ValidateWeightsArePositive(List<string> errors)
    {
        foreach (NodeRoom node in _tables.TbNodeRoom.DataList)
        {
            if (node.Weight <= 0)
                errors.Add($"node_room {node.Id}: weight must be > 0, was {node.Weight}");
        }

        foreach (LootTable loot in _tables.TbLootTable.DataList)
        {
            if (loot.Weight <= 0)
                errors.Add($"loot_table {loot.Id} entry {loot.EntryId}: weight must be > 0, was {loot.Weight}");

            if (loot.MinCount < 0)
                errors.Add($"loot_table {loot.Id} entry {loot.EntryId}: min_count must be >= 0, was {loot.MinCount}");

            if (loot.MaxCount < loot.MinCount)
            {
                errors.Add(
                    $"loot_table {loot.Id} entry {loot.EntryId}: max_count ({loot.MaxCount}) " +
                    $"is below min_count ({loot.MinCount})");
            }
        }

        foreach (ContractOffer offer in _tables.TbContractOffer.DataList)
        {
            if (offer.Weight <= 0)
                errors.Add($"contract_offer {offer.Id}: weight must be > 0, was {offer.Weight}");
        }
    }

    // ---- localization --------------------------------------------------------

    /// <summary>
    /// Every key the tables reference must exist in both languages with real text.
    /// </summary>
    private void ValidateLocalizationCoverage(List<string> errors)
    {
        foreach ((string key, string source) in EnumerateTableKeys())
        {
            if (!_th.Contains(key))
                errors.Add($"localization th: missing key '{key}' (referenced by {source})");

            if (!_en.Contains(key))
                errors.Add($"localization en: missing key '{key}' (referenced by {source})");

            // A key that exists but is blank renders as an empty label in game.
            if (_th.Contains(key) && _th[key].Length == 0)
                errors.Add($"localization th: key '{key}' is empty");

            if (_en.Contains(key) && _en[key].Length == 0)
                errors.Add($"localization en: key '{key}' is empty");
        }
    }

    /// <summary>Yields every localization key the tables reference, with its origin.</summary>
    private IEnumerable<(string Key, string Source)> EnumerateTableKeys()
    {
        foreach (AgentClass c in _tables.TbAgentClass.DataList)
            yield return (c.NameKey, $"agent_class {c.Id}");

        foreach (RoomType r in _tables.TbRoomType.DataList)
            yield return (r.NameKey, $"room_type {r.Id}");

        foreach (Trait t in _tables.TbTrait.DataList)
        {
            yield return (t.NameKey, $"trait {t.Id}");
            yield return (t.DescKey, $"trait {t.Id}");
        }

        foreach (MissionType m in _tables.TbMissionType.DataList)
            yield return (m.NameKey, $"mission_type {m.Id}");

        foreach (NodeRoom n in _tables.TbNodeRoom.DataList)
            yield return (n.NameKey, $"node_room {n.Id}");

        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
        {
            yield return (e.NameKey, $"mission_event {e.Id}");
            yield return (e.NarrativeKey, $"mission_event {e.Id}");
        }

        foreach (Gadget g in _tables.TbGadget.DataList)
            yield return (g.NameKey, $"gadget {g.Id}");

        foreach (Item i in _tables.TbItem.DataList)
            yield return (i.NameKey, $"item {i.Id}");
    }

    // ---- skills --------------------------------------------------------------

    /// <summary>
    /// Every skill a room trains must be a real, trainable skill.
    /// </summary>
    /// <remarks>
    /// A room whose <c>trains_skill</c> is <c>None</c> must also declare no training
    /// rate, otherwise it silently produces free exp for nobody.
    /// </remarks>
    private void ValidateSkillReferences(List<string> errors)
    {
        var valid = Enum.GetValues<TableSkillKind>().ToHashSet();

        foreach (RoomType room in _tables.TbRoomType.DataList)
        {
            if (!valid.Contains(room.TrainsSkill))
            {
                errors.Add(
                    $"room_type {room.Id}: trains_skill '{room.TrainsSkill}' is not a known skill");
                continue;
            }

            bool trains = room.TrainsSkill != TableSkillKind.None;

            if (trains && room.TrainRatePerTick <= 0)
            {
                errors.Add(
                    $"room_type {room.Id}: trains {room.TrainsSkill} but train_rate_per_tick " +
                    $"is {room.TrainRatePerTick}");
            }

            if (!trains && room.TrainRatePerTick != 0)
            {
                errors.Add(
                    $"room_type {room.Id}: trains_skill is None but train_rate_per_tick is " +
                    $"{room.TrainRatePerTick}");
            }
        }

        // The five core skills Core knows about must all be represented by some room,
        // otherwise that skill cannot be trained at all.
        var trainable = new HashSet<TableSkillKind>();
        foreach (RoomType room in _tables.TbRoomType.DataList)
        {
            if (room.TrainsSkill != TableSkillKind.None)
                trainable.Add(room.TrainsSkill);
        }

        foreach (TableSkillKind skill in valid)
        {
            if (skill == TableSkillKind.None)
                continue;

            if (!trainable.Contains(skill))
                errors.Add($"no room_type trains {skill}");
        }

        // mission_event.skill_checked must also be a real skill (None = pure Nerve/no check).
        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
        {
            if (!valid.Contains(e.SkillChecked))
                errors.Add($"mission_event {e.Id}: skill_checked '{e.SkillChecked}' is not a known skill");
        }

        // gadget effect_type strings must be ones Core understands (stage 3/4 effect code).
        var knownGadgetEffects = new HashSet<string>(StringComparer.Ordinal)
        {
            "InfiltrationBonus", "SocialBonus", "TechBonus", "CombatBonus", "NerveBonus",
            "AlarmReduction", "IntelBonus", "LootBonus", "RevealBonus", "HeatGainReduction",
            "StaminaRestore", "MentalRestore", "RecruitCostReduction",
        };

        foreach (Gadget gadget in _tables.TbGadget.DataList)
        {
            if (!knownGadgetEffects.Contains(gadget.EffectType))
                errors.Add($"gadget {gadget.Id}: unknown effect_type '{gadget.EffectType}'");
        }

        // trait effect_type strings likewise.
        foreach (Trait trait in _tables.TbTrait.DataList)
        {
            if (trait.EffectType.Length == 0)
                errors.Add($"trait {trait.Id}: effect_type is empty");
        }
    }

    // ---- mission tier coverage ----------------------------------------------

    /// <summary>
    /// Every mission tier must have at least three node rooms it can actually use.
    /// </summary>
    /// <remarks>
    /// The floor is three because a map needs an entry, an objective and an
    /// extraction; below that the generator cannot build a legal map and the failure
    /// surfaces in play rather than in a build.
    /// </remarks>
    private void ValidateNodeRoomCoveragePerTier(List<string> errors)
    {
        var tiers = _tables.TbMissionType.DataList
            .Select(m => m.Tier)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        foreach (int tier in tiers)
        {
            int usable = _tables.TbNodeRoom.DataList.Count(node =>
            {
                var allowed = LocalizationCatalog.ParseStringList(node.AllowedTiers);
                return allowed.Any(t => int.TryParse(t, out int tierValue) && tierValue == tier);
            });

            if (usable < 3)
            {
                errors.Add(
                    $"mission tier {tier} has only {usable} usable node_room(s); " +
                    "at least 3 are required to build a legal map");
            }
        }
    }

    // ---- id spaces -----------------------------------------------------------

    /// <summary>
    /// Every table owns a private id range, so a stray id cannot silently cross
    /// reference from one table into another.
    /// </summary>
    private void ValidateIdSpacesAreNonOverlapping(List<string> errors)
    {
        var spaces = new List<(string Table, IEnumerable<int> Ids)>
        {
            ("agent_class", _tables.TbAgentClass.DataList.Select(x => x.Id)),
            ("agent_name", _tables.TbAgentName.DataList.Select(x => x.Id)),
            ("trait", _tables.TbTrait.DataList.Select(x => x.Id)),
            ("room_type", _tables.TbRoomType.DataList.Select(x => x.Id)),
            ("room_upgrade", _tables.TbRoomUpgrade.DataList.Select(x => x.Id)),
            ("mission_type", _tables.TbMissionType.DataList.Select(x => x.Id)),
            ("node_room", _tables.TbNodeRoom.DataList.Select(x => x.Id)),
            ("mission_event", _tables.TbMissionEvent.DataList.Select(x => x.Id)),
            ("gadget", _tables.TbGadget.DataList.Select(x => x.Id)),
            ("item", _tables.TbItem.DataList.Select(x => x.Id)),
            ("contract_offer", _tables.TbContractOffer.DataList.Select(x => x.Id)),
        };

        var seen = new Dictionary<int, string>();

        foreach ((string table, IEnumerable<int> ids) in spaces)
        {
            foreach (int id in ids)
            {
                if (seen.TryGetValue(id, out string? owner))
                {
                    if (!string.Equals(owner, table, StringComparison.Ordinal))
                    {
                        errors.Add(
                            $"id {id} is used by both {owner} and {table}; " +
                            "id spaces must not overlap");
                    }
                }
                else
                {
                    seen[id] = table;
                }
            }
        }
    }

    // ---- numeric sanity ------------------------------------------------------

    private void ValidateNumericRanges(List<string> errors)
    {
        foreach (SkillCurve curve in _tables.TbSkillCurve.DataList)
        {
            if (curve.Level < 1)
                errors.Add($"skill_curve level {curve.Level}: must be >= 1");

            if (curve.ExpRequired <= 0)
                errors.Add($"skill_curve level {curve.Level}: exp_required must be > 0");
        }

        // Levels must run 1..N with no gaps: Core indexes this table by level.
        var levels = _tables.TbSkillCurve.DataList.Select(c => c.Level).OrderBy(l => l).ToList();
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i] != i + 1)
            {
                errors.Add($"skill_curve: levels must be contiguous from 1; expected {i + 1}, found {levels[i]}");
                break;
            }
        }

        foreach (RoomType room in _tables.TbRoomType.DataList)
        {
            if (room.BuildCost <= 0)
                errors.Add($"room_type {room.Id}: build_cost must be > 0");

            if (room.Width <= 0)
                errors.Add($"room_type {room.Id}: width must be > 0");

            if (room.MaxLevel < 1)
                errors.Add($"room_type {room.Id}: max_level must be >= 1");

            if (room.WorkerSlots < 0)
                errors.Add($"room_type {room.Id}: worker_slots must be >= 0");

            if (room.MinDepth < 0)
                errors.Add($"room_type {room.Id}: min_depth must be >= 0");

            if (room.RequiredAct < 1)
                errors.Add($"room_type {room.Id}: required_act must be >= 1");

            if (room.MergeGroup.Length == 0)
                errors.Add($"room_type {room.Id}: merge_group is empty, so merging can never match");
        }

        foreach (MissionType m in _tables.TbMissionType.DataList)
        {
            if (m.NodeCountMin > m.NodeCountMax)
                errors.Add($"mission_type {m.Id}: node_count_min exceeds node_count_max");

            if (m.NodeCountMin <= 0)
                errors.Add($"mission_type {m.Id}: node_count_min must be > 0");

            if (m.RequiredAgentsMin > m.RequiredAgentsMax)
                errors.Add($"mission_type {m.Id}: required_agents_min exceeds required_agents_max");

            if (m.RequiredAgentsMin < 1)
                errors.Add($"mission_type {m.Id}: required_agents_min must be >= 1");

            if (m.BaseRewardFunds < 0 || m.BaseRewardIntel < 0)
                errors.Add($"mission_type {m.Id}: base rewards must be >= 0");

            if (m.DurationTicksPerNode <= 0)
                errors.Add($"mission_type {m.Id}: duration_ticks_per_node must be > 0");
        }

        foreach (NodeRoom node in _tables.TbNodeRoom.DataList)
        {
            if (node.BaseSecurity < 0 || node.BaseSecurity > 100)
                errors.Add($"node_room {node.Id}: base_security must be 0-100, was {node.BaseSecurity}");

            var tiers = LocalizationCatalog.ParseStringList(node.AllowedTiers);
            if (tiers.Count == 0)
                errors.Add($"node_room {node.Id}: allowed_tiers is empty, so the node is unreachable");

            if (LocalizationCatalog.ParseStringList(node.Tags).Count == 0)
                errors.Add($"node_room {node.Id}: tags is empty, so no event can ever match it");
        }

        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
        {
            if (e.AlarmOnFailure < e.AlarmOnPartial || e.AlarmOnPartial < e.AlarmOnSuccess)
            {
                errors.Add(
                    $"mission_event {e.Id}: alarm must be non-decreasing " +
                    "(success <= partial <= failure)");
            }

            if (e.AlarmOnFailure < 0 || e.AlarmOnFailure > 100)
                errors.Add($"mission_event {e.Id}: alarm_on_failure must be 0-100");

            if (e.PhysicalCost < 0 || e.MentalCost < 0)
                errors.Add($"mission_event {e.Id}: stamina costs must be >= 0");
        }

        foreach (Item item in _tables.TbItem.DataList)
        {
            if (item.SellValue < 0)
                errors.Add($"item {item.Id}: sell_value must be >= 0");

            if (item.StackMax < 1)
                errors.Add($"item {item.Id}: stack_max must be >= 1");
        }

        foreach (Gadget gadget in _tables.TbGadget.DataList)
        {
            if (gadget.CraftCost <= 0)
                errors.Add($"gadget {gadget.Id}: craft_cost must be > 0");

            if (gadget.Uses < 1)
                errors.Add($"gadget {gadget.Id}: uses must be >= 1");

            if (gadget.RequiredRoomLevel < 1)
                errors.Add($"gadget {gadget.Id}: required_room_level must be >= 1");
        }

        // Heat tiers must be strictly ascending and start at or below zero so that a
        // fresh agency resolves to the first tier.
        var heatTiers = _tables.TbHeatTier.DataList.OrderBy(t => t.Threshold).ToList();
        for (int i = 1; i < heatTiers.Count; i++)
        {
            if (heatTiers[i].Threshold <= heatTiers[i - 1].Threshold)
            {
                errors.Add(
                    $"heat_tier: thresholds must strictly ascend; {heatTiers[i].Threshold} " +
                    $"does not exceed {heatTiers[i - 1].Threshold}");
            }
        }

        if (heatTiers.Count > 0 && heatTiers[0].Threshold > 0)
        {
            errors.Add(
                $"heat_tier: the lowest threshold is {heatTiers[0].Threshold}; " +
                "a new agency at heat 0 must resolve to a tier");
        }
    }
}
