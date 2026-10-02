using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Training: does it reward the right skill, at the right rate, and is overworking
/// actually a mistake the player can see?
/// </summary>
public class TrainingSystemTests
{
    private const ulong Seed = 991UL;

    // ---- rate ----------------------------------------------------------------

    [Fact]
    public void Training_ImprovesTheRoomTrainedSkillOnly()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session, World.Classes.Operative);

        SkillSet before = agent.Skills;
        World.Assign(session, agent, gym, AgentStatus.Training);

        TrainingSystem.Tick(session.World, null);

        Assert.True(agent.Skills.Nerve > before.Nerve, "the gym trains Nerve");
        Assert.Equal(before.Combat, agent.Skills.Combat);
        Assert.Equal(before.Tech, agent.Skills.Tech);
        Assert.Equal(before.Social, agent.Skills.Social);
        Assert.Equal(before.Infiltration, agent.Skills.Infiltration);
    }

    [Fact]
    public void Training_CostsStaminaFromTheRoomItsOwnPool()
    {
        GameSession session = World.Session(Seed);

        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Room server = World.Place(session, World.Rooms.ServerRoom, 4, 0);

        Agent physical = World.Hire(session, World.Classes.Operative);
        Agent mental = World.Hire(session, World.Classes.Technician);

        World.Assign(session, physical, gym, AgentStatus.Training);
        World.Assign(session, mental, server, AgentStatus.Training);

        TrainingSystem.Tick(session.World, null);

        Assert.True(physical.PhysicalStamina < Agent.MaxStamina, "the gym drains physical stamina");
        Assert.Equal(Agent.MaxStamina, physical.MentalStamina);

        Assert.True(mental.MentalStamina < Agent.MaxStamina, "the server room drains mental stamina");
        Assert.Equal(Agent.MaxStamina, mental.PhysicalStamina);
    }

    [Fact]
    public void Training_AFasterRoomAwardsMorePerTick()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Room isolation = World.Place(session, World.Rooms.Isolation, 4, 0);

        // Both agents start identical, so any difference is the room's rate.
        Agent fast = World.Hire(session, World.Classes.Operative);
        Agent slow = World.Hire(session, World.Classes.Operative);
        fast.Skills = SkillSet.Zero;
        slow.Skills = SkillSet.Zero;

        World.Assign(session, fast, gym, AgentStatus.Training);
        World.Assign(session, slow, isolation, AgentStatus.Training);

        TrainingSystem.Tick(session.World, null);

        Assert.True(fast.Skills.Nerve > slow.Skills.Infiltration,
            $"gym gained {fast.Skills.Nerve}, isolation gained {slow.Skills.Infiltration}");
    }

    // ---- diminishing returns --------------------------------------------------

    [Fact]
    public void Training_FullRateBelowTheSoftCapAndTaperedNearTheHardCap()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session, World.Classes.Operative);

        int soft = SimulationRules.SoftCapFor(SkillKind.Nerve);
        int hard = SimulationRules.HardCapFor(SkillKind.Nerve);

        Assert.Equal(100, TrainingSystem.DiminishingFactorPercent(session.World, WithNerve(agent, 0), SkillKind.Nerve));
        Assert.Equal(100, TrainingSystem.DiminishingFactorPercent(session.World, WithNerve(agent, soft), SkillKind.Nerve));

        int mid = soft + ((hard - soft) / 2);
        int midFactor = TrainingSystem.DiminishingFactorPercent(session.World, WithNerve(agent, mid), SkillKind.Nerve);

        Assert.InRange(midFactor, 1, 99);
        Assert.True(midFactor < 100, "the rate tapers once past the soft cap");
    }

    [Fact]
    public void Training_DiminishingFactorFallsMonotonicallyFromSoftToHardCap()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        int soft = SimulationRules.SoftCapFor(SkillKind.Nerve);
        int hard = SimulationRules.HardCapFor(SkillKind.Nerve);

        int previous = int.MaxValue;
        for (int value = soft; value <= hard; value++)
        {
            int factor = TrainingSystem.DiminishingFactorPercent(session.World, WithNerve(agent, value), SkillKind.Nerve);

            Assert.True(factor <= previous,
                $"factor rose at value {value}: {factor} > {previous}");
            previous = factor;
        }

        Assert.Equal(0, TrainingSystem.DiminishingFactorPercent(session.World, WithNerve(agent, hard), SkillKind.Nerve));
    }

    [Fact]
    public void Training_AtTheHardCapYieldsNothingButStillCostsStamina()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);
        int hard = SimulationRules.HardCapFor(SkillKind.Nerve);

        agent.Skills = agent.Skills.With(SkillKind.Nerve, hard);
        World.Assign(session, agent, gym, AgentStatus.Training);

        int physicalBefore = agent.PhysicalStamina;
        int granted = TrainingSystem.Train(session.World, agent, null);

        Assert.Equal(0, granted);
        Assert.Equal(hard, agent.Skills.Nerve);
        Assert.True(agent.PhysicalStamina < physicalBefore,
            "a capped agent still pays the stamina cost, so idling in a gym is not free");
    }

    [Fact]
    public void AddSkillExp_DoesNotBankProgressPastTheHardCap()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        int hard = SimulationRules.HardCapFor(SkillKind.Combat);

        agent.Skills = agent.Skills.With(SkillKind.Combat, hard - 1);

        int granted = agent.AddSkillExp(SkillKind.Combat, 100);

        Assert.Equal(1, granted);
        Assert.Equal(hard, agent.Skills.Combat);
    }

    // ---- affinity -------------------------------------------------------------

    [Fact]
    public void Affinity_ClassThatGrowsASkillFasterTrainsItFaster()
    {
        GameSession session = World.Session(Seed);

        Agent technician = World.Hire(session, World.Classes.Technician);
        Agent facilitator = World.Hire(session, World.Classes.Facilitator);

        int techAffinity = TrainingSystem.AffinityPercent(session.World, technician, SkillKind.Tech);
        int socialAffinity = TrainingSystem.AffinityPercent(session.World, facilitator, SkillKind.Tech);

        Assert.True(techAffinity > socialAffinity,
            $"Technician tech affinity {techAffinity} should exceed Facilitator's {socialAffinity}");
    }

    // ---- overworking ----------------------------------------------------------

    [Fact]
    public void Training_BelowTheStaminaThreshold_TrainingYieldsLess()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);

        Agent rested = World.Hire(session);
        Agent tired = World.Hire(session);

        // Identical starting skills so the only difference is the stamina pool.
        rested.Skills = SkillSet.Zero;
        tired.Skills = SkillSet.Zero;

        tired.PhysicalStamina = Agent.OverworkThreshold - 1;

        World.Assign(session, rested, gym, AgentStatus.Training);
        World.Assign(session, tired, gym, AgentStatus.Training);

        int restedGain = TrainingSystem.Train(session.World, rested, null);
        int tiredGain = TrainingSystem.Train(session.World, tired, null);

        Assert.True(restedGain > tiredGain,
            $"rested gained {restedGain}, overworked gained {tiredGain}");
    }

    [Fact]
    public void Training_BelowTheStaminaThreshold_DrainsLoyalty()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);

        agent.PhysicalStamina = Agent.OverworkThreshold - 1;
        World.Assign(session, agent, gym, AgentStatus.Training);

        int loyaltyBefore = agent.Loyalty;
        TrainingSystem.Train(session.World, agent, null);

        Assert.True(agent.Loyalty < loyaltyBefore,
            $"overworking must cost loyalty: {loyaltyBefore} -> {agent.Loyalty}");
    }

    [Fact]
    public void Training_OverworkingRepeatedlyPushesLoyaltyDown()
    {
        // The brief's "overworking must be a real, visible mistake" test: keep pushing
        // an exhausted agent and the loyalty band must visibly deteriorate.
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);

        World.Assign(session, agent, gym, AgentStatus.Training);
        agent.PhysicalStamina = Agent.OverworkThreshold - 5;

        LoyaltyBand startBand = agent.LoyaltyBand;
        int startLoyalty = agent.Loyalty;

        for (int day = 0; day < 8; day++)
        {
            TrainingSystem.Train(session.World, agent, null);
            agent.PhysicalStamina = Agent.OverworkThreshold - 5;
        }

        Assert.True(agent.Loyalty < startLoyalty - 10,
            $"loyalty should fall substantially, went {startLoyalty} -> {agent.Loyalty}");
        Assert.NotEqual(startBand, agent.LoyaltyBand);
    }

    [Fact]
    public void Training_OverworkingPublishesAnEvent()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);
        agent.PhysicalStamina = 5;
        World.Assign(session, agent, gym, AgentStatus.Training);

        var sink = new RecordingSink();
        TrainingSystem.Tick(session.World, sink);

        Assert.True(sink.Saw(GameEventKind.AgentOverworked),
            "overworking must be visible to the player, not just a silent loyalty change");
    }

    [Fact]
    public void Training_NotAtFullRateWhenStaminaIsHealthy()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);

        int loyaltyBefore = agent.Loyalty;
        World.Assign(session, agent, gym, AgentStatus.Training);
        TrainingSystem.Train(session.World, agent, null);

        Assert.Equal(loyaltyBefore, agent.Loyalty);
    }

    // ---- phase integration ----------------------------------------------------

    [Fact]
    public void TrainingPhase_AdvancesSkillsOverAWeek()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);
        World.Assign(session, agent, gym, AgentStatus.Training);

        int before = agent.Skills.Nerve;
        session.AdvanceTicks(Tick.TicksPerDay);

        Assert.True(agent.Skills.Nerve > before);
    }

    [Fact]
    public void Training_AnAgentUnderConstructionRoomDoesNotTrain()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        gym.ConstructionTicksRemaining = 10;

        Agent agent = World.Hire(session);
        World.Assign(session, agent, gym, AgentStatus.Training);

        int granted = TrainingSystem.Train(session.World, agent, null);

        Assert.Equal(0, granted);
    }

    [Fact]
    public void Training_BurntOutAgentIsPulledOutOfTheGym()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);
        World.Assign(session, agent, gym, AgentStatus.Training);

        agent.IsBurntOut = true;
        TrainingSystem.Tick(session.World, null);

        Assert.NotEqual(AgentStatus.Training, agent.Status);
        Assert.DoesNotContain(agent.Id, gym.AssignedAgentIds);
    }

    /// <summary>
    /// Returns the agent with its Nerve skill forced to a value, so diminishing returns
    /// can be probed at a specific point on the curve.
    /// </summary>
    private static Agent WithNerve(Agent agent, int value)
    {
        agent.Skills = agent.Skills.With(SkillKind.Nerve, value);
        return agent;
    }
}