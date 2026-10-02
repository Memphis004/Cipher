namespace ProjectSpy.Core;

/// <summary>
/// The one piece of agency the base has over itself.
/// </summary>
/// <remarks>
/// <para>
/// Autonomy here is deliberately narrow, and the boundary is a design decision rather
/// than an omission. The director will move an agent who <em>cannot</em> work — one
/// who is exhausted, burnt out, or injured — into a recovery room, because leaving them
/// idle in a corridor is not a strategic position, it is a bug the player has to
/// notice and clean up by hand.
/// </para>
/// <para>
/// It will never send anyone to train. Training is where the player's decisions live:
/// which class to develop, which skill to neglect, when to push someone past their
/// limits. An agent that quietly walked itself into the gym would erase that choice,
/// and with it the whole reason to keep an agent at base instead of in the field.
/// </para>
/// <para>
/// It also will not move a healthy, idle agent. An idle agent with full stamina is
/// either being saved for a mission or has been deliberately parked, and guessing which
/// would be the director making a decision it has no business making.
/// </para>
/// </remarks>
public static class BaseDirector
{
    /// <summary>
    /// Moves agents who cannot usefully be doing what they are doing into recovery.
    /// </summary>
    /// <remarks>
    /// Runs every tick but only acts on a genuine need, so it is cheap in the common case
    /// and quiet most of the time.
    /// </remarks>
    public static void Run(WorldState world, IEventSink? events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        foreach (Agent agent in world.Agents.Values.OrderBy(a => a.Id.Value).ToArray())
        {
            if (!agent.Status.IsActive())
                continue;

            if (agent.Status is AgentStatus.OnMission or AgentStatus.Captured)
                continue;

            if (!NeedsRest(world, agent))
                continue;

            // Burned-out agents have already been pulled out of their room by the
            // training and recovery systems, so they have nowhere assigned. The rest
            // are still holding their post and must be moved off it.
            if (agent.AssignedRoomId != 0 && !IsRecoveryRoom(world, agent.AssignedRoomId))
                ContinueWorking(world, agent, events);
            else if (agent.AssignedRoomId == 0)
                SeekRest(world, agent, events);
        }
    }

    /// <summary>True when this agent is in no state to work.</summary>
    public static bool NeedsRest(WorldState world, Agent agent)
    {
        // Burnout first: an exhausted agent in the dorm is exactly where they should be.
        if (agent.IsBurntOut)
            return !IsInBurnoutReliefRoom(world, agent);

        if (agent.InjurySeverity >= InjuryRestThreshold)
            return !IsInfirmary(world, agent);

        // Nothing to recover from and already resting.
        return false;
    }

    /// <summary>Injury severity at or above which the director sends someone to the infirmary.</summary>
    public const int InjuryRestThreshold = 40;

    /// <summary>
    /// Releases an agent from their post so the director can place them somewhere
    /// better.
    /// </summary>
    private static void ContinueWorking(WorldState world, Agent agent, IEventSink? events)
    {
        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
        room?.AssignedAgentIds.Remove(agent.Id);

        AgentStatus from = agent.Status;
        agent.AssignedRoomId = 0;
        agent.Status = AgentStatus.Idle;
        agent.Normalize();

        events?.Publish(new AgentStatusChanged(world.Clock.Current, agent.Id, from, AgentStatus.Idle));
    }

    /// <summary>
    /// Puts an idle agent into the best available recovery room.
    /// </summary>
    /// <remarks>
    /// "Best" is the room that matches what they actually need: someone burnt out or
    /// badly injured wants a room that offers relief, not merely any bed. Where two
    /// rooms are equally suitable the closest by id wins, so the choice is deterministic
    /// and does not depend on dictionary ordering.
    /// </remarks>
    private static void SeekRest(WorldState world, Agent agent, IEventSink? events)
    {
        Room? target = SelectRestRoom(world, agent);
        if (target is null)
            return;

        target.AssignedAgentIds.Add(agent.Id);
        agent.AssignedRoomId = target.Id.Value;

        AgentStatus from = agent.Status;
        agent.Status = agent.IsBurntOut || agent.InjurySeverity > 0
            ? AgentStatus.Recovering
            : AgentStatus.Resting;

        events?.Publish(new AgentStatusChanged(world.Clock.Current, agent.Id, from, agent.Status));
    }

    /// <summary>The recovery room best suited to what this agent needs, or null.</summary>
    public static Room? SelectRestRoom(WorldState world, Agent agent)
    {
        Room? fallback = null;

        foreach (Room room in world.BaseLayout.Rooms.OrderBy(r => r.Id.Value))
        {
            if (room.IsUnderConstruction)
                continue;

            ProjectSpy.Tables.RecoveryRule? rule = SimulationRules.RecoveryFor(room.TypeId);
            if (rule is null)
                continue;

            int capacity = RoomDefinitions.WorkerSlotsFor(world, room.TypeId);
            if (room.AssignedAgentIds.Count >= capacity)
                continue;

            bool needsRelief = agent.IsBurntOut || agent.InjurySeverity >= InjuryRestThreshold;
            bool offersRelief = rule.BurnoutReliefPercentPerTick > 0 || rule.InjuryHealPercentPerTick > 0;

            if (needsRelief && offersRelief)
                return room;

            fallback ??= room;
        }

        // Any recovery room beats standing in a corridor, but nothing is better than a
        // wrong one — an agent with nothing to recover simply waits for the player.
        return fallback;
    }

    private static bool IsRecoveryRoom(WorldState world, int roomId)
    {
        Room? room = world.BaseLayout.GetRoom(new RoomId(roomId));
        return room is not null && SimulationRules.RecoveryFor(room.TypeId) is not null;
    }

    private static bool IsInBurnoutReliefRoom(WorldState world, Agent agent)
    {
        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
        if (room is null)
            return false;

        ProjectSpy.Tables.RecoveryRule? rule = SimulationRules.RecoveryFor(room.TypeId);
        return rule is not null && rule.BurnoutReliefPercentPerTick > 0;
    }

    private static bool IsInfirmary(WorldState world, Agent agent)
    {
        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
        if (room is null)
            return false;

        ProjectSpy.Tables.RecoveryRule? rule = SimulationRules.RecoveryFor(room.TypeId);
        return rule is not null && rule.InjuryHealPercentPerTick > 0;
    }
}