using ProjectSpy.Core;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Economy: weekly settlement, loans, and bankruptcy that is always survivable until
/// it genuinely is not.
/// </summary>
public class EconomySystemTests
{
    private const ulong Seed = 777UL;

    /// <summary>
    /// The tick of the settlement day in week zero, used when calling
    /// <see cref="EconomySystem.Settle"/> directly.
    /// </summary>
    private static Tick FirstSettlementDay => Tick.FromDayHour(EconomySystem.SettlementDayOfWeek, 0);

    // ---- settlement ----------------------------------------------------------

    [Fact]
    public void Settlement_ChargesSalariesAndUpkeepOnTheSettlementDay()
    {
        // Driven through the pipeline rather than by calling Settle directly: when
        // settlement happens is the pipeline's decision, and that wiring is worth
        // testing in its own right.
        GameSession session = World.Agency(Seed);

        // Subscribed rather than reading World.Events: tick phases publish through the
        // session's pending buffer, which is the only place a subscriber can see them.
        var settlements = new List<WeekSettled>();
        using IDisposable _ = session.Subscribe(e => { if (e is WeekSettled w) settlements.Add(w); });

        long fundsBefore = session.World.Resources.Funds;

        while (session.World.LastSettledWeek < 0)
            session.AdvanceTick();

        Assert.Equal(0, session.World.LastSettledWeek);
        Assert.True(session.World.Resources.Funds < fundsBefore);

        var settled = Assert.Single(settlements);
        Assert.True(settled.SalariesPaid > 0, "the roster costs salaries");
        Assert.True(settled.UpkeepPaid > 0, "rooms cost upkeep");
    }

    [Fact]
    public void Settlement_DoesNotRunOnOtherDays()
    {
        GameSession session = World.Agency(Seed);

        int otherDay = (EconomySystem.SettlementDayOfWeek + 1) % 7;

        Assert.Null(EconomySystem.Settle(session.World, Tick.FromDayHour(otherDay, 0), null));
        Assert.Equal(-1, session.World.LastSettledWeek);
    }

    [Fact]
    public void Settlement_RunsExactlyOncePerWeek_EvenAcrossBatchedTicks()
    {
        GameSession session = World.Agency(Seed);

        var settlements = new List<WeekSettled>();
        using IDisposable _ = session.Subscribe(e => { if (e is WeekSettled w) settlements.Add(w); });

        // Two whole weeks advanced in one batched call, so both week boundaries are
        // crossed inside AdvanceTicks rather than between calls. A settlement keyed only
        // to "is it the settlement day", with no week guard, would fire on every tick
        // spent on that day.
        session.AdvanceTicks(Tick.TicksPerWeek * 2);

        Assert.Equal(2, settlements.Count);

        // Two weeks elapsed, but only two of the three weeks contained a settlement
        // day within the ticks actually visited, so the marker names the last week that
        // settled — not the current week, which is still mid-flight.
        int weekSettled = session.World.LastSettledWeek;
        Assert.True(weekSettled < session.CurrentTick.Week,
            "the current week has not reached its settlement day yet");

        // Asking for another settlement in the current week is a no-op however it is
        // called, and must not move the marker.
        Assert.Null(EconomySystem.Settle(session.World, session.CurrentTick, null));
        Assert.Equal(weekSettled, session.World.LastSettledWeek);
    }

    [Fact]
    public void Settlement_UpkeepScalesWithRoomsBuilt()
    {
        GameSession session = World.Session(Seed);
        long before = EconomySystem.WeeklyUpkeep(session.World);

        World.Place(session, World.Rooms.Dorm, 0, 0, 3);

        Assert.True(EconomySystem.WeeklyUpkeep(session.World) > before);
    }

    [Fact]
    public void Settlement_ARoomStillUnderConstructionIsNotYetCharged()
    {
        GameSession session = World.Session(Seed);
        Room room = World.Place(session, World.Rooms.Dorm, 0, 0, 3);

        long after = EconomySystem.WeeklyUpkeep(session.World);
        room.ConstructionTicksRemaining = 10;

        Assert.True(EconomySystem.WeeklyUpkeep(session.World) < after);
    }

    [Fact]
    public void Settlement_PublishesTheRealTotals()
    {
        GameSession session = World.Agency(Seed);
        var sink = new RecordingSink();

        // Called directly against a week that has not settled yet, so this asserts on
        // what Settle returns rather than on pipeline timing.
        SettlementResult? result = EconomySystem.Settle(session.World, FirstSettlementDay, sink);

        Assert.NotNull(result);
        Assert.True(sink.Saw(GameEventKind.WeekSettled));

        EconomyState economy = session.World.Economy;
        Assert.Equal(
            result!.Value.Total,
            economy.TotalSalariesPaid + economy.TotalUpkeepPaid + economy.TotalInterestPaid);
    }

    // ---- loans ---------------------------------------------------------------

    [Fact]
    public void Loan_AddsOutstandingPrincipalAndAccruedInterest()
    {
        GameSession session = World.Agency(Seed);
        LoanTier tier = SimulationRules.LoanTiers().First();

        LoanState? loan = EconomySystem.TakeLoan(session.World, tier.Id, null);

        Assert.NotNull(loan);
        Assert.Equal(tier.Amount, loan!.Outstanding);
        Assert.Equal(
            SimulationRules.PercentOf(tier.Amount, tier.WeeklyInterestPercent),
            loan.AccruedInterest);
        Assert.Equal(1, session.World.Economy.ActiveLoanCount);
    }

    [Fact]
    public void Loan_InterestCompoundsOntoThePrincipal()
    {
        GameSession session = World.Agency(Seed);
        LoanTier tier = SimulationRules.LoanTiers().First();
        LoanState? loan = EconomySystem.TakeLoan(session.World, tier.Id, null);

        long firstOutstanding = loan!.Outstanding;
        EconomySystem.SettleInterest(session.World);

        Assert.True(
            loan.Outstanding > firstOutstanding,
            $"compounding interest should grow the balance: {firstOutstanding} -> {loan.Outstanding}");
    }

    [Fact]
    public void Loan_RepaymentReducesThePrincipalAndTheFundBalance()
    {
        GameSession session = World.Agency(Seed);
        LoanTier tier = SimulationRules.LoanTiers().First();
        EconomySystem.TakeLoan(session.World, tier.Id, null);

        long fundsBefore = session.World.Resources.Funds;
        long applied = EconomySystem.RepayLoan(session.World, 1000);

        Assert.Equal(1000, applied);
        Assert.Equal(fundsBefore - 1000, session.World.Resources.Funds);
    }

    [Fact]
    public void Loan_RepaymentNeverExceedsWhatIsOwed()
    {
        GameSession session = World.Agency(Seed);
        LoanTier tier = SimulationRules.LoanTiers().First();
        EconomySystem.TakeLoan(session.World, tier.Id, null);

        long applied = EconomySystem.RepayLoan(session.World, tier.Amount * 10);

        Assert.Equal(tier.Amount, applied);
        Assert.Equal(0, session.World.Economy.ActiveLoanCount);
    }

    [Fact]
    public void TakeLoanCommand_RespectsReputationAndLoanLimits()
    {
        GameSession session = World.Agency(Seed);

        LoanTier gated = SimulationRules.LoanTiers()
            .OrderByDescending(t => t.ReputationRequired)
            .First();

        Assert.True(session.World.Resources.Reputation < gated.ReputationRequired);

        CommandResult tooSoon = session.Execute(new TakeLoanCommand(gated.Id));
        Assert.True(tooSoon.IsRejected, "a high-reputation tier is locked at low reputation");
        Assert.Equal(CommandReason.InsufficientReputation, tooSoon.Reason);

        foreach (LoanTier tier in SimulationRules.LoanTiers())
        {
            if (tier.ReputationRequired <= session.World.Resources.Reputation)
                Assert.True(session.Execute(new TakeLoanCommand(tier.Id)).IsOk);
        }

        Assert.True(session.World.Economy.ActiveLoanCount > 0);

        // The tier with no reputation gate has now been taken to its max_active.
        LoanTier open = SimulationRules.LoanTiers().First(t => t.ReputationRequired == 0);
        CommandResult overLimit = session.Execute(new TakeLoanCommand(open.Id));
        Assert.True(overLimit.IsRejected);
        Assert.Equal(CommandReason.LoanLimitReached, overLimit.Reason);
    }

    [Fact]
    public void TakeLoanCommand_UnknownTierIsRejectedWithAReason()
    {
        GameSession session = World.Agency(Seed);

        CommandResult result = session.Execute(new TakeLoanCommand(999_999));

        Assert.True(result.IsRejected);
        Assert.Equal(CommandReason.UnknownLoanTier, result.Reason);
    }

    [Fact]
    public void Loan_WithNoOutstandingRejectsTheRepay()
    {
        GameSession session = World.Agency(Seed);

        CommandResult result = session.Execute(new RepayLoanCommand(500));

        Assert.True(result.IsRejected);
        Assert.Equal(CommandReason.NoOutstandingLoan, result.Reason);
    }

    // ---- bankruptcy ----------------------------------------------------------

    [Fact]
    public void Bankruptcy_NeverTriggersInstantlyOnASingleUnaffordableSettlement()
    {
        // The single most important economy guarantee: an agency that cannot pay one
        // week is in trouble, not finished.
        GameSession session = World.Agency(Seed, funds: 1);

        EconomySystem.Settle(session.World, FirstSettlementDay, null);

        Assert.Equal(1, session.World.Economy.DaysInDeficit);
        Assert.Equal(EconomyStatus.InGrace, session.World.Economy.Status(session.World));
        Assert.False(session.World.GetFlag("economy.game_over"));
    }

    [Fact]
    public void Bankruptcy_ResourcesNeverGoNegative()
    {
        // Several consecutive unsettled weeks against an agency that cannot pay any.
        GameSession session = World.Agency(Seed, funds: 10);

        for (int week = 0; week < 5; week++)
        {
            int day = (week * 7) + EconomySystem.SettlementDayOfWeek;
            EconomySystem.Settle(session.World, Tick.FromDayHour(day, 0), null);
        }

        Assert.True(
            session.World.Resources.IsValid,
            $"funds={session.World.Resources.Funds} must never go negative");
        Assert.Equal(5, session.World.Economy.DaysInDeficit);
    }

    [Fact]
    public void Bankruptcy_GracePeriodRunsBeforeAnyForcedResignations()
    {
        int resignDay = SimulationRules.Economy("bankruptcy_stage_resign_day", 3);

        Assert.True(
            resignDay >= 2,
            "the agency must get at least a couple of days of warning before shedding staff");

        Assert.True(
            Bankruptcy.PenaltyStageForDay(1) < Bankruptcy.PenaltyStageForDay(resignDay),
            "an early deficit is a lighter penalty than a prolonged one");
    }

    [Fact]
    public void Bankruptcy_EscalatesThroughEveryStageAsTheDeficitGrows()
    {
        int maxStage = SimulationRules.Economy("bankruptcy_max_stage", 4);

        int previous = 0;
        for (int day = 1; day <= Bankruptcy.LastSurvivableDay; day++)
        {
            int stage = Bankruptcy.PenaltyStageForDay(day);

            Assert.True(
                stage >= previous,
                $"the ladder went backwards on day {day}: {stage} < {previous}");

            previous = stage;
        }

        Assert.True(
            Bankruptcy.PenaltyStageForDay(Bankruptcy.LastSurvivableDay) == maxStage,
            $"the final rung must be reachable; expected stage {maxStage}, got " +
            $"{Bankruptcy.PenaltyStageForDay(Bankruptcy.LastSurvivableDay)}");
    }

    [Fact]
    public void Bankruptcy_ForcedResignationsHappenOnlyPastTheStageThreshold()
    {
        GameSession session = World.Agency(Seed, funds: 1);

        session.World.Economy.DaysInDeficit = 1;
        session.World.Economy.SyncPenaltyStage();

        Assert.Equal(EconomyStatus.InGrace, session.World.Economy.Status(session.World));
        Assert.Equal(1, Bankruptcy.PenaltyStageForDay(1));
    }

    [Fact]
    public void Bankruptcy_GameOverOnlyAfterGraceAndEveryStage()
    {
        Assert.False(Bankruptcy.IsGameOver(0));
        Assert.False(Bankruptcy.IsGameOver(1));
        Assert.False(Bankruptcy.IsGameOver(Bankruptcy.LastSurvivableDay));
        Assert.True(Bankruptcy.IsGameOver(Bankruptcy.LastSurvivableDay + 1));
    }

    [Fact]
    public void Bankruptcy_GameOverFlagIsSetOnlyWhenTheRunIsActuallyLost()
    {
        GameSession session = World.Agency(Seed, funds: 1);

        for (int week = 0; week < Bankruptcy.LastSurvivableDay; week++)
        {
            int day = (week * 7) + EconomySystem.SettlementDayOfWeek;
            EconomySystem.Settle(session.World, Tick.FromDayHour(day, 0), null);
            Assert.False(session.World.GetFlag("economy.game_over"), $"flag set too early, week {week}");
        }

        int finalDay = ((Bankruptcy.LastSurvivableDay + 1) * 7) + EconomySystem.SettlementDayOfWeek;
        EconomySystem.Settle(session.World, Tick.FromDayHour(finalDay, 0), null);

        Assert.True(session.World.GetFlag("economy.game_over"));
    }

    [Fact]
    public void Bankruptcy_RecoveryIsPossibleBeforeTheEnd()
    {
        // "Never instantly lose" has a converse worth asserting: the player can climb
        // back out. If nothing could recover, the grace period would be theatre.
        GameSession session = World.Agency(Seed, funds: 1);

        session.World.Economy.DaysInDeficit = 4;
        session.World.Economy.SyncPenaltyStage();

        int stageWhileBehind = session.World.Economy.PenaltyStage;
        Assert.True(stageWhileBehind > 0, "a four-day deficit should have escalated");

        // Funds recover, and the next settled week clears the counter.
        session.World.Resources = session.World.Resources.EarnFunds(100_000);

        int nextDay = (7 * 2) + EconomySystem.SettlementDayOfWeek;
        EconomySystem.Settle(session.World, Tick.FromDayHour(nextDay, 0), null);

        Assert.Equal(0, session.World.Economy.DaysInDeficit);
        Assert.Equal(EconomyStatus.Solvent, session.World.Economy.Status(session.World));
    }

    [Fact]
    public void Bankruptcy_SettlementOverrunIsRecordedAsShortfall_NotAsNegativeFunds()
    {
        GameSession session = World.Agency(Seed, funds: 100);

        SettlementResult? result = EconomySystem.Settle(session.World, FirstSettlementDay, null);

        Assert.NotNull(result);
        Assert.True(result!.Value.Shortfall > 0, "the unpaid portion is recorded as a shortfall");
        Assert.True(
            session.World.Resources.Funds == 0,
            "the balance clamps at zero and the debt is tracked by the counter");
    }

    [Fact]
    public void Bankruptcy_StatusIsDerivedNotStored()
    {
        GameSession session = World.Agency(Seed);

        session.World.Economy.DaysInDeficit = 0;
        Assert.Equal(EconomyStatus.Solvent, session.World.Economy.Status(session.World));

        session.World.Economy.DaysInDeficit = Bankruptcy.GraceDays - 1;
        Assert.Equal(EconomyStatus.InGrace, session.World.Economy.Status(session.World));

        session.World.Economy.DaysInDeficit = Bankruptcy.GraceDays;
        Assert.Equal(EconomyStatus.Defaulting, session.World.Economy.Status(session.World));

        session.World.Economy.DaysInDeficit = Bankruptcy.LastSurvivableDay + 1;
        Assert.Equal(EconomyStatus.Collapsed, session.World.Economy.Status(session.World));
    }
}