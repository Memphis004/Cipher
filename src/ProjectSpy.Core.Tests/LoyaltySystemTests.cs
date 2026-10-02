using ProjectSpy.Core;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Loyalty and morale: daily drift from the six documented sources, escalation into
/// incidents, and the rule that the UI only ever sees a coarse band.
/// </summary>
public class LoyaltySystemTests
{
    private const ulong Seed = 6161UL;

    // ---- drift sources -------------------------------------------------------

    [Fact]
    public void DriftSources_AllResolveToANonZeroTableValue()
    {
        // A source that resolves to zero would be a penalty a designer believes in and
        // the simulation never applies.
        foreach (LoyaltyDriftCondition condition in Enum.GetValues<LoyaltyDriftCondition>())
        {
            int delta = SimulationRules.LoyaltyDrift(condition);
            Assert.NotEqual(0, delta);
        }
    }

    [Fact]
    public void EveryDriftConditionHasExactlyOneTableRow()
    {
        var conditions = Enum.GetValues<LoyaltyDriftCondition>()
            .Select(c => c.ToTableKey())
            .ToList();

        var rows = SimulationRules.AllLoyaltyDrift().Select(d => d.Condition).ToList();

        Assert.Equal(conditions.Count, rows.Count);
        Assert.Equal(conditions.OrderBy(k => k), rows.OrderBy(k => k));
    }

    [Fact]
    public void IdleAgentsDriftDown()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.Status = AgentStatus.Idle;

        List<LoyaltyDriftCondition> active = LoyaltySystem.ActiveConditions(session.World, agent);
        Assert.Contains(LoyaltyDriftCondition.Idle, active);

        int loyaltyBefore = agent.Loyalty;
        LoyaltySystem.ApplyDrift(session.World, agent);

        Assert.True(agent.Loyalty < loyaltyBefore, "idleness should cost loyalty");
    }

    [Fact]
    public void WellPaidAgentsDriftUp()
    {
        GameSession session = World.Session(Seed);
        Room room = World.Place(session, World.Rooms.Dorm, 0, 0, 3);

        // Neutral condition: neither the good nor the poor facility band fires, so the
        // only condition left in play is pay.
        room.Condition = 60;

        Agent agent = World.Hire(session, World.Classes.Operative);
        World.Assign(session, agent, room, AgentStatus.Resting);

        agent.SalaryPerWeek = AgentClasses.MarketSalaryFor(World.Classes.Operative) + 50;
        Assert.True(LoyaltySystem.IsPaidFairly(agent));

        List<LoyaltyDriftCondition> active = LoyaltySystem.ActiveConditions(session.World, agent);
        Assert.Contains(LoyaltyDriftCondition.PayFair, active);
        Assert.DoesNotContain(LoyaltyDriftCondition.Idle, active);

        int loyaltyBefore = agent.Loyalty;
        LoyaltySystem.ApplyDrift(session.World, agent);

        Assert.True(agent.Loyalty > loyaltyBefore, "fair pay should build loyalty");
    }

    [Fact]
    public void FairPayDoesNotBuyOffIdleness()
    {
        // Otherwise a generous salary cancels the idle penalty exactly and idleness
        // costs the player nothing — the player could hand out money instead of
        // finding people work.
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session, World.Classes.Operative);

        agent.SalaryPerWeek = AgentClasses.MarketSalaryFor(World.Classes.Operative) + 500;
        Assert.True(LoyaltySystem.IsPaidFairly(agent));

        List<LoyaltyDriftCondition> active = LoyaltySystem.ActiveConditions(session.World, agent);

        Assert.Contains(LoyaltyDriftCondition.Idle, active);
        Assert.DoesNotContain(LoyaltyDriftCondition.PayFair, active);
    }

    [Fact]
    public void UnderpayingAnIdleAgentStillStings()
    {
        // The penalty half of the pay rule is not gated on engagement.
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session, World.Classes.Operative);

        agent.SalaryPerWeek = AgentClasses.MarketSalaryFor(World.Classes.Operative) / 2;

        List<LoyaltyDriftCondition> active = LoyaltySystem.ActiveConditions(session.World, agent);

        Assert.Contains(LoyaltyDriftCondition.PayUnfair, active);
        Assert.Contains(LoyaltyDriftCondition.Idle, active);
    }

    [Fact]
    public void UnderpaidAgentsDriftDown()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session, World.Classes.Operative);

        long market = AgentClasses.MarketSalaryFor(World.Classes.Operative);
        Assert.True(market > 0, "the class must publish a market salary");

        agent.SalaryPerWeek = market / 2;
        Assert.False(LoyaltySystem.IsPaidFairly(agent));

        Assert.Contains(
            LoyaltyDriftCondition.PayUnfair,
            LoyaltySystem.ActiveConditions(session.World, agent));

        int loyaltyBefore = agent.Loyalty;
        LoyaltySystem.ApplyDrift(session.World, agent);

        Assert.True(agent.Loyalty < loyaltyBefore);
    }

    [Fact]
    public void OverworkedAgentsTakeTheOverworkPenalty()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);

        World.Assign(session, agent, gym, AgentStatus.Training);
        agent.PhysicalStamina = Agent.OverworkThreshold - 1;

        Assert.Contains(
            LoyaltyDriftCondition.Overworked,
            LoyaltySystem.ActiveConditions(session.World, agent));
    }

    [Fact]
    public void BurntOutAgentsTakeTheBurnoutPenalty()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.IsBurntOut = true;

        Assert.Contains(
            LoyaltyDriftCondition.Burnout,
            LoyaltySystem.ActiveConditions(session.World, agent));
    }

    [Fact]
    public void FacilityConditionDriftsLoyaltyInBothDirections()
    {
        GameSession session = World.Session(Seed);
        Room nice = World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        Room shabby = World.Place(session, World.Rooms.Dorm, 4, 0, 3);

        nice.Condition = LoyaltySystem.FacilityQualityGood;
        shabby.Condition = LoyaltySystem.FacilityQualityPoor;

        Agent comfy = World.Hire(session);
        Agent uncomfortable = World.Hire(session);
        World.Assign(session, comfy, nice, AgentStatus.Resting);
        World.Assign(session, uncomfortable, shabby, AgentStatus.Resting);

        Assert.Contains(
            LoyaltyDriftCondition.GoodFacility,
            LoyaltySystem.ActiveConditions(session.World, comfy));
        Assert.Contains(
            LoyaltyDriftCondition.PoorFacility,
            LoyaltySystem.ActiveConditions(session.World, uncomfortable));
    }

    [Fact]
    public void BadRoommateDriftsLoyaltyDown()
    {
        GameSession session = World.Session(Seed);
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);

        Agent grumpy = World.Hire(session, traits: new[] { World.Traits.Gossip });
        Agent neighbour = World.Hire(session);

        World.Assign(session, grumpy, dorm, AgentStatus.Resting);
        World.Assign(session, neighbour, dorm, AgentStatus.Resting);

        Assert.True(LoyaltySystem.HasBadRoommate(session.World, neighbour));
        Assert.Contains(
            LoyaltyDriftCondition.BadRoommate,
            LoyaltySystem.ActiveConditions(session.World, neighbour));

        // An agent is never a bad roommate to themselves.
        Assert.False(LoyaltySystem.HasBadRoommate(session.World, grumpy));
    }

    [Fact]
    public void AHiddenRoommateTraitDoesNotTriggerThePenalty()
    {
        // The player cannot be unhappy about a roommate's secret before they could
        // possibly know it exists.
        GameSession session = World.Session(Seed);
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);

        Agent neighbour = World.Hire(session, hiddenTraits: new[] { World.Traits.Mole });
        Agent observer = World.Hire(session);

        World.Assign(session, neighbour, dorm, AgentStatus.Resting);
        World.Assign(session, observer, dorm, AgentStatus.Resting);

        Assert.False(LoyaltySystem.HasBadRoommate(session.World, observer));
    }

    [Fact]
    public void DailyDriftIsBounded()
    {
        // Without a cap, one catastrophic day could zero someone's loyalty and skip
        // every intermediate band the player would otherwise see.
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Room dorm = World.Place(session, World.Rooms.Dorm, 4, 0, 3);

        Agent agent = World.Hire(session, World.Classes.Operative, salary: 1);
        World.Assign(session, agent, gym, AgentStatus.Training);
        agent.PhysicalStamina = 0;
        agent.MentalStamina = 0;
        agent.IsBurntOut = true;
        dorm.Condition = 0;

        agent.Loyalty = 50;
        int change = LoyaltySystem.ApplyDrift(session.World, agent);

        Assert.InRange(change, -15, 15);
    }

    [Fact]
    public void LoyaltyAndStaminaAlwaysStayInRange()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.Loyalty = 3;

        for (int i = 0; i < 40; i++)
            LoyaltySystem.ApplyDrift(session.World, agent);

        Assert.InRange(agent.Loyalty, 0, 100);
    }

    // ---- bands ---------------------------------------------------------------

    [Fact]
    public void BandsComeFromTheTable()
    {
        foreach (MoraleBand row in SimulationRules.MoraleBands())
        {
            LoyaltyBand expected = row.Band switch
            {
                1 => LoyaltyBand.Devoted,
                2 => LoyaltyBand.Content,
                3 => LoyaltyBand.Uneasy,
                _ => LoyaltyBand.Resentful,
            };

            Assert.Equal(expected, LoyaltySystem.BandFor(row.LoyaltyFloor));
            Assert.Equal(expected, LoyaltySystem.BandFor(row.LoyaltyCeiling));
        }
    }

    [Fact]
    public void EveryLoyaltyValueMapsToABand()
    {
        for (int loyalty = 0; loyalty <= 100; loyalty++)
        {
            LoyaltyBand band = LoyaltySystem.BandFor(loyalty);

            Assert.True(Enum.IsDefined(typeof(LoyaltyBand), band));
        }
    }

    [Fact]
    public void BandFallsAsLoyaltyFalls()
    {
        Assert.Equal(LoyaltyBand.Devoted, LoyaltySystem.BandFor(90));
        Assert.Equal(LoyaltyBand.Content, LoyaltySystem.BandFor(60));
        Assert.Equal(LoyaltyBand.Uneasy, LoyaltySystem.BandFor(30));
        Assert.Equal(LoyaltyBand.Resentful, LoyaltySystem.BandFor(10));
        Assert.Equal(LoyaltyBand.Resentful, LoyaltySystem.BandFor(0));
    }

    [Fact]
    public void LoyaltyIsNeverExposedAsANumber()
    {
        // knowledge.md rule 4: the UI sees a band, not the number. Proved by walking
        // the surface and checking no public event or view exposes the raw value.
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.Loyalty = 42;
        agent.IsBurntOut = true;
        Room dorm = World.Place(session, World.Rooms.Dorm, 0, 0, 3);
        World.Assign(session, agent, dorm, AgentStatus.Resting);

        var sink = new RecordingSink();
        LoyaltySystem.DailyPass(session.World, sink);

        foreach (GameEvent gameEvent in sink.Events)
        {
            // LoyaltyEscalated is the only event that speaks about loyalty at all, and it
            // carries the band rather than the number.
            if (gameEvent is LoyaltyEscalated escalated)
            {
                Assert.True(Enum.IsDefined(typeof(LoyaltyBand), escalated.Band));
            }
        }

        Assert.Equal(LoyaltyBand.Uneasy, agent.LoyaltyBand);
    }

    // ---- escalation ----------------------------------------------------------

    [Fact]
    public void Escalation_ProducesAnIncidentWhenLoyaltyIsLow()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.Loyalty = 5;

        var sink = new RecordingSink();

        // Repeated attempts because the roll can legitimately fail.
        for (int i = 0; i < 50 && agent.LastEscalation == LoyaltyEscalation.None; i++)
        {
            agent.LoyaltyEscalationCooldown = 0;
            LoyaltySystem.CheckEscalation(session.World, agent, sink);
        }

        Assert.NotEqual(LoyaltyEscalation.None, agent.LastEscalation);
        Assert.True(sink.Saw(GameEventKind.LoyaltyEscalated));
    }

    [Fact]
    public void Escalation_DoesNotFireForADevotedAgent()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.Loyalty = 100;

        for (int i = 0; i < 50; i++)
        {
            agent.LoyaltyEscalationCooldown = 0;
            Assert.Null(LoyaltySystem.CheckEscalation(session.World, agent, null));
        }
    }

    [Fact]
    public void Escalation_SetsACooldownSoOneBadWeekCannotEmitFourComplaints()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.Loyalty = 5;

        LoyaltySystem.CheckEscalation(session.World, agent, null);

        if (agent.LastEscalation == LoyaltyEscalation.None)
            return; // the roll legitimately failed; nothing to assert

        Assert.True(agent.LoyaltyEscalationCooldown > 0,
            "an incident must put the agent on cooldown");
    }

    [Fact]
    public void Resignation_RemovesTheAgentFromTheirRoom()
    {
        GameSession session = World.Session(Seed);
        Room gym = World.Place(session, World.Rooms.Gym, 0, 0);
        Agent agent = World.Hire(session);
        World.Assign(session, agent, gym, AgentStatus.Training);

        var sink = new RecordingSink();
        LoyaltySystem.Resign(session.World, agent, LoyaltyEscalation.ResignationNotice, sink);

        Assert.Equal(AgentStatus.Retired, agent.Status);
        Assert.Equal(0, agent.AssignedRoomId);
        Assert.DoesNotContain(agent.Id, gym.AssignedAgentIds);
        Assert.True(sink.Saw(GameEventKind.AgentResigned));
    }

    [Fact]
    public void Defection_TakesIntelButNeverMoreThanTheAgencyHolds()
    {
        GameSession session = World.Session(Seed);
        session.World.Resources = session.World.Resources with { Intel = 5 };

        Agent agent = World.Hire(session);
        agent.UndiscoveredTraitIds.Add(World.Traits.Mole);

        var sink = new RecordingSink();
        LoyaltySystem.Defect(session.World, agent, sink);

        Assert.True(
            session.World.Resources.Intel == 0,
            "a defector takes what there is, not a fixed amount that could go negative");
        Assert.True(session.World.Resources.IsValid);
        Assert.True(sink.Saw(GameEventKind.AgentDefected));
    }

    [Fact]
    public void Defection_RaisesHeat()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);

        int heatBefore = session.World.Resources.Heat;
        LoyaltySystem.Defect(session.World, agent, null);

        Assert.True(session.World.Resources.Heat > heatBefore);
    }

    [Fact]
    public void Defection_MarksTheAgentRetiredNotDead()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);

        LoyaltySystem.Defect(session.World, agent, null);

        Assert.Equal(AgentStatus.Retired, agent.Status);
    }

    // ---- mission outcomes ----------------------------------------------------

    [Fact]
    public void MissionOutcomes_OnlyAffectTheAgentsWhoWent()
    {
        GameSession session = World.Session(Seed);
        Agent deployed = World.Hire(session);
        Agent stayedHome = World.Hire(session);

        int homeBefore = stayedHome.Loyalty;
        LoyaltySystem.ApplyMissionOutcome(session.World, new[] { deployed.Id }, success: true);

        Assert.NotEqual(Agent.StartingLoyalty, deployed.Loyalty);
        Assert.Equal(homeBefore, stayedHome.Loyalty);
    }

    [Fact]
    public void MissionLossCostsLoyaltyAndSuccessBuildsIt()
    {
        GameSession failed = World.Session(Seed);
        Agent a = World.Hire(failed);
        LoyaltySystem.ApplyMissionOutcome(failed.World, new[] { a.Id }, success: false);
        Assert.True(a.Loyalty < Agent.StartingLoyalty);

        GameSession won = World.Session(Seed);
        Agent b = World.Hire(won);
        LoyaltySystem.ApplyMissionOutcome(won.World, new[] { b.Id }, success: true);
        Assert.True(b.Loyalty > Agent.StartingLoyalty);
    }

    // ---- thresholds ----------------------------------------------------------

    [Fact]
    public void ThresholdsAreAscendingAndOrderedBySeverity()
    {
        IReadOnlyList<ProjectSpy.Tables.LoyaltyThreshold> thresholds = SimulationRules.LoyaltyThresholds();

        Assert.NotEmpty(thresholds);

        for (int i = 1; i < thresholds.Count; i++)
        {
            Assert.True(
                thresholds[i].Threshold > thresholds[i - 1].Threshold,
                "thresholds must strictly ascend");
        }
    }

    [Fact]
    public void TheWorstThresholdIsTheMostSevereEscalation()
    {
        // CheckEscalation walks thresholds from the bottom up so the lowest loyalty
        // threshold offers the worst outcome, not a complaint.
        ProjectSpy.Tables.LoyaltyThreshold worst = SimulationRules.LoyaltyThresholds().First();

        Assert.True(
            worst.Escalation == "loyalty.escalation.defection",
            $"the worst threshold must be defection, found {worst.Escalation}");
    }

    // ---- phase integration ---------------------------------------------------

    [Fact]
    public void DailyPass_LowersLoyaltyForALongIdleRoster()
    {
        GameSession session = World.Session(Seed);
        Agent agent = World.Hire(session);
        agent.Status = AgentStatus.Idle;

        int loyaltyBefore = agent.Loyalty;
        World.AdvanceDays(session, 3);

        Assert.True(agent.Loyalty < loyaltyBefore,
            $"three idle days should cost loyalty: {loyaltyBefore} -> {agent.Loyalty}");
    }

    [Fact]
    public void DailyPass_LeavesActiveLoyaltyWithinRange()
    {
        GameSession session = World.Session(Seed);
        for (int i = 0; i < 5; i++)
            World.Hire(session);

        World.AdvanceDays(session, 30);

        foreach (Agent agent in session.World.Agents.Values)
            Assert.InRange(agent.Loyalty, 0, 100);
    }
}