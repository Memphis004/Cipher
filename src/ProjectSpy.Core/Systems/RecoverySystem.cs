using ProjectSpy.Tables;

namespace ProjectSpy.Core;

/// <summary>
/// Restores stamina, clears injuries and manages burnout, once per tick.
/// </summary>
/// <remarks>
/// <para>
/// Three things happen here, and the order is deliberate:
/// </para>
/// <list type="number">
/// <item><b>Restore.</b> An agent in a recovery room regains stamina at the room's
/// rate. Mental is deliberately slower than physical — roughly a third, by design —
/// and that ratio is <c>recovery_rule.mental_ratio_percent</c>, a table value rather
/// than a constant, so the gap between resting the body and resting the mind is
/// something a designer can tune.</item>
/// <item><b>Heal.</b> The infirmary clears injury over several ticks. The rate is
/// scaled by severity so a critical wound is never cleared as fast as a graze.</item>
/// <item><b>Burnout.</b> Entered when mental hits zero, exited only after enough
/// dedicated rest. It is recoverable, and deliberately expensive.</item>
/// </list>
/// <para>
/// Burnout is checked before restoration so an agent at zero mental cannot oscillate
/// in and out of the state within a single tick.
/// </para>
/// </remarks>
public static class RecoverySystem
{
    /// <summary>
    /// Burnout is never free. An agent in it loses loyalty every day whether or not they
    /// are resting, but the drain is reduced while they are getting proper rest.
    /// </summary>
    /// <remarks>
    /// Without the resting deduction, recovery would be strictly better than doing
    /// nothing — the player could park a burnt-out agent in a therapy office and walk
    /// away with no further cost. Charging a reduced rate during real rest keeps
    /// "recoverable" true while keeping "expensive" true too.
    /// </remarks>
    private const bool DeductLoyaltyForBurnout = true;
    /// <summary>Runs one recovery tick for every agent on the roster.</summary>
    public static void Tick(WorldState world, IEventSink? events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        foreach (Agent agent in world.Agents.Values.OrderBy(a => a.Id.Value).ToArray())
        {
            if (!agent.Status.IsActive())
                continue;

            // Burnout first: an agent at zero mental must not be topped up by the
            // very room they were sent to in order to escape the state.
            if (agent.IsBurntOut)
            {
                TickBurnout(world, agent, events);
                continue;
            }

            if (agent.Status is AgentStatus.OnMission or AgentStatus.Captured)
                continue;

            Restore(world, agent, events);
            Heal(world, agent, events);
            CheckBurnoutEntry(world, agent, events);
        }
    }

    /// <summary>
    /// Applies a recovery room's restoration to an agent.
    /// </summary>
    /// <returns>How much of each pool was restored, for tests and diagnostics.</returns>
    public static (int Physical, int Mental) Restore(WorldState world, Agent agent, IEventSink? events = null)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
        if (room is null || room.IsUnderConstruction)
            return (0, 0);

        RecoveryRule? rule = SimulationRules.RecoveryFor(room.TypeId);
        if (rule is null)
            return (0, 0);

        // A room that is neither resting nor healing is not a recovery room as far as
        // this pass is concerned, even if the table happens to carry a row for it.
        bool resting = agent.Status == AgentStatus.Resting || agent.Status == AgentStatus.Recovering;
        if (!resting)
            return (0, 0);

        int physical = RestoreOne(world, agent, room, rule, restoresPhysical: true);
        int mental = RestoreOne(world, agent, room, rule, restoresPhysical: false);

        // Unassigned agents idle at the base and creep back very slowly, so nobody is
        // stuck at zero forever, but nobody recovers properly without a room either.
        if (physical == 0 && mental == 0)
            (physical, mental) = IdleRecovery(world, agent, room);

        _ = events;
        return (physical, mental);
    }

    /// <summary>
    /// Restores one stamina pool for a tick, scaled by the room's level.
    /// </summary>
    /// <remarks>
    /// Percentages are of the maximum pool, so a 3% room tick restores 3 points at any
    /// level. Recovery is deliberately not improved by room upgrades: upgrades buy
    /// slots and effect multipliers elsewhere, and a faster full heal would make an
    /// upgrade strictly dominate a second room.
    /// </remarks>
    private static int RestoreOne(
        WorldState world,
        Agent agent,
        Room room,
        RecoveryRule rule,
        bool restoresPhysical)
    {
        int percent = restoresPhysical
            ? rule.RestorePhysicalPercentPerTick
            : rule.RestoreMentalPercentPerTick;

        if (percent <= 0)
            return 0;

        // Facility condition scales what a room can actually deliver: a derelict dorm
        // heals worse than a maintained one.
        int conditionFactor = Math.Max(0, room.Condition);
        int amount = SimulationRules.PercentOf(SimulationRules.PercentOf(Agent.MaxStamina, percent), conditionFactor);

        if (amount <= 0)
            return 0;

        int before = restoresPhysical ? agent.PhysicalStamina : agent.MentalStamina;
        agent.AdjustStamina(restoresPhysical ? amount : 0, restoresPhysical ? 0 : amount);
        int after = restoresPhysical ? agent.PhysicalStamina : agent.MentalStamina;

        return Math.Max(0, after - before);
    }

    /// <summary>
    /// The slow baseline recovery an unassigned agent gets. Derived from the table's
    /// dorm rate so it can never exceed what a real dorm delivers.
    /// </summary>
    private static (int Physical, int Mental) IdleRecovery(WorldState world, Agent agent, Room room)
    {
        RecoveryRule? dorm = SimulationRules.RecoveryFor(FindRoomOfCategory(RoomCategory.Recover));
        if (dorm is null || dorm.RestorePhysicalPercentPerTick <= 0)
            return (0, 0);

        // Idle recovery is a fraction of a full room's rate. Expressed as a divisor so
        // a designer changing the dorm rate cannot accidentally make idling better than
        // actually resting.
        const int idleDivisor = 4;
        int physical = SimulationRules.PercentOf(Agent.MaxStamina, dorm.RestorePhysicalPercentPerTick) / idleDivisor;
        int mental = SimulationRules.PercentOf(Agent.MaxStamina, dorm.RestoreMentalPercentPerTick) / idleDivisor;

        if (physical > 0)
            agent.AdjustStamina(physical, 0);

        if (mental > 0)
            agent.AdjustStamina(0, mental);

        return (Math.Min(physical, Agent.MaxStamina), Math.Min(mental, Agent.MaxStamina));
    }

    /// <summary>Clears injury in the infirmary, faster for less severe wounds.</summary>
    public static int Heal(WorldState world, Agent agent, IEventSink? events = null)
    {
        if (agent.InjurySeverity <= 0)
            return 0;

        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
        RecoveryRule? rule = room is null ? null : SimulationRules.RecoveryFor(room.TypeId);
        if (rule is null || rule.InjuryHealPercentPerTick <= 0)
            return 0;

        // Severity-scaled: a light injury clears fast, a critical one takes many
        // visits. The scaling is 1 - severity/100 so the worst case still heals,
        // just slowly, rather than becoming unhealable.
        int severityFactor = Math.Max(1, 100 - agent.InjurySeverity);
        int baseHeal = SimulationRules.PercentOf(Agent.MaxInjurySeverity, rule.InjuryHealPercentPerTick);
        int healed = SimulationRules.PercentOf(baseHeal, severityFactor);

        if (healed <= 0)
            return 0;

        int before = agent.InjurySeverity;
        agent.InjurySeverity = Math.Max(0, agent.InjurySeverity - healed);
        int actuallyHealed = before - agent.InjurySeverity;

        if (agent.InjurySeverity == 0)
            events?.Publish(new AgentHealed(world.Clock.Current, agent.Id, 0));

        return actuallyHealed;
    }

    /// <summary>Applies an injury. Higher severity heals more slowly.</summary>
    public static int Injure(WorldState world, Agent agent, int severity, IEventSink? events = null)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        if (severity <= 0) return 0;

        int before = agent.InjurySeverity;
        agent.InjurySeverity = Math.Min(Agent.MaxInjurySeverity, agent.InjurySeverity + severity);
        int applied = agent.InjurySeverity - before;

        events?.Publish(new AgentInjured(
            world.Clock.Current, agent.Id, 0, 0, agent.InjurySeverity));

        return applied;
    }

    /// <summary>Enters burnout when mental has bottomed out.</summary>
    private static void CheckBurnoutEntry(WorldState world, Agent agent, IEventSink? events)
    {
        int threshold = SimulationRules.Burnout("burnout_mental_threshold", 0);
        if (agent.MentalStamina > threshold)
            return;

        EnterBurnout(world, agent, events);
    }

    /// <summary>Forces an agent into burnout regardless of current mental.</summary>
    public static void EnterBurnout(WorldState world, Agent agent, IEventSink? events)
    {
        if (agent.IsBurntOut)
            return;

        agent.IsBurntOut = true;
        agent.BurnoutRecoveryTicks = 0;

        // Burned-out agents are pulled out of whatever they were doing. Refusing the
        // work is the punishment; leaving them at the workstation would let them keep
        // training through it.
        if (agent.AssignedRoomId != 0)
        {
            Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
            room?.AssignedAgentIds.Remove(agent.Id);
            agent.AssignedRoomId = 0;
        }

        AgentStatus from = agent.Status;
        agent.Status = AgentStatus.Resting;

        events?.Publish(new AgentStatusChanged(world.Clock.Current, agent.Id, from, AgentStatus.Resting));
        events?.Publish(new AgentBurnoutEntered(world.Clock.Current, agent.Id));
    }

    /// <summary>
    /// Advances burnout recovery. Only dedicated rest in a recovery room makes
    /// progress; sitting idle at the base does not.
    /// </summary>
    private static void TickBurnout(WorldState world, Agent agent, IEventSink? events)
    {
        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
        RecoveryRule? rule = room is null ? null : SimulationRules.RecoveryFor(room.TypeId);

        bool dedicatedRest =
            room is not null &&
            !room.IsUnderConstruction &&
            rule is not null &&
            rule.BurnoutReliefPercentPerTick > 0;

        if (dedicatedRest)
        {
            agent.BurnoutRecoveryTicks++;

            int mentalPercent = SimulationRules.Burnout("burnout_recovery_mental_percent", 2);
            int physicalPercent = SimulationRules.Burnout("burnout_recovery_physical_percent", 1);
            agent.AdjustStamina(
                SimulationRules.PercentOf(Agent.MaxStamina, physicalPercent),
                SimulationRules.PercentOf(Agent.MaxStamina, mentalPercent));

            // Cheaper than leaving them to rot, but not free.
            agent.AdjustLoyalty(-SimulationRules.Burnout("burnout_resting_loyalty_drain", 1));
        }
        else
        {
            // Untreated burnout bleeds loyalty much faster, and it erodes any recovery
            // progress already made.
            agent.AdjustLoyalty(-SimulationRules.Burnout("burnout_daily_loyalty_drain", 4));
            agent.BurnoutRecoveryTicks = Math.Max(0, agent.BurnoutRecoveryTicks - 1);
        }

        int exitAt = SimulationRules.Burnout("burnout_exit_mental", 30);
        bool recovered = dedicatedRest && agent.MentalStamina >= exitAt;

        if (recovered)
        {
            agent.IsBurntOut = false;
            int ticks = agent.BurnoutRecoveryTicks;
            agent.BurnoutRecoveryTicks = 0;

            events?.Publish(new AgentBurnoutRecovered(world.Clock.Current, agent.Id, ticks));
        }
    }

    /// <summary>The first recovery-category room type in the table.</summary>
    private static int FindRoomOfCategory(RoomCategory category)
    {
        foreach (RoomType room in SimulationRules.AllRoomTypes())
        {
            if (room.Category == category && SimulationRules.RecoveryFor(room.Id) is not null)
                return room.Id;
        }

        return 0;
    }
}