namespace ProjectSpy.Core;

/// <summary>
/// The six per-tick phases.
/// </summary>
/// <remarks>
/// <para>
/// The execution order is fixed and is part of the save contract:
/// </para>
/// <code>
/// RoomConstruction → Training → Recovery → MissionProgress → EventChecks → StatusDecay
/// </code>
/// <para>
/// It is declared once in <see cref="TickPipeline.CanonicalOrder"/> and asserted by
/// <c>TickOrderTests</c>. Reordering these phases changes every outcome for an existing
/// save and silently invalidates every recorded replay, which is why the pipeline walks
/// the enum rather than its registration list.
/// </para>
/// <para>
/// The order is chosen so that:
/// </para>
/// <list type="bullet">
/// <item>a room completing construction this tick is usable this tick, rather than
/// costing the agency a tick of use per room;</item>
/// <item>training is charged <em>before</em> recovery, so the stamina cost of a hard
/// session is felt immediately instead of being cancelled out by the same tick's rest;</item>
/// <item>weekly settlement lands in <see cref="EventChecks"/>, where the calendar
/// belongs, so affordability is known before the week's consequences are decided;</item>
/// <item>morale is judged last, on the tick's finished state — an agent who trained into
/// exhaustion this tick is evaluated as overworked, not as they were at midnight.</item>
/// </list>
/// </remarks>
public static class Phases
{
    /// <summary>Builds a pipeline with all six phases registered.</summary>
    public static TickPipeline CreateDefault(IEventSink events)
    {
        if (events is null) throw new ArgumentNullException(nameof(events));

        var pipeline = new TickPipeline(events);
        pipeline.Register(new RoomConstructionPhase());
        pipeline.Register(new TrainingPhase());
        pipeline.Register(new RecoveryPhase());
        pipeline.Register(new MissionProgressPhase());
        pipeline.Register(new EventChecksPhase());
        pipeline.Register(new StatusDecayPhase());
        return pipeline;
    }

    /// <summary>Advances construction timers and completes finished rooms.</summary>
    public sealed class RoomConstructionPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.RoomConstruction;

        public void Run(WorldState world, PhaseContext context)
        {
            foreach (Room room in world.BaseLayout.Rooms.ToArray())
            {
                if (!room.IsUnderConstruction)
                    continue;

                room.ConstructionTicksRemaining--;

                if (room.ConstructionTicksRemaining <= 0)
                {
                    room.ConstructionTicksRemaining = 0;
                    context.Events.Publish(new RoomConstructionCompleted(context.Tick, room.Id, room.TypeId));
                }
            }
        }
    }

    /// <summary>Applies training gains and the stamina they cost.</summary>
    public sealed class TrainingPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.Training;

        public void Run(WorldState world, PhaseContext context)
            => TrainingSystem.Tick(world, context.Events);
    }

    /// <summary>Restores stamina, heals injuries and manages burnout.</summary>
    public sealed class RecoveryPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.Recovery;

        public void Run(WorldState world, PhaseContext context)
            => RecoverySystem.Tick(world, context.Events);
    }

    /// <summary>
    /// Advances in-flight missions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stage 3 owns only what can be decided without a mission map: advancing the clock
    /// on each mission, and stamping the mole's accumulated leak difficulty onto the
    /// mission at dispatch so a later replay cannot retroactively change it. Node
    /// resolution, alarm and fog of war belong to stage 4's <c>MissionRunner</c>, and
    /// this phase deliberately does not try to anticipate them.
    /// </para>
    /// <para>
    /// A mission that overruns its estimate is left running rather than force-completed.
    /// Guessing at an outcome here would bake stage 3's assumptions into every replay
    /// recorded after it.
    /// </para>
    /// </remarks>
    public sealed class MissionProgressPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.MissionProgress;

        public void Run(WorldState world, PhaseContext context)
        {
            if (world.ActiveMissions.Count == 0)
                return;

            foreach (MissionState mission in world.ActiveMissions.Values.OrderBy(m => m.Id).ToArray())
            {
                if (mission.Outcome != MissionOutcome.Unresolved)
                    continue;

                mission.Set("ticks_elapsed", mission.Get("ticks_elapsed") + 1);
            }
        }
    }

/// <summary>
/// Rolls the calendar: weekly settlement, weekly mole heat, contract refreshes,
/// investigations and heat-driven raids.
/// </summary>
/// <remarks>
/// <para>
/// Everything periodic lives here. The weekly block is keyed off the
/// <b>settlement day</b>, not off the week boundary, and that distinction is the whole
/// point: <c>economy_rule.settlement_day_of_week</c> is a day <em>within</em> the week
/// (day 6 by default), so gating the weekly work on the week boundary would mean the
/// settlement tick never arrives and the agency is never billed for anything. The
/// pipeline has a test for exactly that.
/// </para>
/// <para>
/// The block is further guarded by
/// <see cref="WorldState.LastSettledWeek"/>, so advancing a large batch of ticks at
/// once still settles each week exactly once rather than once per tick spent on the
/// settlement day.
/// </para>
/// </remarks>
public sealed class EventChecksPhase : ITickPhase
    {
        /// <summary>
        /// How many investigation steps before a case is called. Long enough that
        /// accusing on a hunch is usually wrong.
        /// </summary>
        /// <remarks>
        /// Backed by <c>counter_intel_rule.investigation_max_steps</c> rather than a
        /// literal, because this window and the exposure threshold have to stay in a
        /// ratio that makes a conclusive case possible at all — a pair of numbers that
        /// only make sense together does not belong in two places.
        /// </remarks>
        private static int MaxInvestigationSteps => MoleSystem.MaxSteps;

        public TickPhase Phase => TickPhase.EventChecks;

        public void Run(WorldState world, PhaseContext context)
        {
            RecruitmentSystem.RefreshPoolIfDue(world, context.Events);
            MoleSystem.TickInvestigations(world, context.Events);

            // Sleeper work is per-tick rather than weekly, so it sits above the weekly
            // gate: an operation has to accrue on the hour, not on the settlement day.
            SleeperSystem.Tick(world, context.Tick, context.Events);

            // The weekly block fires on the settlement day itself, and only once per
            // week. Keying it to the week boundary instead would never reach the
            // settlement day at all.
            bool weeklyDue = context.Tick.DayOfWeek == EconomySystem.SettlementDayOfWeek
                             && world.LastSettledWeek != context.Tick.Week;

            if (!weeklyDue)
                return;

            // Settlement first: the agency has to learn what it can afford before the
            // week's consequences are decided.
            EconomySystem.Settle(world, context.Tick, context.Events);
            MoleSystem.ApplyWeeklyMoleHeat(world, context.Events);
            RefreshContracts(world, context);
            MoleSystem.ExpireStaleInvestigations(world, MaxInvestigationSteps, context.Events);
            CheckRaid(world, context);
        }

        /// <summary>
        /// Clears contracts that were dispatched or have expired, then announces the
        /// refreshed board.
        /// </summary>
        /// <remarks>
        /// TODO(stage-4): offer generation, reward scaling and tier gating arrive with
        /// missions. Stage 3 owns only the weekly window and the event that tells the
        /// player the board changed.
        /// </remarks>
        private static void RefreshContracts(WorldState world, PhaseContext context)
        {
            var stale = new List<int>();
            foreach (ContractState contract in world.Contracts.Values)
            {
                if (contract.Dispatched || context.Tick >= contract.ExpiresOnTick)
                    stale.Add(contract.Id);
            }

            foreach (int id in stale)
                world.Contracts.Remove(id);

            context.Events.Publish(new ContractOfferRefreshed(context.Tick, world.Contracts.Count));
        }

        /// <summary>Rolls for a raid based on the agency's current Heat tier.</summary>
        /// <remarks>
        /// TODO(stage-4): resolve the raid with base combat and <c>RaidMitigation</c>.
        /// Until then no damage is applied — a raid that announced itself and then did
        /// nothing would be worse than not rolling it at all, because the player would
        /// reasonably expect to have lost something.
        /// </remarks>
        private static void CheckRaid(WorldState world, PhaseContext context)
        {
            int chance = RaidChance(world.Resources.Heat);
            if (chance <= 0)
                return;

            IRng rng = world.RngStreams[RngStreams.StreamKind.Event];
            if (rng.NextInt(1, 101) > chance)
                return;

            context.Events.Publish(new FlagChanged(context.Tick, RaidFlagName, true));
        }

        private const string RaidFlagName = "raid.pending";
    }

    /// <summary>Applies the auto-rest director and the daily loyalty pass.</summary>
    public sealed class StatusDecayPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.StatusDecay;

        public void Run(WorldState world, PhaseContext context)
        {
            BaseDirector.Run(world, context.Events);

            if (!context.IsDayStart)
                return;

            LoyaltySystem.DailyPass(world, context.Events);
        }
    }

    /// <summary>Weekly raid chance for a Heat level, from the heat tier table.</summary>
    public static int RaidChance(int heat)
    {
        int chance = 0;

        foreach (ProjectSpy.Tables.HeatTier tier in SimulationRules.HeatTiers())
        {
            if (heat < tier.Threshold)
                break;

            chance = tier.RaidChancePerWeek;
        }

        return chance;
    }
}