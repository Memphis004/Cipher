using ProjectSpy.Core.Missions;
using ProjectSpy.Tables;

// The generated manager class is named `Tables`, which collides with the
// ProjectSpy.Tables namespace. Because this file lives under ProjectSpy.*, the bare
// name binds to the namespace, so an alias is required.
using GameTables = ProjectSpy.Tables.Tables;

// The table's SkillKind has a `None` member with no Core equivalent; the two types
// are deliberately kept distinct (see data/README.md), so this alias marks every
// crossing between them as an explicit conversion.
using TableSkillKind = ProjectSpy.Tables.SkillKind;

// Core already owns `InteractableType` (the enum) and `RoomContents`/`Room`, and the
// generated beans are named `InteractableType` and `NodeInteriorTemplate`. The table enum is
// aliased to InteractableKind below; these two rows are the same idea: the generated
// bean is a *definition* of a kind, where Core's enum is a *resolved* value, so they
// are kept as separate types and converted explicitly at the one crossing point.
using InteractableTypeRow = ProjectSpy.Tables.InteractableType;
using NodeInteriorTemplateRow = ProjectSpy.Tables.NodeInteriorTemplate;

// The stage-2 tactical `room_template` is a different thing from the stage-1
// `node_interior_template` that RoomTemplateFor below returns, and both are named
// "room template". Aliased so that the two are never confused at a call site: the
// node one describes an abstract node's contents in slot indices, the tactical one
// describes a walkable interval on a floor.
using TacticalRoomTemplateRow = ProjectSpy.Tables.RoomTemplate;
using SiteTemplateRow = ProjectSpy.Tables.SiteTemplate;
using ConnectionTypeRow = ProjectSpy.Tables.ConnectionType;
using GuardArchetypeRow = ProjectSpy.Tables.GuardArchetype;
using LightingProfileRow = ProjectSpy.Tables.LightingProfile;
using LightSourceRow = ProjectSpy.Tables.LightSource;
using NoiseProfileRow = ProjectSpy.Tables.NoiseProfile;
using SiteGenRuleRow = ProjectSpy.Tables.SiteGenRule;
using LootTableRow = ProjectSpy.Tables.LootTable;
using SleeperOpRow = ProjectSpy.Tables.SleeperOp;
using CaptureSiteRow = ProjectSpy.Tables.CaptureSite;
using IntelRuleRow = ProjectSpy.Tables.IntelRule;

// Stage 4c. The action system reads every one of these by name rather than by column
// index, so the rows are aliased to their table names exactly as the stage-2 and 4b
// tables are above. `ActionCategory` is used unaliased: Core deliberately has no
// counterpart enum of its own (see the class remarks on rule 3 — a tactical action is
// named by its row id, and Core re-reads the row rather than mirroring its columns).
using TacticalActionRow = ProjectSpy.Tables.TacticalAction;
using ActionCategory = ProjectSpy.Tables.ActionCategory;
using ThrowableRow = ProjectSpy.Tables.Throwable;
using MeleeWeaponRow = ProjectSpy.Tables.MeleeWeapon;
using GadgetRow = ProjectSpy.Tables.Gadget;
using ItemRow = ProjectSpy.Tables.Item;
using GoapGoalRow = ProjectSpy.Tables.GoapGoal;
using GoapActionRow = ProjectSpy.Tables.GoapAction;
using GoapRuleRow = ProjectSpy.Tables.GoapRule;

// Stage 4e. The squad layer reads four more tables, aliased the same way: the action
// system's aliases exist because Core deliberately holds no counterpart type for an
// action, and the same reasoning applies to a role, an objective rule, a support
// ability and a resolve class — Core converts the table's enums at the crossing rather
// than mirroring the rows.
using AgentRoleRow = ProjectSpy.Tables.AgentRole;
using SquadRuleRow = ProjectSpy.Tables.SquadRule;
using ObjectiveRuleRow = ProjectSpy.Tables.ObjectiveRule;
using AgentClassRow = ProjectSpy.Tables.AgentClass;
using CommandPostAbilityRow = ProjectSpy.Tables.CommandPostAbility;
using ResolveRuleRow = ProjectSpy.Tables.ResolveRule;
using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using TableSupportAbility = ProjectSpy.Tables.SupportAbility;
using TableResolveClass = ProjectSpy.Tables.ResolveClass;

// The table enums for the three concepts that land in a generated SiteLayout. Core
// keeps its own resolved values for these, numbered independently, so the crossing
// is an explicit switch (see ToCoreConnectionKind) rather than a cast.
using TableConnectionKind = ProjectSpy.Tables.ConnectionKind;
using TableLightLevel = ProjectSpy.Tables.LightLevel;
using TableGuardRole = ProjectSpy.Tables.GuardRole;

// The table's interactable kinds and Core's. Values are shared (the table enum is
// 1-based per __beans__.xml, Core's is 0-based and append-only), so the conversion is
// an explicit switch rather than a cast — a cast would silently mis-map every kind if
// either enum were ever renumbered.
using TableInteractableKind = ProjectSpy.Tables.InteractableKind;

namespace ProjectSpy.Core;

/// <summary>
/// Core's read-only window onto every stage-3 balance number.
/// </summary>
/// <remarks>
/// <para>
/// knowledge.md rule 3 forbids hand-typed balance constants in Core once the tables
/// exist. Rather than let each system reach into Luban's generated types directly,
/// every tunable is funnelled through this one facade. That buys three things:
/// </para>
/// <list type="bullet">
/// <item>The rule systems stay readable — they ask for a name, not a column index.</item>
/// <item>There is exactly one place to look when a designer asks "where does 33 come
/// from?", and the answer is always a CSV row.</item>
/// <item>Tests can assert that a rule is actually backed by table data.</item>
/// </list>
/// <para>
/// Every accessor degrades to a documented default when the tables are unavailable.
/// Stage 5 deliberately loads saves that may reference retired ids, and a missing
/// table binary should not crash a tick mid-simulation. <see cref="AreTablesLoaded"/>
/// lets tests distinguish "the rule returned its fallback" from "the rule returned
/// real data", which is what makes the fallback honest rather than a silent lie.
/// </para>
/// <para>
/// Arithmetic is integer-only. A percentage is applied as
/// <c>value * percent / 100</c> with integer division, ordered consistently so two
/// platforms cannot disagree (knowledge.md rule 6).
/// </para>
/// </remarks>
public static class SimulationRules
{
    private static GameTables? _tables;

    /// <summary>The loaded tables, or null when the binaries are absent.</summary>
    private static GameTables? TablesOrNull
    {
        get
        {
            if (_tables is not null)
                return _tables;

            try
            {
                _tables = TableService.Load();
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            return _tables;
        }
    }

    /// <summary>
    /// True when the real tables loaded. Tests assert this first so a fallback default
    /// can never be mistaken for real balance data.
    /// </summary>
    public static bool AreTablesLoaded => TablesOrNull is not null;

    /// <summary>
    /// Supplies the tables explicitly, for a host that already has them loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lazy <see cref="TablesOrNull"/> search walks up from
    /// <c>AppContext.BaseDirectory</c> looking for <c>assets/data/tables</c>. That works for
    /// the console projects and the tests, and it is the right default. It does not work
    /// inside Unity: there <c>AppContext.BaseDirectory</c> is the <b>Editor install
    /// directory</b> (<c>C:\Program Files\Unity\Hub\Editor\&lt;version&gt;\Editor</c>), not the
    /// project, so the search walks the Unity installation and finds nothing.
    /// </para>
    /// <para>
    /// Left alone that failure is silent. Every accessor degrades to a documented default,
    /// so a Unity build would have run the entire game on fallback numbers — a balance table
    /// change simply not applying, with no error anywhere. This method is the way out:
    /// Unity's <c>TableService</c> loads the binaries from StreamingAssets and hands them
    /// here, and the same bytes are used either way.
    /// </para>
    /// <para>
    /// Idempotent and last-call-wins, so a host may re-supply after a hot reload.
    /// </para>
    /// </remarks>
    /// <param name="tables">The loaded tables, or null to go back to searching.</param>
    public static void UseTables(GameTables? tables)
    {
        _tables = tables;
    }

    /// <summary>Highest agent level. Structural (it bounds the level curve).</summary>
    public static int MaxLevel
    {
        get
        {
            GameTables? t = TablesOrNull;
            if (t is null) return DefaultMaxLevel;

            int max = 0;
            foreach (SkillCurve curve in t.TbSkillCurve.DataList)
                max = Math.Max(max, curve.Level);

            return max > 0 ? max : DefaultMaxLevel;
        }
    }

    /// <summary>Level cap used when the tables are unavailable.</summary>
    public const int DefaultMaxLevel = 20;

    /// <summary>Experience needed to advance from <paramref name="level"/> to the next.</summary>
    public static int ExpRequiredForLevel(int level)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return DefaultExpPerLevel * Math.Max(1, level);

        foreach (SkillCurve curve in t.TbSkillCurve.DataList)
        {
            if (curve.Level == level)
                return curve.ExpRequired;
        }

        return DefaultExpPerLevel * Math.Max(1, level);
    }

    /// <summary>Fallback exp-per-level when no table row matches.</summary>
    public const int DefaultExpPerLevel = 100;

    /// <summary>
    /// The point past which training returns progressively less, as a percentage of
    /// the full rate. Zero disables diminishing returns for that skill.
    /// </summary>
    public static int SoftCapFor(SkillKind skill)
        => SkillCapRow(skill)?.SoftCap ?? 0;

    /// <summary>
    /// The point at which training returns only <see cref="SkillCapFloorPercent"/> of
    /// the full rate. Training past it makes no progress at all.
    /// </summary>
    public static int HardCapFor(SkillKind skill)
        => SkillCapRow(skill)?.HardCap ?? 0;

    /// <summary>Fraction of the full rate still granted at the hard cap, in percent.</summary>
    public static int SkillCapFloorPercent(SkillKind skill)
        => SkillCapRow(skill)?.FloorPercent ?? 100;

    private static SkillCap? SkillCapRow(SkillKind skill)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        TableSkillKind target = ToTableSkill(skill);
        foreach (SkillCap cap in t.TbSkillCap.DataList)
        {
            if (cap.SkillKind == target)
                return cap;
        }

        return null;
    }

    /// <summary>
    /// Converts Core's <see cref="SkillKind"/> to the table enum. They are deliberately
    /// separate types (see data/README.md): the table's has a <c>None</c> member with
    /// no meaning in Core.
    /// </summary>
    public static TableSkillKind ToTableSkill(SkillKind skill) => skill switch
    {
        SkillKind.Infiltration => TableSkillKind.Infiltration,
        SkillKind.Combat => TableSkillKind.Combat,
        SkillKind.Tech => TableSkillKind.Tech,
        SkillKind.Social => TableSkillKind.Social,
        SkillKind.Nerve => TableSkillKind.Nerve,
        _ => throw new ArgumentOutOfRangeException(nameof(skill), skill, "Unknown skill."),
    };

    /// <summary>Inverse of <see cref="ToTableSkill"/>.</summary>
    public static SkillKind ToCoreSkill(TableSkillKind skill) => skill switch
    {
        TableSkillKind.Infiltration => SkillKind.Infiltration,
        TableSkillKind.Combat => SkillKind.Combat,
        TableSkillKind.Tech => SkillKind.Tech,
        TableSkillKind.Social => SkillKind.Social,
        TableSkillKind.Nerve => SkillKind.Nerve,
        _ => throw new ArgumentOutOfRangeException(nameof(skill), skill, "Skill 'None' has no Core equivalent."),
    };

    // ---- recovery -------------------------------------------------------------

    /// <summary>Recovery row for a room type, or null when the room is not a recovery room.</summary>
    public static RecoveryRule? RecoveryFor(int roomTypeId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (RecoveryRule rule in t.TbRecoveryRule.DataList)
        {
            if (rule.RoomTypeId == roomTypeId)
                return rule;
        }

        return null;
    }

    /// <summary>
    /// The mental-to-physical recovery ratio, in percent. Mental is deliberately
    /// slower than physical by design, and the ratio is designer data rather than a
    /// constant so the gap can be tuned without a code change.
    /// </summary>
    public static int MentalRecoveryRatioPercent
    {
        get
        {
            GameTables? t = TablesOrNull;
            if (t is null) return DefaultMentalRatioPercent;

            foreach (RecoveryRule rule in t.TbRecoveryRule.DataList)
                return rule.MentalRatioPercent;

            return DefaultMentalRatioPercent;
        }
    }

    /// <summary>Fallback mental ratio when no table row is available.</summary>
    public const int DefaultMentalRatioPercent = 33;

    // ---- training -------------------------------------------------------------

    /// <summary>Training row for a room type, or null when the room trains nothing.</summary>
    public static TrainingRule? TrainingFor(int roomTypeId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (TrainingRule rule in t.TbTrainingRule.DataList)
        {
            if (rule.RoomTypeId == roomTypeId)
                return rule;
        }

        return null;
    }

    /// <summary>
    /// Base exp a training room awards per tick, before any multiplier.
    /// </summary>
    /// <remarks>
    /// Read from <c>room_type.train_rate_per_tick</c> rather than duplicated in
    /// <c>training_rule</c>: the rate and the skill a room trains are properties of
    /// the room, while the cost and the affinity base are properties of how training
    /// works. Splitting them that way means upgrading a room's output does not require
    /// remembering to also edit the training table.
    /// </remarks>
    public static int TrainRatePerTick(int roomTypeId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return 0;

        RoomType? type = t.TbRoomType.GetOrDefault(roomTypeId);
        return type?.TrainRatePerTick ?? 0;
    }

    /// <summary>Every room type, or empty when the tables are unavailable.</summary>
    public static IReadOnlyList<RoomType> AllRoomTypes()
        => TablesOrNull?.TbRoomType.DataList ?? Array.Empty<RoomType>();

    // ---- keyed rule tables ----------------------------------------------------

    /// <summary>
    /// Reads an <c>economy_rule</c> row by its string key, falling back to
    /// <paramref name="fallback"/> when the key is absent.
    /// </summary>
    public static int Economy(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (EconomyRule rule in t.TbEconomyRule.DataList)
        {
            if (rule.RuleKey == key)
                return rule.Value;
        }

        return fallback;
    }

    /// <summary>Reads a <c>burnout_rule</c> row by key, with a fallback.</summary>
    public static int Burnout(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (BurnoutRule rule in t.TbBurnoutRule.DataList)
        {
            if (rule.RuleKey == key)
                return rule.Value;
        }

        return fallback;
    }

    /// <summary>Reads a <c>counter_intel_rule</c> row by key, with a fallback.</summary>
    public static int CounterIntel(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (CounterIntelRule rule in t.TbCounterIntelRule.DataList)
        {
            if (rule.RuleKey == key)
                return rule.Value;
        }

        return fallback;
    }

    /// <summary>
    /// Every <c>counter_intel_rule</c> row, so the validator and diagnostics can prove
    /// each key the mole system reads actually exists rather than falling back.
    /// </summary>
    public static IReadOnlyList<CounterIntelRule> AllCounterIntel()
        => TablesOrNull?.TbCounterIntelRule.DataList ?? Array.Empty<CounterIntelRule>();

    /// <summary>Daily loyalty drift for a condition, or zero when the source is unused today.</summary>
    public static int LoyaltyDrift(LoyaltyDriftCondition condition)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return 0;

        string key = condition.ToTableKey();
        foreach (LoyaltyDrift drift in t.TbLoyaltyDrift.DataList)
        {
            if (string.Equals(drift.Condition, key, StringComparison.Ordinal))
                return drift.Delta;
        }

        return 0;
    }

    /// <summary>All loyalty drift rows, for the validator and for diagnostics.</summary>
    public static IReadOnlyList<LoyaltyDrift> AllLoyaltyDrift()
        => TablesOrNull?.TbLoyaltyDrift.DataList ?? Array.Empty<LoyaltyDrift>();

    /// <summary>Loyalty thresholds in ascending order of threshold.</summary>
    public static IReadOnlyList<LoyaltyThreshold> LoyaltyThresholds()
    {
        GameTables? t = TablesOrNull;
        if (t is null) return Array.Empty<LoyaltyThreshold>();

        var rows = new List<LoyaltyThreshold>(t.TbLoyaltyThreshold.DataList);
        rows.Sort((a, b) => a.Threshold.CompareTo(b.Threshold));
        return rows;
    }

    /// <summary>Morale bands in ascending order of loyalty floor.</summary>
    public static IReadOnlyList<MoraleBand> MoraleBands()
    {
        GameTables? t = TablesOrNull;
        if (t is null) return Array.Empty<MoraleBand>();

        var rows = new List<MoraleBand>(t.TbMoraleBand.DataList);
        rows.Sort((a, b) => a.LoyaltyFloor.CompareTo(b.LoyaltyFloor));
        return rows;
    }

    /// <summary>Loan tiers in ascending order of amount.</summary>
    public static IReadOnlyList<LoanTier> LoanTiers()
    {
        GameTables? t = TablesOrNull;
        if (t is null) return Array.Empty<LoanTier>();

        var rows = new List<LoanTier>(t.TbLoanTier.DataList);
        rows.Sort((a, b) => a.Amount.CompareTo(b.Amount));
        return rows;
    }

    /// <summary>Heat tiers in ascending order of threshold.</summary>
    public static IReadOnlyList<ProjectSpy.Tables.HeatTier> HeatTiers()
    {
        GameTables? t = TablesOrNull;
        if (t is null) return Array.Empty<ProjectSpy.Tables.HeatTier>();

        var rows = new List<ProjectSpy.Tables.HeatTier>(t.TbHeatTier.DataList);
        rows.Sort((a, b) => a.Threshold.CompareTo(b.Threshold));
        return rows;
    }

    /// <summary>
    /// The recruitment row for a given HR room level and reputation tier.
    /// </summary>
    /// <remarks>
    /// Returns the best available row when the exact pair is missing — specifically,
    /// the highest HR level that does not exceed the one built, at the requested
    /// reputation tier. A missing exact row should degrade the pool, not halt
    /// recruiting entirely.
    /// </remarks>
    public static RecruitRule? RecruitFor(int hrLevel, int reputationTier)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        RecruitRule? best = null;
        foreach (RecruitRule rule in t.TbRecruitRule.DataList)
        {
            if (rule.ReputationTier != reputationTier || rule.HrLevel > hrLevel)
                continue;

            if (best is null || rule.HrLevel > best.HrLevel)
                best = rule;
        }

        return best;
    }

    /// <summary>Converts raw Reputation into the 0-2 tier the recruit table is keyed by.</summary>
    public static int ReputationTier(int reputation)
        => reputation >= 50 ? 2 : reputation >= 20 ? 1 : 0;

    // ---- stage 4: mission maps ------------------------------------------------

    /// <summary>
    /// The map generation row for a tier, or null when the tier has no row.
    /// </summary>
    public static MapGenRule? MapGenRuleFor(int tier)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (MapGenRule rule in t.TbMapGenRule.DataList)
        {
            if (rule.Tier == tier)
                return rule;
        }

        return null;
    }

    /// <summary>Every map generation row, in table order.</summary>
    public static IReadOnlyList<MapGenRule> AllMapGenRules()
        => TablesOrNull?.TbMapGenRule.DataList ?? Array.Empty<MapGenRule>();

    /// <summary>A mission type row by id, or null.</summary>
    public static MissionType? MissionTypeFor(int missionTypeId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbMissionType.GetOrDefault(missionTypeId);
    }

    /// <summary>Every <c>node_room</c> row, in table order.</summary>
    public static IReadOnlyList<NodeRoom> AllNodeRooms()
        => TablesOrNull?.TbNodeRoom.DataList ?? Array.Empty<NodeRoom>();

    /// <summary>
    /// The node rooms a map at <paramref name="tier"/> is allowed to assign, in table
    /// order.
    /// </summary>
    /// <remarks>
    /// Filtering happens here rather than in the generator so that "which rooms can
    /// a tier use" has exactly one answer, shared by the generator, the fog of war
    /// and the validator.
    /// </remarks>
    public static IReadOnlyList<NodeRoom> NodeRoomsForTier(int tier)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return Array.Empty<NodeRoom>();

        var allowed = new List<NodeRoom>();
        foreach (NodeRoom room in t.TbNodeRoom.DataList)
        {
            if (room.Weight <= 0)
                continue;

            if (TierList(room.AllowedTiers).Contains(tier))
                allowed.Add(room);
        }

        return allowed;
    }

    /// <summary>
    /// The node rooms carrying the <c>security</c> tag that a map at
    /// <paramref name="tier"/> is allowed to assign.
    /// </summary>
    /// <remarks>
    /// The generator needs this to build the checkpoint layer that guarantees a
    /// security node on every route to the objective. If a tier had no security
    /// rooms at all the map could not honour that guarantee, so this returning
    /// empty is a data error the table validator catches rather than something to
    /// paper over at runtime.
    /// </remarks>
    public static IReadOnlyList<NodeRoom> SecurityNodeRoomsForTier(int tier)
    {
        var security = new List<NodeRoom>();
        foreach (NodeRoom room in NodeRoomsForTier(tier))
        {
            if (Tags(room.Tags).Contains("security"))
                security.Add(room);
        }

        return security;
    }

    /// <summary>Mission event rows by id, for a mission's event resolution.</summary>
    public static MissionEvent? MissionEventFor(int eventId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbMissionEvent.GetOrDefault(eventId);
    }

    /// <summary>Every mission event row, in table order.</summary>
    public static IReadOnlyList<MissionEvent> AllMissionEvents()
        => TablesOrNull?.TbMissionEvent.DataList ?? Array.Empty<MissionEvent>();

    /// <summary>
    /// Splits a comma-separated table list into trimmed, non-empty entries.
    /// </summary>
    /// <remarks>
    /// <c>node_room.tags</c>, <c>allowed_tiers</c> and <c>possible_event_ids</c> are
    /// all stored as one string column. Parsing lives here so that an empty field, a
    /// stray space or a trailing comma is handled the same way everywhere instead of
    /// becoming a per-callsite surprise.
    /// </remarks>
    public static IReadOnlyList<string> Tags(string? value) => SplitList(value);

    /// <summary>Parses a comma-separated list of ints, skipping anything unparseable.</summary>
    public static IReadOnlyList<int> IntList(string? value)
    {
        var parsed = new List<int>();
        foreach (string part in SplitList(value))
        {
            if (int.TryParse(part, out int number))
                parsed.Add(number);
        }

        return parsed;
    }

    private static IReadOnlyList<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        string[] parts = value.Split(',');
        var cleaned = new List<string>(parts.Length);

        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
                cleaned.Add(trimmed);
        }

        return cleaned;
    }

    private static IReadOnlyList<int> TierList(string? value) => IntList(value);

    // ---- stage 4: fog of war and node interiors ------------------------------

    /// <summary>
    /// Reads a <c>fog_rule</c> value by key, with a fallback.
    /// </summary>
    /// <remarks>
    /// The reveal-radius formula and every constant in it live in a table rather
    /// than in code (knowledge.md rule 3). A designer tuning how far a specialist
    /// infiltrator can see should be editing <c>fog_rule.csv</c>, and should not
    /// need a programmer.
    /// </remarks>
    public static int Fog(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (FogRule rule in t.TbFogRule.DataList)
        {
            if (string.Equals(rule.RuleKey, key, StringComparison.Ordinal))
                return rule.Value;
        }

        return fallback;
    }/// <summary>Every <c>fog_rule</c> row, so the validator can prove the keys exist.</summary>
    public static IReadOnlyList<FogRule> AllFog() => TablesOrNull?.TbFogRule.DataList ?? Array.Empty<FogRule>();

    // ---- stage 4: interactables and room templates ---------------------------

    /// <summary>An <c>interactable_type</c> row by id, or null.</summary>
    public static InteractableTypeRow? InteractableTypeFor(int id)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbInteractableType.GetOrDefault(id);
    }

    /// <summary>Every <c>interactable_type</c> row, in table order.</summary>
    public static IReadOnlyList<InteractableTypeRow> AllInteractableTypes()
        => TablesOrNull?.TbInteractableType.DataList ?? Array.Empty<InteractableTypeRow>();

    /// <summary>
    /// Every <c>interactable_type</c> whose <c>allowed_room_tags</c> intersects
    /// <paramref name="roomTags"/>.
    /// </summary>
    /// <remarks>
    /// An empty tag list means "allowed anywhere", which is how <c>Exit</c> and
    /// <c>Container</c> stay available in every room without a row per room type.
    /// </remarks>
    public static IReadOnlyList<InteractableTypeRow> InteractableTypesForRoom(IReadOnlyList<string> roomTags)
    {
        GameTables? t = TablesOrNull;
        if (t is null || roomTags is null) return Array.Empty<InteractableTypeRow>();

        var allowed = new List<InteractableTypeRow>();

        foreach (InteractableTypeRow row in t.TbInteractableType.DataList)
        {
            if (row.Weight <= 0)
                continue;

            IReadOnlyList<string> permitted = Tags(row.AllowedRoomTags);

            if (permitted.Count == 0 || permitted.Any(tag => roomTags.Contains(tag)))
                allowed.Add(row);
        }

        return allowed;
    }

    /// <summary>
    /// The <c>room_template</c> for a node room, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Scanned rather than looked up, because <c>room_template</c> is keyed by its own
    /// surrogate id and the query is by <c>node_room_id</c>. There are a couple of dozen
    /// rows, so a scan is cheaper than any index and keeps a duplicate row a validator
    /// error rather than an arbitrary choice.
    /// </remarks>
    public static NodeInteriorTemplateRow? RoomTemplateFor(int nodeRoomId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (NodeInteriorTemplateRow row in t.TbNodeInteriorTemplate.DataList)
        {
            if (row.NodeRoomId == nodeRoomId)
                return row;
        }

        return null;
    }

    /// <summary>Every <c>room_template</c> row, for the validator.</summary>
    public static IReadOnlyList<NodeInteriorTemplateRow> AllRoomTemplates()
        => TablesOrNull?.TbNodeInteriorTemplate.DataList ?? Array.Empty<NodeInteriorTemplateRow>();

    /// <summary>
    /// Converts a table interactable kind to Core's <see cref="InteractableType"/>.
    /// </summary>
    /// <remarks>
    /// An explicit switch rather than a cast. The two enums are numbered independently
    /// — the table's is 1-based per <c>Defines/__beans__.xml</c>, Core's is 0-based and
    /// append-only — so a cast would map every kind to the wrong value while still
    /// compiling and still running. Writing the pairs out means renumbering either enum
    /// turns into a compile error here instead of a building full of wrong rooms.
    /// </remarks>
    public static InteractableType ToCoreKind(TableInteractableKind kind) => kind switch
    {
        TableInteractableKind.Container => InteractableType.Container,
        TableInteractableKind.Door => InteractableType.Door,
        TableInteractableKind.Terminal => InteractableType.Terminal,
        TableInteractableKind.Guard => InteractableType.Guard,
        TableInteractableKind.Trap => InteractableType.Trap,
        TableInteractableKind.Camera => InteractableType.Camera,
        TableInteractableKind.Objective => InteractableType.Objective,
        TableInteractableKind.Exit => InteractableType.Exit,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown interactable kind."),
    };

    /// <summary>The localization key for a kind, e.g. <c>interactable.guard</c>.</summary>
    /// <remarks>
    /// Derived from the enum name rather than stored in the table: the key is a naming
    /// convention, and a column the designer can typo would fail silently at the point
    /// where the label is needed.
    /// </remarks>
    public static string NameKeyFor(InteractableType type) => type switch
    {
        InteractableType.Container => "interactable.container",
        InteractableType.Door => "interactable.door",
        InteractableType.Terminal => "interactable.terminal",
        InteractableType.Guard => "interactable.guard",
        InteractableType.Trap => "interactable.trap",
        InteractableType.Camera => "interactable.camera",
        InteractableType.Objective => "interactable.objective",
        InteractableType.Exit => "interactable.exit",
        _ => "interactable.unknown",
    };

    /// <summary>
    /// True when a kind may appear in a room carrying these tags.
    /// </summary>
    /// <remarks>
    /// An empty <c>allowed_room_tags</c> means anywhere. Mirrors
    /// <see cref="InteractableTypesForRoom"/> so a row that fails this check fails the
    /// generator's candidate list too, rather than the two disagreeing about what a
    /// room may contain.
    /// </remarks>
    public static bool IsKindAllowedIn(InteractableTypeRow row, IReadOnlyList<string> roomTags)
    {
        if (row is null || roomTags is null) return false;

        IReadOnlyList<string> permitted = Tags(row.AllowedRoomTags);

        return permitted.Count == 0 || permitted.Any(tag => roomTags.Contains(tag));
    }

    /// <summary>Reads a <c>node_interior_rule</c> value by key, with a fallback.</summary>
    public static int NodeInterior(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (NodeInteriorRule rule in t.TbNodeInteriorRule.DataList)
        {
            if (string.Equals(rule.RuleKey, key, StringComparison.Ordinal))
                return rule.Value;
        }

        return fallback;
    }

    /// <summary>
    /// Every <c>node_interior_rule</c> row, so the validator can prove the keys exist.
    /// </summary>
    public static IReadOnlyList<NodeInteriorRule> AllNodeInterior()
        => TablesOrNull?.TbNodeInteriorRule.DataList ?? Array.Empty<NodeInteriorRule>();

    // ---- stage 4a: site generation ------------------------------------------

    /// <summary>
    /// A <c>site_gen_rule</c> value by key, with a fallback.
    /// </summary>
    /// <remarks>
    /// Every tunable the site generator reads lives here rather than in
    /// <c>SiteGenerator</c>, for knowledge.md rule 3: a designer tuning how many
    /// rooms a floor should hold, how often a door is locked or how far apart
    /// light emitters sit should be editing a CSV, not asking a programmer.
    /// <c>TableValidator</c> lists the keys it must contain, so deleting one is a
    /// test failure rather than a silent fallback.
    /// </remarks>
    public static int SiteGen(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (SiteGenRuleRow rule in t.TbSiteGenRule.DataList)
        {
            if (string.Equals(rule.RuleKey, key, StringComparison.Ordinal))
                return rule.Value;
        }

        return fallback;
    }

    /// <summary>Every <c>site_gen_rule</c> row, so the validator can prove the keys exist.</summary>
    public static IReadOnlyList<SiteGenRuleRow> AllSiteGen()
        => TablesOrNull?.TbSiteGenRule.DataList ?? Array.Empty<SiteGenRuleRow>();

    /// <summary>A <c>site_template</c> row by id, or null.</summary>
    public static SiteTemplateRow? SiteTemplateFor(int siteTemplateId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbSiteTemplate.GetOrDefault(siteTemplateId);
    }

    /// <summary>Every <c>site_template</c> row, in table order.</summary>
    public static IReadOnlyList<SiteTemplateRow> AllSiteTemplates()
        => TablesOrNull?.TbSiteTemplate.DataList ?? Array.Empty<SiteTemplateRow>();

    /// <summary>
    /// A tactical <c>room_template</c> row by id, or null.
    /// </summary>
    /// <remarks>
    /// Named to keep it apart from <see cref="RoomTemplateFor"/>, which returns a
    /// <c>node_interior_template</c>. The two tables model opposite things — slot
    /// indices for an abstract node versus a walkable interval on a floor — and a
    /// single shared name for both was how the stage-2 rename of the node table
    /// ended up being necessary.
    /// </remarks>
    public static TacticalRoomTemplateRow? TacticalRoomTemplateFor(int roomTemplateId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbRoomTemplate.GetOrDefault(roomTemplateId);
    }

    /// <summary>Every tactical <c>room_template</c> row, in table order.</summary>
    public static IReadOnlyList<TacticalRoomTemplateRow> AllTacticalRoomTemplates()
        => TablesOrNull?.TbRoomTemplate.DataList ?? Array.Empty<TacticalRoomTemplateRow>();

    /// <summary>A <c>connection_type</c> row by id, or null.</summary>
    public static ConnectionTypeRow? ConnectionTypeFor(int connectionTypeId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbConnectionType.GetOrDefault(connectionTypeId);
    }

    /// <summary>
    /// The <c>connection_type</c> that a kind of connection defaults to.
    /// </summary>
    /// <remarks>
    /// A kind is a category (Stair, Vent); a type is the specific row that says what
    /// it costs and whether it blocks sight. Which row is "the" row for a kind is a
    /// data question, so the first row in table order wins and the table is ordered
    /// so that it is deliberate.
    /// </remarks>
    public static ConnectionTypeRow? DefaultConnectionTypeFor(SiteConnectionKind kind)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        SiteConnectionKind target = kind;

        foreach (ConnectionTypeRow row in t.TbConnectionType.DataList)
        {
            if (ToCoreConnectionKind(row.Kind) == target)
                return row;
        }

        return null;
    }

    /// <summary>Every <c>connection_type</c> row, in table order.</summary>
    public static IReadOnlyList<ConnectionTypeRow> AllConnectionTypes()
        => TablesOrNull?.TbConnectionType.DataList ?? Array.Empty<ConnectionTypeRow>();

    /// <summary>A <c>guard_archetype</c> row by id, or null.</summary>
    public static GuardArchetypeRow? GuardArchetypeFor(int archetypeId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbGuardArchetype.GetOrDefault(archetypeId);
    }

    /// <summary>Every <c>guard_archetype</c> row, in table order.</summary>
    public static IReadOnlyList<GuardArchetypeRow> AllGuardArchetypes()
        => TablesOrNull?.TbGuardArchetype.DataList ?? Array.Empty<GuardArchetypeRow>();

    /// <summary>A <c>lighting_profile</c> row by id, or null.</summary>
    public static LightingProfileRow? LightingProfileFor(int profileId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbLightingProfile.GetOrDefault(profileId);
    }

    /// <summary>A <c>light_source</c> row by id, or null.</summary>
    public static LightSourceRow? LightSourceFor(int lightSourceId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbLightSource.GetOrDefault(lightSourceId);
    }

    /// <summary>Every <c>light_source</c> row, in table order.</summary>
    public static IReadOnlyList<LightSourceRow> AllLightSources()
        => TablesOrNull?.TbLightSource.DataList ?? Array.Empty<LightSourceRow>();

    /// <summary>
    /// The <c>loot_table</c> entries for one table id, in entry order.
    /// </summary>
    /// <remarks>
    /// Scanned rather than looked up because <c>loot_table</c> is keyed by its own
    /// surrogate id with a second, per-table entry id, and the query is by the first.
    /// There are a couple of dozen entries, so a scan is cheaper than any index and keeps
    /// a duplicate entry a validator error rather than an arbitrary choice.
    /// </remarks>
    public static IReadOnlyList<LootTableRow> LootEntriesFor(int lootTableId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return Array.Empty<LootTableRow>();

        var entries = new List<LootTableRow>();
        foreach (LootTableRow entry in t.TbLootTable.DataList)
        {
            if (entry.Id == lootTableId)
                entries.Add(entry);
        }

        return entries;
    }

    /// <summary>A <c>noise_profile</c> row by id, or null.</summary>
    public static NoiseProfileRow? NoiseProfileFor(int noiseProfileId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbNoiseProfile.GetOrDefault(noiseProfileId);
    }

    /// <summary>
    /// Every <c>noise_profile</c> row, so the validator and the noise tests can prove
    /// the rows the simulation relies on actually exist.
    /// </summary>
    public static IReadOnlyList<NoiseProfileRow> AllNoiseProfiles()
        => TablesOrNull?.TbNoiseProfile.DataList ?? Array.Empty<NoiseProfileRow>();

    /// <summary>
    /// True when a room template is allowed on a floor of the given kind.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the generator so that "which rooms may sit on a
    /// basement" has exactly one answer, shared by the generator and the validator.
    /// <see cref="ProjectSpy.Tables.FloorKind.Any"/> is the permissive case and
    /// matches every floor.
    /// </remarks>
    public static bool IsRoomAllowedOnFloor(TacticalRoomTemplateRow row, SiteFloorKind floor)
    {
        if (row is null) return false;

        return row.ValidFloors switch
        {
            FloorKind.Any => true,
            FloorKind.Ground => floor == SiteFloorKind.Ground,
            FloorKind.Upper => floor == SiteFloorKind.Upper,
            FloorKind.Basement => floor == SiteFloorKind.Basement,
            _ => false,
        };
    }

    /// <summary>
    /// Converts a table connection kind to Core's <see cref="SiteConnectionKind"/>.
    /// </summary>
    /// <remarks>
    /// An explicit switch rather than a cast, for the reason given on
    /// <see cref="ToCoreKind"/>: the two enums are numbered independently, so a cast
    /// would silently mis-map every connection in the building while still compiling.
    /// </remarks>
    public static SiteConnectionKind ToCoreConnectionKind(TableConnectionKind kind) => kind switch
    {
        TableConnectionKind.Door => SiteConnectionKind.Door,
        TableConnectionKind.LockedDoor => SiteConnectionKind.LockedDoor,
        TableConnectionKind.Stair => SiteConnectionKind.Stair,
        TableConnectionKind.Ladder => SiteConnectionKind.Ladder,
        TableConnectionKind.Vent => SiteConnectionKind.Vent,
        TableConnectionKind.Window => SiteConnectionKind.Window,
        TableConnectionKind.Hole => SiteConnectionKind.Hole,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown connection kind."),
    };

    /// <summary>Inverse of <see cref="ToCoreConnectionKind"/>.</summary>
    public static TableConnectionKind ToTableConnectionKind(SiteConnectionKind kind) => kind switch
    {
        SiteConnectionKind.Door => TableConnectionKind.Door,
        SiteConnectionKind.LockedDoor => TableConnectionKind.LockedDoor,
        SiteConnectionKind.Stair => TableConnectionKind.Stair,
        SiteConnectionKind.Ladder => TableConnectionKind.Ladder,
        SiteConnectionKind.Vent => TableConnectionKind.Vent,
        SiteConnectionKind.Window => TableConnectionKind.Window,
        SiteConnectionKind.Hole => TableConnectionKind.Hole,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown connection kind."),
    };

    /// <summary>Converts a table light level to Core's <see cref="SiteLightLevel"/>.</summary>
    public static SiteLightLevel ToCoreLightLevel(TableLightLevel level) => level switch
    {
        TableLightLevel.Dark => SiteLightLevel.Dark,
        TableLightLevel.Dim => SiteLightLevel.Dim,
        TableLightLevel.Lit => SiteLightLevel.Lit,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown light level."),
    };

    /// <summary>Inverse of <see cref="ToCoreLightLevel"/>.</summary>
    public static TableLightLevel ToTableLightLevel(SiteLightLevel level) => level switch
    {
        SiteLightLevel.Dark => TableLightLevel.Dark,
        SiteLightLevel.Dim => TableLightLevel.Dim,
        SiteLightLevel.Lit => TableLightLevel.Lit,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown light level."),
    };

    /// <summary>Converts a table guard role to Core's <see cref="SiteGuardRole"/>.</summary>
    public static SiteGuardRole ToCoreGuardRole(TableGuardRole role) => role switch
    {
        TableGuardRole.Patrol => SiteGuardRole.Patrol,
        TableGuardRole.Sentry => SiteGuardRole.Sentry,
        TableGuardRole.Responder => SiteGuardRole.Responder,
        TableGuardRole.Specialist => SiteGuardRole.Specialist,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown guard role."),
    };

    /// <summary>Inverse of <see cref="ToCoreGuardRole"/>.</summary>
    public static TableGuardRole ToTableGuardRole(SiteGuardRole role) => role switch
    {
        SiteGuardRole.Patrol => TableGuardRole.Patrol,
        SiteGuardRole.Sentry => TableGuardRole.Sentry,
        SiteGuardRole.Responder => TableGuardRole.Responder,
        SiteGuardRole.Specialist => TableGuardRole.Specialist,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown guard role."),
    };

    // ---- stage 4b: pre-mission intel ----------------------------------------

    /// <summary>Reads an <c>intel_rule</c> row by key, with a fallback.</summary>
    public static int Intel(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (IntelRuleRow rule in t.TbIntelRule.DataList)
        {
            if (rule.RuleKey == key)
                return rule.Value;
        }

        return fallback;
    }

    /// <summary>
    /// Every <c>intel_rule</c> row, so the validator can prove the keys the intel system
    /// reads actually exist rather than falling back to an unapproved number.
    /// </summary>
    public static IReadOnlyList<IntelRuleRow> AllIntel()
        => TablesOrNull?.TbIntelRule.DataList ?? Array.Empty<IntelRuleRow>();

    /// <summary>The <c>sleeper_op</c> row for a site tier, or null.</summary>
    public static SleeperOpRow? SleeperOpFor(int siteTier)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (SleeperOpRow op in t.TbSleeperOp.DataList)
        {
            if (op.SiteTier == siteTier)
                return op;
        }

        return null;
    }

    /// <summary>Every <c>sleeper_op</c> row.</summary>
    public static IReadOnlyList<SleeperOpRow> AllSleeperOps()
        => TablesOrNull?.TbSleeperOp.DataList ?? Array.Empty<SleeperOpRow>();

    /// <summary>The <c>capture_site</c> row for a tier, or null.</summary>
    public static CaptureSiteRow? CaptureSiteFor(int siteTier)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (CaptureSiteRow capture in t.TbCaptureSite.DataList)
        {
            if (capture.SiteTier == siteTier)
                return capture;
        }

        return null;
    }

    /// <summary>Every <c>capture_site</c> row.</summary>
    public static IReadOnlyList<CaptureSiteRow> AllCaptureSites()
        => TablesOrNull?.TbCaptureSite.DataList ?? Array.Empty<CaptureSiteRow>();

    // ---- stage 4c: the tactical action set -----------------------------------

    /// <summary>A <c>tactical_action</c> row by id, or null.</summary>
    /// <remarks>
    /// The stage-4c brief says "implement every row of tactical_action.csv", which
    /// makes this table the action system's whole vocabulary. Reading it through one
    /// accessor rather than reaching into Luban from the action system means there is
    /// exactly one answer to "what does this action cost", and
    /// <c>TableValidator</c> can assert that every row is reachable.
    /// </remarks>
    public static TacticalActionRow? TacticalActionFor(int actionId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbTacticalAction.GetOrDefault(actionId);
    }

    /// <summary>
    /// The <c>tactical_action</c> id whose <c>name_key</c> matches, or zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By name rather than by id because every caller of this wants an action's
    /// <em>meaning</em> — "crouch-walk" — and a literal 12403 in a policy says only that
    /// somebody once wrote down a number. Renumbering the table would otherwise silently
    /// turn a cautious policy into a sprinting one, which is the kind of bug that only
    /// shows up in a balance report six stages later.
    /// </para>
    /// <para>
    /// Returns zero — which is <see cref="TacticalOrder.StopActionId"/> — rather than
    /// throwing, so a caller that has no table loaded degrades to "do nothing" instead of
    /// taking the process down. A missing action is a data error worth a loud test, not
    /// worth a crash inside a 5,000-mission sweep.
    /// </para>
    /// </remarks>
    public static int TacticalActionIdFor(string nameKey)
    {
        GameTables? t = TablesOrNull;
        if (t is null || string.IsNullOrEmpty(nameKey))
            return 0;

        foreach (TacticalActionRow row in t.TbTacticalAction.DataList)
        {
            if (string.Equals(row.NameKey, nameKey, StringComparison.Ordinal))
                return row.Id;
        }

        return 0;
    }

    /// <summary>Every <c>tactical_action</c> row, in table order.</summary>
    public static IReadOnlyList<TacticalActionRow> AllTacticalActions()
        => TablesOrNull?.TbTacticalAction.DataList ?? Array.Empty<TacticalActionRow>();

    /// <summary>The <c>tactical_action</c> rows in one category, in table order.</summary>
    public static IReadOnlyList<TacticalActionRow> TacticalActionsIn(ActionCategory category)
    {
        IReadOnlyList<TacticalActionRow> all = AllTacticalActions();
        var matching = new List<TacticalActionRow>();

        foreach (TacticalActionRow row in all)
        {
            if (row.Category == category)
                matching.Add(row);
        }

        return matching;
    }

    /// <summary>A <c>throwable</c> row by id, or null.</summary>
    public static ThrowableRow? ThrowableFor(int throwableId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbThrowable.GetOrDefault(throwableId);
    }

    /// <summary>Every <c>throwable</c> row, in table order.</summary>
    public static IReadOnlyList<ThrowableRow> AllThrowables()
        => TablesOrNull?.TbThrowable.DataList ?? Array.Empty<ThrowableRow>();

    /// <summary>A <c>melee_weapon</c> row by id, or null.</summary>
    public static MeleeWeaponRow? MeleeWeaponFor(int weaponId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbMeleeWeapon.GetOrDefault(weaponId);
    }

    /// <summary>Every <c>melee_weapon</c> row, in table order.</summary>
    public static IReadOnlyList<MeleeWeaponRow> AllMeleeWeapons()
        => TablesOrNull?.TbMeleeWeapon.DataList ?? Array.Empty<MeleeWeaponRow>();

    /// <summary>A <c>gadget</c> row by id, or null.</summary>
    /// <remarks>
    /// Stage 4e needs two things from this table that nothing else wanted: how many uses
    /// a gadget has left when an agent is handed one at dispatch, and how much it weighs,
    /// because the brief makes total carry weight a dispatch gate. Both live here so
    /// there is one answer to "what does gadget 8504 cost to carry".
    /// </remarks>
    public static GadgetRow? GadgetFor(int gadgetId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbGadget.GetOrDefault(gadgetId);
    }

    /// <summary>Every <c>gadget</c> row, in table order.</summary>
    public static IReadOnlyList<GadgetRow> AllGadgets()
        => TablesOrNull?.TbGadget.DataList ?? Array.Empty<GadgetRow>();

    /// <summary>An <c>item</c> row by id, or null.</summary>
    public static ItemRow? ItemFor(int itemId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbItem.GetOrDefault(itemId);
    }

    /// <summary>Every <c>item</c> row, in table order.</summary>
    public static IReadOnlyList<ItemRow> AllItems()
        => TablesOrNull?.TbItem.DataList ?? Array.Empty<ItemRow>();

    /// <summary>A <c>goap_goal</c> row by id, or null.</summary>
    public static GoapGoalRow? GoapGoalFor(int goalId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbGoapGoal.GetOrDefault(goalId);
    }

    /// <summary>Every <c>goap_goal</c> row, in table order.</summary>
    public static IReadOnlyList<GoapGoalRow> AllGoapGoals()
        => TablesOrNull?.TbGoapGoal.DataList ?? Array.Empty<GoapGoalRow>();

    /// <summary>A <c>goap_action</c> row by id, or null.</summary>
    public static GoapActionRow? GoapActionFor(int actionId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbGoapAction.GetOrDefault(actionId);
    }/// <summary>Every <c>goap_action</c> row, in table order.</summary>
    public static IReadOnlyList<GoapActionRow> AllGoapActions() => TablesOrNull?.TbGoapAction.DataList ?? Array.Empty<GoapActionRow>();

    // ---- stage 4d: the GOAP planner ------------------------------------------

    /// <summary>Reads a <c>goap_rule</c> row by key, with a fallback.</summary>
    /// <remarks>
    /// The stage-4d brief requires the planning budget to be a table value rather than
    /// a constant: how many NPCs may replan in a single step is the knob that decides
    /// whether 30 guards cost 2ms or 20ms, and a designer tuning a building's density
    /// should be editing a CSV rather than calling a programmer. It sits in its own
    /// <c>goap_rule</c> table for the same reason <c>intel_rule</c> and
    /// <c>site_gen_rule</c> do — a tactical-AI budget filed under site generation would
    /// be read by the next person as belonging there.
    /// </remarks>
    public static int Goap(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (GoapRuleRow rule in t.TbGoapRule.DataList)
        {
            if (string.Equals(rule.RuleKey, key, StringComparison.Ordinal))
                return rule.Value;
        }

        return fallback;
    }

    /// <summary>Every <c>goap_rule</c> row, so the validator can prove the keys exist.</summary>
    public static IReadOnlyList<GoapRuleRow> AllGoapRules() => TablesOrNull?.TbGoapRule.DataList ?? Array.Empty<GoapRuleRow>();

    // ---- stage 4e: the squad -------------------------------------------------

    /// <summary>Reads a <c>squad_rule</c> row by key, with a fallback.</summary>
    /// <remarks>
    /// The stage-4e brief makes squad composition a decision the player makes under
    /// constraints, and every one of those constraints is a number a designer will want
    /// to move: how rested a team has to be, how much a squad may carry, how far apart a
    /// following ally trails. They live in their own table rather than being appended to
    /// <c>goap_rule</c> because a squad's parameters have nothing to do with how the
    /// site's guards think, and a future reader of that table should not have to work
    /// out which half is theirs.
    /// </remarks>
    public static int Squad(string key, int fallback)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return fallback;

        foreach (SquadRuleRow rule in t.TbSquadRule.DataList)
        {
            if (string.Equals(rule.RuleKey, key, StringComparison.Ordinal))
                return rule.Value;
        }

        return fallback;
    }

    /// <summary>Every <c>squad_rule</c> row, so the validator can prove the keys exist.</summary>
    public static IReadOnlyList<SquadRuleRow> AllSquadRules() => TablesOrNull?.TbSquadRule.DataList ?? Array.Empty<SquadRuleRow>();

    /// <summary>An <c>agent_role</c> row by id, or null.</summary>
    /// <remarks>
    /// The dispatch screen reads these to show what a role does and what it may be
    /// ordered to do, and the squad validator reads them to check a composition against
    /// the objective's required coverage. Both go through here so "what may a Hacker be
    /// told to do" has one answer.
    /// </remarks>
    public static AgentRoleRow? AgentRoleFor(int roleId)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        return t.TbAgentRole.GetOrDefault(roleId);
    }

    /// <summary>
    /// Every <c>agent_class</c> row, in table order.
    /// </summary>
    /// <remarks>
    /// Read rather than hard-coded by anything that needs a class id. The test harness
    /// kept its own consts for these, which was fine until a headless harness needed one
    /// too and a literal would have meant renumbering the table silently re-roled every
    /// agent in a balance report.
    /// </remarks>
    public static IReadOnlyList<AgentClassRow> AllAgentClasses()
        => TablesOrNull?.TbAgentClass.DataList ?? Array.Empty<AgentClassRow>();

    /// <summary>An <c>agent_class</c> row by <c>name_key</c>, or null.</summary>
    public static AgentClassRow? AgentClassFor(string nameKey)
    {
        GameTables? t = TablesOrNull;
        if (t is null || string.IsNullOrEmpty(nameKey))
            return null;

        foreach (AgentClassRow row in t.TbAgentClass.DataList)
        {
            if (string.Equals(row.NameKey, nameKey, StringComparison.Ordinal))
                return row;
        }

        return null;
    }

    /// <summary>Every <c>agent_role</c> row, in table order.</summary>
    public static IReadOnlyList<AgentRoleRow> AllAgentRoles() => TablesOrNull?.TbAgentRole.DataList ?? Array.Empty<AgentRoleRow>();

    /// <summary>
    /// The <c>objective_rule</c> row for one objective type, or null.
    /// </summary>
    /// <remarks>
    /// One row per <see cref="TableObjectiveType"/> rather than a key/value table,
    /// because the six objective types have genuinely different shapes of rule — a recon
    /// needs a room count, a sabotage needs a blast interval, a rescue needs a follow
    /// speed — and forcing them into one shared set of columns would mean most of the
    /// table being zeros that mean "not applicable", which is a worse encoding than a
    /// per-type row.
    /// </remarks>
    public static ObjectiveRuleRow? ObjectiveRuleFor(TableObjectiveType objectiveType)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (ObjectiveRuleRow row in t.TbObjectiveRule.DataList)
        {
            if (row.ObjectiveType == objectiveType)
                return row;
        }

        return null;
    }

    /// <summary>Every <c>objective_rule</c> row, in table order.</summary>
    public static IReadOnlyList<ObjectiveRuleRow> AllObjectiveRules() => TablesOrNull?.TbObjectiveRule.DataList ?? Array.Empty<ObjectiveRuleRow>();

    /// <summary>A <c>command_post_ability</c> row by kind, or null.</summary>
    public static CommandPostAbilityRow? CommandPostAbilityFor(TableSupportAbility ability)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (CommandPostAbilityRow row in t.TbCommandPostAbility.DataList)
        {
            if (row.Ability == ability)
                return row;
        }

        return null;
    }

    /// <summary>Every <c>command_post_ability</c> row, in table order.</summary>
    public static IReadOnlyList<CommandPostAbilityRow> AllCommandPostAbilities() => TablesOrNull?.TbCommandPostAbility.DataList ?? Array.Empty<CommandPostAbilityRow>();

    /// <summary>The <c>resolve_rule</c> row for one outcome class, or null.</summary>
    /// <remarks>
    /// This is the whole reward model for a mission in one row per class, which is the
    /// point of the table: "what does a compromised mission pay" is a single spreadsheet
    /// cell rather than a switch spread across the debrief and the strategic layer.
    /// </remarks>
    public static ResolveRuleRow? ResolveRuleFor(TableResolveClass resolveClass)
    {
        GameTables? t = TablesOrNull;
        if (t is null) return null;

        foreach (ResolveRuleRow row in t.TbResolveRule.DataList)
        {
            if (row.ResolveClass == resolveClass)
                return row;
        }

        return null;
    }

    /// <summary>Every <c>resolve_rule</c> row, in table order.</summary>
    public static IReadOnlyList<ResolveRuleRow> AllResolveRules() => TablesOrNull?.TbResolveRule.DataList ?? Array.Empty<ResolveRuleRow>();

    // ---- helpers --------------------------------------------------------------

    /// <summary>
    /// Applies a percentage to an integer amount with truncation toward zero.
    /// </summary>
    /// <remarks>
    /// The one place percentages become numbers. Centralizing it means every rule
    /// rounds the same way, which is what makes the hash in
    /// <see cref="WorldState.ComputeStateHash"/> comparable across runs.
    /// </remarks>
    public static int PercentOf(int value, int percent)
        => (int)((long)value * percent / 100L);

    /// <summary>Applies a percentage to a currency amount with truncation toward zero.</summary>
    public static long PercentOf(long value, int percent)
        => value * percent / 100L;
}