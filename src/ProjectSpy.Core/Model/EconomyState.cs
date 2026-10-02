namespace ProjectSpy.Core;

/// <summary>How the agency is doing financially.</summary>
public enum EconomyStatus
{
    /// <summary>Funds are positive and settlement is not a concern.</summary>
    Solvent = 0,

    /// <summary>Funds are below zero and the grace period is running.</summary>
    InGrace = 1,

    /// <summary>Grace period spent; penalties are escalating toward forced resignations.</summary>
    Defaulting = 2,

    /// <summary>Every escalation stage spent. The run is over.</summary>
    Collapsed = 3,
}

/// <summary>
/// The bankruptcy escalation ladder, derived from <c>economy_rule</c>.
/// </summary>
/// <remarks>
/// <para>
/// Kept as a separate static class rather than members on <see cref="EconomyStatus"/>
/// so the enum stays a pure vocabulary of states — it is written into saves, and an
/// enum with behaviour in it invites someone to add a member and renumber it.
/// </para>
/// <para>
/// The ladder is deliberately survivable. Stage 1 is merely "in deficit" and costs a
/// little loyalty; forced resignations only begin once the day threshold passes; the
/// run is lost only when every stage and the full grace period have elapsed. A player
/// who is behind can always cut upkeep or let someone go, which is the decision the
/// system is trying to provoke.
/// </para>
/// </remarks>
public static class Bankruptcy
{
    /// <summary>
    /// How far through the escalation ladder a deficit has climbed. Derived from the
    /// day count rather than stored, so the stage and the day count can never disagree.
    /// </summary>
    public static int PenaltyStageForDay(int daysInDeficit)
    {
        if (daysInDeficit <= 0)
            return 0;

        // Stage 1 is simply "in deficit". Every later stage has a day threshold, and
        // once a threshold has passed it stays passed — so skipping a day never skips a
        // penalty, and the ladder cannot be gamed by advancing time in large jumps.
        int stage = 1;

        if (daysInDeficit >= SimulationRules.Economy("bankruptcy_stage_resign_day", 3))
            stage = 2;

        if (daysInDeficit >= SimulationRules.Economy("bankruptcy_stage_three_day", 6))
            stage = 3;

        if (daysInDeficit >= SimulationRules.Economy("bankruptcy_stage_four_day", 10))
            stage = 4;

        int maxStage = SimulationRules.Economy("bankruptcy_max_stage", 4);
        return Math.Min(stage, maxStage);
    }

    /// <summary>
    /// True when the deficit has run past every stage, i.e. the run is lost.
    /// </summary>
    /// <remarks>
    /// This is the only path to <see cref="EconomyStatus.Collapsed"/>, and it is
    /// reached only after the grace period plus the stage thresholds have all elapsed.
    /// </remarks>
    public static bool IsGameOver(int daysInDeficit)
    {
        // Anchored on the last rung of the ladder rather than on a separate total, so
        // the day the run ends and the day the final penalty applies can never drift
        // apart if a designer edits one threshold.
        int finalStageDay = SimulationRules.Economy("bankruptcy_stage_four_day", 10);
        return daysInDeficit > finalStageDay;
    }

    /// <summary>Days the agency may run a deficit before the run is lost.</summary>
    public static int GraceDays => SimulationRules.Economy("bankruptcy_grace_days", 7);

    /// <summary>
    /// The total number of days the agency survives a deficit, starting from the day it
    /// first fails to settle.
    /// </summary>
    public static int LastSurvivableDay
        => SimulationRules.Economy("bankruptcy_stage_four_day", 10);
}

/// <summary>One outstanding loan.</summary>
public sealed class LoanState
{
    /// <summary>Which <c>loan_tier</c> row this came from.</summary>
    public int TierId { get; init; }

    /// <summary>Principal originally borrowed.</summary>
    public long Principal { get; set; }

    /// <summary>Outstanding principal. Interest compounds onto this when the table says so.</summary>
    public long Outstanding { get; set; }

    /// <summary>Weekly interest rate as a percentage of the outstanding principal.</summary>
    public int WeeklyInterestPercent { get; init; }

    /// <summary>Tick the loan was taken.</summary>
    public Tick TakenOnTick { get; init; }

    /// <summary>
    /// Weekly interest due on the next settlement. Computed when the loan is taken
    /// and after each settlement, so the amount charged is always the amount shown.
    /// </summary>
    public long AccruedInterest { get; set; }

    /// <summary>Ticks of missed settlement. Drives the default penalty.</summary>
    public int MissedSettlements { get; set; }
}

/// <summary>
/// The organisation's financial condition: grace period, escalation stage, and loans.
/// </summary>
/// <remarks>
/// <para>
/// Lives on <see cref="WorldState"/> rather than in a system object because it is
/// serializable state that must survive a save/load and must hash into the state
/// fingerprint. A rule path that kept the grace countdown in a static field would be
/// invisible to the replay verifier.
/// </para>
/// <para>
/// Bankruptcy never triggers instantly. Funds below zero starts a grace period whose
/// length and penalties come from <c>economy_rule</c>; only when every escalation stage
/// is spent does the run end. That is a deliberate design choice: an unrecoverable
/// instant loss teaches the player nothing and reads as a bug.
/// </para>
/// </remarks>
public sealed class EconomyState
{
    /// <summary>Creates the starting condition for a new agency.</summary>
    public EconomyState()
    {
        Loans = new List<LoanState>();
    }

    /// <summary>Outstanding loans.</summary>
    public List<LoanState> Loans { get; }

    /// <summary>
    /// Consecutive days the agency has been unable to settle. Zero while solvent.
    /// </summary>
    public int DaysInDeficit { get; set; }

    /// <summary>
    /// How far through the escalation ladder the agency is. Only meaningful while
    /// <see cref="DaysInDeficit"/> is non-zero.
    /// </summary>
    public int PenaltyStage { get; set; }

    /// <summary>Total interest paid across the run, for the after-action report.</summary>
    public long TotalInterestPaid { get; set; }

    /// <summary>Total salaries paid across the run.</summary>
    public long TotalSalariesPaid { get; set; }

    /// <summary>Total room upkeep paid across the run.</summary>
    public long TotalUpkeepPaid { get; set; }

    /// <summary>Outstanding principal across all loans.</summary>
    public long TotalOutstanding
    {
        get
        {
            long total = 0;
            foreach (LoanState loan in Loans)
                total += loan.Outstanding;

            return total;
        }
    }

    /// <summary>Sum of interest due on the next settlement.</summary>
    public long PendingInterest
    {
        get
        {
            long total = 0;
            foreach (LoanState loan in Loans)
                total += loan.AccruedInterest;

            return total;
        }
    }

    /// <summary>Number of currently active loans.</summary>
    public int ActiveLoanCount => Loans.Count;

    /// <summary>
    /// The current financial condition, derived rather than stored so the status can
    /// never disagree with the numbers it summarises.
    /// </summary>
    public EconomyStatus Status(WorldState world)
    {
        if (DaysInDeficit <= 0 && world.Resources.Funds >= 0)
            return EconomyStatus.Solvent;

        if (Bankruptcy.IsGameOver(DaysInDeficit))
            return EconomyStatus.Collapsed;

        return DaysInDeficit < Bankruptcy.GraceDays
            ? EconomyStatus.InGrace
            : EconomyStatus.Defaulting;
    }

    /// <summary>
    /// Recomputes <see cref="PenaltyStage"/> from the day count. Called after the
    /// deficit counter moves so the stored stage always matches the derived one.
    /// </summary>
    public void SyncPenaltyStage() => PenaltyStage = Bankruptcy.PenaltyStageForDay(DaysInDeficit);
}