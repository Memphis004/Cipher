using ProjectSpy.Core;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Recruitment: the pool is gated by HR and Reputation, candidates vary around their
/// class baseline, traits respect <c>conflicts_with</c>, and hidden traits stay hidden.
/// </summary>
public class RecruitmentSystemTests
{
    private const ulong Seed = 31337UL;

    // ---- pool gating ---------------------------------------------------------

    [Fact]
    public void WithoutAnHrRoom_NobodyIsRecruiting()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Dorm, 0, 0, 3);

        RecruitmentSystem.RefreshPool(session.World, null);

        Assert.Empty(session.World.Recruits);
        Assert.Equal(0, RecruitmentSystem.HrRoomLevel(session.World));
    }

    [Fact]
    public void WithAnHrRoom_ThePoolFills()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        RecruitmentSystem.RefreshPool(session.World, null);

        Assert.NotEmpty(session.World.Recruits);
        Assert.Equal(1, RecruitmentSystem.HrRoomLevel(session.World));
    }

    [Fact]
    public void PoolSize_GrowsWithHrLevel()
    {
        GameSession low = World.Session(Seed);
        Room lowHr = World.Place(low, World.Rooms.Hr, 0, 0, 3);
        lowHr.Level = 1;

        GameSession high = World.Session(Seed);
        Room highHr = World.Place(high, World.Rooms.Hr, 0, 0, 3);
        highHr.Level = 3;

        RecruitmentSystem.RefreshPool(low.World, null);
        RecruitmentSystem.RefreshPool(high.World, null);

        Assert.True(high.World.Recruits.Count > low.World.Recruits.Count,
            $"hr3 produced {high.World.Recruits.Count}, hr1 produced {low.World.Recruits.Count}");
    }

    [Fact]
    public void PoolSize_GrowsWithReputation()
    {
        GameSession obscure = World.Session(Seed);
        obscure.World.Resources = obscure.World.Resources with { Reputation = 0 };
        World.Place(obscure, World.Rooms.Hr, 0, 0, 3);

        GameSession famous = World.Session(Seed);
        famous.World.Resources = famous.World.Resources with { Reputation = 100 };
        World.Place(famous, World.Rooms.Hr, 0, 0, 3);

        RecruitmentSystem.RefreshPool(obscure.World, null);
        RecruitmentSystem.RefreshPool(famous.World, null);

        Assert.True(famous.World.Recruits.Count > obscure.World.Recruits.Count,
            $"high reputation produced {famous.World.Recruits.Count}, low produced {obscure.World.Recruits.Count}");
    }

    [Fact]
    public void ReputationTier_MapsThresholdsConsistently()
    {
        Assert.Equal(0, SimulationRules.ReputationTier(0));
        Assert.Equal(0, SimulationRules.ReputationTier(19));
        Assert.Equal(1, SimulationRules.ReputationTier(20));
        Assert.Equal(1, SimulationRules.ReputationTier(49));
        Assert.Equal(2, SimulationRules.ReputationTier(50));
        Assert.Equal(2, SimulationRules.ReputationTier(100));
    }

    [Fact]
    public void RefreshPool_ReplacesThePoolRatherThanAccumulating()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        RecruitmentSystem.RefreshPool(session.World, null);
        int first = session.World.Recruits.Count;

        RecruitmentSystem.RefreshPool(session.World, null);

        Assert.Equal(first, session.World.Recruits.Count);
    }

    [Fact]
    public void RefreshPool_DoesNotFireBeforeTheIntervalElapses()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        RecruitmentSystem.RefreshPool(session.World, null);
        session.World.Recruits.Clear();

        Assert.False(RecruitmentSystem.RefreshPoolIfDue(session.World, null));
        Assert.Empty(session.World.Recruits);
    }

    // ---- candidate generation -------------------------------------------------

    [Fact]
    public void Candidates_CarryAClassAndASalary()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);
        RecruitmentSystem.RefreshPool(session.World, null);

        foreach (Agent recruit in session.World.Recruits.Values)
        {
            Assert.NotNull(AgentClasses.Find(recruit.ClassId));
            Assert.True(recruit.SalaryPerWeek > 0, "a candidate expects a salary");
            Assert.False(string.IsNullOrEmpty(recruit.Codename));
        }
    }

    [Fact]
    public void CandidateSkills_VaryAroundTheClassBaseline()
    {
        // If every candidate of a class came back identical, the variance column would
        // be decorative and recruiting would be a formality.
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        var seen = new HashSet<int>();
        for (int i = 0; i < 40; i++)
        {
            RecruitmentSystem.RefreshPool(session.World, null);

            foreach (Agent recruit in session.World.Recruits.Values)
                seen.Add(recruit.Skills.Average());
        }

        Assert.True(seen.Count > 1, $"all candidates had identical average skill: {string.Join(",", seen)}");
    }

    [Fact]
    public void CandidateSkills_NeverGoNegative()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        for (int i = 0; i < 20; i++)
        {
            RecruitmentSystem.RefreshPool(session.World, null);

            foreach (Agent recruit in session.World.Recruits.Values)
            {
                foreach (SkillKind skill in SkillSet.Kinds)
                {
                    Assert.True(recruit.Skills[skill] >= 0, $"skill {skill} went negative");
                }
            }
        }
    }

    // ---- traits --------------------------------------------------------------

    [Fact]
    public void Candidates_ReceiveBetweenOneAndThreeTraits()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        for (int i = 0; i < 20; i++)
        {
            RecruitmentSystem.RefreshPool(session.World, null);

            foreach (Agent recruit in session.World.Recruits.Values)
            {
                int total = recruit.TraitIds.Count + recruit.UndiscoveredTraitIds.Count;

                Assert.InRange(total, 1, 3);
            }
        }
    }

    [Fact]
    public void ConflictingTraitsAreNeverGeneratedTogether()
    {
        // trait 3004 conflicts with 3006 and 3014 with 3017. Neither pair may co-occur,
        // in either order.
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        var pairs = new (int A, int B)[]
        {
            (3004, 3006),
            (3014, 3017),
        };

        for (int i = 0; i < 60; i++)
        {
            RecruitmentSystem.RefreshPool(session.World, null);

            foreach (Agent recruit in session.World.Recruits.Values)
            {
                var all = recruit.TraitIds.Concat(recruit.UndiscoveredTraitIds).ToList();

                foreach ((int a, int b) in pairs)
                {
                    bool both = all.Contains(a) && all.Contains(b);
                    Assert.False(both, $"candidate has conflicting traits {a} and {b}");
                }
            }
        }
    }

    [Fact]
    public void ConflictingTraitIdsResolveToRealTraits()
    {
        // Guards the parse: a conflicts_with value pointing at a nonexistent id would
        // make the conflict check silently pass.
        foreach (Trait trait in TraitLookup.All())
        {
            if (string.IsNullOrWhiteSpace(trait.ConflictsWith))
                continue;

            foreach (string part in trait.ConflictsWith.Split(','))
            {
                Assert.True(int.TryParse(part.Trim(), out int id), $"trait {trait.Id} has a non-numeric conflict");
                Assert.NotNull(TraitLookup.Find(id));
            }
        }
    }

    [Fact]
    public void HiddenTraitsAreStoredUndiscoveredNotAsVisibleTraits()
    {
        // The whole security model: Core knows, the player does not. A hidden trait in
        // TraitIds would be readable by any UI query that walks the visible list.
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        var moleTrait = TraitLookup.MoleTraitId;
        Assert.True(moleTrait > 0, "the mole trait must resolve from the table");

        bool foundAny = false;
        for (int i = 0; i < 200 && !foundAny; i++)
        {
            RecruitmentSystem.RefreshPool(session.World, null);

            foreach (Agent recruit in session.World.Recruits.Values)
            {
                if (!recruit.UndiscoveredTraitIds.Contains(moleTrait))
                    continue;

                foundAny = true;
                Assert.False(recruit.TraitIds.Contains(moleTrait),
                    "a hidden trait must never appear in the visible list");
                Assert.True(recruit.HasTrait(moleTrait), "Core must still know about it");
                Assert.False(recruit.HasRevealedTrait(moleTrait));
            }
        }

        Assert.True(foundAny, "no candidate ever rolled a hidden trait, so the chance is too low to matter");
    }

    [Fact]
    public void EveryGeneratedTraitIdResolvesToARealTrait()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);

        for (int i = 0; i < 30; i++)
        {
            RecruitmentSystem.RefreshPool(session.World, null);

            foreach (Agent recruit in session.World.Recruits.Values)
            {
                foreach (int traitId in recruit.TraitIds.Concat(recruit.UndiscoveredTraitIds))
                    Assert.NotNull(TraitLookup.Find(traitId));
            }
        }
    }

    // ---- hiring --------------------------------------------------------------

    [Fact]
    public void Hiring_MovesTheCandidateOntoTheRoster()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);
        RecruitmentSystem.RefreshPool(session.World, null);

        Agent candidate = session.World.Recruits.Values.First();
        AgentId id = candidate.Id;
        long salary = candidate.SalaryPerWeek;
        int poolBefore = session.World.Recruits.Count;

        long fee = RecruitmentSystem.HiringFeeFor(candidate);
        CommandResult result = session.Execute(new HireRecruitCommand(id, fee));

        Assert.True(result.IsOk);

        // Only the hired candidate leaves; the rest of the pool stays on the market.
        Assert.Null(session.World.GetRecruit(id));
        Assert.Equal(poolBefore - 1, session.World.Recruits.Count);

        Agent hired = Assert.Single(session.World.Agents.Values);
        Assert.True(hired.Id.IsValid);
        Assert.NotEqual(id, hired.Id);
        Assert.Equal(salary, hired.SalaryPerWeek);
        Assert.Equal(session.CurrentTick, hired.HiredOnTick);
    }

    [Fact]
    public void Hiring_CommitsToTheWeeklySalary()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);
        RecruitmentSystem.RefreshPool(session.World, null);

        Agent candidate = session.World.Recruits.Values.First();
        long salary = candidate.SalaryPerWeek;
        long fee = RecruitmentSystem.HiringFeeFor(candidate);

        session.Execute(new HireRecruitCommand(candidate.Id, fee));

        long weekly = session.World.WeeklySalaryCost();
        Assert.Equal(salary, weekly);
    }

    [Fact]
    public void HiringFee_GrowsWithCandidateQuality()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);
        RecruitmentSystem.RefreshPool(session.World, null);

        List<Agent> pool = session.World.Recruits.Values.OrderBy(a => a.Skills.Average()).ToList();
        Agent weakest = pool.First();
        Agent strongest = pool.Last();

        Assert.True(
            RecruitmentSystem.HiringFeeFor(strongest) >= RecruitmentSystem.HiringFeeFor(weakest),
            "a better candidate should not cost less to hire");
    }

    [Fact]
    public void Hiring_AnUnaffordableFeeIsRejectedAndKeepsTheCandidate()
    {
        GameSession session = World.Session(Seed, funds: 10);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);
        RecruitmentSystem.RefreshPool(session.World, null);

        Agent candidate = session.World.Recruits.Values.First();

        CommandResult result = session.Execute(new HireRecruitCommand(candidate.Id, 100_000));

        Assert.True(result.IsRejected);
        Assert.Equal(CommandReason.InsufficientFunds, result.Reason);
        Assert.NotEmpty(session.World.Recruits);
        Assert.Empty(session.World.Agents);
    }

    [Fact]
    public void Hiring_KeepsHiddenTraitsThroughToTheRoster()
    {
        GameSession session = World.Session(Seed);
        World.Place(session, World.Rooms.Hr, 0, 0, 3);
        RecruitmentSystem.RefreshPool(session.World, null);

        Agent candidate = session.World.Recruits.Values.First();
        session.Execute(new HireRecruitCommand(candidate.Id, RecruitmentSystem.HiringFeeFor(candidate)));

        Agent hired = session.World.Agents.Values.Single();

        Assert.Equal(
            candidate.UndiscoveredTraitIds.Count,
            hired.UndiscoveredTraitIds.Count);
        Assert.DoesNotContain(hired.UndiscoveredTraitIds, t => !TraitLookup.Find(t)!.IsHidden);
    }

    [Fact]
    public void RefreshPool_IsDeterministicForTheSameSeed()
    {
        static List<string> Run()
        {
            GameSession session = World.Session(Seed);
            World.Place(session, World.Rooms.Hr, 0, 0, 3);
            RecruitmentSystem.RefreshPool(session.World, null);

            return session.World.Recruits.Values
                .OrderBy(a => a.Id.Value)
                .Select(a => $"{a.ClassId}:{a.Skills}:{string.Join("+", a.TraitIds.Concat(a.UndiscoveredTraitIds).OrderBy(t => t))}")
                .ToList();
        }

        Assert.Equal(Run(), Run());
    }
}