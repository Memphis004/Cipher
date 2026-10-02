using ProjectSpy.Core;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Recovery: stamina restore, the mental-to-physical ratio, infirmary healing, and
/// burnout being recoverable but expensive.
/// </summary>
public class RecoverySystemTests
{
    private const ulong Seed = 5150UL;

    // ---- stamina -------------------------------------------------------------

    [Fact]
    public void Dorm_RestoresPhysical()
    {
        GameSession session = World.Session(Seed);
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        Agent agent = World.Hire(session);
        agent.PhysicalStamina = 50;
        World.Assign(session, agent, dorm, AgentStatus.Resting);

        (int physical, int mental) = RecoverySystem.Restore(session.World, agent);

        Assert.True(physical > 0, "the dorm restores physical stamina");
        Assert.Equal(50 + physical, agent.PhysicalStamina);
        Assert.Equal(0, mental);
    }

    [Fact]
    public void Lounge_RestoresMentalNotPhysical()
    {
        GameSession session = World.Session(Seed);
        Room lounge = World.Place(session, World.Rooms.Lounge, 0, 0, 3);
        Agent agent = World.Hire(session);
        agent.MentalStamina = 50;
        World.Assign(session, agent, lounge, AgentStatus.Resting);

        (int physical, int mental) = RecoverySystem.Restore(session.World, agent);

        Assert.True(mental > 0, "the lounge restores mental stamina");
        Assert.Equal(50 + mental, agent.MentalStamina);
        Assert.Equal(0, physical);
    }

    [Fact]
    public void MentalRecoversRoughlyAThirdAsFastAsPhysical()
    {
        // The brief asks for this ratio to be table data, not a constant. Core exposes
        // the authored ratio, and the relationship it describes is enforced across the
        // whole table by TableValidator — so this test asserts the authored value is in
        // the designed band and that the scarcity is visible in the room data.
        int ratio = SimulationRules.MentalRecoveryRatioPercent;

        Assert.InRange(ratio, 20, 45);

        int physicalTotal = 0;
        int mentalTotal = 0;
        foreach (RecoveryRule? rule in SimulationRules.AllRoomTypes()
                     .Select(r => SimulationRules.RecoveryFor(r.Id)))
        {
            if (rule is null)
                continue;

            physicalTotal += rule.RestorePhysicalPercentPerTick;
            mentalTotal += rule.RestoreMentalPercentPerTick;
        }

        Assert.True(physicalTotal > 0, "recovery rooms must actually restore physical");
        Assert.True(mentalTotal > 0, "recovery rooms must actually restore mental");

        // Roughly a third, with headroom for integer rounding.
        Assert.InRange(mentalTotal * 100 / physicalTotal, 20, 60);
    }

    [Fact]
    public void Therapy_IsTheStrongestWayToRestoreMental()
    {
        RecoveryRule? therapy = SimulationRules.RecoveryFor(World.Rooms.Therapy);
        RecoveryRule? dorm = SimulationRules.RecoveryFor(World.Rooms.Dorm);

        Assert.NotNull(therapy);
        Assert.NotNull(dorm);

        Assert.True(
            therapy!.RestoreMentalPercentPerTick > dorm!.RestoreMentalPercentPerTick,
            "the dedicated mental room must beat the dorm at restoring mental stamina");
    }

    [Fact]
    public void Restore_IsScaledByRoomCondition()
    {
        GameSession session = World.Session(Seed);
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        Room derelict = World.Place(session, World.Rooms.Dorm, 4, 0, 3);

        derelict.Condition = 40;

        Agent inGood = World.Hire(session);
        Agent inBad = World.Hire(session);
        inGood.PhysicalStamina = 10;
        inBad.PhysicalStamina = 10;

        World.Assign(session, inGood, dorm, AgentStatus.Resting);
        World.Assign(session, inBad, derelict, AgentStatus.Resting);

        RecoverySystem.Restore(session.World, inGood);
        RecoverySystem.Restore(session.World, inBad);

        Assert.True(inGood.PhysicalStamina > inBad.PhysicalStamina,
            $"maintained dorm {inGood.PhysicalStamina} should beat derelict {inBad.PhysicalStamina}");
    }

    [Fact]
    public void Restore_NeverExceedsTheMaximum()
    {
        GameSession session = World.Session(Seed);
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        Agent agent = World.Hire(session);
        agent.PhysicalStamina = Agent.MaxStamina - 1;
        World.Assign(session, agent, dorm, AgentStatus.Resting);

        RecoverySystem.Restore(session.World, agent);

        Assert.True(agent.PhysicalStamina <= Agent.MaxStamina);
    }

    // ---- injuries ------------------------------------------------------------

    [Fact]
    public void Infirmary_ClearsInjuryOverSeveralTicks()
    {
        GameSession session = World.Session(Seed);
        Room infirmary = World.Place(session, World.Rooms.Infirmary, 0, 0, 3);
        Agent agent = World.Hire(session);
        agent.InjurySeverity = 40;
        World.Assign(session, agent, infirmary, AgentStatus.Recovering);

        RecoverySystem.Heal(session.World, agent);

        Assert.True(agent.InjurySeverity < 40, "the infirmary reduces severity");

        for (int i = 0; i < 40; i++)
            RecoverySystem.Heal(session.World, agent);

        Assert.Equal(0, agent.InjurySeverity);
    }

    [Fact]
    public void Infirmary_HealsLessSevereInjuriesFaster()
    {
        GameSession session = World.Session(Seed);
        Room infirmary = World.Place(session, World.Rooms.Infirmary, 0, 0, 3);

        Agent grazed = World.Hire(session);
        Agent critical = World.Hire(session);
        grazed.InjurySeverity = 10;
        critical.InjurySeverity = 90;

        World.Assign(session, grazed, infirmary, AgentStatus.Recovering);
        World.Assign(session, critical, infirmary, AgentStatus.Recovering);

        int grazedHealed = RecoverySystem.Heal(session.World, grazed);
        int criticalHealed = RecoverySystem.Heal(session.World, critical);

        Assert.True(grazedHealed > criticalHealed,
            $"a graze should heal faster than a critical wound: {grazedHealed} vs {criticalHealed}");
    }

    [Fact]
    public void Recovery_OutsideARecoveryRoomHealsNothing()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);
        agent.InjurySeverity = 50;
        World.Assign(session, agent, gym, AgentStatus.Training);

        int healed = RecoverySystem.Heal(session.World, agent);

        Assert.Equal(0, healed);
        Assert.Equal(50, agent.InjurySeverity);
    }

    // ---- burnout -------------------------------------------------------------

    [Fact]
    public void Burnout_EntersWhenMentalHitsZero()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);
        World.Assign(session, agent, gym, AgentStatus.Training);

        agent.MentalStamina = 0;

        var sink = new RecordingSink();
        RecoverySystem.Tick(session.World, sink);

        Assert.True(agent.IsBurntOut);
        Assert.True(sink.Saw(GameEventKind.AgentBurnoutEntered));
    }

    [Fact]
    public void Burnout_RemovesTheAgentFromTheirPost()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);
        World.Assign(session, agent, gym, AgentStatus.Training);

        RecoverySystem.EnterBurnout(session.World, agent, null);

        Assert.Equal(0, agent.AssignedRoomId);
        Assert.DoesNotContain(agent.Id, gym.AssignedAgentIds);
        Assert.NotEqual(AgentStatus.Training, agent.Status);
    }

    [Fact]
    public void Burnout_AgentRefusesAssignment()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);

        Assert.False(agent.RefusesAssignment);

        agent.IsBurntOut = true;

        Assert.True(agent.RefusesAssignment);
        Assert.False(agent.IsDeployable, "a burnt-out agent must not be deployable");
    }

    [Fact]
    public void Burnout_CostsLoyaltyEveryDayItIsUnresolved()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.IsBurntOut = true;
        agent.MentalStamina = 0;

        int loyaltyBefore = agent.Loyalty;

        for (int i = 0; i < 10; i++)
            RecoverySystem.Tick(session.World, null);

        Assert.True(agent.Loyalty < loyaltyBefore,
            $"untreated burnout must cost loyalty: {loyaltyBefore} -> {agent.Loyalty}");
    }

    [Fact]
    public void Burnout_RecoversWithDedicatedRest()
    {
        GameSession session = World.Session(Seed);
        Room therapy = World.Place(session, World.Rooms.Therapy, 0, 0, 3);
        Agent agent = World.Hire(session);

        agent.IsBurntOut = true;
        agent.MentalStamina = 10;
        World.Assign(session, agent, therapy, AgentStatus.Resting);

        var sink = new RecordingSink();
        for (int i = 0; i < 30 && agent.IsBurntOut; i++)
            RecoverySystem.Tick(session.World, sink);

        Assert.False(agent.IsBurntOut, "burnout must be recoverable with dedicated rest");
        Assert.True(sink.Saw(GameEventKind.AgentBurnoutRecovered));
    }

    [Fact]
    public void Burnout_DoesNotRecoverFromIdlingAlone()
    {
        // "Recovers only with dedicated rest" — the dorm is a recovery room but offers no
        // burnout relief, so standing in it must not be a way out.
        GameSession session = World.Session(Seed);
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        Agent agent = World.Hire(session);

        agent.IsBurntOut = true;
        agent.MentalStamina = 10;
        World.Assign(session, agent, dorm, AgentStatus.Resting);

        for (int i = 0; i < 40; i++)
            RecoverySystem.Tick(session.World, null);

        Assert.True(agent.IsBurntOut, "a plain dorm does not clear burnout");
    }

    [Fact]
    public void Burnout_IsExpensive_ItCostsLoyaltyEvenWhileResting()
    {
        // Recoverable but expensive: proper rest still costs loyalty, just less than
        // leaving them to rot. If resting were free, the optimal play would be to park
        // a burnt-out agent in therapy and never think about them again.
        GameSession session = World.Session(Seed);
        Room therapy = World.Place(session, World.Rooms.Therapy, 0, 0, 3);
        Agent agent = World.Hire(session);

        agent.IsBurntOut = true;
        agent.MentalStamina = 5;
        agent.Loyalty = Agent.StartingLoyalty;
        World.Assign(session, agent, therapy, AgentStatus.Resting);

        int startLoyalty = agent.Loyalty;

        for (int i = 0; i < 5; i++)
        {
            RecoverySystem.Tick(session.World, null);
            agent.MentalStamina = 5; // keep them burnt out across the sample
        }

        Assert.True(agent.IsBurntOut, "they should still be burnt out after 5 ticks of rest");
        Assert.True(agent.Loyalty < startLoyalty,
            $"resting through burnout should still cost loyalty: {startLoyalty} -> {agent.Loyalty}");
    }

    [Fact]
    public void Burnout_RestingCostsLessLoyaltyThanBeingLeftAlone()
    {
        GameSession session = World.Session(Seed);
        Room therapy = World.Place(session, World.Rooms.Therapy, 0, 0, 3);

        Agent resting = World.Hire(session);
        Agent neglected = World.Hire(session);

        resting.IsBurntOut = true;
        resting.MentalStamina = 5;
        neglected.IsBurntOut = true;
        neglected.MentalStamina = 5;

        World.Assign(session, resting, therapy, AgentStatus.Resting);

        int restingBefore = resting.Loyalty;
        int neglectedBefore = neglected.Loyalty;

        for (int i = 0; i < 5; i++)
        {
            RecoverySystem.Tick(session.World, null);
            resting.MentalStamina = 5;
            neglected.MentalStamina = 5;
        }

        int restingLoss = restingBefore - resting.Loyalty;
        int neglectedLoss = neglectedBefore - neglected.Loyalty;

        Assert.True(restingLoss < neglectedLoss,
            $"resting cost {restingLoss}, neglect cost {neglectedLoss}; rest must be cheaper");
    }

    // ---- phase integration ---------------------------------------------------

    [Fact]
    public void RecoveryPhase_RestoresOverAWeek()
    {
        GameSession session = World.Session(Seed);
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        Agent agent = World.Hire(session);
        agent.PhysicalStamina = 20;
        World.Assign(session, agent, dorm, AgentStatus.Resting);

        session.AdvanceTicks(Tick.TicksPerDay);

        Assert.True(agent.PhysicalStamina > 20);
    }

    [Fact]
    public void Recovery_AnAgentOnAMissionDoesNotRecover()
    {
        GameSession session = World.Session(Seed);
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        Agent agent = World.Hire(session);
        agent.PhysicalStamina = 50;
        World.Assign(session, agent, dorm, AgentStatus.OnMission);

        RecoverySystem.Tick(session.World, null);

        Assert.Equal(50, agent.PhysicalStamina);
    }

    [Fact]
    public void Recovery_AtRestingStatusOutsideARoomIsNotHealedInstantly()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.PhysicalStamina = 10;
        agent.Status = AgentStatus.Idle;

        (int physical, _) = RecoverySystem.Restore(session.World, agent);

        Assert.Equal(0, physical);
    }
}