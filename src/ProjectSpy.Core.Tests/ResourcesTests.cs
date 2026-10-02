using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves the central economy invariant: no resource can ever go negative, and a
/// refused spend changes nothing.
/// </summary>
public class ResourcesTests
{
    [Fact]
    public void TrySpend_SucceedsWhenAffordable_AndReducesTheBalance()
    {
        var r = new Resources { Funds = 100 };

        Assert.True(r.TrySpendFunds(40, out Resources result));

        Assert.Equal(60, result.Funds);
    }

    [Fact]
    public void TrySpend_AllowsSpendingTheExactBalance()
    {
        var r = new Resources { Funds = 100 };

        Assert.True(r.TrySpendFunds(100, out Resources result));
        Assert.Equal(0, result.Funds);
    }

    [Fact]
    public void TrySpend_FailsWhenShort_AndLeavesTheBalanceUnchanged()
    {
        var r = new Resources { Funds = 100 };

        Assert.False(r.TrySpendFunds(101, out Resources result));

        Assert.Equal(100, result.Funds);
    }

    [Fact]
    public void TrySpend_NeverProducesANegativeBalance()
    {
        var r = new Resources { Funds = 50 };

        // Sweep a wide range of amounts, including absurd ones, at several balances.
        foreach (long balance in new long[] { 0, 1, 7, 100, 10_000, long.MaxValue / 2 })
        {
            Resources start = r with { Funds = balance };

            foreach (long amount in new[] { 1L, 5L, 49L, 50L, 51L, 1_000L, balance + 1, long.MaxValue })
            {
                Resources after = start;
                bool ok = after.TrySpendFunds(amount, out Resources result);
                after = result;

                Assert.True(after.Funds >= 0, $"Negative funds {after.Funds} after spending {amount} of {balance}.");
                Assert.True(after.IsValid);

                // Success must mean exactly the requested deduction, nothing else.
                if (ok)
                    Assert.Equal(balance - amount, after.Funds);
                else
                    Assert.Equal(balance, after.Funds);
            }
        }
    }

    [Fact]
    public void TrySpend_RejectsNegativeAmounts()
    {
        var r = new Resources { Funds = 100 };

        // A negative "cost" would be a money printer in the caller; refuse it outright.
        Assert.False(r.TrySpendFunds(-50, out Resources result));
        Assert.Equal(100, result.Funds);
    }

    [Fact]
    public void TrySpend_MultiResource_IsAllOrNothing()
    {
        var r = new Resources { Funds = 100, Intel = 10, Materials = 5 };

        // Materials are short, so nothing at all may be deducted.
        Assert.False(r.TrySpend(50, 5, 99, out Resources result));

        Assert.Equal(100, result.Funds);
        Assert.Equal(10, result.Intel);
        Assert.Equal(5, result.Materials);
    }

    [Fact]
    public void TrySpend_MultiResource_SucceedsAndDeductsAll()
    {
        var r = new Resources { Funds = 100, Intel = 10, Materials = 5 };

        Assert.True(r.TrySpend(50, 5, 2, out Resources result));

        Assert.Equal(50, result.Funds);
        Assert.Equal(5, result.Intel);
        Assert.Equal(3, result.Materials);
    }

    [Fact]
    public void SingleResourceSpends_NeverGoNegative()
    {
        var r = new Resources { Funds = 100, Intel = 10, Materials = 5 };

        for (int amount = 0; amount <= 40; amount++)
        {
            r.TrySpendIntel(amount, out Resources intel);
            Assert.True(intel.Intel >= 0);
            Assert.True(intel.IsValid);

            r.TrySpendMaterials(amount, out Resources materials);
            Assert.True(materials.Materials >= 0);
        }
    }

    [Fact]
    public void EarnFunds_AddsWithoutOverflowingToNegative()
    {
        var r = new Resources { Funds = long.MaxValue - 1 };

        Resources after = r.EarnFunds(1000);

        Assert.Equal(long.MaxValue, after.Funds);
    }

    [Fact]
    public void AddIntel_SaturatesRatherThanWrappingNegative()
    {
        var r = new Resources { Intel = int.MaxValue - 2 };

        Resources after = r.AddIntel(100);

        Assert.Equal(int.MaxValue, after.Intel);
        Assert.True(after.IsValid);
    }

    [Fact]
    public void Heat_NeverGoesNegative_EvenWhenDecayedPastZero()
    {
        var r = new Resources { Heat = 3 };

        Resources after = r.DecayHeat(50);

        Assert.Equal(0, after.Heat);
        Assert.True(after.IsValid);
    }

    [Fact]
    public void Normalize_ClampsEveryFieldToZero()
    {
        var r = new Resources
        {
            Funds = -500,
            Intel = -1,
            Materials = -2,
            Reputation = -3,
            Heat = -4,
        };

        Resources after = r.Normalize();

        Assert.True(after.IsValid);
        Assert.Equal(0, after.Funds);
        Assert.Equal(0, after.Intel);
    }

    [Fact]
    public void IsValid_DetectsANegativeSnapshot()
    {
        Assert.True(new Resources { Funds = 1, Intel = 1 }.IsValid);
        Assert.False(new Resources { Intel = -1 }.IsValid);
    }

    [Fact]
    public void RepeatedFailedSpends_LeaveTheBalanceIntact()
    {
        var r = new Resources { Funds = 10 };

        for (int i = 0; i < 1000; i++)
            Assert.False(r.TrySpendFunds(11, out _));

        Assert.Equal(10, r.Funds);
    }

    [Fact]
    public void ProjectedWeeklyCost_SumsOnlyActiveAgents()
    {
        var active = new Agent { SalaryPerWeek = 100, Status = AgentStatus.Idle };
        var dead = new Agent { SalaryPerWeek = 100, Status = AgentStatus.Dead };

        long total = Resources.Empty.ProjectedWeeklyCost(new[] { active, dead });

        Assert.Equal(100, total);
    }
}
