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
    /// <summary>
    /// The reputation tiers <c>recruit_rule</c> is keyed by. A field so the checks
    /// read as "for each tier" instead of repeating the bounds.
    /// </summary>
    private static readonly int[] ReputationTiers = { 0, 1, 2 };

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
        ValidateStageThreeTables(errors);

        return errors;
    }

    // ---- stage 3 -------------------------------------------------------------

    /// <summary>
    /// Checks the stage-3 tables. Kept together because they share one idea: every
    /// value the simulation reads at runtime must be reachable and sane, or a rule
    /// silently does the wrong thing instead of failing loudly.
    /// </summary>
    /// <summary>
    /// Reads an economy_rule value by key, falling back to the same default Core uses.
    /// </summary>
    /// <remarks>
    /// The fallback mirrors <c>SimulationRules.Economy</c> so the validator reasons
    /// about the ladder using the numbers Core would actually use, rather than
    /// disagreeing with it about what a missing key means.
    /// </remarks>
    private int SimulationFallback(string key)
    {
        foreach (EconomyRule rule in _tables.TbEconomyRule.DataList)
        {
            if (rule.RuleKey == key)
                return rule.Value;
        }

        return key switch
        {
            "settlement_day_of_week" => 6,
            "bankruptcy_grace_days" => 7,
            "bankruptcy_stage_loyalty_drain" => 2,
            "bankruptcy_stage_resign_day" => 3,
            "bankruptcy_stage_three_day" => 6,
            "bankruptcy_stage_four_day" => 10,
            "bankruptcy_max_stage" => 4,
            "upkeep_multiplier_percent" => 100,
            "loan_interest_compounds" => 1,
            _ => 0,
        };
    }

    private void ValidateStageThreeTables(List<string> errors)
    {
        var roomIds = new HashSet<int>();
        foreach (RoomType room in _tables.TbRoomType.DataList)
            roomIds.Add(room.Id);

        // Every stage-3 table row must point at a room type that exists. A recovery or
        // training rule about a room that is not in room_type is dead data.
        foreach (RecoveryRule rule in _tables.TbRecoveryRule.DataList)
        {
            if (!roomIds.Contains(rule.RoomTypeId))
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: references unknown room_type {rule.RoomTypeId}");
            }

            if (rule.RestorePhysicalPercentPerTick < 0 || rule.RestoreMentalPercentPerTick < 0)
            {
                errors.Add($"recovery_rule {rule.Id}: restore percentages must be >= 0");
            }

            if (rule.MentalRatioPercent is < 0 or > 100)
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: mental_ratio_percent must be 0..100, " +
                    $"was {rule.MentalRatioPercent}");
            }

            // A recovery row that restores nothing and heals nothing is unreachable
            // data; a designer would reasonably assume the room does something.
            if (rule.RestorePhysicalPercentPerTick == 0
                && rule.RestoreMentalPercentPerTick == 0
                && rule.InjuryHealPercentPerTick == 0
                && rule.BurnoutReliefPercentPerTick == 0)
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: has no effect at all, so the room is unreachable");
            }
        }

        foreach (TrainingRule rule in _tables.TbTrainingRule.DataList)
        {
            if (!roomIds.Contains(rule.RoomTypeId))
                errors.Add($"training_rule {rule.Id}: references unknown room_type {rule.RoomTypeId}");

            if (rule.StaminaCostPhysical < 0 || rule.StaminaCostMental < 0)
                errors.Add($"training_rule {rule.Id}: stamina costs must be >= 0");

            // A training room that costs no stamina makes overworking impossible, which
            // silently removes the entire "overworking is a mistake" design.
            if (rule.StaminaCostPhysical == 0 && rule.StaminaCostMental == 0)
                errors.Add($"training_rule {rule.Id}: must consume stamina, or overworking cannot happen");

            if (rule.OverworkThreshold is < 0 or > 100)
                errors.Add($"training_rule {rule.Id}: overwork_threshold must be 0..100");

            if (rule.OverworkGainPercent is < 0 or > 100)
            {
                errors.Add(
                    $"training_rule {rule.Id}: overwork_gain_percent must be 0..100, " +
                    $"was {rule.OverworkGainPercent}");
            }
        }

        foreach (SkillCap cap in _tables.TbSkillCap.DataList)
        {
            if (cap.SoftCap < 0)
                errors.Add($"skill_cap {cap.Id}: soft_cap must be >= 0");

            if (cap.HardCap <= cap.SoftCap)
            {
                errors.Add(
                    $"skill_cap {cap.Id}: hard_cap ({cap.HardCap}) must exceed soft_cap ({cap.SoftCap}); " +
                    "otherwise the diminishing-returns ramp has no width");
            }

            if (cap.FloorPercent is < 0 or > 100)
                errors.Add($"skill_cap {cap.Id}: floor_percent must be 0..100");
        }

        // All five skills need a cap row. A missing one leaves that skill uncapped,
        // which would let one agent grow indefinitely while others taper off.
        foreach (TableSkillKind skill in Enum.GetValues<TableSkillKind>())
        {
            if (skill == TableSkillKind.None)
                continue;

            bool hasCap = _tables.TbSkillCap.DataList.Any(c => c.SkillKind == skill);
            if (!hasCap)
                errors.Add($"skill_cap: no row for skill {skill}, so it can never be capped");
        }

        // morale_band must tile 0..100 exactly. A gap would make some loyalty values
        // fall through to Resentful and make the UI lie.
        var bandFloors = new List<int>();
        foreach (MoraleBand band in _tables.TbMoraleBand.DataList)
        {
            bandFloors.Add(band.LoyaltyFloor);

            if (band.LoyaltyFloor > band.LoyaltyCeiling)
                errors.Add($"morale_band {band.Id}: loyalty_floor exceeds loyalty_ceiling");

            if (band.MissChancePercent is < 0 or > 100)
                errors.Add($"morale_band {band.Id}: miss_chance_percent must be 0..100");
        }

        bandFloors.Sort();
        if (bandFloors.Count > 0 && bandFloors[0] != 0)
        {
            errors.Add(
                $"morale_band: lowest floor is {bandFloors[0]}, must be 0 so every loyalty value has a band");
        }

        foreach (MoraleBand band in _tables.TbMoraleBand.DataList)
        {
            // The next band up must start exactly where this one ends. Comparing against
            // the floor rather than the ceiling would let a band end at 74 and the next
            // begin at 60, silently re-labelling the overlap.
            bool continues = bandFloors.Any(f => f == band.LoyaltyCeiling + 1);

            if (!continues && band.LoyaltyCeiling < 100)
            {
                errors.Add(
                    $"morale_band {band.Id}: loyalty range {band.LoyaltyFloor}-{band.LoyaltyCeiling} " +
                    "is not followed by a band starting at " + (band.LoyaltyCeiling + 1));
            }
        }

        // loyalty_drift.condition must be a name Core knows about. Core maps the enum to
        // a string; a typo would return 0 and the penalty would silently vanish.
        var knownConditions = new HashSet<string>(StringComparer.Ordinal)
        {
            "Overworked", "Working", "Idle", "MissionLoss", "MissionSuccess",
            "BadRoommate", "PoorFacility", "GoodFacility", "PayFair", "PayUnfair", "Burnout",
        };

        var seenConditions = new HashSet<string>(StringComparer.Ordinal);
        foreach (LoyaltyDrift drift in _tables.TbLoyaltyDrift.DataList)
        {
            if (!knownConditions.Contains(drift.Condition))
            {
                errors.Add(
                    $"loyalty_drift {drift.Id}: condition '{drift.Condition}' is not a condition Core knows");
            }

            if (!seenConditions.Add(drift.Condition))
                errors.Add($"loyalty_drift: condition '{drift.Condition}' is defined more than once");

            if (drift.Delta == 0)
                errors.Add($"loyalty_drift {drift.Id}: delta is 0, so this source can never affect anything");
        }

        foreach (LoyaltyThreshold threshold in _tables.TbLoyaltyThreshold.DataList)
        {
            if (threshold.Threshold is < 0 or > 100)
                errors.Add($"loyalty_threshold {threshold.Id}: threshold must be 0..100");

            if (threshold.ChancePercent is < 0 or > 100)
                errors.Add($"loyalty_threshold {threshold.Id}: chance_percent must be 0..100");

            if (threshold.CooldownDays < 0)
                errors.Add($"loyalty_threshold {threshold.Id}: cooldown_days must be >= 0");
        }

        foreach (LoanTier tier in _tables.TbLoanTier.DataList)
        {
            if (tier.Amount <= 0)
                errors.Add($"loan_tier {tier.Id}: amount must be > 0");

            if (tier.WeeklyInterestPercent < 0)
                errors.Add($"loan_tier {tier.Id}: weekly_interest_percent must be >= 0");

            if (tier.MaxActive < 1)
                errors.Add($"loan_tier {tier.Id}: max_active must be >= 1");
        }

        foreach (RecruitRule rule in _tables.TbRecruitRule.DataList)
        {
            if (rule.HrLevel < 1)
                errors.Add($"recruit_rule {rule.Id}: hr_level must be >= 1");

            if (rule.ReputationTier is < 0 or > 2)
                errors.Add($"recruit_rule {rule.Id}: reputation_tier must be 0..2");

            if (rule.PoolSize < 1)
                errors.Add($"recruit_rule {rule.Id}: pool_size must be >= 1");

            if (rule.SkillVariance < 0)
                errors.Add($"recruit_rule {rule.Id}: skill_variance must be >= 0");

            if (rule.TraitCountMin < 1)
                errors.Add($"recruit_rule {rule.Id}: trait_count_min must be >= 1");

            if (rule.TraitCountMax < rule.TraitCountMin)
                errors.Add($"recruit_rule {rule.Id}: trait_count_max is below trait_count_min");

            if (rule.HiddenTraitChance is < 0 or > 100)
                errors.Add($"recruit_rule {rule.Id}: hidden_trait_chance must be 0..100");
        }

        // Every (hr_level, reputation_tier) pair Core can ask about must resolve to a
        // row. The lookup degrades to a lower HR level on purpose, but only within the
        // same tier — a tier with no rows at all would stop recruiting entirely.
        foreach (int repTier in ReputationTiers)
        {
            bool any = _tables.TbRecruitRule.DataList.Any(r => r.ReputationTier == repTier);
            if (!any)
                errors.Add($"recruit_rule: no rows for reputation tier {repTier}, so recruiting stops at that tier");
        }

        // Keyed rule tables must not be missing a key Core reads. A missing key falls
        // back to a hard-coded default in SimulationRules, which is precisely the
        // silent-wrong-answer failure the facade exists to prevent.
        var requiredEconomyKeys = new[]
        {"settlement_day_of_week", "bankruptcy_grace_days", "bankruptcy_stage_loyalty_drain",
      "bankruptcy_stage_resign_day", "bankruptcy_stage_three_day", "bankruptcy_stage_four_day",
      "bankruptcy_forced_resign_count",
            "bankruptcy_forced_resign_loyalty_floor", "bankruptcy_forced_resign_chance",
            "bankruptcy_penalty_scale_percent", "bankruptcy_max_stage", "upkeep_multiplier_percent",
            "loan_interest_compounds",
        };

        foreach (string key in requiredEconomyKeys)
        {
            bool found = _tables.TbEconomyRule.DataList.Any(r => r.RuleKey == key);
            if (!found)
                errors.Add($"economy_rule: missing required key '{key}'");
        }

        var requiredBurnoutKeys = new[]
        {
            "burnout_mental_threshold", "burnout_daily_loyalty_drain",
            "burnout_resting_loyalty_drain",
            "burnout_recovery_mental_percent", "burnout_recovery_physical_percent",
            "burnout_exit_mental",
        };

        foreach (string key in requiredBurnoutKeys)
        {
            bool found = _tables.TbBurnoutRule.DataList.Any(r => r.RuleKey == key);
            if (!found)
                errors.Add($"burnout_rule: missing required key '{key}'");
        }

        var requiredIntelKeys = new[]
        {
            "investigation_ticks_per_step", "investigation_evidence_chance",
            "investigation_false_lead_chance", "mole_weekly_heat", "mole_leak_difficulty_bonus",
            "mole_leak_chance_percent", "wrong_accusation_loyalty_drain",
            "counter_intel_capacity_per_level",
            "investigation_evidence_gain", "investigation_false_lead_penalty",
            "investigation_max_steps", "expose_threshold",
        };

        foreach (string key in requiredIntelKeys)
        {
            bool found = _tables.TbCounterIntelRule.DataList.Any(r => r.RuleKey == key);
            if (!found)
                errors.Add($"counter_intel_rule: missing required key '{key}'");
        }

        ValidateCounterIntelBalance(errors);
    }

    /// <summary>
    /// Asserts that an investigation can actually conclude.
    /// </summary>
    /// <remarks>
    /// A case that gains <c>gain</c> evidence per good step, loses <c>penalty</c> per
    /// false lead, runs for <c>maxSteps</c> steps and must clear <c>threshold</c> to
    /// expose the mole is a system that silently does nothing if a designer raises the
    /// threshold or lowers the gain — the desk simply never produces a verdict and the
    /// mole becomes uncatchable. Nothing errors at runtime; the feature just quietly
    /// stops existing. So the relationship is checked here instead.
    /// </remarks>
    private void ValidateCounterIntelBalance(List<string> errors)
    {
        int Value(string key, int fallback)
            => _tables.TbCounterIntelRule.DataList.FirstOrDefault(r => r.RuleKey == key)?.Value ?? fallback;

        int gain = Value("investigation_evidence_gain", -1);
        int penalty = Value("investigation_false_lead_penalty", -1);
        int maxSteps = Value("investigation_max_steps", -1);
        int threshold = Value("expose_threshold", -1);

        if (gain < 0 || penalty < 0 || maxSteps <= 0 || threshold <= 0)
            return; // keys already reported as missing above

        if (gain <= 0)
        {
            errors.Add(
                "counter_intel_rule: investigation_evidence_gain must be positive or no case can ever conclude");
        }

        // The ceiling of the evidence scale is 100; the gain has to be able to reach it.
        if (gain > 100)
            errors.Add($"counter_intel_rule: investigation_evidence_gain {gain} exceeds the 0-100 evidence scale");

        // Best case: every step is a success.
        int bestCase = maxSteps * gain;
        if (bestCase <= threshold)
        {
            errors.Add(
                $"counter_intel_rule: a case cannot expose a mole — {maxSteps} steps at {gain} evidence "
                + $"reaches {bestCase}, below the expose threshold of {threshold}");
        }

        // Realistic case: the expected outcome of the configured branch probabilities.
        //
        // This one is deliberately expected to fall SHORT of the threshold. Evidence
        // accrues identically whoever you investigate, so if the average case cleared
        // the threshold the desk would expose agents at random and the player's own
        // work — correlating Heat spikes against the mission log — would be worth
        // nothing. The desk is a machine for converting a suspicion you already hold
        // into proof, and only good rolls on top of that will do it.
        int evidenceChance = Value("investigation_evidence_chance", 0);
        int falseLeadChance = Value("investigation_false_lead_chance", 0);
        int expectedPerStep = evidenceChance * gain / 100 - falseLeadChance * penalty / 100;

        if (expectedPerStep <= 0)
        {
            errors.Add(
                "counter_intel_rule: false leads cancel out evidence on average "
                + $"({evidenceChance}% x {gain} vs {falseLeadChance}% x {penalty}) — "
                + "investigations can only ever get worse, so a conclusive case is a "
                + "matter of luck rather than of narrowing the suspect list down");
        }
        else if (maxSteps * expectedPerStep >= threshold)
        {
            errors.Add(
                $"counter_intel_rule: the average investigation reaches "
                + $"{maxSteps * expectedPerStep}, at or above the expose threshold of {threshold} — "
                + "the desk would expose people at random and correlating Heat against "
                + "the mission log would be pointless");
        }

        // The settlement day must be a real day of the week.
        foreach (EconomyRule rule in _tables.TbEconomyRule.DataList)
        {
            if (rule.RuleKey == "settlement_day_of_week" && rule.Value is < 0 or > 6)
            {
                errors.Add(
                    $"economy_rule: settlement_day_of_week must be 0..6, was {rule.Value}");
            }

            if (rule.RuleKey == "bankruptcy_max_stage" && rule.Value < 1)
            {
                errors.Add(
                    "economy_rule: bankruptcy_max_stage must be >= 1, " +
                    "or the run can end before the grace period is even spent");
            }


        // The ladder's stage thresholds must ascend, or a later stage would never be
        // reachable and the designer would believe in a penalty that never fires.
        int stage2 = SimulationFallback("bankruptcy_stage_resign_day");
        int stage3 = SimulationFallback("bankruptcy_stage_three_day");
        int stage4 = SimulationFallback("bankruptcy_stage_four_day");

        if (!(stage2 < stage3 && stage3 < stage4))
        {
            errors.Add(
                $"economy_rule: bankruptcy stage thresholds must ascend, got {stage2}, {stage3}, {stage4}");
        }

        // "Never instantly lose": the grace period has to be a real period.
        if (SimulationFallback("bankruptcy_grace_days") < 1)
        {
            errors.Add("economy_rule: bankruptcy_grace_days must be >= 1");
        }
    }

        // The counter-intel room lookup in MoleSystem is derived from category, effect
        // and cost. If no room matches, investigations can never run.
        bool hasCounterIntel = _tables.TbRoomType.DataList.Any(
            r => r.Category == RoomCategory.Admin
                 && r.EffectType == EffectType.HeatReduction
                 && r.BuildCost > 2500);

        if (!hasCounterIntel)
            errors.Add("room_type: no Admin/HeatReduction room above 2500 cost, so counter-intel can never be identified");// The designed relationship: mental is a scarcer resource than physical. Enforced
        // as a band rather than an exact ratio because the per-room numbers are integers
        // and rounding would otherwise fail a correct table.
        long physicalTotal = 0;
        long mentalTotal = 0;
        foreach (RecoveryRule rule in _tables.TbRecoveryRule.DataList)
        {
            physicalTotal += rule.RestorePhysicalPercentPerTick;
            mentalTotal += rule.RestoreMentalPercentPerTick;
        }

        if (physicalTotal > 0)
        {
            // "Roughly a third" — wide enough to survive integer rounding and a designer
            // nudging a room, narrow enough to catch the ratio being quietly inverted.
            if (mentalTotal * 100L < physicalTotal * 20L || mentalTotal * 100L > physicalTotal * 60L)
            {
                errors.Add(
                    $"recovery_rule: total mental restore ({mentalTotal}) should be roughly a third " +
                    $"of total physical restore ({physicalTotal}); the designed ratio is " +
                    $"mental_ratio_percent");
            }
        }

        // A room that serves both pools must never restore mental faster than physical: that
        // would invert the scarcity the rest economy depends on. Specialist rooms are
        // exempt — the therapy office restoring almost no physical stamina is the whole
        // point of it being a specialist.
        foreach (RecoveryRule rule in _tables.TbRecoveryRule.DataList)
        {
            bool servesBoth = rule.RestorePhysicalPercentPerTick > 0
                              && rule.RestoreMentalPercentPerTick > 0;

            if (servesBoth && rule.RestoreMentalPercentPerTick > rule.RestorePhysicalPercentPerTick)
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: a room that serves both pools restores mental faster " +
                    "than physical, which inverts the intended scarcity of mental stamina");
            }
        }

        // At least one room must be able to relieve burnout, or burnout is permanent —
        // which contradicts the design requirement that it be recoverable.
        bool anyBurnoutRelief = _tables.TbRecoveryRule.DataList.Any(r => r.BurnoutReliefPercentPerTick > 0);
        if (!anyBurnoutRelief)
        {
            errors.Add(
                "recovery_rule: no room provides burnout_relief_percent_per_tick, " +
                "which would make burnout permanent");
        }

        // The mental ratio must be a sane percentage, and it is the documented source
        // of the "mental is slower" design.
        foreach (RecoveryRule rule in _tables.TbRecoveryRule.DataList)
        {
            if (rule.MentalRatioPercent is < 10 or > 90)
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: mental_ratio_percent ({rule.MentalRatioPercent}) " +
                    "is outside the plausible 10..90 band");
            }
        }

        // There must be a mole trait for the heat system to act on.
        bool hasMole = _tables.TbTrait.DataList.Any(t => t.EffectType == "HeatGain");
        if (!hasMole)
        {
            errors.Add(
                "trait: no trait with effect_type HeatGain exists, so the mole adds no Heat");
        }
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
            ("skill_cap", _tables.TbSkillCap.DataList.Select(x => x.Id)),
            ("recovery_rule", _tables.TbRecoveryRule.DataList.Select(x => x.Id)),
            ("training_rule", _tables.TbTrainingRule.DataList.Select(x => x.Id)),
            ("economy_rule", _tables.TbEconomyRule.DataList.Select(x => x.Id)),
            ("loan_tier", _tables.TbLoanTier.DataList.Select(x => x.Id)),
            ("morale_band", _tables.TbMoraleBand.DataList.Select(x => x.Id)),
            ("loyalty_drift", _tables.TbLoyaltyDrift.DataList.Select(x => x.Id)),
            ("loyalty_threshold", _tables.TbLoyaltyThreshold.DataList.Select(x => x.Id)),
            ("recruit_rule", _tables.TbRecruitRule.DataList.Select(x => x.Id)),
            ("counter_intel_rule", _tables.TbCounterIntelRule.DataList.Select(x => x.Id)),
            ("burnout_rule", _tables.TbBurnoutRule.DataList.Select(x => x.Id)),
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
