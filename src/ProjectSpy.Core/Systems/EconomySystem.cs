using ProjectSpy.Tables;

namespace ProjectSpy.Core;

/// <summary>
/// Weekly settlement, loans, and the bankruptcy ladder.
/// </summary>
/// <remarks>
/// <para>
/// Settlement runs once per week on the day named by
/// <c>economy_rule.settlement_day_of_week</c> and deducts salaries, room upkeep and
/// loan interest in that order. All three totals travel on the
/// <see cref="WeekSettled"/> event so the top bar can animate the change without
/// re-deriving it.
/// </para>
/// <para>
/// <b>The central rule: funds may go negative on the books but the player never loses
/// instantly.</b> A negative balance starts a countdown
/// (<c>economy_rule.bankruptcy_grace_days</c>). During the grace period the agency
/// loses a little loyalty each day. Past the stage threshold, the agency starts
/// forcibly shedding staff. Only when every stage and the full grace period have
/// elapsed does <see cref="EconomyStatus.Collapsed"/> happen. Everything is
/// recoverable in between, and <see cref="Resources"/> is still clamped at zero so no
/// downstream system ever sees a negative balance.
/// </para>
/// <para>
/// The deficit counter and the penalty stage are derived from each other, never stored
/// independently, so the status shown can never disagree with the numbers behind it.
/// </para>
/// </remarks>
public static class EconomySystem
{
    /// <summary>The day of the week settlement falls on, from <c>economy_rule</c>.</summary>
    public static int SettlementDayOfWeek => SimulationRules.Economy("settlement_day_of_week", 6);

    /// <summary>
    /// Runs weekly settlement if the given tick is the settlement day and the week has
    /// not already been settled.
    /// </summary>
    /// <returns>The settlement result, or null when settlement did not run.</returns>
    public static SettlementResult? Settle(WorldState world, Tick tick, IEventSink? events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        if (tick.DayOfWeek != SettlementDayOfWeek)
            return null;

        // Guard on the week, not just the day. Without this, a caller advancing a large
        // batch of ticks in one call could settle the same week twice.
        if (world.LastSettledWeek == tick.Week)
            return null;

        world.LastSettledWeek = tick.Week;

        long salaries = world.WeeklySalaryCost();
        long upkeep = WeeklyUpkeep(world);
        long interest = SettleInterest(world);

        long total = salaries + upkeep + interest;
        world.Economy.TotalSalariesPaid += salaries;
        world.Economy.TotalUpkeepPaid += upkeep;
        world.Economy.TotalInterestPaid += interest;

        // Resources clamps at zero rather than going negative, so the deficit is
        // tracked by the counter rather than by the balance.
        long affordable = Math.Min(total, Math.Max(0, world.Resources.Funds));
        world.Resources = world.Resources.TrySpend(affordable, out Resources paid) ? paid : world.Resources;
        long shortfall = total - affordable;

        events?.Publish(new WeekSettled(tick, tick.Week, salaries, upkeep, interest));

        var result = new SettlementResult(tick.Week, salaries, upkeep, interest, shortfall);

        if (shortfall > 0)
        {
            ApplyDeficit(world, events, result);
        }
        else
        {
            // A week that is paid in full clears the countdown. Without this the agency
            // would stay on the ladder forever even after recovering, and the "never
            // instantly lose" promise would come with no way back.
            world.Economy.DaysInDeficit = 0;
            world.Economy.SyncPenaltyStage();
        }

        return result;
    }

    /// <summary>
    /// Charges interest on every loan and rolls accrued interest into the principal
    /// when the table says it compounds.
    /// </summary>
    public static long SettleInterest(WorldState world)
    {
        long charged = 0;
        bool compounds = SimulationRules.Economy("loan_interest_compounds", 1) != 0;

        foreach (LoanState loan in world.Economy.Loans)
        {
            long interest = SimulationRules.PercentOf(loan.Outstanding, loan.WeeklyInterestPercent);
            loan.AccruedInterest = interest;
            charged += interest;

            if (compounds && loan.Outstanding > 0)
            {
                loan.Outstanding += interest;
                loan.MissedSettlements = 0;
            }
        }

        return charged;
    }

    /// <summary>Total weekly upkeep for every room that is standing.</summary>
    public static long WeeklyUpkeep(WorldState world)
    {
        long upkeep = 0;
        int multiplier = SimulationRules.Economy("upkeep_multiplier_percent", 100);

        foreach (Room room in world.BaseLayout.Rooms)
        {
            // A room still being built or demolished is not being maintained.
            if (room.IsUnderConstruction)
                continue;

            long weekly = RoomDefinitions.UpkeepFor(world, room.TypeId);
            weekly += RoomDefinitions.UpgradeUpkeepDeltaFor(room.TypeId, room.Level);
            upkeep += weekly;
        }

        return SimulationRules.PercentOf(upkeep, multiplier);
    }

    /// <summary>
    /// Moves the deficit counter one day forward and applies whatever that stage of the
    /// ladder costs.
    /// </summary>
    private static void ApplyDeficit(WorldState world, IEventSink? events, SettlementResult settlement)
    {
        EconomyState economy = world.Economy;
        int previousStage = economy.PenaltyStage;

        economy.DaysInDeficit++;
        economy.SyncPenaltyStage();

        int stage = economy.PenaltyStage;

        // Stage 1: the grace period proper. A steady loyalty bleed across the whole
        // roster — the agency is short and everyone can tell.
        if (stage >= 1)
        {
            int baseDrain = SimulationRules.Economy("bankruptcy_stage_loyalty_drain", 2);
            int scale = SimulationRules.Economy("bankruptcy_penalty_scale_percent", 150);
            int scaled = SimulationRules.PercentOf(baseDrain, scale);

            foreach (Agent agent in world.Agents.Values.OrderBy(a => a.Id.Value))
            {
                if (agent.Status.IsActive())
                    agent.AdjustLoyalty(-scaled);
            }
        }

        // Stage 2 and beyond: the agency starts letting people go without asking.
        if (stage >= 2)
            ForceResignations(world, events);

        if (previousStage != stage)
        {
            events?.Publish(new BankruptcyStageChanged(
                world.Clock.Current, stage, economy.DaysInDeficit, economy.Status(world)));
        }

        if (Bankruptcy.IsGameOver(economy.DaysInDeficit))
        {
            world.SetFlag("economy.game_over", true);
            events?.Publish(new EconomyCollapsed(world.Clock.Current, economy.DaysInDeficit));
        }

        _ = settlement;
    }

    /// <summary>
    /// Fires the most unhappy, lowest-loyalty staff first. Resigning someone who is
    /// already devoted would be both cruel and unintuitive; the people leaving are the
    /// ones who were closest to walking out anyway.
    /// </summary>
    private static void ForceResignations(WorldState world, IEventSink? events)
    {
        int chance = SimulationRules.Economy("bankruptcy_forced_resign_chance", 25);
        int count = SimulationRules.Economy("bankruptcy_forced_resign_count", 1);
        int floor = SimulationRules.Economy("bankruptcy_forced_resign_loyalty_floor", 20);

        if (count <= 0 || chance <= 0)
            return;

        IRng rng = world.RngStreams[RngStreams.StreamKind.World];
        if (rng.NextInt(1, 101) > chance)
            return;

        for (int i = 0; i < count; i++)
        {
            Agent? victim = SelectForResignation(world);
            if (victim is null)
                return;

            victim.Loyalty = Math.Min(victim.Loyalty, floor);
            AgentStatus from = victim.Status;
            victim.Status = AgentStatus.Retired;
            victim.AssignedRoomId = 0;
            victim.Normalize();

            events?.Publish(new AgentResigned(world.Clock.Current, victim.Id, LoyaltyEscalation.ResignationNotice));
            events?.Publish(new AgentStatusChanged(world.Clock.Current, victim.Id, from, AgentStatus.Retired));
        }
    }

    private static Agent? SelectForResignation(WorldState world)
    {
        Agent? worst = null;
        foreach (Agent agent in world.Agents.Values)
        {
            if (!agent.Status.IsActive() || agent.Status == AgentStatus.OnMission || agent.Status == AgentStatus.Captured)
                continue;

            if (worst is null || agent.Loyalty < worst.Loyalty)
                worst = agent;
        }

        return worst;
    }

    /// <summary>
    /// Opens a loan of the given tier.
    /// </summary>
    /// <remarks>
    /// Interest is computed up front and stored on the loan, so the figure the player
    /// is shown and the figure charged at settlement can never disagree.
    /// </remarks>
    public static LoanState? TakeLoan(WorldState world, int tierId, IEventSink? events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        foreach (LoanTier tier in SimulationRules.LoanTiers())
        {
            if (tier.Id != tierId)
                continue;

            if (world.Resources.Reputation < tier.ReputationRequired)
                return null;

            if (world.Economy.ActiveLoanCount >= tier.MaxActive)
                return null;

            var loan = new LoanState
            {
                TierId = tier.Id,
                Principal = tier.Amount,
                Outstanding = tier.Amount,
                WeeklyInterestPercent = tier.WeeklyInterestPercent,
                TakenOnTick = world.Clock.Current,
                AccruedInterest = SimulationRules.PercentOf(tier.Amount, tier.WeeklyInterestPercent),
            };

            world.Economy.Loans.Add(loan);
            events?.Publish(new LoanTaken(world.Clock.Current, tier.Id, tier.Amount, tier.WeeklyInterestPercent));
            return loan;
        }

        return null;
    }

    /// <summary>
    /// Pays down the oldest loan, reducing the outstanding principal.
    /// </summary>
    /// <returns>How much was actually applied, or zero if there was nothing to repay.</returns>
    public static long RepayLoan(WorldState world, long amount)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (amount <= 0 || world.Economy.Loans.Count == 0)
            return 0;

        long applied = 0;
        foreach (LoanState loan in world.Economy.Loans)
        {
            if (applied >= amount) break;
            if (loan.Outstanding <= 0) continue;

            long payment = Math.Min(loan.Outstanding, amount - applied);
            loan.Outstanding -= payment;
            applied += payment;
        }

        if (applied > 0)
            world.Resources = world.Resources.TrySpend(applied, out Resources paid) ? paid : world.Resources;

        // Drop cleared loans so the active-loan count reflects reality.
        world.Economy.Loans.RemoveAll(l => l.Outstanding <= 0);

        return applied;
    }

    /// <summary>The cheapest loan tier the agency currently qualifies for.</summary>
    public static LoanTier? AvailableLoanTier(WorldState world)
    {
        LoanTier? best = null;
        foreach (LoanTier tier in SimulationRules.LoanTiers())
        {
            if (world.Resources.Reputation < tier.ReputationRequired)
                continue;

            if (best is null || tier.Amount < best.Amount)
                best = tier;
        }

        return best;
    }
}

/// <summary>What one weekly settlement charged.</summary>
/// <param name="Week">The week that was settled.</param>
/// <param name="Salaries">Gross salary owed.</param>
/// <param name="Upkeep">Room upkeep owed.</param>
/// <param name="Interest">Loan interest owed.</param>
/// <param name="Shortfall">
/// How much of the total could not be paid. This is what drives the deficit counter —
/// the balance is clamped at zero and the debt is recorded here rather than as a
/// negative number (knowledge.md rule 2).
/// </param>
public readonly record struct SettlementResult(
    int Week,
    long Salaries,
    long Upkeep,
    long Interest,
    long Shortfall)
{
    /// <summary>Total charged.</summary>
    public long Total => Salaries + Upkeep + Interest;
}