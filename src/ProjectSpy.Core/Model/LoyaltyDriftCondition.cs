namespace ProjectSpy.Core;

/// <summary>
/// The conditions the daily loyalty drift pass can charge for.
/// </summary>
/// <remarks>
/// <para>
/// This is the Core-side name for a <c>loyalty_drift.condition</c> value. It exists so
/// the drift pass reads as code rather than as a pile of string comparisons, and so a
/// typo becomes a compile error instead of a silently-zero drift.
/// </para>
/// <para>
/// Each condition maps to exactly one row in <c>loyalty_drift.csv</c>. Several may
/// apply on the same day and they sum — a burnt-out, overworked agent left idle in a
/// run-down base accumulates all four penalties, which is the point.
/// </para>
/// </remarks>
public enum LoyaltyDriftCondition
{
    /// <summary>Training or working below the stamina threshold.</summary>
    Overworked = 0,

    /// <summary>Doing useful work rather than sitting idle.</summary>
    Working = 1,

    /// <summary>Assigned to no room and not on a mission.</summary>
    Idle = 2,

    /// <summary>Came back from a failed mission.</summary>
    MissionLoss = 3,

    /// <summary>Came back from a successful mission.</summary>
    MissionSuccess = 4,

    /// <summary>Roomed with a trait the agent dislikes.</summary>
    BadRoommate = 5,

    /// <summary>Assigned facility condition is poor.</summary>
    PoorFacility = 6,

    /// <summary>Assigned facility condition is good.</summary>
    GoodFacility = 7,

    /// <summary>Salary is at or above the market rate for the class.</summary>
    PayFair = 8,

    /// <summary>Salary is below the market rate for the class.</summary>
    PayUnfair = 9,

    /// <summary>Currently burnt out.</summary>
    Burnout = 10,
}

/// <summary>Maps <see cref="LoyaltyDriftCondition"/> to the table's condition string.</summary>
public static class LoyaltyDriftConditions
{
    /// <summary>
    /// The <c>loyalty_drift.condition</c> value for a condition.
    /// </summary>
    /// <remarks>
    /// Kept as an explicit map rather than <c>ToString()</c> so renaming the Core enum
    /// member cannot silently orphan a table row — the lookup would return 0 and the
    /// penalty would quietly vanish.
    /// </remarks>
    public static string ToTableKey(this LoyaltyDriftCondition condition) => condition switch
    {
        LoyaltyDriftCondition.Overworked => "Overworked",
        LoyaltyDriftCondition.Working => "Working",
        LoyaltyDriftCondition.Idle => "Idle",
        LoyaltyDriftCondition.MissionLoss => "MissionLoss",
        LoyaltyDriftCondition.MissionSuccess => "MissionSuccess",
        LoyaltyDriftCondition.BadRoommate => "BadRoommate",
        LoyaltyDriftCondition.PoorFacility => "PoorFacility",
        LoyaltyDriftCondition.GoodFacility => "GoodFacility",
        LoyaltyDriftCondition.PayFair => "PayFair",
        LoyaltyDriftCondition.PayUnfair => "PayUnfair",
        LoyaltyDriftCondition.Burnout => "Burnout",
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown drift condition."),
    };
}