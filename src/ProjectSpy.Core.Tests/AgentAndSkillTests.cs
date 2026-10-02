using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>Proves agent invariants: clamping, loyalty banding and hidden-trait isolation.</summary>
public class AgentAndSkillTests
{
    [Fact]
    public void Stamina_ClampsToLegalRange()
    {
        var agent = new Agent { PhysicalStamina = 500, MentalStamina = -40 };
        agent.Normalize();

        Assert.Equal(Agent.MaxStamina, agent.PhysicalStamina);
        Assert.Equal(0, agent.MentalStamina);
    }

    [Fact]
    public void AdjustStamina_ClampsRatherThanOverflowing()
    {
        var agent = new Agent { PhysicalStamina = 95, MentalStamina = 2 };

        agent.AdjustStamina(50, -50);

        Assert.Equal(Agent.MaxStamina, agent.PhysicalStamina);
        Assert.Equal(0, agent.MentalStamina);
    }

    [Fact]
    public void AdjustLoyalty_ClampsToZeroHundred()
    {
        var agent = new Agent { Loyalty = 95 };

        agent.AdjustLoyalty(50);
        Assert.Equal(100, agent.Loyalty);

        agent.AdjustLoyalty(-500);
        Assert.Equal(0, agent.Loyalty);
    }

    [Theory]
    [InlineData(100, LoyaltyBand.Devoted)]
    [InlineData(75, LoyaltyBand.Devoted)]
    [InlineData(74, LoyaltyBand.Content)]
    [InlineData(50, LoyaltyBand.Content)]
    [InlineData(25, LoyaltyBand.Uneasy)]
    [InlineData(0, LoyaltyBand.Resentful)]
    public void LoyaltyBand_MapsRawLoyaltyToACoarseBand(int loyalty, LoyaltyBand expected)
    {
        var agent = new Agent { Loyalty = loyalty };
        Assert.Equal(expected, agent.LoyaltyBand);
    }

    [Fact]
    public void IsOverworking_TriggersBelowTwenty()
    {
        Assert.False(new Agent { PhysicalStamina = 20, MentalStamina = 20 }.IsOverworking);
        Assert.True(new Agent { PhysicalStamina = 19, MentalStamina = 100 }.IsOverworking);
        Assert.True(new Agent { PhysicalStamina = 100, MentalStamina = 0 }.IsOverworking);
    }

    [Fact]
    public void IsDeployable_ExcludesCapturedDeadAndOverworked()
    {
        Assert.True(new Agent { Status = AgentStatus.Idle }.IsDeployable);
        Assert.False(new Agent { Status = AgentStatus.Captured }.IsDeployable);
        Assert.False(new Agent { Status = AgentStatus.Dead }.IsDeployable);
        Assert.False(new Agent { Status = AgentStatus.Idle, PhysicalStamina = 5 }.IsDeployable);
    }

    [Fact]
    public void HiddenTraits_AreCountedButNotReportedAsRevealed()
    {
        var agent = new Agent();
        agent.TraitIds.Add(11);
        agent.UndiscoveredTraitIds.Add(12);

        // The mole must be detectable by the simulation...
        Assert.True(agent.HasTrait(12));
        Assert.True(agent.HasTrait(11));

        // ...but no UI-facing query may reveal which trait is undiscovered.
        Assert.False(agent.HasRevealedTrait(12));
        Assert.True(agent.HasRevealedTrait(11));
    }

    [Fact]
    public void AddExp_LevelsUpAndCarriesRemainder()
    {
        var agent = new Agent { Level = 1 };

        agent.AddExp(100);

        Assert.Equal(2, agent.Level);
        Assert.Equal(0, agent.Exp);
    }

    [Fact]
    public void AddExp_IgnoresNonPositiveAmounts()
    {
        var agent = new Agent { Level = 1, Exp = 5 };

        agent.AddExp(0);
        agent.AddExp(-100);

        Assert.Equal(1, agent.Level);
        Assert.Equal(5, agent.Exp);
    }

    [Fact]
    public void AgentStatus_AvailabilityHelpers()
    {
        Assert.True(AgentStatus.Idle.IsActive());
        Assert.True(AgentStatus.OnMission.IsActive());
        Assert.False(AgentStatus.Dead.IsActive());
        Assert.False(AgentStatus.Retired.IsActive());

        Assert.True(AgentStatus.OnMission.IsUnavailable());
        Assert.True(AgentStatus.Captured.IsUnavailable());
        Assert.False(AgentStatus.Idle.IsUnavailable());
    }

    // ---- SkillSet ------------------------------------------------------------

    [Fact]
    public void SkillSet_IndexerReadsEverySkill()
    {
        var s = new SkillSet(1, 2, 3, 4, 5);

        Assert.Equal(1, s[SkillKind.Infiltration]);
        Assert.Equal(2, s[SkillKind.Combat]);
        Assert.Equal(3, s[SkillKind.Tech]);
        Assert.Equal(4, s[SkillKind.Social]);
        Assert.Equal(5, s[SkillKind.Nerve]);
    }

    [Fact]
    public void SkillSet_With_ReplacesOnlyTheNamedSkill()
    {
        var s = new SkillSet(1, 2, 3, 4, 5);

        SkillSet updated = s.With(SkillKind.Tech, 99);

        Assert.Equal(99, updated.Tech);
        Assert.Equal(1, updated.Infiltration);
        Assert.Equal(5, updated.Nerve);
    }

    [Fact]
    public void SkillSet_SumIsComponentWise()
    {
        var sum = new SkillSet(1, 2, 3, 4, 5) + new SkillSet(10, 20, 30, 40, 50);

        Assert.Equal(new SkillSet(11, 22, 33, 44, 55), sum);
    }

    [Fact]
    public void SkillSet_ClampNonNegative_FloorsAtZero()
    {
        SkillSet clamped = SkillSet.ClampNonNegative(new SkillSet(-5, 3, -1, 9, 0));

        Assert.Equal(0, clamped.Infiltration);
        Assert.Equal(3, clamped.Combat);
        Assert.Equal(0, clamped.Tech);
    }

    [Fact]
    public void SkillSet_HighestKind_BreaksTiesInDeclarationOrder()
    {
        Assert.Equal(SkillKind.Nerve, new SkillSet(1, 2, 3, 4, 5).HighestKind());
        Assert.Equal(SkillKind.Infiltration, new SkillSet(10, 10, 10, 10, 10).HighestKind());
        Assert.Equal(SkillKind.Combat, new SkillSet(3, 9, 3, 3, 3).HighestKind());
    }

    [Fact]
    public void SkillSet_Average_IsIntegerRoundedDown()
    {
        Assert.Equal(3, new SkillSet(1, 2, 3, 4, 5).Average()); // 15 / 5
        Assert.Equal(2, new SkillSet(1, 2, 3, 4, 4).Average()); // 14 / 5, truncated
    }
}
