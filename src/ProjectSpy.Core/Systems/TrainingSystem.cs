using ProjectSpy.Tables;

// The table's SkillKind is a distinct type from Core's (see data/README.md); this
// alias keeps every crossing between the two explicit.
using TableSkillKind = ProjectSpy.Tables.SkillKind;

namespace ProjectSpy.Core;

/// <summary>
/// Applies training progress and the stamina it costs, once per tick.
/// </summary>
/// <remarks>
/// <para>
/// The gain is <c>train_rate_per_tick × roomLevelMultiplier × classAffinity ×
/// traitModifier</c>, then shaped by two independent factors:
/// </para>
/// <list type="number">
/// <item>Diminishing returns as the trained skill approaches its cap from
/// <c>skill_cap.csv</c>. Full rate below the soft cap, tapering linearly to the floor
/// percentage at the hard cap, and nothing at all beyond it.</item>
/// <item>The overwork penalty: below the room's stamina threshold the agent still
/// trains, but at a fraction of the rate, and loses Loyalty. Overworking has to be a
/// mistake the player can see they made, or they will simply always overwork.</item>
/// </list>
/// <para>
/// Integer arithmetic throughout. Every percentage goes through
/// <see cref="SimulationRules.PercentOf(int,int)"/> so rounding is identical on every
/// platform (knowledge.md rule 6).
/// </para>
/// </remarks>
public static class TrainingSystem
{
    /// <summary>
    /// Runs one training tick for every agent assigned to a training room.
    /// </summary>
    /// <param name="world">The world to mutate.</param>
    /// <param name="events">Where <c>AgentTrained</c> results are published.</param>
    public static void Tick(WorldState world, IEventSink? events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        // Snapshot first: training mutates agent state, and iterating the live
        // dictionary while other systems could observe it would make the pass
        // order-dependent.
        foreach (Agent agent in world.Agents.Values.OrderBy(a => a.Id.Value).ToArray())
        {
            if (agent.Status != AgentStatus.Training)
                continue;

            if (agent.IsBurntOut)
            {
                // Burnout refuses the work outright. The assignment is broken here
                // rather than in Recovery so the reason is visible at the moment it
                // happens, and so a burnt-out agent never silently burns stamina.
                TrainingSystem.ReleaseForBurnout(world, agent, events);
                continue;
            }

            Train(world, agent, events);
        }
    }

    /// <summary>
    /// Computes and applies one tick of training for a single agent.
    /// </summary>
    /// <returns>The experience granted, after caps and the overwork penalty.</returns>
    public static int Train(WorldState world, Agent agent, IEventSink? events)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
        if (room is null || room.IsUnderConstruction)
            return 0;

        TrainingRule? rule = SimulationRules.TrainingFor(room.TypeId);
        if (rule is null)
            return 0;

        SkillKind skill = ruleRoomSkill(room.TypeId);
        int baseRate = SimulationRules.TrainRatePerTick(room.TypeId);

        if (baseRate <= 0)
            return 0;

        // Room level: +level_multiplier_percent per level above 1.
        int levelPercent = 100 + (SimulationRules.PercentOf(rule.LevelMultiplierPercent, Math.Max(0, room.Level - 1)));
        int affinityPercent = AffinityPercent(world, agent, skill);
        int traitPercent = TraitModifierPercent(world, agent);

        long gain = baseRate;
        gain = (long)gain * levelPercent / 100L;
        gain = gain * affinityPercent / 100L;
        gain = gain * traitPercent / 100L;

        // Diminishing returns, then the overwork penalty. Order matters: the cap is a
        // property of the skill, so it applies to what the agent would otherwise have
        // learned, while overworking is a property of this tick.
        gain = gain * DiminishingFactorPercent(world, agent, skill) / 100L;

        bool overworked = IsOverworking(agent, rule);
        if (overworked)
        {
            gain = gain * rule.OverworkGainPercent / 100L;
            agent.AdjustLoyalty(-rule.OverworkLoyaltyDrain);
        }

        int granted = agent.AddSkillExp(skill, (int)Math.Max(0, gain));

        // Stamina is spent regardless of whether the gain was capped away, otherwise an
        // agent at max skill could idle in a training room for free.
        if (rule.StaminaCostPhysical > 0)
            agent.AdjustStamina(-rule.StaminaCostPhysical, 0);
        else if (rule.StaminaCostMental > 0)
            agent.AdjustStamina(0, -rule.StaminaCostMental);

        events?.Publish(new AgentTrained(world.Clock.Current, agent.Id, skill, granted, agent.Skills[skill]));

        if (overworked)
            events?.Publish(new AgentOverworked(world.Clock.Current, agent.Id, agent.PhysicalStamina, agent.MentalStamina));

        return granted;
    }

    /// <summary>
    /// The gain multiplier, in percent, for a skill given the agent's current value.
    /// </summary>
    /// <remarks>
    /// Full rate at or below the soft cap. Between the soft and hard caps the rate
    /// tapers linearly to the floor percentage, so a skill that is nearly maxed still
    /// moves — just slowly — rather than hitting a wall the player cannot see coming.
    /// Past the hard cap there is nothing left to gain at all.
    /// </remarks>
    public static int DiminishingFactorPercent(WorldState world, Agent agent, SkillKind skill)
    {
        int soft = SimulationRules.SoftCapFor(skill);
        int hard = SimulationRules.HardCapFor(skill);

        // A skill with no cap row trains at full rate; this keeps an incomplete table
        // from silently freezing every agent's progress.
        if (soft <= 0 || hard <= soft)
            return 100;

        int value = agent.Skills[skill];
        int floor = SimulationRules.SkillCapFloorPercent(skill);

        if (value <= soft)
            return 100;

        if (value >= hard)
            return 0;

        int span = hard - soft;
        int above = value - soft;
        return floor + ((100 - floor) * (span - above) / span);
    }

    /// <summary>
    /// How well this class trains this skill, in percent.
    /// </summary>
    /// <remarks>
    /// Derived from the class's growth value for the skill, so a Technician genuinely
    /// trains faster in the server room than an Operative does. Normalised against the
    /// room's own affinity base so a class that grows slowly in a skill is not
    /// penalised twice.
    /// </remarks>
    public static int AffinityPercent(WorldState world, Agent agent, SkillKind skill)
    {
        AgentClass? agentClass = AgentClasses.Find(agent.ClassId);
        TrainingRule? rule = SimulationRules.TrainingFor(TrainingRoomFor(skill));
        int baseAffinity = rule?.AffinityBasePercent ?? 100;

        if (agentClass is null)
            return baseAffinity;

        int growth = GrowthFor(agentClass, skill);

        // Growth of 3 is the strongest in the table and 1 the weakest; map onto the
        // room's base affinity without ever dropping below it.
        const int strongestGrowth = 3;
        if (growth <= 0)
            return baseAffinity;

        return baseAffinity + SimulationRules.PercentOf(baseAffinity, ((growth - 1) * 100) / strongestGrowth);
    }

    private static int GrowthFor(AgentClass agentClass, SkillKind skill) => skill switch
    {
        SkillKind.Infiltration => agentClass.GrowthInfiltration,
        SkillKind.Combat => agentClass.GrowthCombat,
        SkillKind.Tech => agentClass.GrowthTech,
        SkillKind.Social => agentClass.GrowthSocial,
        SkillKind.Nerve => agentClass.GrowthNerve,
        _ => 0,
    };

    /// <summary>
    /// The multiplier from the agent's traits, in percent. Negative-penitenced traits
    /// (slow learner, absent minded) slow training down.
    /// </summary>
    public static int TraitModifierPercent(WorldState world, Agent agent)
    {
        int percent = 100;

        foreach (int traitId in agent.TraitIds)
        {
            Trait? trait = TraitLookup.Find(traitId);
            if (trait is null || !trait.EffectType.Equals("SkillGainPenalty", StringComparison.Ordinal))
                continue;

            percent -= trait.EffectValue;
        }

        return Math.Max(0, percent);
    }

    /// <summary>True when the agent's relevant stamina pool is below the room's threshold.</summary>
    public static bool IsOverworking(Agent agent, TrainingRule rule)
    {
        if (rule.StaminaCostPhysical > 0)
            return agent.PhysicalStamina < rule.OverworkThreshold;

        if (rule.StaminaCostMental > 0)
            return agent.MentalStamina < rule.OverworkThreshold;

        return false;
    }

    /// <summary>
    /// Takes a burnt-out agent out of their training room and parks them as Resting.
    /// </summary>
    private static void ReleaseForBurnout(WorldState world, Agent agent, IEventSink? events)
    {
        if (agent.AssignedRoomId != 0)
            world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId))?.AssignedAgentIds.Remove(agent.Id);

        agent.AssignedRoomId = 0;
        AgentStatus from = agent.Status;
        agent.Status = AgentStatus.Resting;

        events?.Publish(new AgentStatusChanged(world.Clock.Current, agent.Id, from, AgentStatus.Resting));
        events?.Publish(new AgentBurnoutEntered(world.Clock.Current, agent.Id));
    }

    /// <summary>The skill a room type trains, as a Core skill.</summary>
    private static SkillKind ruleRoomSkill(int roomTypeId)
    {
        RoomType? type = RoomDefinitions.Find(roomTypeId);

        // A room whose table row is missing trains nothing. Returning a real skill here
        // would silently funnel training into Nerve on any save referencing a retired
        // room id, which is far harder to notice than doing nothing.
        if (type is null || type.TrainsSkill == TableSkillKind.None)
            return SkillKind.Nerve;

        try
        {
            return SimulationRules.ToCoreSkill(type.TrainsSkill);
        }
        catch (ArgumentOutOfRangeException)
        {
            return SkillKind.Nerve;
        }
    }

    /// <summary>
    /// The room type that trains a given skill, used to resolve the affinity base when
    /// affinity is asked about a skill rather than about a specific room.
    /// </summary>
    private static int TrainingRoomFor(SkillKind skill)
    {
        foreach (RoomType room in RoomDefinitions.AllRoomTypes())
        {
            if (room.TrainsSkill == SimulationRules.ToTableSkill(skill))
                return room.Id;
        }

        return 0;
    }
}