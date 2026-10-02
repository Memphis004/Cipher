using ProjectSpy.Tables;

// The generated manager class is named `Tables`, which collides with the
// ProjectSpy.Tables namespace. Because this file lives under ProjectSpy.*, the bare
// name binds to the namespace, so an alias is required.
using GameTables = ProjectSpy.Tables.Tables;

// The table's SkillKind has a `None` member with no Core equivalent; the two types
// are deliberately kept distinct (see data/README.md), so this alias marks every
// crossing between them as an explicit conversion.
using TableSkillKind = ProjectSpy.Tables.SkillKind;

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